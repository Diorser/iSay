using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Diagnostics;
using iSay.Voice;
using iSay.Voice.Engines;
using iSay.Desktop.Services;

namespace iSay.Desktop;

public partial class App
{
    private VocabularyLearningStore? learnedVocabulary;
    private VocabularyLearningCoordinator? vocabularyLearning;
    private IVocabularyLearningService? learningService;
    private Func<IVocabularyLearningService>? createVocabularyLearningService = null;
    private readonly HashSet<Task> learningTasks = [];
    private Task learningInitialization = Task.CompletedTask;
    private VocabularyLearnedEntry[] learnedRecognitionSnapshot = [];
    private long learnedRefreshRevision;
    private string activeLearningAppName = "";

    private static string LearningApplicationName(TsfEndpoint? endpoint)
    {
        if (endpoint is null) return "";
        try
        {
            using var target = Process.GetProcessById(checked((int)endpoint.ProcessId));
            var name = target.ProcessName;
            // 应用上下文只使用稳定进程名，不读取窗口标题、文件路径或输入框其他正文。
            return name.Length is > 0 and <= 128 && !name.Any(char.IsControl) ? name + ".exe" : "";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OverflowException) { return ""; }
    }

    private void InitializeVocabularyLearning()
        => learningInitialization = InitializeVocabularyLearningAsync();

    private async Task InitializeVocabularyLearningAsync()
    {
        try
        {
            var path = Path.Combine(store.DirectoryPath, "自动学习词库.json");
            var loaded = await Task.Run(() => new VocabularyLearningStore(path));
            if (exiting || lifetime.IsCancellationRequested) return;
            learnedVocabulary = loaded;
            learningService = createVocabularyLearningService?.Invoke()
                ?? new NativePersonalizationService(NativePersonalizationOptions.Production(AppContext.BaseDirectory, store.DirectoryPath));
            vocabularyLearning = new VocabularyLearningCoordinator(learnedVocabulary, learningService);
            ApplyVocabularyLearningSettings();
            await RefreshLearnedVocabularyAsync();
            RefreshVocabularyEngine();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            learnedVocabulary = null;
            vm.AutoLearningStatusText = "自动学习词库暂不可用，原文件已保留；语音输入不受影响。";
            LogEvent("自动学习初始化", error.GetType().Name);
        }
    }

    private void ApplyVocabularyLearningSettings()
    {
        var enabled = savedSettings.AutoVocabularyLearningEnabled && savedSettings.Engine == "native";
        vocabularyLearning?.SetEnabled(enabled);
        if (!enabled) CancelModificationObservation();
        vm.AutoLearningStatusText = enabled
            ? savedSettings.AsrVocabularyEnabled ? "已开启：语音结束后异步提取术语，已提交语音范围内的手工修正也会反馈；学到的词从下一段录音使用。"
                : "已开启学词；识别热词尚未开启，已学词暂不随识别请求使用。"
            : "自动学习已关闭，停止新的学习请求；既有词保留在本机。";
        PrepareVocabularyLearningIfRecognitionReady();
    }

    private void PrepareVocabularyLearningIfRecognitionReady(bool finishingPreparation = false)
    {
        if (!exiting && savedSettings.AutoVocabularyLearningEnabled && savedSettings.Engine == "native"
            && (finishingPreparation || Volatile.Read(ref preparing) == 0)
            && engine is not null && preparedEngineOptions is not null
            && learningService is NativePersonalizationService native)
            _ = PrepareVocabularyLearningAsync(native);
    }

    private async Task PrepareVocabularyLearningAsync(NativePersonalizationService native)
    {
        try { await Task.Run(() => native.PrepareAsync(lifetime.Token)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { LogEvent("自动学习预热", error.GetType().Name); }
    }

    private IEnumerable<VocabularySelectionCandidate> LearnedRecognitionCandidates() =>
        Volatile.Read(ref learnedRecognitionSnapshot).Select(entry => new VocabularySelectionCandidate(entry.Word, entry.Frequency,
            entry.UpdatedAtUtc, Source: VocabularyCandidateSource.Learned));

    private async Task RefreshLearnedVocabularyAsync()
    {
        var revision = Interlocked.Increment(ref learnedRefreshRevision);
        var source = learnedVocabulary;
        if (source is null) return;
        // 识别开录只读不可变内存快照；文件读取与跨实例锁不得进入 F3 的响应链。
        var snapshot = await Task.Run(() => source.CaptureSnapshot()).ConfigureAwait(false);
        if (exiting || lifetime.IsCancellationRequested) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (revision != Interlocked.Read(ref learnedRefreshRevision) || exiting) return;
            var wordList = window?.FindName("LearnedWordList") as ListBox;
            var ignoredList = window?.FindName("SuppressedLearningWordList") as ListBox;
            var selectedWord = (wordList?.SelectedItem as LearningToken)?.Word;
            var selectedIgnored = ignoredList?.SelectedItem as string;
            Volatile.Write(ref learnedRecognitionSnapshot, snapshot.Entries.ToArray());
            vm.LearnedWords.ReplaceAll(snapshot.Entries);
            vm.SuppressedLearningWords.ReplaceAll(snapshot.SuppressedWords);
            vm.NotifyLearnedWordsChanged();
            if (wordList is not null && selectedWord is not null)
                wordList.SelectedItem = vm.VisibleLearningTokens.FirstOrDefault(entry => entry.Word == selectedWord);
            if (ignoredList is not null && selectedIgnored is not null)
                ignoredList.SelectedItem = vm.SuppressedLearningWords.FirstOrDefault(word => word == selectedIgnored);
        });
    }

    private void QueueVocabularyLearning(Guid id, string raw, VoiceSettings settings)
    {
        if (exiting || !settings.AutoVocabularyLearningEnabled
            || !savedSettings.AutoVocabularyLearningEnabled || settings.Engine != "native" || string.IsNullOrWhiteSpace(raw)) return;
        // 快照只保存本次参数；迟到结果不得读取下一段的 RawText 或修改下一段输入范围。
        var request = new VocabularyNerRequest(id.ToString("N"), raw, activeLearningAppName);
        var task = ObserveVocabularyLearningAsync(request);
        TrackVocabularyLearning(task);
    }

    private void QueueVocabularyCorrection(string mistaken, string corrected)
    {
        if (exiting || !savedSettings.AutoVocabularyLearningEnabled || savedSettings.Engine != "native"
            || string.IsNullOrWhiteSpace(mistaken) || string.IsNullOrWhiteSpace(corrected) || mistaken == corrected) return;
        // 这里的上游是用户明确点击“记住纠错”，不把任意输入框的按键推测为已确认修改对。
        var correction = new VocabularyModifyPairRequest("correction:" + Guid.NewGuid().ToString("N"), mistaken, corrected, "");
        TrackVocabularyLearning(ObserveVocabularyLearningAsync(null, correction));
    }

    private void TrackVocabularyLearning(Task task)
    {
        learningTasks.Add(task);
        _ = task.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => learningTasks.Remove(task))), TaskScheduler.Default);
    }

    private async Task ObserveVocabularyLearningAsync(VocabularyNerRequest? request, VocabularyModifyPairRequest? correction = null)
    {
        try
        {
            await learningInitialization.WaitAsync(lifetime.Token).ConfigureAwait(false);
            var coordinator = vocabularyLearning;
            if (coordinator is null) return;
            // 入队和文件事务都在后台；F3 的编辑态提交与此任务没有等待关系。
            var result = await Task.Run(() => correction is null
                ? coordinator.LearnFinalAsync(request!, lifetime.Token)
                : coordinator.LearnModificationAsync(correction, lifetime.Token)).ConfigureAwait(false);
            if (exiting || lifetime.IsCancellationRequested) return;
            if (result.Status == VocabularyLearningStatus.Applied)
                await RefreshLearnedVocabularyAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!savedSettings.AutoVocabularyLearningEnabled || savedSettings.Engine != "native") return;
                vm.AutoLearningStatusText = $"最近一次学习：{LearningStatusText(result.Status)}；新增 {result.AddedCount}，更新 {result.UpdatedCount}，忽略 {result.RejectedCount}。";
                LogEvent("语音学习结果", $"状态={result.Status};新增={result.AddedCount};更新={result.UpdatedCount};拒绝={result.RejectedCount};错误分类={result.ErrorCode}");
                if (result.AddedCount + result.UpdatedCount > 0) RefreshVocabularyEngine();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            LogEvent("语音学习失败", error.GetType().Name);
            if (!exiting && !lifetime.IsCancellationRequested)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (savedSettings.AutoVocabularyLearningEnabled && savedSettings.Engine == "native")
                        vm.AutoLearningStatusText = "这次学词未完成，原词库与转写已保留；下次录音可继续使用。";
                });
        }
    }

    private static string LearningStatusText(VocabularyLearningStatus status) => status switch
    {
        VocabularyLearningStatus.Applied => "已更新词库",
        VocabularyLearningStatus.AlreadyApplied => "已处理过，无需重复学习",
        VocabularyLearningStatus.NoTerms => "没有新的专有词",
        VocabularyLearningStatus.Disabled => "已关闭",
        VocabularyLearningStatus.Cancelled => "已取消",
        VocabularyLearningStatus.QueueFull => "请求较多，本次已跳过",
        VocabularyLearningStatus.CapacityExceeded => "本地学习存储已达容量上限，既有词库保留；请导出后清理或迁移资料",
        VocabularyLearningStatus.ServiceFailed => "服务暂未完成，原词库保留",
        VocabularyLearningStatus.StorageFailed => "保存暂未完成，原词库保留",
        _ => "本次结果未采用，原词库保留"
    };

    private async Task<bool> HandleAutoLearningActionAsync(ActionRequestedEventArgs action)
    {
        if (action.Action is not ("forget-learned-word" or "restore-learning-word" or "clear-learned-words")) return false;
        if (!vm.CanManageVocabulary || learnedVocabulary is null) return true;
        vm.VocabularyWorking = true;
        Interlocked.Increment(ref learnedRefreshRevision);
        try
        {
            if (action.Action == "forget-learned-word" && action.Argument is VocabularyLearnedEntry entry)
            {
                await Task.Run(() => learnedVocabulary.Remove(entry.Word));
                vm.AutoLearningStatusText = "该自动学词已删除并加入忽略名单；手工热词和纠错规则保持不变。";
            }
            else if (action.Action == "restore-learning-word" && action.Argument is string word)
            {
                await Task.Run(() => learnedVocabulary.RestoreSuppressed(word));
                vm.AutoLearningStatusText = "已允许重新学习该词；没有凭空增加识别频次。";
            }
            else if (action.Action == "clear-learned-words")
            {
                // 清空先切换学习代次，已经在途的旧响应也不能把整张词表重新填回。
                vocabularyLearning?.SetEnabled(false);
                await Task.Run(() => learnedVocabulary.Clear());
                vm.AutoLearningStatusText = "自动学词已清空并加入忽略名单；手工热词和明确纠错仍保留。";
            }
            await RefreshLearnedVocabularyAsync();
            RefreshVocabularyEngine();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LogEvent("自动词库管理", error.GetType().Name);
            vm.AutoLearningStatusText = "自动词库操作未完成，原词库已保留。";
        }
        finally
        {
            if (action.Action == "clear-learned-words")
                vocabularyLearning?.SetEnabled(savedSettings.AutoVocabularyLearningEnabled && savedSettings.Engine == "native");
            vm.VocabularyWorking = false;
        }
        return true;
    }

    private async Task DisposeVocabularyLearningAsync()
    {
        CancelModificationObservation();
        try { await learningInitialization.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        vocabularyLearning?.SetEnabled(false);
        try
        {
            if (vocabularyLearning is not null) await vocabularyLearning.DisposeAsync();
            if (learningService is IAsyncDisposable service) await service.DisposeAsync();
        }
        catch (Exception error) { LogEvent("自动学习退出", error.GetType().Name); }
        try { await Task.WhenAll(learningTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { }
    }
}
