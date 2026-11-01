using System.IO;
using iSay.Desktop.Services;
using iSay.Voice;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("iSay.AutoEdit.Tests")]

namespace iSay.Desktop;

public partial class App
{
    private CancellationTokenSource? modificationObservationCancellation;
    private Func<Guid, ITsfLearningObserver?>? createModificationObserver = null;
    private Guid modificationArmSession;
    private Task modificationArm = Task.CompletedTask;

    private void CancelModificationObservation()
    {
        modificationObservationCancellation?.Cancel();
        modificationObservationCancellation = null;
    }

    private void StartModificationLearning(Guid id, string committedText)
    {
        CancelModificationObservation();
        if (exiting || !savedSettings.AutoVocabularyLearningEnabled || savedSettings.Engine != "native"
            || string.IsNullOrWhiteSpace(committedText) || committedText.Length > TsfLearningClient.MaximumTextLength) return;
        var observer = createModificationObserver is not null
            ? createModificationObserver(id) : delivery?.TryCreateLearningObserver(id);
        if (observer is null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(60));
        modificationObservationCancellation = cancellation;
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        modificationArmSession = id;
        modificationArm = armed.Task;
        // 返回热键处理前只安排观察任务；观察、查询和学习网络均不进入 F3 等待链。
        TrackVocabularyLearning(ObserveModificationAsync(observer, id, committedText, armed, cancellation));
    }

    private async Task ObserveModificationAsync(ITsfLearningObserver observer, Guid id, string committedText,
        TaskCompletionSource armed, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var reply = await observer.ArmAsync(id, committedText, token).ConfigureAwait(false);
            armed.TrySetResult();
            while (reply.HResult == 0 && reply.State is TsfLearningState.Watching or TsfLearningState.Edited)
            {
                // 只在存在一段已经提交的语音时轮询；不扫描其他文档，也不订阅全局键盘。
                await Task.Delay(250, token).ConfigureAwait(false);
                reply = await observer.QueryAsync(id, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (!reply.HasModification || reply.Revision == 0) return;
            var request = new VocabularyModifyPairRequest($"edit:{id:N}:{reply.Revision}",
                reply.SourceText!, reply.TargetText!, observer.ApplicationName);
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                if (exiting || !savedSettings.AutoVocabularyLearningEnabled || savedSettings.Engine != "native") return;
                // 只在原范围修改完成后取一次固定请求；云端提取认可词对后才改变词频。
                TrackVocabularyLearning(ObserveVocabularyLearningAsync(null, request));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // 旧组件或不支持观察的宿主只跳过本次反馈，不影响已交付语音和后续录音。
            LogEvent("手改学习观察", error.GetType().Name);
        }
        finally
        {
            armed.TrySetResult();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
            try { await observer.CancelAsync(id, cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or OperationCanceledException) { }
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(modificationObservationCancellation, cancellation)) modificationObservationCancellation = null;
            });
            cancellation.Dispose();
        }
    }

    private Task CancelDeliveryAfterLearningArmAsync(Guid id)
    {
        var arm = modificationArmSession == id ? modificationArm : Task.CompletedTask;
        if (arm.IsCompleted) return delivery.CancelAsync(id);
        return WaitThenCancelAsync();
        async Task WaitThenCancelAsync()
        {
            await arm.ConfigureAwait(false);
            await delivery.CancelAsync(id).ConfigureAwait(false);
        }
    }
}
