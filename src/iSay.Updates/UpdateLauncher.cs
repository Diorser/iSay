using System.Diagnostics;
using System.Text.Json;

namespace iSay.Updates;

public static class UpdateLauncher
{
    public static string WriteRequest(PreparedUpdate prepared, string cacheDirectory)
    {
        using var parent = Process.GetCurrentProcess();
        var root = UpdateSafety.PrepareDirectory(cacheDirectory);
        var path = Path.Combine(root, $"install-{Guid.NewGuid():N}.json");
        var request = new UpdateInstallRequest
        {
            EnvelopePath = Path.GetFullPath(prepared.EnvelopePath),
            PackagePaths = prepared.PackagePaths.ToDictionary(pair => pair.Key, pair => Path.GetFullPath(pair.Value)),
            ParentProcessId = parent.Id,
            ParentStartedUtcTicks = parent.StartTime.ToUniversalTime().Ticks,
            ReadyMarkerPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(prepared.EnvelopePath))!, $"ready-{parent.Id}-{parent.StartTime.ToUniversalTime().Ticks}.json")
        };
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(JsonSerializer.SerializeToUtf8Bytes(request, UpdateJson.Options));
        file.Flush(true);
        return path;
    }

    public static void Start(string requestPath) => StartAsync(requestPath).GetAwaiter().GetResult();

    public static async Task StartAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(requestPath).Length > 65536) throw new InvalidDataException("更新请求超出上限。");
        var request = JsonSerializer.Deserialize<UpdateInstallRequest>(await File.ReadAllTextAsync(requestPath, cancellationToken), UpdateJson.Options)
            ?? throw new InvalidDataException("更新请求为空。");
        using var parent = Process.GetCurrentProcess();
        if (request.ParentProcessId != parent.Id || request.ParentStartedUtcTicks != parent.StartTime.ToUniversalTime().Ticks)
            throw new InvalidDataException("只能从发起更新的进程启动更新器。");
        var marker = ValidateMarker(request);
        UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(marker)!, marker);
        File.Delete(marker);
        var executable = Path.Combine(AppContext.BaseDirectory, "iSay.Updater.exe");
        UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(executable)!, executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("更新器未随本版本安装，请使用完整安装器升级。", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--install");
        start.ArgumentList.Add(Path.GetFullPath(requestPath));
        using var process = Process.Start(start) ?? throw new IOException("更新器未能启动。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (!File.Exists(marker))
            {
                if (process.HasExited) throw new IOException("更新器未通过安装前检查；请查看更新诊断或使用完整安装器。");
                await Task.Delay(100, timeout.Token);
            }
            File.Delete(marker);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("更新器准备超时，主程序保持运行。"); }
    }

    internal static string ValidateMarker(UpdateInstallRequest request)
    {
        if (string.IsNullOrEmpty(request.ReadyMarkerPath)) throw new InvalidDataException("更新请求缺少握手文件。");
        var marker = Path.GetFullPath(request.ReadyMarkerPath);
        var root = Path.GetFullPath(Path.GetDirectoryName(request.EnvelopePath)!);
        if (!Path.GetDirectoryName(marker)!.Equals(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(marker) != $"ready-{request.ParentProcessId}-{request.ParentStartedUtcTicks}.json")
            throw new InvalidDataException("更新握手文件位置无效。");
        return marker;
    }
}

public static class UpdateParentGuard
{
    public static async Task WaitForExitAsync(UpdateInstallRequest request, string installDirectory,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (request.ParentProcessId <= 0 || request.ParentStartedUtcTicks <= 0 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
            throw new InvalidDataException("更新必须指定有效的父进程标识与启动时间。");
        Process parent;
        try { parent = Process.GetProcessById(request.ParentProcessId); }
        catch (ArgumentException) { throw new InvalidDataException("父进程已退出，无法确认本次更新请求来源；请重新发起更新。"); }
        using (parent)
        {
            var expected = Path.GetFullPath(Path.Combine(installDirectory, "iSay.exe"));
            var actual = parent.MainModule?.FileName;
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartedUtcTicks ||
                actual is null || !Path.GetFullPath(actual).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("父进程身份或安装位置不匹配，已拒绝更新。");
            UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(expected)!, expected);
            // 先确认握手再让桌面退出，避免按 PID 查找时进程已经消失。
            var marker = UpdateLauncher.ValidateMarker(request);
            await UpdateSafety.WriteTextAtomicAsync(marker, "{}", cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try { await parent.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("等待主程序退出超时，更新已取消；不会强制结束任何进程。"); }
        }
    }
}
