using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using iSay.Voice;

namespace iSay.Desktop;

public sealed partial class VoiceViewModel : INotifyPropertyChanged
{
    public VoiceViewModel()
    {
        System.ComponentModel.PropertyChangedEventManager.AddHandler(UiText.Current, OnLanguageChanged, nameof(UiText.Language));
    }
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        // 空属性名让现有绑定一起刷新，正文与用户词条本身不参与翻译。
        PropertyChanged?.Invoke(this, new(null));
        RefreshLearningBrowser();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private string statusText = "正在启动，稍后即可使用录音快捷键。";
    public string StatusText { get => UiText.Translate(statusText); set { statusText = value; Changed(); } }
    private string engineStatusText = "正在准备识别引擎";
    public string EngineStatusText { get => UiText.Translate(engineStatusText); set { engineStatusText = value; Changed(); } }
    private bool recording, busy, formatting;
    public bool IsRecording { get => recording; set { if (recording == value) return; recording = value; if (value) IsFormatting = false; Changed(); StateChanged(); } }
    public bool IsBusy { get => busy; set { if (busy == value) return; busy = value; Changed(); StateChanged(); } }
    public bool IsFormatting { get => formatting; set { if (formatting == value) return; formatting = value; Changed(); } }
    private double level;
    public double MicrophoneLevel { get => level; set { if (level == value) return; level = value; Changed(); } }
    private string rawText = "", formattedText = "";
    private bool hasFormattedResult;
    public string RawText { get => rawText; set { rawText = value; IsFormatting = false; Changed(); TextChanged(); } }
    public string FormattedText { get => formattedText; set { formattedText = value; hasFormattedResult = true; IsFormatting = false; Changed(); TextChanged(); } }
    private string? livePreviewText;
    public string LivePreviewText { get => livePreviewText ?? RawText; set { livePreviewText = value; TextChanged(); } }
    public void ApplyLiveSnapshot(string raw, string preview)
    {
        if (rawText == raw && livePreviewText == preview) return;
        var rawChanged = rawText != raw;
        rawText = raw; livePreviewText = preview; IsFormatting = false;
        if (rawChanged) Changed(nameof(RawText));
        // 同一识别快照只通知一次显示正文，避免先显示原文、再立即布局优化后的同一段。
        TextChanged();
    }
    private VoiceSettings settings = new();
    public VoiceSettings Settings { get => settings; set { settings = value; UiText.Current.SetLanguage(value.UiLanguage); Changed(); Changed(nameof(SelectedMicrophoneId)); Changed(nameof(DraftHotkey)); Changed(nameof(DraftHotkeyDisplay)); } }
    public string DraftHotkey { get => Settings.Hotkey; set { Settings.Hotkey = value; Changed(); Changed(nameof(DraftHotkeyDisplay)); } }
    public string SelectedMicrophoneId { get => Settings.MicrophoneId; set { Settings.MicrophoneId = value; Changed(); } }
    public ObservableCollection<MicrophoneInfo> Microphones { get; } = [];
    public ObservableCollection<HistoryEntry> History { get; } = [];
    public ObservableCollection<PersonalCorrectionEntry> PersonalCorrections { get; } = [];
    public ObservableCollection<RecognitionVocabularyEntry> RecognitionWords { get; } = [];
    public BatchObservableCollection<VocabularyLearnedEntry> LearnedWords { get; } = [];
    public BatchObservableCollection<string> SuppressedLearningWords { get; } = [];
    public BatchObservableCollection<string> KeyboardHotwords { get; } = [];
    private string keyboardLearningStatusText = "普通打字学习尚未开启。";
    public string KeyboardLearningStatusText { get => UiText.Translate(keyboardLearningStatusText); set { if (keyboardLearningStatusText == value) return; keyboardLearningStatusText = value; Changed(); } }
    private string keyboardWordCount = "尚无键盘学习记录";
    public string KeyboardWordCount { get => UiText.Translate(keyboardWordCount); set { if (keyboardWordCount == value) return; keyboardWordCount = value; Changed(); } }
    private string autoLearningStatusText = "自动学习尚未开启；已学词仅保存在 iSay 自家目录。";
    public string AutoLearningStatusText { get => UiText.Translate(autoLearningStatusText); set { autoLearningStatusText = value; Changed(); } }
    public string LearnedWordCount => UiText.Translate($"{LearnedWords.Count} 个自动学词 · {SuppressedLearningWords.Count} 个已忽略词");
    public void NotifyLearnedWordsChanged() { Changed(nameof(LearnedWordCount)); RefreshLearningBrowser(); }
    private string vocabularySearch = "", vocabularyDraft = "";
    private string vocabularyStatusText = "";
    private string vocabularyImportPreview = "";
    private bool hasVocabularyImport, vocabularyWorking;
    public string VocabularySearch { get => vocabularySearch; set { vocabularySearch = value; Changed(); NotifyRecognitionWordsChanged(); } }
    public string VocabularyDraft { get => vocabularyDraft; set { vocabularyDraft = value; Changed(); } }
    public string VocabularyStatusText { get => UiText.Translate(vocabularyStatusText); set { vocabularyStatusText = value; Changed(); Changed(nameof(HasVocabularyFeedback)); } }
    public bool HasVocabularyFeedback => !string.IsNullOrWhiteSpace(VocabularyStatusText);
    public string VocabularyImportPreview { get => vocabularyImportPreview; set { vocabularyImportPreview = value; Changed(); } }
    public bool HasVocabularyImport { get => hasVocabularyImport; set { hasVocabularyImport = value; Changed(); Changed(nameof(CanApplyVocabularyImport)); } }
    public bool VocabularyWorking { get => vocabularyWorking; set { vocabularyWorking = value; Changed(); Changed(nameof(CanManageVocabulary)); Changed(nameof(CanApplyVocabularyImport)); } }
    public bool CanManageVocabulary => CanEditSettings && !VocabularyWorking;
    public bool CanApplyVocabularyImport => CanManageVocabulary && HasVocabularyImport;
    public string MistakenWord { get; set; } = "";
    public string CorrectedWord { get; set; } = "";
    public bool CanStartOrStop => !IsBusy;
    public bool CanCopyTranscript => !IsRecording && !IsBusy && HasTranscript;
    public bool CanCopyRaw => !IsRecording && !IsBusy && HasRawText;
    public bool CanReformat => !IsRecording && !IsBusy && HasRawText;
    public bool CanEditSettings => !IsRecording && !IsBusy;
    public bool HasTranscript => !string.IsNullOrWhiteSpace(TranscriptText);
    public bool HasRawText => !string.IsNullOrWhiteSpace(RawText);
    public string TranscriptHeading => UiText.Translate(IsRecording ? "实时转写" : "整理结果");
    // 录音期间与整理完成共用一个结果面，仍保留独立原文供随时核对。
    public string TranscriptText { get => IsRecording ? LivePreviewText : !hasFormattedResult ? RawText : FormattedText; set => FormattedText = value; }
    public void ResetTranscript()
    {
        rawText = ""; formattedText = ""; livePreviewText = null; hasFormattedResult = false; IsFormatting = false;
        Changed(nameof(RawText)); Changed(nameof(FormattedText)); TextChanged();
    }
    private string appliedHotkey = "F3";
    public string AppliedHotkey { get => appliedHotkey; set { appliedHotkey = value; Changed(); Changed(nameof(AppliedHotkeyDisplay)); } }
    private string appliedGestureLabel = "按一下开始 / 结束";
    public string AppliedGestureLabel { get => UiText.Translate(appliedGestureLabel); private set { appliedGestureLabel = value; Changed(); } }
    public string ApplicationVersion => typeof(VoiceViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    public string CompositionVersion => typeof(VoiceViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "CompositionVersion")?.Value ?? "0.9.7";
    private string appliedOutputHint = "在其他应用按 F3，边说边输入。";
    public string AppliedOutputHint { get => UiText.Translate(appliedOutputHint); private set { appliedOutputHint = value; Changed(); } }
    private string recordingElapsed = "00:00";
    public string RecordingElapsed { get => recordingElapsed; set { if (recordingElapsed == value) return; recordingElapsed = value; Changed(); } }
    private bool settingsDirty;
    public bool SettingsDirty { get => settingsDirty; set { settingsDirty = value; Changed(); } }
    private string formattingSummary = "基础整理";
    public string FormattingSummary { get => UiText.Translate(formattingSummary); set { formattingSummary = value; Changed(); } }
    public void ApplySavedSettings(VoiceSettings saved)
    {
        AppliedHotkey = saved.Hotkey;
        AppliedGestureLabel = saved.HotkeyMode == "hold" ? "按住说话，松开结束" : "按一下开始 / 结束";
        var gesture = saved.HotkeyMode == "hold" ? $"按住 {HotkeyDefinition.DisplayName(saved.Hotkey)} 说话，松开结束" : $"按 {HotkeyDefinition.DisplayName(saved.Hotkey)} 开始，再按结束";
        AppliedOutputHint = saved.OutputMode switch
        {
            "on-stop" => $"{gesture}，结束时输入到当前光标。",
            "clipboard" => $"{gesture}，结束后复制到剪贴板。",
            _ => $"{gesture}，边说边输入。"
        };
        FormattingSummary = !saved.SmartFormattingEnabled || saved.FormattingStyle == "literal" ? "基础转写" :
            TextFormatting.UsesCustomApi(saved) ? "自定义 AI 整理" : "内置智能整理";
        SettingsDirty = false;
    }
    private void StateChanged()
    {
        Changed(nameof(CanStartOrStop)); Changed(nameof(CanEditSettings)); Changed(nameof(CanManageVocabulary));
        Changed(nameof(CanApplyVocabularyImport)); Changed(nameof(TranscriptHeading)); TextChanged();
    }
    private void TextChanged()
    {
        Changed(nameof(TranscriptText)); Changed(nameof(HasTranscript)); Changed(nameof(HasRawText));
        Changed(nameof(CanCopyTranscript)); Changed(nameof(CanCopyRaw)); Changed(nameof(CanReformat));
    }
}
