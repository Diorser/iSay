using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace iSay.Voice.Engines;

// 兼容文件转写协议的备用后端；录音时缓存PCM，结束后一次上传，不伪装成实时识别。
public sealed class OpenAiCompatibleSpeechEngine : IStreamingSpeechEngine
{
    private readonly SpeechEngineOptions _options;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MemoryStream? _audio;
    private Guid _sessionId;
    private bool _disposed;
    private string _lastText = "";
    private const int MaxAudioBytes = 32000 * 3600;

    public event EventHandler<SpeechResult>? ResultReceived;

    public OpenAiCompatibleSpeechEngine(SpeechEngineOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        if (handler is null)
        {
            var sockets = new SocketsHttpHandler();
            if (options.ProxyMode == "direct") sockets.UseProxy = false;
            else if (options.ProxyMode == "custom")
            {
                if (!Uri.TryCreate(options.ProxyUrl, UriKind.Absolute, out var proxyUri)) throw new ArgumentException("自定义代理地址无效。");
                sockets.Proxy = new WebProxy(proxyUri);
            }
            handler = sockets;
        }
        _client = new HttpClient(handler) { Timeout = options.HttpTimeout };
    }

    public Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        _ = GetEndpoint();
        if (string.IsNullOrWhiteSpace(_options.Model)) throw new ArgumentException("请填写语音识别模型名称。");
        return Task.CompletedTask;
    }

    public async Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("识别会话编号不能为空。", nameof(sessionId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareAsync(cancellationToken).ConfigureAwait(false);
            if (_audio is not null) throw new InvalidOperationException("已有录音会话正在进行。");
            _sessionId = sessionId;
            _audio = new MemoryStream();
            _lastText = "";
        }
        finally { _gate.Release(); }
    }

    public async ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm16KhzMono, CancellationToken cancellationToken = default)
    {
        if ((pcm16KhzMono.Length & 1) != 0) throw new ArgumentException("16位PCM音频的字节数必须是偶数。", nameof(pcm16KhzMono));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_audio is null) throw new InvalidOperationException("录音会话尚未开始。");
            if (_audio.Length + pcm16KhzMono.Length > MaxAudioBytes) throw new InvalidOperationException("单次录音超过一小时，请先结束本次录音。");
            await _audio.WriteAsync(pcm16KhzMono, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_audio is null) return _lastText;
            using var audio = _audio;
            _audio = null;
            if (audio.Length == 0)
            {
                _lastText = "";
                Raise(SpeechResultKind.Final, "");
                return "";
            }
            using var multipart = new MultipartFormDataContent();
            var waveBytes = new ByteArrayContent(CreateWave(audio.GetBuffer().AsSpan(0, (int)audio.Length)));
            waveBytes.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            multipart.Add(waveBytes, "file", "recording.wav");
            multipart.Add(new StringContent(_options.Model), "model");
            multipart.Add(new StringContent("json"), "response_format");
            if (!string.IsNullOrWhiteSpace(_options.Language)) multipart.Add(new StringContent(_options.Language), "language");
            using var request = new HttpRequestMessage(HttpMethod.Post, GetEndpoint()) { Content = multipart };
            if (!string.IsNullOrWhiteSpace(_options.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            // 总时限覆盖上传、响应头和正文，避免服务器只返回头部后永久占用处理状态。
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.HttpTimeout);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"语音识别接口返回HTTP {(int)response.StatusCode}，请检查接口地址、模型和密钥。", null, response.StatusCode);
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: deadline.Token).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("语音识别接口没有返回有效的text字段。");
            _lastText = textElement.GetString() ?? "";
            Raise(SpeechResultKind.Final, _lastText);
            return _lastText;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException)
        {
            // 不转发远端响应正文，避免错误页面或第三方服务把密钥、请求信息带入界面日志。
            Raise(SpeechResultKind.Error, "", exception is TaskCanceledException ? "语音识别请求已取消或超时。" : "语音识别请求失败，请检查网络、接口地址、模型和密钥。");
            throw;
        }
        finally { _gate.Release(); }
    }

    private Uri GetEndpoint()
    {
        if (!Uri.TryCreate(_options.ApiBaseUrl.TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("请填写有效的HTTP或HTTPS语音识别接口地址。");
        return uri.AbsolutePath.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase)
            ? uri : new Uri(uri.AbsoluteUri.TrimEnd('/') + "/audio/transcriptions");
    }

    private static byte[] CreateWave(ReadOnlySpan<byte> pcm)
    {
        var bytes = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), pcm.Length);
        pcm.CopyTo(bytes.AsSpan(44));
        return bytes;
    }

    private void Raise(SpeechResultKind kind, string text, string? error = null)
    {
        if (ResultReceived is not { } handlers) return;
        var result = new SpeechResult(_sessionId, kind, text, error);
        foreach (EventHandler<SpeechResult> handler in handlers.GetInvocationList())
        {
            try { handler(this, result); }
            catch { }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _audio?.Dispose();
            _audio = null;
            _client.Dispose();
        }
        finally { _gate.Release(); }
    }
}
