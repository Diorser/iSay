using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using iSay.Desktop.Services;
using iSay.Voice;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("iSay.KeyboardLearning.Tests")]

namespace iSay.Desktop;

public partial class App
{
    private KeyboardVocabularyStore? keyboardVocabulary;
    private KeyboardVocabularySnapshot? keyboardVocabularySnapshot;
    private Task keyboardLearningInitialization = Task.CompletedTask;
    private Task keyboardLearningTask = Task.CompletedTask;
    private CancellationTokenSource? keyboardLearningCancellation;
    private bool allowKeyboardObservation;
    private Func<ITsfKeyboardLearningObserver?>? captureKeyboardObserver = null;
    private Func<string, IKeyboardVocabularyBackend>? createKeyboardVocabularyBackend = null;
    private string KeyboardProjectionKey => keyboardVocabularySnapshot is { } snapshot
        ? $"{snapshot.Revision}:{snapshot.ProfileFingerprint}" : "";

    private void InitializeKeyboardLearning(bool allowObservation)
    {
        allowKeyboardObservation = allowObservation;
        keyboardLearningInitialization = InitializeKeyboardLearningAsync();
    }

    private async Task InitializeKeyboardLearningAsync()
    {
        KeyboardVocabularyStore? initializing = null;
        try
        {
            var source = await Task.Run(() => new KeyboardVocabularyStore(Path.Combine(store.DirectoryPath, "键盘学习词库")), lifetime.Token);
            initializing = source;
            var snapshot = await source.CaptureSnapshotAsync(lifetime.Token);
            if (exiting) { snapshot.Dispose(); return; }
            keyboardVocabulary = source;
            initializing = null;
            PublishKeyboardVocabulary(snapshot);
            ApplyKeyboardLearningSettings();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Text.Json.JsonException)
        {
            vm.KeyboardLearningStatusText = "键盘学习词库暂不可用，原文件已保留；语音输入可继续。";
            LogEvent("键盘学习初始化", error.GetType().Name);
        }
        finally { initializing?.Dispose(); }
    }

    private void ApplyKeyboardLearningSettings()
    {
        keyboardLearningCancellation?.Cancel();
        keyboardLearningCancellation = null;
        var enabled = !exiting && savedSettings.KeyboardLearningEnabled && savedSettings.InputBackend == "tsf";
        keyboardVocabulary?.SetEnabled(enabled);
        if (!savedSettings.KeyboardLearningEnabled)
        {
            vm.KeyboardLearningStatusText = "普通打字学习已关闭；已学词仍可按识别热词开关使用。";
            return;
        }
        if (!enabled)
        {
            vm.KeyboardLearningStatusText = "普通打字学习需要组合输入模式；已学词库保留。";
            return;
        }
        if (keyboardVocabulary is null) return;
        vm.KeyboardLearningStatusText = "已开启本机学习；等待应用提供候选提交和读音。";
        if (!allowKeyboardObservation && captureKeyboardObserver is null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        keyboardLearningCancellation = cancellation;
        var previous = keyboardLearningTask;
        // 开关切换先等上一轮只读观察退出；后台词典和磁盘操作不进入 F3 处理链。
        keyboardLearningTask = Task.Run(async () =>
        {
            try
            {
                try { await previous.ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or InvalidOperationException) { }
                if (!cancellation.IsCancellationRequested) await ObserveKeyboardLearningAsync(cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(keyboardLearningCancellation, cancellation)) keyboardLearningCancellation = null;
                });
                cancellation.Dispose();
            }
        });
    }

    private async Task ObserveKeyboardLearningAsync(CancellationToken token)
    {
        ITsfKeyboardLearningObserver? observer = null;
        var observationId = Guid.Empty;
        var enabled = false;
        var cooldowns = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(450, token).ConfigureAwait(false);
                try
                {
                    var target = captureKeyboardObserver is { } capture ? capture() : TsfKeyboardLearningClient.TryCaptureForeground();
                    if (observer is not null && observer.TargetIdentity != target?.TargetIdentity)
                    {
                        if (enabled)
                        {
                            var final = await observer.QueryAsync(observationId, token).ConfigureAwait(false);
                            if (final.HResult == unchecked((int)0x80040500)) continue;
                            if (final.HResult == 0 && !await ApplyKeyboardCommitsAsync(observer, observationId, final.Commits, token).ConfigureAwait(false))
                            {
                                // 旧上下文已经停收，只保留已完成队列等待持久化，不能提前 Disable 把它清掉。
                                await Task.Delay(2000, token).ConfigureAwait(false);
                                continue;
                            }
                        }
                        await DisableKeyboardObserverAsync(observer, observationId).ConfigureAwait(false);
                        observer = null; enabled = false;
                    }
                    if (target is null) continue;
                    if (observer is null)
                    {
                        if (cooldowns.TryGetValue(target.TargetIdentity, out var until) && until > DateTimeOffset.UtcNow) continue;
                        observer = target;
                        observationId = Guid.NewGuid();
                        var reply = await observer.EnableAsync(observationId, token).ConfigureAwait(false);
                        enabled = reply.HResult == 0 && reply.State == TsfKeyboardLearningState.Watching;
                        if (!enabled)
                        {
                            await DisableKeyboardObserverAsync(observer, observationId).ConfigureAwait(false);
                            cooldowns[observer.TargetIdentity] = DateTimeOffset.UtcNow.AddSeconds(2);
                            observer = null;
                            continue;
                        }
                        await SetKeyboardStatusAsync("本机学习已就绪；只接收带读音的已提交候选，不记录原始按键。", token).ConfigureAwait(false);
                    }
                    var batch = await observer.QueryAsync(observationId, token).ConfigureAwait(false);
                    if (batch.HResult == unchecked((int)0x80040500)) continue;
                    if (batch.HResult != 0) throw new IOException("键盘候选观察暂未完成。");
                    if (!await ApplyKeyboardCommitsAsync(observer, observationId, batch.Commits, token).ConfigureAwait(false))
                    {
                        await Task.Delay(2000, token).ConfigureAwait(false);
                        continue;
                    }
                    if (batch.State != TsfKeyboardLearningState.Watching)
                    {
                        await DisableKeyboardObserverAsync(observer, observationId).ConfigureAwait(false);
                        observer = null; enabled = false;
                    }
                    if (cooldowns.Count > 64)
                        foreach (var key in cooldowns.Where(item => item.Value <= DateTimeOffset.UtcNow).Select(item => item.Key).ToArray()) cooldowns.Remove(key);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                    or InvalidOperationException or OperationCanceledException)
                {
                    if (observer is not null)
                    {
                        if (cooldowns.Count >= 64) cooldowns.Clear();
                        cooldowns[observer.TargetIdentity] = DateTimeOffset.UtcNow.AddSeconds(error is InvalidDataException ? 300 : 15);
                        await DisableKeyboardObserverAsync(observer, observationId).ConfigureAwait(false);
                    }
                    observer = null; enabled = false;
                    await SetKeyboardStatusAsync("当前应用的输入组件尚未支持或暂未响应键盘学习；保留现有词库，等待其他支持的输入框。", token).ConfigureAwait(false);
                    LogEvent("键盘学习观察", error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (observer is not null) await DisableKeyboardObserverAsync(observer, observationId).ConfigureAwait(false);
        }
    }

    private async Task<bool> ApplyKeyboardCommitsAsync(ITsfKeyboardLearningObserver observer, Guid observationId,
        IReadOnlyList<TsfKeyboardCommit> commits, CancellationToken token)
    {
        var source = keyboardVocabulary;
        if (commits.Count == 0 || source is null) return true;
        var captured = commits.Select(item => new KeyboardCandidateCommit(KeyboardCommitIdentity(observationId, item.CommitId),
            item.Reading, item.Text, item.SourceClsid)).ToArray();
        var factory = createKeyboardVocabularyBackend ?? (data => new NativeKeyboardVocabularyBackend(NativeDictionaryRuntime.Production(AppContext.BaseDirectory, data)));
        var result = await source.ApplyBatchAsync(captured, factory, token).ConfigureAwait(false);
        if (result.Status is not (KeyboardVocabularyStatus.Applied or KeyboardVocabularyStatus.AlreadyProcessed))
        {
            await SetKeyboardStatusAsync(result.Status == KeyboardVocabularyStatus.CapacityExceeded
                ? "键盘学习达到当前容量上限，既有词库已保留。可在词库设置中清理。"
                : "本次键盘学习尚未保存，原词库保留；将在后台重试。", token).ConfigureAwait(false);
            return false;
        }
        var snapshot = await source.CaptureSnapshotAsync(token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            if (exiting || token.IsCancellationRequested) { snapshot.Dispose(); return; }
            PublishKeyboardVocabulary(snapshot);
            vm.KeyboardLearningStatusText = $"本机词库已更新：本批学习 {result.AcceptedCount} 次，跳过 {result.RejectedCount} 个无法确认的候选；下段语音生效。";
        });
        // 磁盘和内存快照已发布才确认队列；确认回复丢失不会撤销已保存的词，后续查询仍用固定编号去重。
        foreach (var commit in commits)
        {
            try
            {
                var confirmation = await observer.AcknowledgeAsync(observationId, commit.CommitId, token).ConfigureAwait(false);
                if (confirmation.HResult == 0) continue;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException) { }
            await SetKeyboardStatusAsync("本机词库已保存；宿主确认暂未完成，重复提交会自动去重。", token).ConfigureAwait(false);
            break;
        }
        return true;
    }

    private static Guid KeyboardCommitIdentity(Guid observationId, ulong commitId)
    {
        Span<byte> value = stackalloc byte[24];
        observationId.TryWriteBytes(value);
        BinaryPrimitives.WriteUInt64LittleEndian(value[16..], commitId);
        return new Guid(SHA256.HashData(value).AsSpan(0, 16));
    }

    private void PublishKeyboardVocabulary(KeyboardVocabularySnapshot snapshot)
    {
        var previous = keyboardVocabularySnapshot;
        var changed = previous?.Revision != snapshot.Revision || previous?.ProfileFingerprint != snapshot.ProfileFingerprint;
        keyboardVocabularySnapshot = snapshot;
        vm.KeyboardHotwords.ReplaceAll(snapshot.Words);
        vm.RefreshLearningBrowser();
        vm.KeyboardWordCount = $"累计学习 {snapshot.AcceptedCount} 次 · 当前选出 {snapshot.Words.Count} 个语音热词";
        previous?.Dispose();
        if (changed) RefreshVocabularyEngine();
    }

    private async Task SetKeyboardStatusAsync(string message, CancellationToken token)
    {
        await Dispatcher.InvokeAsync(() => { if (!exiting && !token.IsCancellationRequested) vm.KeyboardLearningStatusText = message; });
    }

    private static async Task DisableKeyboardObserverAsync(ITsfKeyboardLearningObserver observer, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        try { await observer.DisableAsync(id, timeout.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException) { }
    }

    private async Task<bool> HandleKeyboardLearningActionAsync(ActionRequestedEventArgs action)
    {
        if (action.Action != "clear-keyboard-learning") return false;
        if (!vm.CanManageVocabulary || keyboardVocabulary is null) return true;
        vm.VocabularyWorking = true;
        keyboardVocabulary.SetEnabled(false);
        keyboardLearningCancellation?.Cancel();
        try
        {
            await keyboardLearningTask;
            var cleared = await keyboardVocabulary.ClearAsync(lifetime.Token);
            if (cleared.Status != KeyboardVocabularyStatus.Applied)
                throw new IOException("键盘词库清空尚未提交。");
            var snapshot = await keyboardVocabulary.CaptureSnapshotAsync(lifetime.Token);
            PublishKeyboardVocabulary(snapshot);
            SetVocabularyStatus("已清空 iSay 键盘学习词库；手工词表和原有安装资料保留。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            SetVocabularyStatus("键盘词库清空未完成，原文件已保留。");
            LogEvent("键盘学习清空", error.GetType().Name);
        }
        finally { vm.VocabularyWorking = false; ApplyKeyboardLearningSettings(); }
        return true;
    }

    private async Task DisposeKeyboardLearningAsync()
    {
        keyboardVocabulary?.SetEnabled(false);
        keyboardLearningCancellation?.Cancel();
        try { await Task.WhenAll(keyboardLearningInitialization, keyboardLearningTask).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or InvalidDataException or InvalidOperationException) { }
        keyboardVocabularySnapshot?.Dispose();
        keyboardVocabularySnapshot = null;
        keyboardVocabulary?.Dispose();
        keyboardVocabulary = null;
    }
}
