using iSay.Desktop.Services;
using System.Windows.Threading;

namespace iSay.Desktop;

public partial class App
{
    private readonly AudioDeviceShutdown captureShutdown = new();
    private TimeSpan audioPumpCleanupTimeout = TimeSpan.FromSeconds(1);
    private TimeSpan audioPumpDrainTimeout = TimeSpan.FromSeconds(18);
    private int abandonedAudioPumps;
    private int activityDrainScheduled, audioOverflowScheduled;

    private Task ReleaseCaptureDeviceAsync(bool waitForStop)
    {
        var device = capture;
        if (device is null) return Task.CompletedTask;
        // 所有权只转移一次；迟到回调不能继续向新段写入旧音频。
        capture = null;
        Interlocked.Exchange(ref acceptingAudio, 0);
        var stopped = waitForStop && testAudioPath is null ? captureStopped?.Task ?? Task.CompletedTask : Task.CompletedTask;
        return captureShutdown.RunAsync(device.StopRecording, stopped, device.Dispose, TimeSpan.FromSeconds(3));
    }

    private async Task RetireAudioPumpAsync()
    {
        var pump = audioPump; audioPump = null;
        var cancellation = recordingCancellation; recordingCancellation = null;
        cancellation?.Cancel();
        if (pump is not null)
        {
            try { await pump.WaitAsync(audioPumpCleanupTimeout); }
            catch (TimeoutException) when (!pump.IsCompleted)
            {
                Interlocked.Increment(ref abandonedAudioPumps);
                Interlocked.Increment(ref engineInvalidation);
                LogEvent("音频传输隔离", "旧音频任务未响应取消，已隔离；界面恢复，任务退出前不复用识别对象。");
                _ = ObserveRetiredAudioPumpAsync(pump, cancellation);
                return;
            }
            catch (Exception) { /* 传输错误已由正常观察链记录，清理不能再次阻塞界面。 */ }
        }
        cancellation?.Dispose();
    }

    private async Task ObserveRetiredAudioPumpAsync(Task pump, CancellationTokenSource? cancellation)
    {
        try { await pump.ConfigureAwait(false); }
        catch (Exception) { }
        finally
        {
            cancellation?.Dispose();
            Interlocked.Decrement(ref abandonedAudioPumps);
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!exiting && !vm.IsRecording) _ = PrepareEngineAsync(true); });
        }
    }

    private void QueueInputActivity()
    {
        // 钩子线程只提交一个界面信号；密集按键和滚轮不能堆积数千个回调。
        if (!input.IsRecording || Interlocked.Exchange(ref activityDrainScheduled, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref activityDrainScheduled, 0);
            if (!vm.IsRecording || stopping || activeSettings.OutputMode != "live") return;
            activityTimer?.Stop(); activityTimer?.Start();
        });
    }
}
