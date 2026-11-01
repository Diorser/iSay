using System.Diagnostics;
using iSay.Desktop.Services;

namespace iSay.Desktop;

public partial class App
{
    private readonly RecognitionIdleTimeout recognitionIdle = new();

    private async Task OnRecordingClockAsync()
    {
        if (!vm.IsRecording || stopping || recordingStartedAt is null) return;
        var elapsed = DateTimeOffset.Now - recordingStartedAt.Value;
        vm.RecordingElapsed = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        // 备用文件转写后端只在结束后返回文字，不能据此把正在说话的长录音误判为空闲。
        if (activeSettings.Engine != "native") return;
        if (recognitionIdle.TryExpire(sessionId, Stopwatch.GetTimestamp(), activeSettings.NoNewTextTimeoutSeconds))
            await StopAsync("无新文字自动结束");
    }

    private static bool IsNormalRecordingEnd(string reason) => reason is "用户结束" or "录音时长上限" or "无新文字自动结束";
}
