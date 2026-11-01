using System.Diagnostics;
using System.IO;

namespace iSay.Desktop.Services;

/// <summary>只调用已安装组件的固定维护入口，不在录音过程中修改输入法注册。</summary>
internal static class TsfRegistrationService
{
    private static readonly SemaphoreSlim serial = new(1, 1);
    public static Task<(bool Success, string Message)> ActivateAsync(CancellationToken cancellationToken = default)
        => RunAsync("--activate", cancellationToken);
    public static Task<(bool Success, string Message)> DeactivateAsync(CancellationToken cancellationToken = default)
        => RunAsync("--deactivate", cancellationToken);

    public static async Task<(bool Success, string Message)> RetryCurrentContextAsync(CancellationToken cancellationToken = default)
    {
        var target = TsfEndpoint.CaptureForeground();
        if (target is null) return (false, "未找到其他应用的输入位置。");
        try
        {
            var reply = await new TsfCompositionClient(target).SendAsync(TsfCommand.ResetUnsupported, Guid.NewGuid(), 1, "", cancellationToken).ConfigureAwait(false);
            return reply.Applied ? (true, "已允许重新检测该输入框，可按 F3 开始录音。") : (false, "当前输入框无法重新检测，转写仍可保留。");
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        { return (false, "当前输入框的组合输入组件不可用，未进行修改。"); }
    }

    private static async Task<(bool Success, string Message)> RunAsync(string operation, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "composition", "iSay.TextService.Setup.exe");
        if (!File.Exists(executable)) return (false, "组合输入组件尚未安装，转写仍可在 iSay 中保留。");
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(operation);
            using var process = Process.Start(start);
            if (process is null) return (false, "组合输入服务未能启动，转写已保留。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            // 安装器只输出固定步骤和 HRESULT，不输出语音正文或输入框内容。
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(output, errors).ConfigureAwait(false);
                return process.ExitCode == 0 ? (true, operation == "--activate" ? "组合输入服务已启用" : "组合输入服务已停用") : (false, "组合输入服务未能完成启停，完整转写将保留在 iSay。");
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                // 仅结束本次启动的维护进程，不结束目标应用或系统输入法。
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await Task.WhenAll(output, errors).ConfigureAwait(false); } catch (OperationCanceledException) { }
                return (false, "组合输入服务响应超时，转写仍可保留。");
            }
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return (false, "组合输入服务当前不可用，转写仍可保留。"); }
        finally { serial.Release(); }
    }
}
