using System.IO;
using iSay.Voice;
using iSay.Voice.Engines;

namespace iSay.Desktop;

public partial class App
{
    private CancellationTokenSource? vocabularyProjectionCancellation;
    private Task vocabularyProjectionTask = Task.CompletedTask;
    private long vocabularyProjectionRevision;
    private VocabularySelectionCandidate[] projectedVocabularyCandidates = [];
    private IReadOnlyList<string>? projectedVocabularyWords;
    private NativeDictionaryVocabularyProjection? completedVocabularyProjection;
    private VocabularySelectionCandidate[] completedProjectionManual = [];
    private VocabularySelectionCandidate[] pendingProjectionManual = [];
    private string projectedKeyboardKey = "", completedProjectionKeyboardKey = "", pendingProjectionKeyboardKey = "";
    private Func<IReadOnlyList<VocabularySelectionCandidate>, CancellationToken, Task<NativeDictionaryVocabularyProjection>>?
        createVocabularyProjection = null;

    private void QueueNativeVocabularyProjection()
    {
        var enabled = !exiting && savedSettings.Engine == "native" && savedSettings.AsrVocabularyEnabled;
        var candidates = enabled ? BuildRecognitionCandidates(savedSettings).ToArray() : [];
        var keyboardKey = KeyboardProjectionKey;
        if (enabled && projectedVocabularyWords is not null && keyboardKey == projectedKeyboardKey && candidates.SequenceEqual(projectedVocabularyCandidates)) return;
        var manual = candidates.Where(item => item.Source == VocabularyCandidateSource.Manual).ToArray();
        if (enabled && completedVocabularyProjection is not null && keyboardKey == completedProjectionKeyboardKey && manual.SequenceEqual(completedProjectionManual))
        {
            // 学习词变化不改变原生普通词筛选；保留已确认的过滤结果，只重合并独立增强。
            projectedVocabularyCandidates = candidates;
            projectedKeyboardKey = keyboardKey;
            projectedVocabularyWords = ProjectedVocabularySelection.Select(completedVocabularyProjection,
                candidates.Where(item => item.Source == VocabularyCandidateSource.Learned).ToArray());
            return;
        }
        if (enabled && vocabularyProjectionCancellation is { IsCancellationRequested: false }
            && keyboardKey == pendingProjectionKeyboardKey && manual.SequenceEqual(pendingProjectionManual)) return;
        vocabularyProjectionCancellation?.Cancel();
        vocabularyProjectionCancellation = null;
        var revision = Interlocked.Increment(ref vocabularyProjectionRevision);
        projectedVocabularyWords = null;
        projectedVocabularyCandidates = [];
        projectedKeyboardKey = "";
        completedVocabularyProjection = null;
        completedProjectionManual = [];
        completedProjectionKeyboardKey = "";
        pendingProjectionManual = manual;
        pendingProjectionKeyboardKey = keyboardKey;
        if (!enabled) return;
        if (candidates.Length == 0 && keyboardVocabularySnapshot?.ProfileDirectory is null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        vocabularyProjectionCancellation = cancellation;
        // 词典加载、哈希检查与筛词全部后台执行；开录只读取已发布快照或当前内存回退词表。
        vocabularyProjectionTask = ProjectNativeVocabularyAsync(candidates, keyboardKey, revision, cancellation);
    }

    private async Task ProjectNativeVocabularyAsync(VocabularySelectionCandidate[] candidates, string keyboardKey, long revision,
        CancellationTokenSource cancellation)
    {
        try
        {
            var projection = await Task.Run(async () =>
            {
                if (createVocabularyProjection is not null)
                    return await createVocabularyProjection(candidates, cancellation.Token).ConfigureAwait(false);
                var projector = new NativeDictionaryVocabularyProjector(
                    data => NativeDictionaryRuntime.Production(AppContext.BaseDirectory, data),
                    Path.Combine(store.DirectoryPath, "词典筛选缓存"));
                if (keyboardVocabulary is not null)
                {
                    using var snapshot = await keyboardVocabulary.CaptureSnapshotAsync(cancellation.Token).ConfigureAwait(false);
                    if ($"{snapshot.Revision}:{snapshot.ProfileFingerprint}" != keyboardKey) throw new OperationCanceledException(cancellation.Token);
                    return await projector.BuildAsync(candidates, snapshot, cancellation.Token).ConfigureAwait(false);
                }
                return await projector.BuildAsync(candidates, cancellation.Token).ConfigureAwait(false);
            }, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() =>
            {
                if (exiting || cancellation.IsCancellationRequested || revision != Interlocked.Read(ref vocabularyProjectionRevision)
                    || KeyboardProjectionKey != keyboardKey) return;
                var current = BuildRecognitionCandidates(savedSettings).ToArray();
                var originalManual = candidates.Where(item => item.Source == VocabularyCandidateSource.Manual).ToArray();
                if (!current.Where(item => item.Source == VocabularyCandidateSource.Manual).SequenceEqual(originalManual)) return;
                var words = ProjectedVocabularySelection.Select(projection,
                    current.Where(item => item.Source == VocabularyCandidateSource.Learned).ToArray());
                completedVocabularyProjection = projection;
                completedProjectionManual = originalManual;
                completedProjectionKeyboardKey = keyboardKey;
                projectedVocabularyCandidates = current;
                projectedKeyboardKey = keyboardKey;
                projectedVocabularyWords = words;
                LogEvent("识别词典筛选", $"原生筛选={projection.HasOriginalSelection};词数={words.Count};分类={projection.FailureCode}");
                if (!vm.IsRecording && engine is not null) _ = PrepareEngineAsync(true);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            LogEvent("识别词典筛选", "未完成，继续使用内存词表；分类=" + error.GetType().Name);
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(vocabularyProjectionCancellation, cancellation)) vocabularyProjectionCancellation = null;
            });
            cancellation.Dispose();
        }
    }

    private async Task DisposeVocabularyProjectionAsync()
    {
        vocabularyProjectionCancellation?.Cancel();
        try { await vocabularyProjectionTask.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
    }
}
