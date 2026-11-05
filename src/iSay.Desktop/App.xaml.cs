using System.Diagnostics;
using System.IO;
using System.Media;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using iSay.Desktop.Services;
using iSay.Voice;
using iSay.Voice.Engines;
using NAudio.Wave;

namespace iSay.Desktop;

public partial class App : Application
{
    private readonly VoiceViewModel vm = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim hotkeyActions = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextFormatting formatter = new();
    private readonly WindowsWordSpeaker wordSpeaker = new();
    private readonly OrderedPersistenceQueue persistence = new();
    private readonly object audioLock = new();
    private readonly BackgroundDiagnosticQueue diagnosticWrites = new();
    private RecordingSystemMute? systemMute;
    internal const int CaptureFrameMilliseconds = 20;
    private SettingsStore store = null!;
    private VoiceDeskWindow window = null!;
    private FloatingBarWindow floating = null!;
    private WindowsInputService input = null!;
    private CompositionDeliveryRouter delivery = null!;
    private TrayService tray = null!;
    private Func<string, TrayService> createTrayService = hotkey => new TrayService(hotkey);
    private IStreamingSpeechEngine? engine;
    private SpeechEngineOptions? preparedEngineOptions;
    private string? preparedEngineKind;
    private long engineInvalidation, preparedEngineInvalidation;
    private Func<string, SpeechEngineOptions, IStreamingSpeechEngine> createSpeechEngine = (kind, options) =>
        kind == "native" ? new NativeSpeechEngine(options) : new OpenAiCompatibleSpeechEngine(options);
    private WaveInEvent? capture;
    private MemoryStream? audio;
    private Channel<byte[]>? pcm;
    private Task? audioPump;
    private TaskCompletionSource? captureStopped;
    private CancellationTokenSource? recordingCancellation;
    private DispatcherTimer? limitTimer, deliveryTimer, clockTimer, activityTimer;
    private Guid sessionId;
    private bool exiting, stopping, startAfterStop;
    private int deliveryPending;
    private int preparing;
    private long preparationRevision;
    private int acceptingAudio;
    private bool deliveryRequested, lastDeliverySucceeded;
    private string lastDeliveredSnapshot = "";
    private string cachedPreviewSource = "", cachedPreviewText = "";
    private PersonalVocabularyStore? vocabulary;
    private ManagedAudioStore managedAudio = null!;
    private SpeechPreview? pendingSpeech;
    private int speechDrainScheduled, levelDrainScheduled;
    private double pendingLevel;
    private SessionPerformance? performance;
    private readonly HashSet<Task> formattingTasks = [];
    private CancellationTokenSource? formattingCancellation;
    private readonly SemaphoreSlim compositionRegistration = new(1, 1);
    private long transcriptRevision;
    private VoiceSettings activeSettings = new();
    private VoiceSettings savedSettings = new();
    private Mutex? instance;
    private EventWaitHandle? showSignal, exitSignal, toggleSignal;
    private RegisteredWaitHandle? showWait, exitWait, toggleWait;
    private bool previewOnly;
    private bool allowSystemHotkeys;
    private string startupHotkeyError = "";
    private string startupVocabularyWarning = "";
    private bool ownsSystemCompositionRegistration;
    private Task enginePreparation = Task.CompletedTask;
    private Task<DeliveryResult>? deliveryCapture;
    private string? diagnosticPath, testAudioPath, testOutputPath;
    private DispatcherTimer? diagnosticTimer;
    private DateTimeOffset? recordingStartedAt;
    private long receivedAudioBytes;
    private int finalDeliveryCount;
    private int formattingDeliveryCount;
    private string lastDeliveryMessage = "";
    private bool finalDelivered;
    private int abandonedCaptureStarts;
    private bool localTranscript;
    private long speechSnapshotCount;
    private long recordingFinalSnapshots;
    private int automaticFormattingRequests;
    private string lastStopReason = "";
    private long lastToggleRequestedAt, latestPreviewReceivedAt;
    private int pipeDiagnosticCount;
    private string lastPipeDiagnosticKey = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 4 && e.Args[0] == "--restore-audio-on-exit" && int.TryParse(e.Args[1], out var audioOwner) && long.TryParse(e.Args[2], out var audioStarted))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(() =>
            {
                try { RecordingSystemMute.WatchOwner(audioOwner, audioStarted, e.Args[3]); }
                catch (Exception) { }
                finally { Dispatcher.BeginInvoke(() => Shutdown()); }
            });
            return;
        }
        var launchEnvironment = IndependentProcessLauncher.CaptureEnvironment();
        string? independentLaunchError = null;
        // 隔离测试与单次控制命令不派生常驻实例；正式运行在获取单实例锁前脱离工具作业组。
        if (launchEnvironment.KillOnJobClose && !e.Args.Contains(IndependentProcessLauncher.IndependentMarker) &&
            !e.Args.Contains("--test-data") && !e.Args.Contains("--test-preview") && !e.Args.Contains("--quit") && !e.Args.Contains("--toggle"))
        {
            try
            {
                IndependentProcessLauncher.StartDetached(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 iSay 程序路径。"),
                    e.Args.Append(IndependentProcessLauncher.IndependentMarker).ToArray(), Environment.CurrentDirectory);
                Shutdown(); return;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or TimeoutException)
            { independentLaunchError = error.Message; }
        }
        var instanceIndex = Array.IndexOf(e.Args, "--instance");
        var instanceName = instanceIndex >= 0 && instanceIndex + 1 < e.Args.Length ? e.Args[instanceIndex + 1] : "main";
        if (!System.Text.RegularExpressions.Regex.IsMatch(instanceName, "^[a-zA-Z0-9_-]{1,40}$")) { Shutdown(); return; }
        var instancePrefix = instanceName == "main" ? "Local\\iSayVoiceDesk" : "Local\\iSayVoiceDesk_" + instanceName;
        instance = new Mutex(true, instancePrefix, out var created);
        if (!created)
        {
            if (!e.Args.Contains("--background") || e.Args.Contains("--quit") || e.Args.Contains("--toggle"))
                try { using var signal = EventWaitHandle.OpenExisting(instancePrefix + (e.Args.Contains("--quit") ? "Exit" : e.Args.Contains("--toggle") ? "Toggle" : "Show")); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        if (e.Args.Contains("--quit")) { Shutdown(); return; }
        var dataIndex = Array.IndexOf(e.Args, "--test-data");
        store = new SettingsStore(dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1] : null);
        InitializeLifetimeJournal(e.Args.Contains("--background"));
        lifetimeJournal?.Record("启动环境", new { launchEnvironment.InJob, launchEnvironment.KillOnJobClose,
            launchEnvironment.BreakawayAllowed, launchEnvironment.LimitFlags, launchEnvironment.QueryError,
            IndependentLaunch = e.Args.Contains(IndependentProcessLauncher.IndependentMarker), Error = independentLaunchError });
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instancePrefix + "Show");
        exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instancePrefix + "Exit");
        toggleSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instancePrefix + "Toggle");
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);
        exitWait = ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => Dispatcher.BeginInvoke(async () => await ExitAsync("外部退出信号（更新或命令）")), null, Timeout.Infinite, false);
        toggleWait = ThreadPool.RegisterWaitForSingleObject(toggleSignal, (_, _) => Dispatcher.BeginInvoke(async () => await ToggleFromAsync("进程信号")), null, Timeout.Infinite, false);
        DispatcherUnhandledException += (_, error) =>
        {
            lifetimeJournal?.Exception(startupCompleted ? "界面异常" : "启动异常", error.Exception, !startupCompleted);
            Log(error.Exception); vm.StatusText = "操作失败，已保留当前文字：" + error.Exception.Message; error.Handled = true;
            if (!startupCompleted) Shutdown(1);
        };
        previewOnly = e.Args.Contains("--test-preview");
        systemMute = new RecordingSystemMute(Path.Combine(store.DirectoryPath, "系统声音恢复.json"));
        if (dataIndex < 0 && !previewOnly) _ = Task.Run(() =>
        {
            try { RecordingSystemMute.RecoverAbandoned(Path.Combine(store.DirectoryPath, "系统声音恢复.json")); }
            catch (Exception error) { Log(error); }
        });
        var vocabularyPath = Path.Combine(store.DirectoryPath, "个人纠错词库.json");
        try
        {
            try { vocabulary = new PersonalVocabularyStore(vocabularyPath); }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException)
            {
                File.Move(vocabularyPath, vocabularyPath + ".损坏备份-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"), false);
                vocabulary = new PersonalVocabularyStore(vocabularyPath);
                startupVocabularyWarning = "个人词库格式异常，已另存原文件并安全重建。";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { vocabulary = null; startupVocabularyWarning = "个人词库暂不可用，已保留原文件；语音输入可继续。"; }
        InitializeRecognitionVocabulary();
        managedAudio = new ManagedAudioStore(store.DirectoryPath);
        if (dataIndex >= 0)
        {
            diagnosticPath = Path.Combine(store.DirectoryPath, "后台状态.json");
            var audioIndex = Array.IndexOf(e.Args, "--test-audio");
            if (audioIndex >= 0 && audioIndex + 1 < e.Args.Length) testAudioPath = e.Args[audioIndex + 1];
            var outputIndex = Array.IndexOf(e.Args, "--test-output");
            if (outputIndex >= 0 && outputIndex + 1 < e.Args.Length) testOutputPath = e.Args[outputIndex + 1];
            diagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            diagnosticTimer.Tick += (_, _) => WriteDiagnostics(); diagnosticTimer.Start();
        }
        ownsSystemCompositionRegistration = diagnosticPath is null && !previewOnly;
        vm.Settings = store.Load();
        UiText.Current.SetLanguage(vm.Settings.UiLanguage);
        var runtimeIndex = Array.IndexOf(e.Args, "--runtime");
        if (runtimeIndex >= 0 && runtimeIndex + 1 < e.Args.Length) vm.Settings.NativeRuntimePath = e.Args[runtimeIndex + 1];
        savedSettings = Snapshot(vm.Settings);
        InitializeVocabularyLearning();
        InitializeKeyboardLearning(diagnosticPath is null && !previewOnly);
        vm.ApplySavedSettings(savedSettings);
        foreach (var entry in store.LoadHistory(vm.Settings)) vm.History.Add(entry);
        RefreshVocabulary();
        window = new VoiceDeskWindow { DataContext = vm };
        MainWindow = window;
        floating = new FloatingBarWindow { DataContext = vm };
        window.Closing += (_, args) => { if (!exiting) { args.Cancel = true; window.Hide(); } };
        allowSystemHotkeys = !e.Args.Contains("--no-hotkeys");
        try { input = new WindowsInputService(allowSystemHotkeys, savedSettings.Hotkey, savedSettings.HotkeyMode); }
        catch (InvalidOperationException error)
        {
            // 冲突时保留原设置，仍可从设置页改键；不能擅自占用另一个快捷键。
            input = new WindowsInputService(false, savedSettings.Hotkey, savedSettings.HotkeyMode);
            startupHotkeyError = "录音快捷键暂未启用，请在设置中改键并保存：" + (error.InnerException?.Message ?? error.Message);
        }
        input.ChromiumRangeSyncEnabled = savedSettings.ChromiumRangeSyncEnabled;
        delivery = new CompositionDeliveryRouter(input);
        delivery.PipeDiagnostic += OnCompositionDiagnostic;
        delivery.CleanupDiagnostic += detail => LogEvent("交付清理", detail);
        delivery.FinalTargetDiagnostic += detail => LogEvent("结束空框接入", detail);
        input.Diagnostic += (_, detail) => LogEvent("输入核验", $"阶段={detail.Stage};匹配={detail.MatchKind};焦点={detail.FocusIdentity};预期长度={detail.ExpectedLength};实际长度={detail.ActualLength};耗时毫秒={detail.ElapsedMilliseconds};最终={detail.IsFinal};旧快照长度={detail.PreviousSnapshotLength};新快照长度={detail.NextSnapshotLength};已发快照长度={detail.SentSnapshotLength};保留前缀长度={detail.KeptPrefixLength};编辑起点={detail.EditStart};删除长度={detail.RemovedLength};实际插入长度={detail.InsertedLength}");
        input.RecordingRequested += (_, action) => Dispatcher.BeginInvoke(async () => await HandleRecordingHotkeyAsync(action.Action));
        input.InputActivity += (_, _) => QueueInputActivity();
        // 普通按键和鼠标操作只影响文字交付，绝不能结束录音。
        tray = createTrayService(savedSettings.Hotkey);
        tray.Diagnostic += detail => diagnosticWrites.TryEnqueue(() => lifetimeJournal?.Record("托盘状态", detail));
        lifetimeJournal?.Record("托盘初始化", new { Registered = tray.IsRegistered });
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(vm.RawText) or nameof(vm.FormattedText)) Interlocked.Increment(ref transcriptRevision);
            if (change.PropertyName is nameof(vm.IsRecording) or nameof(vm.IsBusy) or nameof(vm.AppliedHotkey))
                tray.UpdateState(vm.IsRecording, vm.IsBusy, vm.AppliedHotkey);
            if (change.PropertyName == nameof(vm.IsRecording)) UpdateRecordingTimers();
        };
        tray.OpenRequested += ShowWindow;
        tray.ToggleRequested += async () => await ToggleFromAsync("托盘");
        tray.ExitRequested += async () => await ExitAsync("用户点击托盘退出");
        tray.AboutRequested += () => { ShowWindow(); window.ShowAboutPage(); };
        tray.CheckUpdatesRequested += async () => { ShowWindow(); window.ShowAboutPage(); await CheckForUpdatesAsync(true); };
        window.ActionRequested += async (_, action) => await HandleActionAsync(action);
        InitializeUpdates();
        RefreshMicrophones();
        deliveryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        deliveryTimer.Tick += async (_, _) => { if (vm.IsRecording && !stopping && activeSettings.OutputMode == "live") await DeliverPreviewSafelyAsync(); };
        activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        activityTimer.Tick += async (_, _) => { activityTimer.Stop(); if (vm.IsRecording && !stopping && activeSettings.OutputMode == "live") await DeliverPreviewSafelyAsync(); };
        clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        clockTimer.Tick += async (_, _) => await OnRecordingClockAsync();
        if (!e.Args.Contains("--background")) ShowWindow();
        _ = PrepareEngineAsync();
        if (diagnosticPath is null && !previewOnly && savedSettings.InputBackend == "tsf") _ = SyncCompositionRegistrationAsync(true);
        startupCompleted = true;
        lifetimeJournal?.Record("启动就绪");
    }

    private async Task SyncCompositionRegistrationAsync(bool activate, CancellationToken? cancellationToken = null)
    {
        if (!ownsSystemCompositionRegistration) return;
        var token = cancellationToken ?? lifetime.Token;
        try
        {
            await compositionRegistration.WaitAsync(token);
            try
            {
                // 排队期间设置可能已切换；过期启用意图不能覆盖后来的停用。
                if (activate != (!exiting && savedSettings.InputBackend == "tsf")) return;
                var result = activate ? await TsfRegistrationService.ActivateAsync(token) : await TsfRegistrationService.DeactivateAsync(token);
                LogEvent(activate ? "组合组件启用" : "组合组件停用", $"成功={result.Success};状态={result.Message}");
            }
            finally { compositionRegistration.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log(error); }
    }

    private void ShowWindow() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
    private void UpdateRecordingTimers()
    {
        // 空闲时不保留录音时钟和交付轮询，避免每秒无效唤醒；开始录音时恢复原节奏。
        if (vm.IsRecording) { deliveryTimer?.Start(); clockTimer?.Start(); }
        else { deliveryTimer?.Stop(); clockTimer?.Stop(); activityTimer?.Stop(); }
    }
    private SpeechEngineOptions Options(VoiceSettings settings)
    {
        var words = settings.Engine == "native" && settings.AsrVocabularyEnabled ? BuildRecognitionVocabulary(settings) : [];
        return new()
        {
            NativeRuntimeDirectory = string.IsNullOrWhiteSpace(settings.NativeRuntimePath) ? Path.Combine(AppContext.BaseDirectory, "runtime") : settings.NativeRuntimePath,
            // 随包运行库明确采用已验收的持久协议；用户指定的旧运行目录保留兼容路由。
            NativeExecutableName = string.IsNullOrWhiteSpace(settings.NativeRuntimePath) ? "ImeService.Persistent.exe"
                : words.Count == 0 ? "ImeService.exe" : "ImeService.Vocabulary.exe",
            ApiBaseUrl = settings.AsrBaseUrl, ApiKey = settings.AsrApiKey, Model = settings.AsrModel,
            ProxyMode = settings.ProxyMode, ProxyUrl = settings.ProxyUrl, Vocabulary = words
        };
    }
    private IReadOnlyList<string> BuildRecognitionVocabulary(VoiceSettings settings)
    {
        var candidates = BuildRecognitionCandidates(settings);
        if (projectedVocabularyWords is not null && KeyboardProjectionKey == projectedKeyboardKey
            && candidates.SequenceEqual(projectedVocabularyCandidates)) return projectedVocabularyWords;
        if (completedVocabularyProjection is not null
            && KeyboardProjectionKey == completedProjectionKeyboardKey
            && candidates.Where(item => item.Source == VocabularyCandidateSource.Manual).SequenceEqual(completedProjectionManual))
            return ProjectedVocabularySelection.Select(completedVocabularyProjection,
                candidates.Where(item => item.Source == VocabularyCandidateSource.Learned).ToArray());
        return ProjectedVocabularySelection.SelectWithNativeFallback(candidates, keyboardVocabularySnapshot?.Words ?? []);
    }
    private IReadOnlyList<VocabularySelectionCandidate> BuildRecognitionCandidates(VoiceSettings settings)
    {
        var explicitWords = settings.CustomVocabulary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(row => row.Contains("=>", StringComparison.Ordinal) ? row.Split("=>", 2, StringSplitOptions.TrimEntries)[1] : row);
        // 明确添加的词优先，再使用独立词表和已启用的纠错目标词；原版资料不会在录音时被扫描。
        var candidates = explicitWords.Select(word => new VocabularySelectionCandidate(word, Preferred: true))
            .Concat(recognitionVocabulary?.Snapshot().Where(entry => entry.Preferred)
                .Select(entry => new VocabularySelectionCandidate(entry.Word, LastUpdatedAtUtc: entry.AddedAtUtc, Preferred: true)) ?? [])
            .Concat((settings.LearnVocabularyEnabled ? vocabulary?.GetRecognitionVocabulary() ?? [] : [])
                .Select(word => new VocabularySelectionCandidate(word, Preferred: true)))
            .Concat(recognitionVocabulary?.Snapshot().Where(entry => !entry.Preferred).OrderByDescending(entry => entry.AddedAtUtc)
                .Select(entry => new VocabularySelectionCandidate(entry.Word, LastUpdatedAtUtc: entry.AddedAtUtc)) ?? [])
            .Concat(LearnedRecognitionCandidates());
        return candidates.ToArray();
    }
    private VoiceSettings EffectiveSettings(VoiceSettings source)
    {
        var settings = Snapshot(source);
        if (settings.LearnVocabularyEnabled) settings.PersonalCorrections = vocabulary?.Snapshot() ?? [];
        return settings;
    }
    private void RefreshVocabulary()
    {
        vm.PersonalCorrections.Clear();
        foreach (var entry in vocabulary?.Snapshot() ?? []) vm.PersonalCorrections.Add(entry);
        RefreshRecognitionVocabulary();
    }
    private async Task HandleRecordingHotkeyAsync(RecordingHotkeyAction action)
    {
        // 按下与释放严格按顺序处理，极短的按住也不能漏掉停止意图。
        await hotkeyActions.WaitAsync();
        try
        {
            if (exiting || window.IsCapturingHotkey) return;
            if (stopping) { await lifecycle.WaitAsync(); lifecycle.Release(); }
            if (action == RecordingHotkeyAction.Stop) { if (vm.IsRecording) await StopAsync(); }
            else if (action == RecordingHotkeyAction.Start) { if (!vm.IsRecording) await ToggleFromAsync("按住热键开始"); }
            else await ToggleFromAsync("系统热键");
        }
        catch (Exception error) { Log(error); if (!exiting) vm.StatusText = "录音热键处理失败：" + error.Message; }
        finally { hotkeyActions.Release(); }
    }
    private Task PrepareEngineAsync(bool preserveStatus = false)
    {
        if (exiting || vm.IsRecording || Volatile.Read(ref abandonedAudioPumps) != 0) return enginePreparation;
        Interlocked.Increment(ref preparationRevision);
        if (Interlocked.Exchange(ref preparing, 1) != 0) return enginePreparation;
        vm.EngineStatusText = "正在准备识别引擎，可按快捷键开始录音";
        enginePreparation = PrepareLatestEngineAsync(preserveStatus);
        return enginePreparation;
    }
    private async Task PrepareLatestEngineAsync(bool preserveStatus)
    {
        try
        {
            long revision;
            do
            {
                revision = Interlocked.Read(ref preparationRevision);
                var settings = Snapshot(savedSettings);
                await PrepareCoreAsync(settings, preserveStatus);
                if (engine is not null && !exiting)
                {
                    // 先让录音识别就绪，再排队预热可选服务，避免首次 F3 被学词或整理的冷启动挡住。
                    _ = PrepareFormattingAsync(settings);
                    PrepareVocabularyLearningIfRecognitionReady(true);
                }
                // 预热期间的连续词表修改不能被去重吞掉，返回前必须准备最新配置。
            } while (!exiting && revision != Interlocked.Read(ref preparationRevision));
        }
        finally { Interlocked.Exchange(ref preparing, 0); }
    }
    private static bool SameEngineOptions(SpeechEngineOptions? first, SpeechEngineOptions second) =>
        first is not null && (first with { Vocabulary = Array.Empty<string>() }) == (second with { Vocabulary = Array.Empty<string>() })
        && first.Vocabulary.SequenceEqual(second.Vocabulary, StringComparer.Ordinal);
    private async Task PrepareFormattingAsync(VoiceSettings settings)
    {
        try { await Task.Run(() => formatter.PrepareAsync(settings, lifetime.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception) { LogEvent("整理组件预热", "预热未完成，录音不受影响；结束后可重试整理。"); }
    }
    private async Task PrepareCoreAsync(VoiceSettings settings, bool preserveStatus)
    {
        var options = Options(settings);
        var invalidation = Interlocked.Read(ref engineInvalidation);
        try
        {
            // 原生文件校验、进程启动和预热均不占用界面线程。
            await Task.Run(async () =>
            {
                if (engine is IUpdatableVocabularySpeechEngine updatable && preparedEngineOptions is not null
                    && preparedEngineInvalidation == invalidation && preparedEngineKind == settings.Engine
                    && !SameEngineOptions(preparedEngineOptions, options)
                    && (preparedEngineOptions with { Vocabulary = Array.Empty<string>() }) == (options with { Vocabulary = Array.Empty<string>() }))
                {
                    // 文件占用或权限失败时保留旧配置，再由下方重建流程恢复，不能把未接纳词表标成已就绪。
                    var updated = false;
                    try { updated = await updatable.TryUpdateVocabularyAsync(options.Vocabulary, lifetime.Token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { LogEvent("段间词表更新", $"更新未完成，改为重建识别对象；分类={error.GetType().Name}"); }
                    if (updated)
                    {
                        preparedEngineOptions = options;
                        LogEvent("段间词表更新", $"词数={options.Vocabulary.Count};识别对象已复用");
                    }
                }
                if (engine is null || preparedEngineInvalidation != invalidation || preparedEngineKind != settings.Engine || !SameEngineOptions(preparedEngineOptions, options))
                {
                    if (engine is not null) { await engine.DisposeAsync(); engine = null; }
                    var prepared = createSpeechEngine(settings.Engine, options);
                    prepared.ResultReceived += OnSpeechResult;
                    engine = prepared;
                    preparedEngineOptions = options;
                    preparedEngineKind = settings.Engine;
                    preparedEngineInvalidation = invalidation;
                }
                // 相同配置复用同一识别对象；持久原生进程正常停录后不会再次冷启动。
                await engine.PrepareAsync(lifetime.Token);
            });
            if (exiting) return;
            vm.EngineStatusText = settings.Engine == "native" ? "本机适配器已就绪 · 实时转写" : "兼容语音接口 · 录音结束后识别";
            if (!preserveStatus && !vm.IsRecording) vm.StatusText = testAudioPath is null && WaveInEvent.DeviceCount == 0
                ? "未检测到可用麦克风，连接设备后在设置中刷新。"
                : settings.HotkeyMode == "hold" ? $"已就绪，按住 {HotkeyDefinition.DisplayName(settings.Hotkey)} 说话，松开结束。" : $"已就绪，按 {HotkeyDefinition.DisplayName(settings.Hotkey)} 开始 / 结束录音。";
            if (startupHotkeyError.Length > 0) vm.StatusText = startupHotkeyError;
            if (startupVocabularyWarning.Length > 0) vm.StatusText += " · " + startupVocabularyWarning;
        }
        catch (Exception error)
        {
            if (engine is not null) { await engine.DisposeAsync(); engine = null; }
            preparedEngineOptions = null; preparedEngineKind = null;
            if (!exiting) { Log(error); vm.EngineStatusText = "识别引擎尚未就绪"; vm.StatusText = error.Message; }
        }
    }
    private Task ToggleAsync() => ToggleFromAsync("内部请求");
    private async Task ToggleFromAsync(string source)
    {
        if (window.IsCapturingHotkey) return;
        var requestedAt = Stopwatch.GetTimestamp();
        var previousRequest = Interlocked.Exchange(ref lastToggleRequestedAt, requestedAt);
        LogEvent("录音切换请求", $"来源={source};距上次毫秒={(previousRequest == 0 ? -1 : Stopwatch.GetElapsedTime(previousRequest, requestedAt).TotalMilliseconds):F1};录音={vm.IsRecording};忙碌={vm.IsBusy};停止中={stopping}");
        if (stopping && !exiting)
        {
            // 最终快照收尾的短暂窗口也接收下一段意图，避免快捷键被静默吞掉。
            startAfterStop = true;
            vm.StatusText = "已收到下一段录音请求，当前文字保存后立即开始。";
            return;
        }
        if (vm.IsRecording) { await StopAsync(); return; }
        if (exiting) return;
        if (vm.IsBusy) { vm.StatusText = "正在完成上一段，请稍后再开始。"; return; }
        await lifecycle.WaitAsync();
        try
        {
            if (exiting) return;
            if (Volatile.Read(ref abandonedCaptureStarts) != 0) throw new InvalidOperationException("麦克风驱动尚未返回，请重新连接设备或重启 iSay 后重试。");
            if (captureShutdown.IsPending) throw new InvalidOperationException("麦克风驱动仍在停止或释放，界面可继续使用；设备恢复后再开始录音。");
            if (Volatile.Read(ref abandonedAudioPumps) != 0) throw new InvalidOperationException("上一段音频传输尚未退出，文字已保留；连接恢复前暂不开始新录音。");
            savedSettings.Validate();
            wordSpeaker.Stop();
            vm.IsBusy = true;
            activeSettings = EffectiveSettings(savedSettings);
            localTranscript = window.IsActive && activeSettings.OutputMode != "clipboard";
            CancelModificationObservation();
            // 新段先停止旧段的网络整理和组合；旧结果仍保存在草稿历史，不能迟到改写新段。
            formattingCancellation?.Cancel();
            // 取消意图同步生效，清理在后台完成；新段组合仍由调度器串行等待，音频反馈不等待旧宿主。
            _ = ObserveCompositionCleanupAsync(delivery.CancelAsync(sessionId));
            sessionId = Guid.NewGuid();
            activeLearningAppName = activeSettings.AutoVocabularyLearningEnabled
                ? localTranscript ? "iSay.exe" : LearningApplicationName(TsfEndpoint.CaptureForeground()) : "";
            performance = new SessionPerformance(sessionId, requestedAt)
            { OutputKind = previewOnly ? "preview" : localTranscript ? "local" : activeSettings.OutputMode == "clipboard" ? "clipboard" : "target" };
            Interlocked.Increment(ref transcriptRevision);
            vm.ResetTranscript();
            receivedAudioBytes = 0; finalDeliveryCount = 0; formattingDeliveryCount = 0;
            Interlocked.Exchange(ref pipeDiagnosticCount, 0); Interlocked.Exchange(ref latestPreviewReceivedAt, 0);
            Interlocked.Exchange(ref lastPipeDiagnosticKey, "");
            Interlocked.Exchange(ref speechSnapshotCount, 0); Interlocked.Exchange(ref recordingFinalSnapshots, 0);
            Interlocked.Exchange(ref audioOverflowScheduled, 0);
            automaticFormattingRequests = 0; lastStopReason = "";
            lastDeliveryMessage = ""; finalDelivered = false;
            deliveryRequested = false; lastDeliverySucceeded = false; lastDeliveredSnapshot = "";
            cachedPreviewSource = ""; cachedPreviewText = "";
            Interlocked.Exchange(ref pendingSpeech, null);
            recordingStartedAt = DateTimeOffset.Now;
            recognitionIdle.Start(sessionId, Stopwatch.GetTimestamp());
            vm.RecordingElapsed = "00:00";
            // 先显示状态并接收音频；输入目标查询和引擎冷启动并行进行。
            vm.IsRecording = true; input.IsRecording = true; stopping = false;
            LogEvent("录音开始", $"会话={sessionId:N};输出={activeSettings.OutputMode};本页转写={localTranscript};悬浮条={activeSettings.ShowFloatingBar && diagnosticPath is null && !localTranscript}");
            performance.MarkFeedback();
            if (activeSettings.ShowFloatingBar && diagnosticPath is null && !localTranscript) floating.BeginRecording();
            vm.StatusText = localTranscript ? $"正在本页转写，按 {HotkeyDefinition.DisplayName(activeSettings.Hotkey)} 结束后可复制结果。" : $"正在录音；可以切窗口和按键。按 {HotkeyDefinition.DisplayName(activeSettings.Hotkey)} 结束。";
            input.ResetDelivery();
            deliveryCapture = !previewOnly && !localTranscript && testOutputPath is null && activeSettings.OutputMode == "live" ?
                delivery.BeginAsync(sessionId, activeSettings.InputBackend == "tsf", lifetime.Token) : Task.FromResult(DeliveryResult.Delivered());
            audio = activeSettings.SaveAudio ? new MemoryStream() : null;
            recordingCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            pcm = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            // 即使准备任务已完成，原生 Start 的同步前半段也不能占用界面或拖住麦克风启动。
            var currentReader = pcm.Reader;
            var currentToken = recordingCancellation.Token;
            var pumpSession = sessionId;
            audioPump = Task.Run(() => StartAndPumpAsync(pumpSession, currentReader, currentToken));
            _ = ObservePumpAsync(audioPump, sessionId);
            captureStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = captureStopped;
            var currentSession = sessionId;
            capture = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = CaptureFrameMilliseconds, NumberOfBuffers = 4,
                DeviceNumber = 0 };
            capture.DataAvailable += OnAudio;
            capture.RecordingStopped += (_, args) =>
            {
                Interlocked.Exchange(ref acceptingAudio, 0);
                stopped.TrySetResult();
                if (args.Exception is not null) Dispatcher.BeginInvoke(async () =>
                {
                    if (currentSession != sessionId) return;
                    Log(args.Exception); vm.StatusText = "麦克风发生错误，正在保留已识别内容。"; await StopAsync("麦克风错误");
                });
            };
            Interlocked.Exchange(ref acceptingAudio, 1);
            if (testAudioPath is null)
            {
                if (systemMute is not null && diagnosticPath is null)
                    await Task.Run(() => systemMute.Begin(activeSettings.MuteSystemAudioWhileRecording, true));
                await StartCaptureAsync(capture);
            }
            else _ = FeedTestAudioAsync(capture, testAudioPath, recordingCancellation.Token);
            limitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(activeSettings.MaxRecordingSeconds) };
            limitTimer.Tick += async (_, _) => await StopAsync("录音时长上限");
            limitTimer.Start();
            if (activeSettings.SoundFeedback) SystemSounds.Asterisk.Play();
        }
        catch (Exception error)
        {
            Log(error); vm.StatusText = "无法开始录音：" + error.Message;
            Interlocked.Exchange(ref acceptingAudio, 0);
            limitTimer?.Stop(); input.CancelDelivery(); floating.EndRecording();
            await delivery.CancelAsync(sessionId);
            try { await ReleaseCaptureDeviceAsync(false); } catch (Exception cleanupError) { Log(cleanupError); }
            pcm?.Writer.TryComplete();
            await RetireAudioPumpAsync();
            lock (audioLock) { audio?.Dispose(); audio = null; }
            vm.IsRecording = false; input.IsRecording = false;
            await RestoreSystemAudioAsync();
            if (diagnosticPath is null && activeSettings.ShowFloatingBar && !exiting && !localTranscript) floating.ShowResult(vm.StatusText, false);
            if (!exiting)
            {
                // 恢复也进入同一个准备循环；不能另起一路与旧准备任务并发释放引擎。
                Interlocked.Increment(ref engineInvalidation);
                _ = PrepareEngineAsync(true);
            }
        }
        finally { vm.IsBusy = false; lifecycle.Release(); }
    }
    private async Task ObserveCompositionCleanupAsync(Task cleanup)
    {
        try { await cleanup; }
        catch (Exception error) { Log(error); }
    }
    private void OnCompositionDiagnostic(TsfPipeDiagnostic value)
    {
        var current = performance;
        if (current?.SessionId != value.SessionId) return;
        current.RecordTransport(value);
        var key = $"{value.Command}:{value.Stage}:{value.HResult}:{value.State}";
        var repeated = Interlocked.Exchange(ref lastPipeDiagnosticKey, key) == key;
        // 常规更新只记首条；结束、异常和慢命令有限留档，不按每个快照刷磁盘。
        if (repeated && value.TotalMilliseconds < 150 && value.Command is TsfCommand.Update or TsfCommand.Query) return;
        if (Interlocked.Increment(ref pipeDiagnosticCount) > 40) return;
        LogEvent("组合通道", $"会话={value.SessionId:N};命令={value.Command};序号={value.Sequence};长度={value.TextLength};阶段={value.Stage};队列毫秒={value.QueueMilliseconds:F1};连接毫秒={value.ConnectMilliseconds:F1};发送毫秒={value.WriteMilliseconds:F1};宿主等待毫秒={value.ReplyMilliseconds:F1};总毫秒={value.TotalMilliseconds:F1};HRESULT={(value.HResult is int hr ? $"0x{hr:X8}" : "无回复")};状态={value.State};确认序号={value.AcceptedSequence}");
    }
    private async Task StartCaptureAsync(WaveInEvent device)
    {
        var currentPerformance = performance;
        var microphoneId = activeSettings.MicrophoneId;
        currentPerformance?.MarkCaptureRequested();
        var start = Task.Run(() =>
        {
            // 设备查询与驱动启动共用原有超时和迟到清理，界面不等待同步驱动调用。
            device.DeviceNumber = DeviceNumber(microphoneId);
            currentPerformance?.MarkMicrophoneResolved();
            device.StartRecording();
            currentPerformance?.MarkCaptureStarted();
        });
        try { await start.WaitAsync(TimeSpan.FromSeconds(3), lifetime.Token); }
        catch
        {
            if (!start.IsCompleted)
            {
                // 不在另一线程并发释放尚未返回的驱动调用；迟到返回后只清理本次设备。
                Interlocked.Increment(ref abandonedCaptureStarts);
                if (ReferenceEquals(capture, device)) capture = null;
                _ = start.ContinueWith(task =>
                {
                    _ = task.Exception;
                    try { device.Dispose(); } catch (Exception) { }
                    Interlocked.Decrement(ref abandonedCaptureStarts);
                }, TaskScheduler.Default);
            }
            throw;
        }
    }
    private void OnAudio(object? sender, WaveInEventArgs args)
    {
        if (Volatile.Read(ref acceptingAudio) == 0 || !ReferenceEquals(sender, capture) || pcm is null) return;
        var bytes = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
        performance?.MarkFirstAudio();
        Interlocked.Add(ref receivedAudioBytes, bytes.Length);
        lock (audioLock) { if (ReferenceEquals(sender, capture)) audio?.Write(bytes); }
        var peak = 0;
        for (var i = 0; i + 1 < bytes.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(bytes, i)));
        if (peak >= 1000) performance?.MarkVoicedAudio();
        pendingLevel = Math.Min(1, peak / 18000.0);
        if (Interlocked.Exchange(ref levelDrainScheduled, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { Interlocked.Exchange(ref levelDrainScheduled, 0); if (vm.IsRecording && !stopping) vm.MicrophoneLevel = pendingLevel; });
        if (!pcm.Writer.TryWrite(bytes) && Interlocked.Exchange(ref audioOverflowScheduled, 1) == 0)
        {
            var overflowSession = sessionId;
            Dispatcher.BeginInvoke(async () =>
            {
                if (overflowSession != sessionId || stopping || !vm.IsRecording) return;
                vm.StatusText = "识别处理过慢，正在停止并保留已识别文字。";
                await StopAsync("音频队列已满");
            });
        }
    }
    private static async Task PumpAsync(IStreamingSpeechEngine target, ChannelReader<byte[]> reader, CancellationToken token, SessionPerformance? sessionPerformance = null)
    {
        await foreach (var bytes in reader.ReadAllAsync(token))
        {
            await target.WritePcmAsync(bytes, token);
            sessionPerformance?.MarkFirstAudioSent();
        }
    }
    private async Task StartAndPumpAsync(Guid id, ChannelReader<byte[]> reader, CancellationToken token)
    {
        var currentPerformance = performance?.SessionId == id ? performance : null;
        await enginePreparation.WaitAsync(token).ConfigureAwait(false);
        currentPerformance?.MarkEnginePrepared();
        var prepared = engine ?? throw new InvalidOperationException("识别引擎未能就绪，无法开始转写。请检查运行库设置。");
        currentPerformance?.MarkEngineStartRequested();
        await prepared.StartAsync(id, token).ConfigureAwait(false);
        currentPerformance?.MarkEngineStarted();
        await PumpAsync(prepared, reader, token, currentPerformance).ConfigureAwait(false);
    }
    private async Task FeedTestAudioAsync(object sender, string path, CancellationToken token)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1 || reader.WaveFormat.BitsPerSample != 16) throw new InvalidOperationException("测试音频需16kHz单声道PCM16。");
            var bytes = new byte[CaptureFrameMilliseconds * 32];
            while (!token.IsCancellationRequested && Volatile.Read(ref acceptingAudio) != 0)
            {
                var length = reader.Read(bytes, 0, bytes.Length);
                if (length == 0) break;
                OnAudio(sender, new WaveInEventArgs(bytes, length));
                await Task.Delay(CaptureFrameMilliseconds, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log(error); }
    }
    private async Task ObservePumpAsync(Task task, Guid expectedSession)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (expectedSession != sessionId || stopping || exiting || !vm.IsRecording) return;
                Log(error); vm.StatusText = "识别连接中断，正在保留已识别文字。"; await StopAsync("识别连接异常");
            }).Task.Unwrap();
        }
    }
    private sealed record SpeechPreview(SpeechResult Result, long ReceivedAt);
    private void OnSpeechResult(object? sender, SpeechResult result)
    {
        recognitionIdle.Observe(result.SessionId, result.Text, Stopwatch.GetTimestamp());
        if (result.Kind == SpeechResultKind.Error)
        {
            // 错误事件可能同时携带最后一份有效转写；网络异常不能把已识别尾句丢掉。
            if (performance?.SessionId == result.SessionId && !string.IsNullOrWhiteSpace(result.Text))
                Interlocked.Exchange(ref pendingSpeech, new(result with { Kind = SpeechResultKind.Partial }, Stopwatch.GetTimestamp()));
            Dispatcher.BeginInvoke(() => { if (result.SessionId == sessionId && vm.IsRecording && !stopping) vm.StatusText = result.Error ?? "识别异常，当前转写已保留。"; });
            return;
        }
        var currentPerformance = performance;
        if (currentPerformance?.SessionId != result.SessionId) return;
        // 从事件入口计时，把同步日志等本地处理也计入识别到界面与交付的等待。
        var receivedAt = Stopwatch.GetTimestamp();
        currentPerformance.MarkRecognition(receivedAt);
        var snapshot = Interlocked.Increment(ref speechSnapshotCount);
        if (snapshot == 1 || result.Kind == SpeechResultKind.Final)
            LogEvent("识别快照", $"会话={result.SessionId:N};序号={snapshot};类型={result.Kind};长度={result.Text.Length};停止中={Volatile.Read(ref stopping)}");
        if (result.Kind == SpeechResultKind.Final && vm.IsRecording && !Volatile.Read(ref stopping)) Interlocked.Increment(ref recordingFinalSnapshots);
        // 录音中的稳定结果仍是同一会话快照，不是停录或整理指令。
        // 停止流程开始后，界面回调会拒绝交付，最终结果只由 StopAsync 写入一次。
        Interlocked.Exchange(ref pendingSpeech, new(result, receivedAt));
        ScheduleSpeechPreview();
    }
    private void ScheduleSpeechPreview()
    {
        if (Interlocked.Exchange(ref speechDrainScheduled, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            Interlocked.Exchange(ref speechDrainScheduled, 0);
            // 收尾期间保留最新快照，由停止流程消费；不能在界面回调中取出后丢弃。
            if (stopping) return;
            var preview = Interlocked.Exchange(ref pendingSpeech, null);
            if (preview is not null && preview.Result.SessionId == sessionId && vm.IsRecording && !stopping)
            {
                vm.ApplyLiveSnapshot(preview.Result.Text, PreferredPreview(preview.Result.Text));
                Interlocked.Exchange(ref latestPreviewReceivedAt, preview.ReceivedAt);
                performance?.MarkPreview(preview.ReceivedAt);
                // UI 预览不等待宿主编辑；交付只追最新快照，慢管道不能排成长串界面回调。
                if (activeSettings.OutputMode == "live") _ = DeliverPreviewSafelyAsync();
            }
            if (!stopping && Volatile.Read(ref pendingSpeech) is not null) ScheduleSpeechPreview();
        });
    }
    private void PreserveLatestRecognizedText()
    {
        var preview = Interlocked.Exchange(ref pendingSpeech, null);
        if (preview is null || preview.Result.SessionId != sessionId || string.IsNullOrWhiteSpace(preview.Result.Text)) return;
        vm.RawText = preview.Result.Text;
        vm.LivePreviewText = PreferredPreview(preview.Result.Text);
    }
    private async Task DeliverPreviewSafelyAsync()
    {
        var expectedSession = sessionId;
        try { await DeliverAsync(vm.RawText, false); }
        catch (Exception error)
        {
            Log(error);
            if (expectedSession == sessionId && vm.IsRecording) vm.StatusText = "输入框暂未完成同步，录音继续，完整转写保留在 iSay。";
        }
    }
    private string PreferredPreview(string source)
    {
        if (source == cachedPreviewSource) return cachedPreviewText;
        cachedPreviewSource = source;
        // 原文单独保留；同一累计快照只处理一次，定时重试不反复遍历长文。
        return cachedPreviewText = TextPreferences.Apply(source, activeSettings);
    }
    private async Task DeliverAsync(string text, bool final)
    {
        if (previewOnly || localTranscript) return;
        if (string.IsNullOrEmpty(text)) return;
        deliveryRequested = true;
        if (Interlocked.Exchange(ref deliveryPending, 1) != 0) return;
        var expectedSession = sessionId;
        try
        {
            while (deliveryRequested && !stopping && !exiting && expectedSession == sessionId)
            {
                deliveryRequested = false;
                var source = vm.RawText;
                var latest = PreferredPreview(source);
                var receivedAt = Interlocked.Read(ref latestPreviewReceivedAt);
                if (string.IsNullOrEmpty(latest) || lastDeliverySucceeded && latest == lastDeliveredSnapshot) continue;
                if (deliveryCapture is not null) await deliveryCapture;
                if (stopping || exiting || expectedSession != sessionId) break;
                // 等待原组合开始期间可能已有更新，首个写入也直接取最新快照。
                source = vm.RawText; latest = PreferredPreview(source); receivedAt = Interlocked.Read(ref latestPreviewReceivedAt);
                if (string.IsNullOrEmpty(latest)) continue;
                var attemptedAt = Stopwatch.GetTimestamp();
                var result = testOutputPath is null ? await delivery.UpdateAsync(expectedSession, latest, lifetime.Token) : WriteTestOutput(latest, false);
                if (expectedSession != sessionId || stopping || exiting) break;
                lastDeliverySucceeded = result.Success;
                if (result.Success) { lastDeliveredSnapshot = latest; performance?.MarkDelivery(receivedAt, attemptedAt); }
                if (result.Paused) vm.StatusText = result.Message;
                else if (lastDeliveryMessage.Length > 0) vm.StatusText = $"正在录音；按 {HotkeyDefinition.DisplayName(activeSettings.Hotkey)} 结束。";
                if (result.Message != lastDeliveryMessage)
                {
                    lastDeliveryMessage = result.Message;
                    LogEvent("文字交付", result.Message);
                }
                // 写入期间有新快照时直接追上最新值，中间快照不排成写入队列。
                if (vm.RawText != source) deliveryRequested = true;
                // 失败由下一次计时器或新快照重查，不能因计时器在等待期间置位而原地重复请求。
                if (!result.Success) break;
            }
        }
        finally { Interlocked.Exchange(ref deliveryPending, 0); }
    }
    private async Task StopAsync(string reason = "用户结束")
    {
        if (stopping || !vm.IsRecording) return;
        stopping = true;
        // 按结束键时固定新空框；等待句尾或整理期间不能追着用户的焦点走。
        var emptyFinalTarget = reason == "用户结束" && !previewOnly && !localTranscript && testOutputPath is null &&
            activeSettings.OutputMode == "live" && activeSettings.InputBackend == "tsf" ?
            delivery.CaptureEmptyFinalTarget(sessionId, lifetime.Token) : null;
        PreserveLatestRecognizedText();
        lastStopReason = reason;
        LogEvent("录音结束请求", $"会话={sessionId:N};原因={reason};快照数={Interlocked.Read(ref speechSnapshotCount)};稳定快照数={Interlocked.Read(ref recordingFinalSnapshots)}");
        performance?.MarkStopRequested();
        if (diagnosticPath is null && !localTranscript) floating.SetProcessing("正在确认句尾，完成后立即结束编辑状态。", "确认句尾");
        vm.StatusText = "录音已结束，正在确认句尾。";
        FormattingWork? formattingWork = null;
        var recognitionStopped = false;
        // 实时模式在开始时固定目标；结束后输入模式则在按结束键当下固定，不能等网络整理后再捕获。
        if (!previewOnly && !localTranscript && testOutputPath is null && activeSettings.OutputMode == "on-stop")
            deliveryCapture = delivery.BeginAsync(sessionId, activeSettings.InputBackend == "tsf", lifetime.Token);
        await lifecycle.WaitAsync();
        try
        {
            limitTimer?.Stop(); vm.IsBusy = true;
            if (testAudioPath is not null) captureStopped?.TrySetResult();
            await ReleaseCaptureDeviceAsync(true);
            performance?.MarkAudioStopped();
            await RestoreSystemAudioAsync();
            pcm?.Writer.TryComplete();
            if (audioPump is not null)
            {
                try { await audioPump.WaitAsync(audioPumpDrainTimeout); }
                catch (TimeoutException) { recordingCancellation?.Cancel(); throw new TimeoutException("识别音频传输超时，已保留当前草稿。"); }
            }
            var stoppingEngine = engine;
            // 异步接口的同步前半段仍可能进入原生驱动，必须从后台调用。
            var final = stoppingEngine is null ? vm.RawText : await Task.Run(() => stoppingEngine.StopAsync(lifetime.Token));
            recognitionStopped = true;
            performance?.MarkRecognitionFinal();
            PreserveLatestRecognizedText();
            if (!string.IsNullOrWhiteSpace(final)) vm.RawText = final;
            var userFinished = IsNormalRecordingEnd(reason);
            var useBackgroundFormatting = userFinished && !exiting && activeSettings.SmartFormattingEnabled && activeSettings.FormattingStyle != "literal" &&
                TextFormatting.CanRequestBackgroundFormatting(activeSettings) && !string.IsNullOrWhiteSpace(vm.RawText);
            if (userFinished) automaticFormattingRequests++;
            LogEvent(userFinished ? "结束整理" : "中断保留", $"会话={sessionId:N};原因={reason};序号={automaticFormattingRequests};后台AI={useBackgroundFormatting};原文长度={vm.RawText.Length}");
            // 最终识别完成后先提交基础转写，立即结束组合编辑；AI 整理单独在后台处理。
            // 到达设置的时长上限正常提交；设备、网络异常和退出仅保留识别结果。
            var result = !userFinished ? new FormattingResult(vm.RawText, reason + " · 原文保留") : useBackgroundFormatting
                ? new FormattingResult(TextFormatting.Basic(vm.RawText, activeSettings), "基础整理 · AI整理进行中")
                : await formatter.FormatAsync(vm.RawText, activeSettings, lifetime.Token);
            vm.FormattedText = result.Text;
            string deliveryStatus;
            if (previewOnly) deliveryStatus = "测试预览已保留";
            else if (localTranscript) { deliveryStatus = string.IsNullOrWhiteSpace(result.Text) ? "没有识别到文字，请检查麦克风后重试。" : "转写已完成，可复制结果或查看原文。"; finalDelivered = !string.IsNullOrWhiteSpace(result.Text); }
            else if (activeSettings.OutputMode == "clipboard") { var copied = await input.CopyAsync(result.Text); deliveryStatus = copied.Message; finalDelivered = copied.Success; }
            else
            {
                // 只有写向外部输入框才等待实时写入；本页转写与剪贴板不受上一段宿主响应影响。
                while (Volatile.Read(ref deliveryPending) != 0) await Task.Delay(30);
                if (deliveryCapture is not null) await deliveryCapture;
                DeliveryResult delivered;
                if (!userFinished)
                {
                    if (delivery is not null) await delivery.CancelAsync(sessionId);
                    delivered = DeliveryResult.Retained("完整转写已保留在 iSay，原输入框保留已经显示的文字。");
                }
                else if (string.IsNullOrWhiteSpace(result.Text)) delivered = DeliveryResult.Retained("没有识别到文字");
                else if (testOutputPath is not null) delivered = WriteTestOutput(result.Text, true);
                else delivered = await delivery.CommitAsync(sessionId, result.Text, lifetime.Token, emptyFinalTarget);
                deliveryStatus = delivered.Message;
                finalDelivered = delivered.Success;
                if (delivered.Success && testOutputPath is null && delivery?.UsesComposition(sessionId) == true)
                    performance?.MarkEditCommitted();
                LogEvent("最终交付", $"会话={sessionId:N};成功={delivered.Success};等待宿主={delivered.AwaitingHost};结果长度={result.Text.Length};组合输入={delivery?.UsesComposition(sessionId) == true};后台AI={useBackgroundFormatting};状态={delivered.Message}");
            }
            // 本页转写和剪贴板完成不能记成目标输入框已接受文字。
            if (finalDelivered && !previewOnly && !localTranscript && activeSettings.OutputMode != "clipboard") performance?.MarkDelivery();
            vm.StatusText = result.Warning is null ? deliveryStatus : deliveryStatus + " · " + result.Warning;
            if (!userFinished) vm.StatusText = reason + "，录音已结束；" + vm.StatusText;
            if (localTranscript) LogEvent("本页转写交付", $"会话={sessionId:N};成功={finalDelivered};结果长度={result.Text.Length}");
            SaveHistory(result.Method);
            // 只有正常结束且交付成功的最终原文才进入学习；不等待学习网络请求，也不把整理后的改写当作语音原文。
            if (userFinished && finalDelivered && !previewOnly) QueueVocabularyLearning(sessionId, vm.RawText, activeSettings);
            if (userFinished && finalDelivered && !previewOnly && !localTranscript && testOutputPath is null
                && activeSettings.InputBackend == "tsf" && activeSettings.OutputMode != "clipboard")
                StartModificationLearning(sessionId, result.Text);
            if (useBackgroundFormatting)
            {
                formattingCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                formattingCancellation.CancelAfter(TimeSpan.FromSeconds(40));
                formattingWork = new(sessionId, vm.RawText, Snapshot(activeSettings), Interlocked.Read(ref transcriptRevision),
                    previewOnly || localTranscript || activeSettings.OutputMode == "clipboard", testOutputPath) { Cancellation = formattingCancellation };
                vm.StatusText += " · AI正在后台整理，可开始下一段。";
            }
            if (activeSettings.SoundFeedback) SystemSounds.Asterisk.Play();
        }
        catch (Exception error)
        {
            // 音频传输或设备失败可能尚未走到原生Stop，下一段必须重建而非复用活动会话。
            if (!recognitionStopped) Interlocked.Increment(ref engineInvalidation);
            Log(error);
            PreserveLatestRecognizedText();
            vm.FormattedText = IsNormalRecordingEnd(reason) ? TextFormatting.Basic(vm.RawText, activeSettings) : vm.RawText;
            vm.StatusText = "处理未完成，转写已保留：" + error.Message;
            SaveHistory("处理中断 · 草稿已保留");
        }
        finally
        {
            Interlocked.Exchange(ref acceptingAudio, 0);
            await RestoreSystemAudioAsync();
            try { await ReleaseCaptureDeviceAsync(false); } catch (Exception cleanupError) { Log(cleanupError); }
            pcm?.Writer.TryComplete();
            await RetireAudioPumpAsync();
            lock (audioLock) { audio?.Dispose(); audio = null; }
            vm.IsRecording = false; input.IsRecording = false; vm.IsBusy = false; vm.MicrophoneLevel = 0; stopping = false;
            if (diagnosticPath is null && activeSettings.ShowFloatingBar && !exiting && !localTranscript)
            {
                if (formattingWork is not null) floating.SetProcessing(finalDelivered ? "文字已提交，正在后台智能整理。" : "转写已保留，正在后台智能整理。", "后台整理");
                else floating.ShowResult(vm.StatusText, finalDelivered, !string.IsNullOrWhiteSpace(vm.RawText));
            }
            else floating.EndRecording();
            if (formattingWork is null)
            {
                if (delivery is not null)
                {
                    // 已提交范围的学习观察先完成克隆；这项后台收尾不延长 F3 的编辑态结束。
                    if (modificationArmSession == sessionId && !modificationArm.IsCompleted)
                        _ = ObserveCompositionCleanupAsync(CancelDeliveryAfterLearningArmAsync(sessionId));
                    else await delivery.CancelAsync(sessionId);
                }
                input.CancelDelivery();
            }
            performance?.MarkReady();
            if (performance is not null) LogEvent("会话时延", System.Text.Json.JsonSerializer.Serialize(performance.Snapshot()));
            lifecycle.Release();
            if (!exiting) _ = PrepareEngineAsync(true);
        }
        if (formattingWork is not null) TrackFormatting(formattingWork);
        if (startAfterStop && !exiting)
        {
            startAfterStop = false;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, async () => await ToggleAsync());
        }
    }
    private sealed record FormattingWork(Guid SessionId, string Raw, VoiceSettings Settings, long Revision, bool RetainOnly, string? TestOutputPath)
    {
        public CancellationTokenSource? Cancellation { get; init; }
        public long? CompletedRevision { get; set; }
    }
    private void TrackFormatting(FormattingWork work)
    {
        if (sessionId == work.SessionId && !vm.IsRecording && Interlocked.Read(ref transcriptRevision) == work.Revision) vm.IsFormatting = true;
        var task = CompleteFormattingAsync(work);
        formattingTasks.Add(task);
        _ = RemoveCompletedFormattingAsync(task, work);
    }
    private async Task RemoveCompletedFormattingAsync(Task task, FormattingWork work)
    {
        try { await task; }
        catch (OperationCanceledException) when (work.Cancellation?.IsCancellationRequested ?? lifetime.IsCancellationRequested)
        {
            if (!exiting && sessionId == work.SessionId && !vm.IsRecording && Interlocked.Read(ref transcriptRevision) == work.Revision)
            {
                vm.StatusText = finalDelivered ? "基础转写已提交，智能整理已取消。" : "智能整理已取消，完整转写已保留在 iSay。";
                if (floating is not null && diagnosticPath is null && activeSettings.ShowFloatingBar && !localTranscript) floating.ShowResult(vm.StatusText, finalDelivered, vm.HasRawText);
            }
        }
        catch (Exception error)
        {
            Log(error);
            if (!exiting && sessionId == work.SessionId && !vm.IsRecording && Interlocked.Read(ref transcriptRevision) == work.Revision)
            {
                vm.StatusText = finalDelivered ? "基础转写已提交，智能整理未完成。" : "智能整理未完成，完整转写已保留在 iSay。";
                if (floating is not null && diagnosticPath is null && activeSettings.ShowFloatingBar && !localTranscript) floating.ShowResult(vm.StatusText, finalDelivered, vm.HasRawText);
            }
        }
        finally
        {
            formattingTasks.Remove(task);
            if (sessionId == work.SessionId && !vm.IsRecording)
            {
                vm.IsFormatting = false;
                // 用户修改本页草稿后保留其文字，但后台任务仍须结束状态和浮条反馈。
                if (!exiting && Interlocked.Read(ref transcriptRevision) != (work.CompletedRevision ?? work.Revision))
                {
                    vm.StatusText = "本次智能整理已结束，当前草稿保持你的修改。";
                    if (floating is not null && diagnosticPath is null && activeSettings.ShowFloatingBar && !localTranscript)
                        floating.ShowResult(vm.StatusText, finalDelivered, vm.HasRawText);
                }
                if (delivery is not null) await delivery.CancelAsync(work.SessionId);
            }
            if (ReferenceEquals(formattingCancellation, work.Cancellation)) formattingCancellation = null;
            work.Cancellation?.Dispose();
        }
    }
    private async Task CompleteFormattingAsync(FormattingWork work)
    {
        LogEvent("智能整理开始", $"会话={work.SessionId:N};原文长度={work.Raw.Length}");
        // 首次运行库哈希核验可能读取数十兆文件，整个调用入口留在后台，不能占住界面线程。
        var result = await Task.Run(() => formatter.FormatAsync(work.Raw, work.Settings, work.Cancellation?.Token ?? lifetime.Token));
        LogEvent("智能整理完成", $"会话={work.SessionId:N};方式={result.Method};结果长度={result.Text.Length};有提示={result.Warning is not null}");
        if (exiting) return;
        // 只更新该段原有历史；清空历史或关闭历史保存后不能重新把旧记录补回来。
        var previous = vm.History.FirstOrDefault(entry => entry.Id == work.SessionId.ToString("N"));
        if (savedSettings.SaveHistory && previous is not null)
        {
            var index = vm.History.IndexOf(previous);
            vm.History[index] = previous with { FormattedText = result.Text, Status = result.Method };
            QueueHistoryPersistence(savedSettings, work.SessionId);
        }
        if (sessionId != work.SessionId || vm.IsRecording || vm.IsBusy || Interlocked.Read(ref transcriptRevision) != work.Revision) return;
        DeliveryResult? delivered = null;
        if (!work.RetainOnly)
        {
            // 学习观察先克隆原提交范围，再允许整理消耗交付层的一次性范围凭据。
            if (modificationArmSession == work.SessionId)
                await modificationArm.WaitAsync(work.Cancellation?.Token ?? lifetime.Token);
            delivered = work.TestOutputPath is null ? await delivery.RefineAsync(work.SessionId, result.Text, work.Cancellation?.Token ?? lifetime.Token) : WriteFormattingTestOutput(work.TestOutputPath, result.Text);
            // 等待宿主期间用户可能已开始新录音；不得把上一段状态和结果盖到新草稿上。
            if (sessionId != work.SessionId || vm.IsRecording || vm.IsBusy || Interlocked.Read(ref transcriptRevision) != work.Revision) return;
            if (delivered?.Success == true && work.TestOutputPath is null && delivery.RefinementRecreatedComposition(work.SessionId))
            {
                // 整理已建立新的自有原生范围，重新以整理结果为学习基线，避免旧会话观察失效。
                StartModificationLearning(work.SessionId, result.Text);
            }
        }
        vm.FormattedText = result.Text;
        // 发布整理结果也会增加界面版本，不能把程序自己的更新误报为用户修改。
        work.CompletedRevision = Interlocked.Read(ref transcriptRevision);
        vm.StatusText = delivered?.Message ?? (work.Settings.OutputMode == "clipboard" ? "整理已完成；可复制整理结果，剪贴板中的基础转写保持不变。" : "整理已完成，可复制结果。");
        if (result.Warning is not null) vm.StatusText += " · " + result.Warning;
        // 整理被安全保护拦下时，已经提交的基础转写仍然有效，不能反报成整段输入失败。
        finalDelivered = finalDelivered || delivered?.Success == true;
        LogEvent("整理范围更新", $"会话={work.SessionId:N};成功={delivered?.Success == true};基础转写已交付={finalDelivered};结果长度={result.Text.Length};状态={vm.StatusText}");
        if (floating is not null && diagnosticPath is null && activeSettings.ShowFloatingBar && !localTranscript)
            floating.ShowResult(vm.StatusText, finalDelivered, !string.IsNullOrWhiteSpace(vm.RawText));
        input.CancelDelivery();
    }
    private DeliveryResult WriteFormattingTestOutput(string path, string text)
    {
        Interlocked.Increment(ref formattingDeliveryCount);
        File.WriteAllText(path, text);
        return DeliveryResult.Delivered("后台整理结果已更新");
    }
    private void SaveHistory(string method)
    {
        try { SaveHistoryCore(method); }
        catch (Exception error) { Log(error); vm.StatusText += " · 本机历史保存失败，当前文字仍可复制。"; }
    }
    private void SaveHistoryCore(string method)
    {
        string? path = null;
        MemoryStream? capturedAudio = null;
        if (activeSettings.SaveAudio && audio is not null)
        {
            path = Path.Combine(store.DirectoryPath, "录音", sessionId.ToString("N") + ".wav");
            // 停录后转移本段缓冲区的所有权，无须在界面线程复制数十兆音频。
            lock (audioLock) { capturedAudio = audio; audio = null; }
        }
        if (activeSettings.SaveHistory && (!string.IsNullOrWhiteSpace(vm.RawText) || path is not null))
        {
            var previous = vm.History.FirstOrDefault(x => x.Id == sessionId.ToString("N"));
            if (previous is not null) vm.History.Remove(previous);
            vm.History.Insert(0, new(sessionId.ToString("N"), DateTimeOffset.Now, vm.RawText, vm.FormattedText, method, path));
            while (vm.History.Count > 500) vm.History.RemoveAt(vm.History.Count - 1);
        }
        var history = vm.History.ToArray();
        var settings = Snapshot(activeSettings);
        var targetStore = store;
        var expectedSession = sessionId;
        if (capturedAudio is null && !activeSettings.SaveHistory) return;
        var pending = persistence.Enqueue(() =>
        {
            Exception? audioError = null;
            try
            {
                if (capturedAudio is not null && path is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
                    writer.Write(capturedAudio.GetBuffer(), 0, checked((int)capturedAudio.Length));
                }
            }
            catch (Exception error) { audioError = error; }
            finally { capturedAudio?.Dispose(); }
            // 录音写盘失败时也尽力保存已经识别的文字历史。
            if (settings.SaveHistory) targetStore.SaveHistory(history, settings);
            if (audioError is not null) throw new IOException("录音文件保存失败，文字历史已另行保存。", audioError);
            if (settings.AudioRetentionEnabled) managedAudio.ArchiveExpired(settings.AudioRetentionDays, DateTimeOffset.UtcNow);
        });
        _ = ObservePersistenceAsync(pending, expectedSession);
    }
    private void QueueHistoryPersistence(VoiceSettings settings, Guid? expectedSession = null)
    {
        var history = vm.History.ToArray();
        var saved = Snapshot(settings);
        var targetStore = store;
        _ = ObservePersistenceAsync(persistence.Enqueue(() => targetStore.SaveHistory(history, saved)), expectedSession ?? sessionId);
    }
    private async Task ObservePersistenceAsync(Task pending, Guid expectedSession)
    {
        try { await pending; }
        catch (Exception error)
        {
            Log(error);
            if (!exiting && expectedSession == sessionId) vm.StatusText += " · 本机保存失败，当前文字仍可复制。";
        }
    }
    private async Task HandleActionAsync(ActionRequestedEventArgs action)
    {
        try
        {
            if (await HandleUpdateActionAsync(action)) return;
            if (await HandleAutoLearningActionAsync(action)) return;
            if (await HandleKeyboardLearningActionAsync(action)) return;
            if (await HandleVocabularyActionAsync(action)) return;
            switch (action.Action)
            {
                case "begin-hotkey-capture":
                    if (!vm.CanEditSettings) { window.EndHotkeyCapture(); break; }
                    var captureVersion = window.HotkeyCaptureVersion;
                    input.BeginHotkeyCapture(new System.Windows.Interop.WindowInteropHelper(window).Handle, (key, down) =>
                        Dispatcher.BeginInvoke(() =>
                        {
                            // 不让上一轮迟到的松键事件影响下一轮录入。
                            if (window.HotkeyCaptureVersion == captureVersion) window.ProcessCapturedHotkey(key, down);
                        }));
                    break;
                case "end-hotkey-capture": input.EndHotkeyCapture(); break;
                case "toggle-recording": await ToggleFromAsync("主面板"); break;
                case "save-settings":
                    if (vm.IsRecording || vm.IsBusy) { vm.StatusText = "请结束录音并等待就绪后保存设置。"; break; }
                    vm.IsBusy = true;
                    try
                    {
                    await enginePreparation;
                    if (exiting || vm.IsRecording) { vm.StatusText = "当前录音尚未结束，请稍后再保存设置。"; break; }
                    vm.Settings.Validate();
                    vm.Settings.AudioRetentionEnabled = true;
                    // 注册新键失败时磁盘和运行状态仍保持旧配置；落盘失败也恢复旧热键。
                    var hadHotkeys = input.HotkeysEnabled;
                    if (allowSystemHotkeys) input.EnableHotkeys(vm.Settings.Hotkey, vm.Settings.HotkeyMode);
                    else input.ConfigureHotkey(vm.Settings.Hotkey, vm.Settings.HotkeyMode);
                    try { store.Save(vm.Settings); }
                    catch
                    {
                        if (hadHotkeys) input.EnableHotkeys(savedSettings.Hotkey, savedSettings.HotkeyMode);
                        else { input.DisableHotkeys(); input.ConfigureHotkey(savedSettings.Hotkey, savedSettings.HotkeyMode); }
                        throw;
                    }
                    startupHotkeyError = "";
                    var previousBackend = savedSettings.InputBackend;
                    savedSettings = Snapshot(vm.Settings);
                    UiText.Current.SetLanguage(savedSettings.UiLanguage);
                    input.ChromiumRangeSyncEnabled = savedSettings.ChromiumRangeSyncEnabled;
                    ApplyVocabularyLearningSettings();
                    ApplyKeyboardLearningSettings();
                    QueueNativeVocabularyProjection();
                    // 设置已落盘就先同步实际状态；可选的系统自启失败不能留下两套配置。
                    vm.ApplySavedSettings(savedSettings); window.MarkSettingsSaved();
                    var startupWarning = "";
                    if (diagnosticPath is null)
                    {
                        try { SettingsStore.SetStartup(savedSettings.StartAtLogin, savedSettings.StartMinimized); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        { Log(error); startupWarning = "登录自启设置未完成，可稍后重试。"; }
                    }
                    if (previousBackend != savedSettings.InputBackend)
                    {
                        formattingCancellation?.Cancel();
                        await delivery.CancelAsync(sessionId);
                    }
                    if (previousBackend != savedSettings.InputBackend && diagnosticPath is null && !previewOnly)
                        _ = SyncCompositionRegistrationAsync(savedSettings.InputBackend == "tsf");
                    if (!savedSettings.SaveHistory) vm.History.Clear();
                    else foreach (var old in vm.History.Where(x => x.CreatedAt < DateTimeOffset.Now.AddDays(-savedSettings.HistoryRetentionDays)).ToList()) vm.History.Remove(old);
                    QueueHistoryPersistence(savedSettings);
                    await persistence.Enqueue(() => managedAudio.ArchiveExpired(savedSettings.AudioRetentionDays, DateTimeOffset.UtcNow));
                    await PrepareEngineAsync(); if (engine is not null) vm.StatusText = "设置已保存。" + startupWarning;
                    }
                    finally { vm.IsBusy = false; }
                    break;
                case "archive-old-audio":
                    if (!vm.CanEditSettings) break;
                    var archivedCount = 0;
                    await persistence.Enqueue(() => archivedCount = managedAudio.ArchiveExpired(savedSettings.AudioRetentionDays, DateTimeOffset.UtcNow));
                    vm.StatusText = $"已将 {archivedCount} 份过期录音移到录音归档，文字历史保留。";
                    break;
                case "archive-audio":
                    if (!vm.CanEditSettings || action.Argument is not HistoryEntry audioEntry) break;
                    var archived = false;
                    await persistence.Enqueue(() => archived = managedAudio.ArchiveRecording(audioEntry.AudioPath));
                    vm.StatusText = archived ? "本条录音已移到可恢复归档，文字历史保留。" : "这条记录没有可归档的录音。";
                    break;
                case "open-audio-archive":
                    Directory.CreateDirectory(managedAudio.ArchiveDirectory);
                    Process.Start(new ProcessStartInfo(managedAudio.ArchiveDirectory) { UseShellExecute = true }); break;
                case "remember-correction":
                    if (!vm.CanEditSettings) break;
                    if (vocabulary is null) { SetVocabularyStatus("个人词库文件不可用，请在数据目录检查文件权限后重启 iSay；原文件已保留。"); break; }
                    if (vocabulary.Snapshot().Any(entry => entry.Mistaken == vm.MistakenWord.Trim())) { SetVocabularyStatus("这个错词已有规则，请在列表中点击编辑，避免覆盖原规则。"); break; }
                    if (!vocabulary.RememberCorrection(vm.MistakenWord, vm.CorrectedWord)) { SetVocabularyStatus("请填写不同的错词和正确词，每项1至64字且不含换行。"); break; }
                    QueueVocabularyCorrection(vm.MistakenWord.Trim(), vm.CorrectedWord.Trim());
                    RefreshVocabulary();
                    vm.CancelCorrectionEdit();
                    QueueNativeVocabularyProjection();
                    SetVocabularyStatus(savedSettings.LearnVocabularyEnabled ? "已记住纠错，下次录音生效。" : "已记住纠错；启用个人纠错并保存设置后生效。");
                    if (savedSettings.LearnVocabularyEnabled) await PrepareEngineAsync(true);
                    break;
                case "forget-correction":
                    if (!vm.CanEditSettings || vocabulary is null || action.Argument is not PersonalCorrectionEntry correction) break;
                    if (vocabulary.RemoveCorrection(correction.Mistaken, correction.Corrected)) { deletedCorrection = correction; vm.CanUndoCorrection = true; }
                    RefreshVocabulary();
                    QueueNativeVocabularyProjection();
                    SetVocabularyStatus("已删除这条个人纠错，下次录音生效。");
                    if (savedSettings.LearnVocabularyEnabled) await PrepareEngineAsync(true);
                    break;
                case "settings-changed": vm.SettingsDirty = true; break;
                case "discard-settings":
                    if (!vm.CanEditSettings) break;
                    vm.Settings = Snapshot(savedSettings); vm.SettingsDirty = false; window.MarkSettingsSaved(); vm.StatusText = "未保存的修改已撤销。"; break;
                case "api-key-changed": if (action.Argument is string key && key.Length > 0) vm.Settings.ApiKey = key; break;
                case "asr-api-key-changed": if (action.Argument is string asrKey && asrKey.Length > 0) vm.Settings.AsrApiKey = asrKey; break;
                case "clear-format-key":
                    vm.Settings.ApiKey = ""; vm.Settings.ApiKeyProtected = ""; vm.SettingsDirty = true;
                    window?.ClearKeyInput(false); vm.StatusText = "整理密钥已清除，保存设置后生效。"; break;
                case "clear-asr-key":
                    vm.Settings.AsrApiKey = ""; vm.Settings.AsrApiKeyProtected = ""; vm.SettingsDirty = true;
                    window?.ClearKeyInput(true); vm.StatusText = "语音接口密钥已清除，保存设置后生效。"; break;
                case "refresh-devices": RefreshMicrophones(); break;
                case "retry-composition":
                    if (vm.IsRecording || vm.IsBusy || vm.IsFormatting) { vm.StatusText = "请结束录音并等待整理完成后重新检测输入框。"; break; }
                    // 独立验收实例不能触发真实前台或系统组件维护。
                    if (!ownsSystemCompositionRegistration) { vm.StatusText = "当前测试实例不会重新检测系统输入框。"; break; }
                    vm.IsBusy = true;
                    try
                    {
                        window.Hide();
                        await Task.Delay(200, lifetime.Token);
                        var retry = await TsfRegistrationService.RetryCurrentContextAsync(lifetime.Token);
                        vm.StatusText = retry.Message;
                        LogEvent("重新检测组合输入框", $"成功={retry.Success};状态={retry.Message}");
                    }
                    finally { vm.IsBusy = false; }
                    break;
                case "copy-raw": case "copy-result": case "copy-transcript":
                    var copyText = action.Action == "copy-raw" ? vm.RawText : action.Action == "copy-result" ? vm.FormattedText : vm.TranscriptText;
                    if (string.IsNullOrWhiteSpace(copyText)) { vm.StatusText = "当前没有可复制的文字。"; break; }
                    vm.StatusText = (await input.CopyAsync(copyText)).Message; break;
                case "copy-history":
                    if (action.Argument is HistoryEntry selected) vm.StatusText = (await input.CopyAsync(string.IsNullOrEmpty(selected.FormattedText) ? selected.RawText : selected.FormattedText)).Message;
                    break;
                case "speak-vocabulary-word":
                    if (!vm.CanManageVocabulary || action.Argument is not string spokenWord) break;
                    SetVocabularyStatus("正在朗读…");
                    var spoken = await wordSpeaker.SpeakAsync(spokenWord);
                    SetVocabularyStatus(spoken.Message);
                    break;
                case "paste-result":
                    if (!vm.CanCopyTranscript) break;
                    if (vm.IsFormatting) { vm.StatusText = "正在后台整理，请等待完成后再补填。"; break; }
                    var pasteText = vm.TranscriptText;
                    window.Hide(); await Task.Delay(150);
                    var pasted = await input.ExplicitPasteAsync(pasteText);
                    vm.StatusText = pasted.Message;
                    if (diagnosticPath is null) floating.ShowResult(pasted.Message, pasted.Success);
                    break;
                case "clear-transcript":
                    if (vm.CanEditSettings) { formattingCancellation?.Cancel(); await delivery.CancelAsync(sessionId); input?.ResetDelivery(); vm.ResetTranscript(); vm.StatusText = "已清空当前草稿，历史记录保留。"; }
                    break;
                case "restore-defaults": if (!vm.IsRecording && !vm.IsBusy) { vm.Settings = new(); vm.Settings.Hotkey = savedSettings.Hotkey; vm.SettingsDirty = true; window.MarkSettingsSaved(); vm.SettingsDirty = true; vm.StatusText = "已恢复默认值，点击保存生效。录音快捷键保持不变。"; } break;
                case "open-data-folder": Process.Start(new ProcessStartInfo(store.DirectoryPath) { UseShellExecute = true }); break;
                case "test-engine":
                    if (vm.SettingsDirty) { vm.StatusText = "请先保存设置，再检查识别配置。"; break; }
                    await PrepareEngineAsync(); break;
                case "test-format": case "retry-formatting":
                    if (vm.IsFormatting) { vm.StatusText = "智能整理正在进行，完成后可再次整理。"; break; }
                    if (vm.IsRecording || vm.IsBusy) { vm.StatusText = "请结束录音并等待就绪后整理。"; break; }
                    if (action.Action == "retry-formatting" && !vm.HasRawText) break;
                    vm.IsBusy = true;
                    try
                    {
                        var sourceText = action.Action == "test-format" ? "今天下午三点开会，讨论新项目进度。" : vm.RawText;
                        var formattingSettings = EffectiveSettings(vm.Settings);
                        var result = await Task.Run(() => formatter.FormatAsync(sourceText, formattingSettings, lifetime.Token));
                        if (!exiting)
                        {
                            // 设置页的接口测试只展示样例结果，不能覆盖正在编辑的录音草稿。
                            if (action.Action == "test-format") vm.StatusText = result.Warning ?? $"{result.Method}测试成功：{result.Text}";
                            else { vm.FormattedText = result.Text; vm.StatusText = result.Warning ?? result.Method + "完成。"; }
                        }
                    }
                    finally { vm.IsBusy = false; }
                    break;
                case "clear-history":
                    if (MessageBox.Show(window, "清空本机转写历史？此操作不会删除单独保存的录音文件。", "清空历史", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { vm.History.Clear(); QueueHistoryPersistence(savedSettings); } break;
                case "history-load": case "history-selected": if (!vm.IsRecording && !vm.IsBusy && action.Argument is HistoryEntry item) { formattingCancellation?.Cancel(); await delivery.CancelAsync(sessionId); input?.ResetDelivery(); vm.RawText = item.RawText; vm.FormattedText = string.IsNullOrEmpty(item.FormattedText) ? item.RawText : item.FormattedText; window.ShowInputPage(); vm.StatusText = "已载入历史记录，可以编辑或复制。"; } break;
            }
        }
        catch (Exception error)
        {
            if (action.Action == "begin-hotkey-capture") window.EndHotkeyCapture("快捷键录入未启动，请重试。");
            Log(error); vm.StatusText = error.Message;
            if (action.Action is "save-settings" or "remember-correction" or "forget-correction") SetVocabularyStatus(error.Message);
        }
    }
    private void RefreshMicrophones()
    {
        // 清空绑定列表可能触发双向回写，先保存原选择；刷新不能悄悄替换录音设备。
        var selected = vm.SelectedMicrophoneId;
        IReadOnlyList<MicrophoneInfo> choices;
        try { choices = new MicrophoneCatalog().GetChoices(); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            // 驱动暂时不可用时也要完成客户端启动；保留原列表和选择，录音时再按真实身份核验。
            Log(error);
            vm.StatusText = "无法读取麦克风列表，已保留原选择；请连接设备后在设置中刷新。";
            return;
        }
        vm.Microphones.Clear();
        foreach (var microphone in choices) vm.Microphones.Add(microphone);
        if (!string.IsNullOrWhiteSpace(selected) && !choices.Any(item => item.Id == selected))
            vm.Microphones.Add(new(selected, "原麦克风不可用或需重新确认，请重新选择"));
        vm.SelectedMicrophoneId = selected;
        var count = choices.Count - 1;
        if (testAudioPath is null) vm.StatusText = count == 0 ? "未检测到可用麦克风，连接设备后在设置中刷新。" : $"已刷新，检测到 {count} 个麦克风。";
    }
    private static void ValidateMicrophoneCount(int count)
    {
        if (count <= 0) throw new InvalidOperationException("未检测到可用麦克风，连接设备后在设置中刷新。");
    }
    private static int DeviceNumber(string id)
    {
        var resolved = new MicrophoneCatalog().Resolve(id);
        if (!resolved.Success) throw new InvalidOperationException(resolved.Error);
        return resolved.DeviceNumber;
    }
    private static VoiceSettings Snapshot(VoiceSettings source)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<VoiceSettings>(System.Text.Json.JsonSerializer.Serialize(source))!;
        copy.ApiKey = source.ApiKey; copy.AsrApiKey = source.AsrApiKey;
        copy.PersonalCorrections = source.PersonalCorrections.ToArray(); return copy;
    }
    private void Log(Exception error)
    {
        LogEvent(error.GetType().Name, error.Message);
        if (diagnosticPath is not null && error.InnerException is not null)
            LogEvent("后台异常明细", error.ToString());
    }
    private async Task RestoreSystemAudioAsync()
    {
        if (systemMute is null || !systemMute.NeedsRestore) return;
        try { await Task.Run(systemMute.Restore); }
        catch (Exception error) { LogEvent("系统声音恢复", error.GetType().Name); }
    }
    private void LogEvent(string category, string detail)
    {
        try
        {
            var path = Path.Combine(store.DirectoryPath, "运行.log");
            var line = $"{DateTimeOffset.Now:O} {category}: {detail}\n";
            diagnosticWrites.TryEnqueue(() => File.AppendAllText(path, line));
        }
        catch (Exception) { }
    }
    private DeliveryResult WriteTestOutput(string text, bool final)
    {
        if (final) Interlocked.Increment(ref finalDeliveryCount);
        File.WriteAllText(testOutputPath!, text);
        return DeliveryResult.Delivered("后台测试输出已更新");
    }
    private void WriteDiagnostics()
    {
        if (diagnosticPath is null) return;
        try
        {
            long bufferedAudioBytes;
            lock (audioLock) bufferedAudioBytes = audio?.Length ?? 0;
            var data = System.Text.Json.JsonSerializer.Serialize(new { Heartbeat = DateTimeOffset.UtcNow, Recording = vm.IsRecording, Busy = vm.IsBusy, Preparing = preparing != 0, QueuedStart = startAfterStop, Formatting = formattingTasks.Count, IsFormatting = vm.IsFormatting, PendingPersistence = persistence.Pending, SessionId = sessionId, RecordingStartedAt = recordingStartedAt, AudioBytes = Interlocked.Read(ref receivedAudioBytes), BufferedAudioBytes = bufferedAudioBytes, RawLength = vm.RawText.Length, SpeechSnapshots = Interlocked.Read(ref speechSnapshotCount), RecordingFinalSnapshots = Interlocked.Read(ref recordingFinalSnapshots), AutomaticFormattingRequests = automaticFormattingRequests, StopReason = lastStopReason, FinalDeliveries = finalDeliveryCount, FormattingDeliveries = formattingDeliveryCount, Performance = performance?.Snapshot(), Status = vm.StatusText, EngineStatus = vm.EngineStatusText });
            var path = diagnosticPath;
            diagnosticWrites.TryEnqueue(() => { File.WriteAllText(path + ".tmp", data); File.Move(path + ".tmp", path, true); });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private async Task ExitAsync(string reason = "应用退出")
    {
        if (exiting) return; exiting = true;
        StopUpdates();
        lifetimeJournal?.BeginExit(reason);
        // 先取消预热和网络请求，再串行等待录音清理，避免与引擎释放交叉执行。
        lifetime.Cancel(); deliveryTimer?.Stop(); activityTimer?.Stop(); limitTimer?.Stop(); clockTimer?.Stop();
        formattingCancellation?.Cancel();
        if (vm.IsRecording) await StopAsync("退出应用");
        await RestoreSystemAudioAsync();
        if (delivery is not null) await delivery.CancelAsync(sessionId);
        if (ownsSystemCompositionRegistration && savedSettings.InputBackend == "tsf")
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await SyncCompositionRegistrationAsync(false, cleanup.Token);
        }
        try { await Task.WhenAll(formattingTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        try { await formatter.DisposeAsync(); } catch (Exception) { }
        await DisposeVocabularyLearningAsync();
        await DisposeKeyboardLearningAsync();
        await DisposeVocabularyProjectionAsync();
        try { await enginePreparation; } catch (Exception) { }
        await lifecycle.WaitAsync();
        try
        {
            if (engine is not null) { await engine.DisposeAsync(); engine = null; }
            try { if (!await persistence.DrainAsync(TimeSpan.FromSeconds(4))) LogEvent("本机保存", "退出等待已达四秒，系统存储仍未返回。"); }
            catch (Exception error) { Log(error); }
            diagnosticTimer?.Stop(); WriteDiagnostics();
            wordSpeaker.Dispose(); input.Dispose(); tray.Dispose(); floating.Close(); window.Close();
            await diagnosticWrites.CompleteAsync(TimeSpan.FromSeconds(1));
            showWait?.Unregister(null); exitWait?.Unregister(null); toggleWait?.Unregister(null); showSignal?.Dispose(); exitSignal?.Dispose(); toggleSignal?.Dispose();
            instance?.Dispose(); exitCleanupCompleted = true; Shutdown();
        }
        finally { lifecycle.Release(); }
    }
}
