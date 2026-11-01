using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace iSay.Voice.Engines;

// 只启动经过校验的独立研究副本，不连接输入法服务，不挂接键盘、鼠标或文本输入上下文。
public sealed class NativeSpeechEngine(SpeechEngineOptions options) : IStreamingSpeechEngine, IUpdatableVocabularySpeechEngine
{
    private PersistentNativeSpeechEngine? persistent;
    private readonly object persistentGate = new();
    private bool IsPersistent => options.NativeExecutableName == "ImeService.Persistent.exe";
    private PersistentNativeSpeechEngine Persistent
    {
        get
        {
            lock (persistentGate)
            {
                ThrowIfDisposed();
                if (persistent is not null) return persistent;
                persistent = new(options);
                persistent.ResultReceived += (_, result) => ResultReceived?.Invoke(this, result);
                return persistent;
            }
        }
    }
    private const string VocabularyAdapterSha256 = "B239956CC60809870EB24F98C301AD189CAC9D62BAC5E6D300F1DFA8EAA3E7CC";
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private NativeSession? _session;
    private bool _disposed;
    private string _lastCompletedText = "";

    public event EventHandler<SpeechResult>? ResultReceived;

    public Task<bool> TryUpdateVocabularyAsync(IReadOnlyList<string> words, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return IsPersistent ? Persistent.TryUpdateVocabularyAsync(words, cancellationToken) : Task.FromResult(false);
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (IsPersistent) { await Persistent.PrepareAsync(cancellationToken).ConfigureAwait(false); return; }
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (IsPersistent) { await Persistent.StartAsync(sessionId, cancellationToken).ConfigureAwait(false); return; }
        if (sessionId == Guid.Empty) throw new ArgumentException("识别会话编号不能为空。", nameof(sessionId));
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var cleanupOnFailure = false;
        try
        {
            ThrowIfDisposed();
            if (_session?.Started == true) throw new InvalidOperationException("已有录音会话正在进行。");
            cleanupOnFailure = true;
            await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
            var session = _session!;
            session.SessionId = sessionId;
            session.Started = true;
            _lastCompletedText = "";
            // 在开始前暂不发送头部，预热进程会等在读取WAV头的位置，不消耗录音内容。
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await session.Pipe.WriteAsync(CreateStreamingWaveHeader(), cancellationToken).ConfigureAwait(false); }
            finally { _writer.Release(); }
        }
        catch
        {
            if (cleanupOnFailure) await ReleaseSessionAsync().ConfigureAwait(false);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm16KhzMono, CancellationToken cancellationToken = default)
    {
        if (IsPersistent) { await Persistent.WritePcmAsync(pcm16KhzMono, cancellationToken).ConfigureAwait(false); return; }
        if ((pcm16KhzMono.Length & 1) != 0) throw new ArgumentException("16位PCM音频的字节数必须是偶数。", nameof(pcm16KhzMono));
        if (pcm16KhzMono.IsEmpty) return;
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var session = _session;
            if (session is null || !session.Started || session.Stopping) throw new InvalidOperationException("录音会话尚未开始或已经结束。");
            await session.Pipe.WriteAsync(pcm16KhzMono, cancellationToken).ConfigureAwait(false);
            session.PcmBytes += pcm16KhzMono.Length;
        }
        finally { _writer.Release(); }
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        if (IsPersistent) return await Persistent.StopAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var session = _session;
            if (session is null || !session.Started) return _lastCompletedText;
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                session.Stopping = true;
                // 关闭单向管道只发出EOF；原生程序自行排空音频、停止会话并返回最终快照。
                await session.Pipe.DisposeAsync().ConfigureAwait(false);
            }
            finally { _writer.Release(); }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.FinalTimeout);
            try
            {
                await session.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(session.OutputTask, session.ErrorTask).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RaiseError(session, "等待语音识别最终结果超时。可从预览或历史草稿恢复已识别文字。");
                throw new TimeoutException("等待语音识别最终结果超时。");
            }

            if (session.Process.ExitCode != 0 || (!session.FinalSeen && !session.CompleteMarkerSeen))
            {
                const string error = "独立识别进程未返回完整结果，请检查网络连接或识别引擎。";
                RaiseError(session, error);
                throw new InvalidOperationException(error);
            }
            _lastCompletedText = session.FinalText ?? "";
            if (!session.FinalSeen) Raise(session, SpeechResultKind.Final, "");
            return _lastCompletedText;
        }
        finally
        {
            await ReleaseSessionAsync().ConfigureAwait(false);
            _lifecycle.Release();
        }
    }

    private async Task PrepareCoreAsync(CancellationToken cancellationToken)
    {
        // 原生组件等待音频时内部会话仍会老化，进程存活不代表闲置后仍能识别。
        // 只复用最近的预热；录音中的会话不回收，重新准备期间由调用方缓存音频。
        if (_session is { } existing && !existing.Process.HasExited
            && (existing.Started || existing.DeferredStart
                || Stopwatch.GetElapsedTime(existing.PreparedTimestamp) <= TimeSpan.FromSeconds(10))) return;
        await ReleaseSessionAsync().ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此独立原生识别引擎仅支持Windows。");
        var runtime = Path.GetFullPath(options.NativeRuntimeDirectory);
        if (Path.GetFileName(options.NativeExecutableName) != options.NativeExecutableName) throw new ArgumentException("辅助程序名不能包含目录。");
        var executable = Path.Combine(runtime, options.NativeExecutableName);
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到独立识别引擎，请在设置中指定已准备的运行目录。", executable);
        await using var executableStream = File.OpenRead(executable);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(executableStream, cancellationToken).ConfigureAwait(false));
        // 仅允许已知0.9.0独立适配器；拒绝误启动用户安装的原始输入法服务。
        if (hash is not "AEAB3110E243302EAACFF38BA88771E3E525AD5F0C3376F5B51963EAA36F6000"
            and not "3AFB8E74886D5978D939B223EB49D5880F33EEB657619125FD553E6AD991AB55"
            and not "85EF820261276AB87E44399CF98724647BDEE3445F0C79BE080F885E88BBA1A4"
            and not "84292BABD1CE1AF8F534F675A6E007277C87B14F2EA02D8B941EE4B8122CFDC2"
            and not "C1ABE109F9150C3F112239E65CA4A69ADE98838AE6FC297DF71EC59AF79A0952"
            and not VocabularyAdapterSha256)
            throw new InvalidOperationException("辅助程序校验失败；请选择经过准备的独立研究运行库，不能使用已安装输入法的服务程序。");

        var vocabularyContext = AsrVocabularyContext.BuildBase64(options.Vocabulary);
        if (vocabularyContext.Length > 0 && hash != VocabularyAdapterSha256)
            throw new InvalidOperationException("当前独立运行库尚未接入原生热词，请更新运行库或暂时关闭识别热词。");

        var pipeName = "iSayAsr_" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = runtime,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--test-sami");
        start.ArgumentList.Add("--wav");
        start.ArgumentList.Add(@"\\.\pipe\" + pipeName);
        start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        // 不继承外部旧词表，空词表保持无context；仅本次显式词条进入SDK。
        start.Environment.Remove("ISAY_ASR_CONTEXT_B64");
        start.Environment.Remove("ISAY_SYNTHETIC_ASR_CONTEXT_B64");
        if (vocabularyContext.Length > 0) start.Environment["ISAY_ASR_CONTEXT_B64"] = vocabularyContext;
        var process = new Process { StartInfo = start };
        var session = new NativeSession(pipe, process)
        {
            // 只有经精确验证的两阶段适配器，才允许长期保留未开始的本机SDK。
            DeferredStart = hash == VocabularyAdapterSha256
        };
        _session = session;
        try
        {
            if (!NativeHelperProcess.Start(process)) throw new InvalidOperationException("无法启动独立识别辅助进程。");
            session.OutputTask = ReadOutputAsync(session);
            session.ErrorTask = DrainErrorAsync(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.PrepareTimeout);
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
            {
                await exited.ConfigureAwait(false);
                throw new InvalidOperationException("独立识别辅助进程在就绪前退出。");
            }
            await connected.ConfigureAwait(false);
            session.PreparedTimestamp = Stopwatch.GetTimestamp();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ReleaseSessionAsync().ConfigureAwait(false);
            throw new TimeoutException("识别引擎预热超时，请检查本机运行库。");
        }
        catch
        {
            await ReleaseSessionAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReadOutputAsync(NativeSession session)
    {
        try
        {
            while (await session.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                // 只解析本次识别结果和结束标志，其他原生日志不保存、不转发。
                if (line.Contains("got_full_vad=1", StringComparison.Ordinal)) session.CompleteMarkerSeen = true;
                if (!line.StartsWith("DBR|", StringComparison.Ordinal)) continue;
                var separator = line.IndexOf('|', 4);
                if (separator < 0 || !session.Started) continue;
                var text = line[(separator + 1)..];
                var final = line.AsSpan(4, separator - 4).SequenceEqual("1");
                session.LatestText = text;
                if (final)
                {
                    session.FinalText = text;
                    if (session.FinalSeen) continue;
                    session.FinalSeen = true;
                }
                else if (session.LastPartial == text) continue;
                else session.LastPartial = text;
                Raise(session, final ? SpeechResultKind.Final : SpeechResultKind.Partial, text);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task DrainErrorAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void RaiseError(NativeSession session, string error) => Raise(session, SpeechResultKind.Error, session.LatestText, error);

    private void Raise(NativeSession session, SpeechResultKind kind, string text, string? error = null)
    {
        if (_disposed) return;
        var handlers = ResultReceived;
        if (handlers is null) return;
        var result = new SpeechResult(session.SessionId, kind, text, error);
        // 订阅者的界面异常不能中断读取标准输出，否则辅助进程可能因管道塞满而卡住。
        foreach (EventHandler<SpeechResult> handler in handlers.GetInvocationList())
        {
            try { handler(this, result); }
            catch { }
        }
    }

    private async Task ReleaseSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        try { await session.Pipe.DisposeAsync().ConfigureAwait(false); }
        catch (IOException) { }
        try
        {
            if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
            await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        catch (TimeoutException) { }
        try { await Task.WhenAll(session.OutputTask, session.ErrorTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        finally { session.Process.Dispose(); }
    }

    private static byte[] CreateStreamingWaveHeader()
    {
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), int.MaxValue);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), int.MaxValue - 36);
        return header;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (IsPersistent)
        {
            PersistentNativeSpeechEngine? previous;
            lock (persistentGate) { if (_disposed) return; _disposed = true; previous = persistent; }
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            return;
        }
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            // 先断开管道，解除可能等待原生消费端的写操作，再等待写锁并回收自己的进程。
            if (_session is { } session)
            {
                try { await session.Pipe.DisposeAsync().ConfigureAwait(false); }
                catch (IOException) { }
            }
            await _writer.WaitAsync().ConfigureAwait(false);
            try { await ReleaseSessionAsync().ConfigureAwait(false); }
            finally { _writer.Release(); }
        }
        finally { _lifecycle.Release(); }
    }

    private sealed class NativeSession(NamedPipeServerStream pipe, Process process)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public Process Process { get; } = process;
        public Task OutputTask { get; set; } = Task.CompletedTask;
        public Task ErrorTask { get; set; } = Task.CompletedTask;
        public Guid SessionId { get; set; }
        public bool Started { get; set; }
        public long PreparedTimestamp { get; set; }
        public bool Stopping { get; set; }
        public bool FinalSeen { get; set; }
        public bool CompleteMarkerSeen { get; set; }
        public string LatestText { get; set; } = "";
        public string? LastPartial { get; set; }
        public string? FinalText { get; set; }
        public long PcmBytes { get; set; }
        public bool DeferredStart { get; init; }
    }
}
