using System.Diagnostics;
using System.IO;
using System.Reflection;
using iSay.Updates;

namespace iSay.Desktop;

public partial class App
{
    private UpdateClient? updateClient;
    private VerifiedRelease? availableUpdate;
    private PreparedUpdate? preparedUpdate;
    private CancellationTokenSource? updateOperation;
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private Task automaticUpdates = Task.CompletedTask;
    private string? lastRemindedVersion;
    private (string Chinese, string English)? lastUpdateText;

    private static string ProductVersion => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "0.10.0";

    private void InitializeUpdates()
    {
        try
        {
            var configuration = UpdateConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "release.json"));
            updateClient = new UpdateClient(configuration);
            window.SetUpdateStatus(configuration.IsConfigured
                ? UpdateText("尚未检查更新", "Updates have not been checked yet")
                : UpdateText("更新服务尚未配置，当前不会自动联网检查。发布地址配置完成后即可使用。", "Update service is not configured. Automatic checks will be available after a release endpoint is configured."));
            if (configuration.IsConfigured && diagnosticPath is null && !previewOnly)
                automaticUpdates = RunAutomaticUpdateChecksAsync();
        }
        catch (Exception error)
        {
            LogEvent("更新配置", error.GetType().Name);
            window.SetUpdateStatus(UpdateText("更新配置无效，请使用可信的完整安装包修复。", "Update configuration is invalid. Repair it with a trusted full installer."));
        }
    }

    private async Task RunAutomaticUpdateChecksAsync()
    {
        try
        {
            // 延迟与抖动减少启动竞争；检查器不启动常驻辅助进程。
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(30, 61)), lifetime.Token);
            var nextCheck = DateTimeOffset.MinValue;
            var wasEnabled = savedSettings.AutoCheckUpdates;
            while (!lifetime.IsCancellationRequested)
            {
                var enabled = savedSettings.AutoCheckUpdates;
                if (enabled && !wasEnabled) nextCheck = DateTimeOffset.MinValue;
                wasEnabled = enabled;
                if (enabled && DateTimeOffset.UtcNow >= nextCheck && !vm.IsRecording && !vm.IsBusy && !vm.IsFormatting && !stopping && formattingTasks.Count == 0)
                {
                    await CheckForUpdatesAsync(false);
                    nextCheck = DateTimeOffset.UtcNow + TimeSpan.FromHours(12) + TimeSpan.FromMinutes(Random.Shared.Next(0, 31));
                }
                // 忙碌时仅推迟一分钟；不能因启动时正在录音而错过整整十二小时。
                await Task.Delay(TimeSpan.FromMinutes(1), lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> HandleUpdateActionAsync(ActionRequestedEventArgs action)
    {
        switch (action.Action)
        {
            case "language-changed":
                UiText.Current.SetLanguage(action.Argument as string ?? vm.Settings.UiLanguage);
                if (availableUpdate is not null && updateOperation is null) ShowAvailableRelease();
                else if (lastUpdateText is { } text) window.RefreshUpdateStatusText(UiText.Current.IsEnglish ? text.English : text.Chinese);
                return true;
            case "third-party-notices":
                var notices = Path.Combine(AppContext.BaseDirectory, "第三方依赖声明.txt");
                if (File.Exists(notices)) Process.Start(new ProcessStartInfo(notices) { UseShellExecute = true });
                else window.SetUpdateStatus(UpdateText("当前构建未附带声明文件，请使用完整安装包查看。", "This development build does not include dependency notices. Use the full installer to view them."));
                return true;
            case "about":
                ShowWindow(); window.ShowAboutPage(); return true;
            case "check-updates":
                ShowWindow(); window.ShowAboutPage(); await CheckForUpdatesAsync(true); return true;
            case "download-update":
                await DownloadUpdateAsync(); return true;
            case "cancel-update":
                updateOperation?.Cancel(); return true;
            case "install-update":
                await InstallPreparedUpdateAsync(); return true;
            case "skip-update":
                if (availableUpdate is not null)
                {
                    // 仅保存更新偏好，不能顺便提交设置页的其他草稿。
                    var next = Snapshot(savedSettings);
                    next.SkippedUpdateVersion = availableUpdate.Manifest.Version;
                    store.Save(next); savedSettings = next;
                    vm.Settings.SkippedUpdateVersion = next.SkippedUpdateVersion;
                    window.SetUpdateStatus(UpdateText("已跳过此版本。仍可手动检查并下载。", "This version is skipped. You can still check and download manually."));
                }
                return true;
            default: return false;
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (updateClient is null || !await updateGate.WaitAsync(0)) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(45));
        updateOperation = operation;
        try
        {
            if (manual) window.SetUpdateStatus(UpdateText("正在检查更新…", "Checking for updates…"), working: true);
            var result = await updateClient.CheckAsync(ProductVersion, operation.Token);
            if (exiting) return;
            switch (result.Status)
            {
                case UpdateCheckStatus.NotConfigured:
                    window.SetUpdateStatus(UpdateText("更新服务尚未配置，当前不会自动联网检查。", "Update service is not configured. No automatic requests will be made."));
                    break;
                case UpdateCheckStatus.UpToDate:
                    availableUpdate = null; preparedUpdate = null;
                    window.SetUpdateStatus(UpdateText("当前已是最新版本。", "You are up to date."));
                    break;
                case UpdateCheckStatus.Available:
                    var previousVersion = availableUpdate?.Manifest.Version;
                    availableUpdate = result.Release;
                    if (previousVersion != availableUpdate!.Manifest.Version) preparedUpdate = null;
                    if (!manual && savedSettings.SkippedUpdateVersion == availableUpdate!.Manifest.Version) return;
                    ShowAvailableRelease();
                    if (!manual && lastRemindedVersion != availableUpdate!.Manifest.Version)
                    {
                        lastRemindedVersion = availableUpdate.Manifest.Version;
                        vm.StatusText = UpdateText($"iSay {lastRemindedVersion} 已发布，可在“关于”页下载更新。", $"iSay {lastRemindedVersion} is available. Open About to download.");
                        tray.ShowNotification("iSay", vm.StatusText);
                    }
                    break;
            }
        }
        catch (OperationCanceledException) when (!exiting)
        { window.SetUpdateStatus(UpdateText("更新检查已取消或超时，可稍后重试。", "Update check was cancelled or timed out. Try again later.")); }
        catch (Exception error) when (!exiting)
        {
            LogEvent("检查更新", error.GetType().Name);
            window.SetUpdateStatus(UpdateText("无法获取可信的更新信息，请稍后重试。", "Could not retrieve trusted update information. Try again later."));
        }
        finally { updateOperation = null; updateGate.Release(); }
    }

    private string UpdateText(string chinese, string english)
    {
        lastUpdateText = (chinese, english);
        return UiText.Current.IsEnglish ? english : chinese;
    }

    private void ShowAvailableRelease()
    {
        if (availableUpdate is null) return;
        var manifest = availableUpdate.Manifest;
        var notes = manifest.ReleaseNotes.GetValueOrDefault(UiText.Current.Language)
            ?? manifest.ReleaseNotes.GetValueOrDefault(UiText.Current.IsEnglish ? "en" : "zh-CN") ?? "";
        var size = manifest.Components.Sum(component => component.Size) / 1048576d;
        var description = UpdateText($"发现新版本 {manifest.Version} · {size:F1} MiB", $"Version {manifest.Version} available · {size:F1} MiB");
        window.SetUpdateStatus(description + (string.IsNullOrWhiteSpace(notes) ? "" : "\n\n" + notes),
            canDownload: preparedUpdate is null, canInstall: preparedUpdate is not null);
    }

    private async Task DownloadUpdateAsync()
    {
        if (availableUpdate is null || updateClient is null || !await updateGate.WaitAsync(0)) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateOperation = operation;
        try
        {
            var version = availableUpdate.Manifest.Version;
            window.SetUpdateStatus(UpdateText("正在下载并校验更新…", "Downloading and verifying update…"), working: true);
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (exiting || operation.IsCancellationRequested) return;
                var percent = value.TotalBytes > 0 ? 100d * value.DownloadedBytes / value.TotalBytes : 0;
                window.SetUpdateStatus(UpdateText($"正在下载 {version} · {percent:F0}%", $"Downloading {version} · {percent:F0}%"), working: true, progress: percent);
            });
            preparedUpdate = await updateClient.DownloadAsync(availableUpdate, Path.Combine(store.DirectoryPath, "更新缓存"), progress, operation.Token);
            window.SetUpdateStatus(UpdateText("更新已下载并通过校验。结束录音后可安装并重启。", "Update downloaded and verified. Finish recording to install and restart."), canInstall: true);
        }
        catch (OperationCanceledException) when (!exiting)
        { window.SetUpdateStatus(UpdateText("下载已取消，已下载部分保留供下次续传。", "Download cancelled. Partial files are kept for resuming."), canDownload: true); }
        catch (Exception error) when (!exiting)
        {
            LogEvent("下载更新", error.GetType().Name);
            window.SetUpdateStatus(UpdateText("下载或校验失败，当前版本保持可用，可重试下载。", "Download or verification failed. Your current installation is unchanged. You can retry."), canDownload: true);
        }
        finally { updateOperation = null; updateGate.Release(); }
    }

    private async Task InstallPreparedUpdateAsync()
    {
        if (preparedUpdate is null || !await updateGate.WaitAsync(0)) return;
        try
        {
            if (vm.IsRecording || vm.IsBusy || vm.IsFormatting || stopping || formattingTasks.Count > 0)
            {
                window.SetUpdateStatus(UpdateText("请先结束录音并等待整理完成，再安装更新。", "Finish recording and formatting before installing."), canInstall: true);
                return;
            }
            var request = UpdateLauncher.WriteRequest(preparedUpdate, Path.Combine(store.DirectoryPath, "更新缓存"));
            await UpdateLauncher.StartAsync(request, lifetime.Token);
            await ExitAsync("安装已验证的更新");
        }
        catch (Exception error)
        {
            LogEvent("启动更新", error.GetType().Name);
            window.SetUpdateStatus(UpdateText("更新器未能启动，请重试或使用完整安装包。", "Could not start the updater. Retry or use the full installer."), canInstall: true);
        }
        finally { updateGate.Release(); }
    }

    private void StopUpdates() => updateOperation?.Cancel();
}
