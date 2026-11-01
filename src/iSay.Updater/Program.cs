using System.Diagnostics;
using System.Text.Json;
using iSay.Updates;

var root = Path.GetFullPath(AppContext.BaseDirectory);
var parentExited = false;
try
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此更新器只用于 Windows。");
    if (args.Length != 2 || args[0] != "--install") throw new ArgumentException("用法：iSay.Updater.exe --install <请求文件>");
    var configuration = UpdateConfiguration.Load(Path.Combine(root, "release.json"));
    if (!configuration.IsConfigured) throw new InvalidDataException("发布地址或公钥尚未配置，不能安装在线更新。");
    var requestPath = Path.GetFullPath(args[1]);
    UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(requestPath)!, requestPath);
    if (!File.Exists(requestPath) || new FileInfo(requestPath).Length > 65536) throw new InvalidDataException("更新请求不存在或超出上限。");
    var request = JsonSerializer.Deserialize<UpdateInstallRequest>(await File.ReadAllTextAsync(requestPath), UpdateJson.Options)
        ?? throw new InvalidDataException("更新请求为空。");
    var executable = Path.Combine(root, "iSay.exe");
    var application = Path.Combine(root, "iSay.dll");
    UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(root)!, application);
    var version = FileVersionInfo.GetVersionInfo(application).FileVersion ?? throw new InvalidDataException("无法确定已安装版本。");
    // 在等待桌面退出前验证清单，让未配置或不可信的请求及时失败。
    if (!File.Exists(request.EnvelopePath) || new FileInfo(request.EnvelopePath).Length > ManifestCodec.MaximumEnvelopeBytes)
        throw new InvalidDataException("更新清单不存在或超出上限。");
    ManifestCodec.Verify(await File.ReadAllTextAsync(request.EnvelopePath), configuration);
    await UpdateParentGuard.WaitForExitAsync(request, root, TimeSpan.FromMinutes(2));
    parentExited = true;
    await ApplicationUpdateInstaller.InstallAsync(configuration, request, root, version);
    File.Delete(requestPath);
    using var restarted = Process.Start(new ProcessStartInfo(executable)
    {
        UseShellExecute = false, WorkingDirectory = root,
        WindowStyle = ProcessWindowStyle.Normal
    }) ?? throw new IOException("更新已安装，但主程序未能重新启动。");
    return 0;
}
catch (Exception error)
{
    // 不记录远端 URL、签名正文或私钥，仅保存本地错误类型和可解释结果。
    try
    {
        var state = UpdateSafety.PrepareDirectory(Path.Combine(root, ".update-state"));
        var path = Path.Combine(state, $"updater-error-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { occurredAtUtc = DateTimeOffset.UtcNow, errorType = error.GetType().Name, message = SafeMessage(error) }, UpdateJson.Options));
    }
    catch { }
    if (parentExited && error is not AggregateException)
    {
        try
        {
            var executable = Path.Combine(root, "iSay.exe");
            UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(root)!, executable);
            using var restarted = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = root });
        }
        catch { }
    }
    return 1;
}

static string SafeMessage(Exception error) => error switch
{
    TimeoutException => "等待主程序退出超时；未强制结束任何进程。",
    AggregateException => "更新失败且自动回滚未完成；请保留事务目录并使用完整安装器修复。",
    System.Security.Cryptography.CryptographicException => "更新签名验证失败。",
    _ => "更新未完成；请重新打开主程序检查更新，或使用完整安装器修复。"
};
