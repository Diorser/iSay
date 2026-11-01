namespace iSay.Voice.Engines;

public enum SpeechResultKind
{
    Partial,
    Final,
    Error
}

// 文字始终是当前会话的累计识别快照，调用方应替换该会话的预览，不应追加整段。
public sealed record SpeechResult(Guid SessionId, SpeechResultKind Kind, string Text, string? Error = null);

public sealed record SpeechEngineOptions
{
    public string NativeRuntimeDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "runtime");
    public string NativeExecutableName { get; init; } = "ImeService.exe";
    public TimeSpan PrepareTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan FinalTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public string ApiBaseUrl { get; init; } = "https://api.openai.com/v1";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "whisper-1";
    public string Language { get; init; } = "zh";
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public string ProxyMode { get; init; } = "system";
    public string ProxyUrl { get; init; } = "";
    // 仅包含用户明确配置的识别词条；不得传入整段草稿或前台窗口内容。
    public IReadOnlyList<string> Vocabulary { get; init; } = Array.Empty<string>();
}

public interface IStreamingSpeechEngine : IAsyncDisposable
{
    event EventHandler<SpeechResult>? ResultReceived;

    // 预先启动独立辅助进程并连接音频管道；不会访问麦克风或用户输入框。
    Task PrepareAsync(CancellationToken cancellationToken = default);
    Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default);
    ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm16KhzMono, CancellationToken cancellationToken = default);

    // 停止后返回最终完整文本；同时发出的 Final 事件与返回值属于同一次结果，不得各插入一次。
    Task<string> StopAsync(CancellationToken cancellationToken = default);
}
