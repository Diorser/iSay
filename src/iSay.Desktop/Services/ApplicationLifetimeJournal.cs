using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace iSay.Desktop.Services;

/// <summary>独立记录进程生存期，致命异常不依赖可能来不及排空的普通日志队列。</summary>
public sealed class ApplicationLifetimeJournal
{
    private readonly object gate = new();
    private readonly string logPath, statePath;
    private readonly long maximumBytes;
    private readonly string launchId = Guid.NewGuid().ToString("N");
    private readonly int processId = Environment.ProcessId;
    private readonly long startedTicks;
    private string phase = "运行中", exitReason = "";
    private bool fatal, completed;
    public string PreviousOutcome { get; private set; } = "首次记录";

    public ApplicationLifetimeJournal(string directory, string version, bool background, long maximumBytes = 1024 * 1024)
    {
        logPath = Path.Combine(directory, "进程诊断.jsonl");
        statePath = Path.Combine(directory, "进程状态.json");
        this.maximumBytes = maximumBytes;
        using var process = Process.GetCurrentProcess();
        startedTicks = process.StartTime.ToUniversalTime().Ticks;
        Safe(() =>
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(statePath))
            {
                try
                {
                    using var previous = JsonDocument.Parse(File.ReadAllText(statePath));
                    var state = previous.RootElement;
                    PreviousOutcome = state.GetProperty("Phase").GetString() == "已退出" ? "上次正常退出"
                        : "上次未完成退出，需结合系统事件判断";
                    Append("上次状态", state.Clone());
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
                { PreviousOutcome = "旧状态无法解析，原文件保留在诊断记录中"; Append("旧状态异常", new { Type = error.GetType().Name }); }
            }
            Append("进程启动", new { Version = version, Background = background, PreviousOutcome });
            WriteState();
        });
    }

    public void Record(string category, object? detail = null) => Safe(() => Append(category, detail));
    public void Heartbeat(object detail) => Safe(() =>
    {
        if (completed || fatal) return;
        Append("存活心跳", detail); WriteState();
    });
    public void BeginExit(string reason) => Safe(() =>
    {
        if (completed || fatal) return;
        phase = "退出中"; exitReason = reason;
        Append("退出请求", new { Reason = reason }); WriteState();
    });
    public void Exception(string source, Exception error, bool terminating) => Safe(() =>
    {
        Append(terminating ? "致命异常" : "已捕获异常", new { Source = source, Type = error.GetType().FullName, error.HResult, Error = error.ToString() });
        if (terminating) { fatal = true; phase = "异常终止"; exitReason = source; WriteState(); }
    });
    public void Complete(int exitCode, bool cleanupCompleted) => Safe(() =>
    {
        if (completed) return;
        completed = true;
        if (!fatal) phase = exitCode == 0 ? "已退出" : "异常退出";
        Append("应用退出", new { ExitCode = exitCode, CleanupCompleted = cleanupCompleted, Reason = exitReason }); WriteState();
    });
    public void ProcessExited(int exitCode) => Safe(() =>
    {
        Append("进程退出", new { ExitCode = exitCode, ApplicationExitObserved = completed });
        if (!completed && !fatal) { phase = "未完成退出"; WriteState(); }
    });

    private void WriteState()
    {
        var content = JsonSerializer.Serialize(new { LaunchId = launchId, ProcessId = processId, StartedUtcTicks = startedTicks,
            Time = DateTimeOffset.Now, Phase = phase, ExitReason = exitReason });
        File.WriteAllText(statePath + ".tmp", content, Encoding.UTF8);
        File.Move(statePath + ".tmp", statePath, true);
    }
    private void Append(string category, object? detail)
    {
        // 轮换仅限这份诊断，最多保留三份，不触碰语音、词库和历史。
        if (File.Exists(logPath) && new FileInfo(logPath).Length >= maximumBytes)
        {
            if (File.Exists(logPath + ".1")) File.Move(logPath + ".1", logPath + ".2", true);
            File.Move(logPath, logPath + ".1", true);
        }
        var line = JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, LaunchId = launchId, ProcessId = processId, Category = category, Detail = detail });
        using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(line + "\n"); stream.Write(bytes);
        // 记录稀疏且在后台写入；致命异常路径也能在进程终止前落盘。
        stream.Flush(true);
    }
    private void Safe(Action action)
    {
        if (!Monitor.TryEnter(gate, TimeSpan.FromMilliseconds(300))) return;
        try { action(); }
        catch (Exception) { /* 诊断磁盘不可用时不得影响录音或触发二次崩溃。 */ }
        finally { Monitor.Exit(gate); }
    }
}
