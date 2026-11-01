using iSay.Voice;

namespace iSay.Desktop;

public sealed record VocabularyEditRequest(RecognitionVocabularyEntry Entry, string Word);
public sealed record CorrectionEditRequest(PersonalCorrectionEntry Entry, string Mistaken, string Corrected);
public sealed record LearningToken(string Word, string Origin, string Detail, VocabularyLearnedEntry? Entry = null,
    RecognitionVocabularyEntry? ManualEntry = null)
{
    public string FrequencyLabel => Entry is null ? "" : $"{Entry.Frequency}";
    public bool HasFrequency => Entry is not null;
    public bool IsSaved => ManualEntry is not null;
    public string KeepLabel => UiText.Translate(IsSaved ? "编辑我的热词" : "编辑并加入我的热词");
}

public sealed partial class VoiceViewModel
{
    public string DraftHotkeyDisplay => HotkeyDefinition.DisplayName(DraftHotkey);
    public string AppliedHotkeyDisplay => HotkeyDefinition.DisplayName(AppliedHotkey);
    private const int DisplayWordLimit = 1000;
    private bool preferredOnly, vocabularyEditorOpen;
    private string vocabularyQuickText = "";
    public string VocabularyQuickText { get => vocabularyQuickText; set { vocabularyQuickText = value; Changed(); } }
    private string vocabularySort = "recent", learningSearch = "", learningSource = "voice", learningSort = "frequency";
    public RecognitionVocabularyEntry? EditingVocabularyEntry { get; private set; }
    public PersonalCorrectionEntry? EditingCorrectionEntry { get; private set; }
    public bool VocabularyEditorOpen { get => vocabularyEditorOpen; set { vocabularyEditorOpen = value; Changed(); } }
    public string VocabularyEditorTitle => UiText.Translate(EditingVocabularyEntry is null ? "添加热词" : "编辑热词");
    private bool canUndoCorrection;
    public bool CanUndoCorrection { get => canUndoCorrection; set { canUndoCorrection = value; Changed(); } }
    public bool PreferredOnly { get => preferredOnly; set { preferredOnly = value; Changed(); NotifyRecognitionWordsChanged(); } }
    public string VocabularySort { get => vocabularySort; set { vocabularySort = value; Changed(); NotifyRecognitionWordsChanged(); } }
    private IEnumerable<RecognitionVocabularyEntry> FilteredRecognitionWords()
    {
        var words = RecognitionWords.Where(entry => (!PreferredOnly || entry.Preferred) &&
            (string.IsNullOrWhiteSpace(VocabularySearch) || entry.Word.Contains(VocabularySearch.Trim(), StringComparison.CurrentCultureIgnoreCase)));
        return VocabularySort == "name" ? words.OrderBy(entry => entry.Word, StringComparer.CurrentCultureIgnoreCase) :
            words.OrderByDescending(entry => entry.Preferred).ThenByDescending(entry => entry.AddedAtUtc);
    }
    public IEnumerable<RecognitionVocabularyEntry> VisibleRecognitionWords => FilteredRecognitionWords().Take(DisplayWordLimit);
    public string RecognitionWordCount => UiText.Translate($"{RecognitionWords.Count} / 1000 个热词 · {RecognitionWords.Count(entry => entry.Preferred)} 个优先词" +
        (string.IsNullOrWhiteSpace(VocabularySearch) && !PreferredOnly ? "" : $" · 匹配 {FilteredRecognitionWords().Count()} 个"));
    public void NotifyRecognitionWordsChanged()
    {
        Changed(nameof(VisibleRecognitionWords)); Changed(nameof(RecognitionWordCount)); RefreshLearningBrowser();
    }
    public void BeginVocabularyEdit(RecognitionVocabularyEntry? entry, string? initial = null)
    {
        EditingVocabularyEntry = entry; VocabularyDraft = entry?.Word ?? initial ?? "";
        Changed(nameof(VocabularyEditorTitle)); VocabularyEditorOpen = true;
    }
    public void CancelVocabularyEdit() { VocabularyEditorOpen = false; EditingVocabularyEntry = null; VocabularyDraft = ""; }
    public void BeginCorrectionEdit(PersonalCorrectionEntry? entry)
    {
        EditingCorrectionEntry = entry; MistakenWord = entry?.Mistaken ?? ""; CorrectedWord = entry?.Corrected ?? "";
        Changed(nameof(MistakenWord)); Changed(nameof(CorrectedWord)); Changed(nameof(CorrectionEditorTitle)); CorrectionEditorOpen = true;
    }
    private bool correctionEditorOpen;
    public bool CorrectionEditorOpen { get => correctionEditorOpen; set { correctionEditorOpen = value; Changed(); } }
    public string CorrectionEditorTitle => UiText.Translate(EditingCorrectionEntry is null ? "添加纠错" : "编辑纠错");
    public void CancelCorrectionEdit() { CorrectionEditorOpen = false; EditingCorrectionEntry = null; }
    public string LearningSource
    {
        get => learningSource;
        set
        {
            learningSource = value; 
            if (value != "voice" && learningSort == "recent") learningSort = "frequency";
            Changed(); Changed(nameof(LearningSort)); Changed(nameof(LearningSourceDescription));
            Changed(nameof(FrequencySortLabel)); Changed(nameof(CanSortLearningByTime)); RefreshLearningBrowser();
        }
    }
    public string FrequencySortLabel => UiText.Translate(LearningSource == "voice" ? "按学习频次" : "默认顺序");
    public bool CanSortLearningByTime => LearningSource == "voice";
    public string LearningSearch { get => learningSearch; set { learningSearch = value; Changed(); RefreshLearningBrowser(); } }
    public string LearningSort { get => learningSort; set { learningSort = value; Changed(); RefreshLearningBrowser(); } }
    public string LearningSourceDescription => UiText.Translate(LearningSource switch
    {
        "keyboard" => "开启识别热词后，这些词会自动参与候选，无需手动添加。原生接口未提供逐词频次。",
        "ignored" => "这些词已停止自动学习；恢复后允许重新学习，不会自动加入我的热词。",
        _ => "开启识别热词后，有效学习词会自动参与候选。学习频次来自术语提取与纠错反馈，不是识别命中次数。"
    });
    private IEnumerable<LearningToken> FilteredLearningTokens()
    {
        var manual = RecognitionWords.ToDictionary(entry => entry.Word, StringComparer.OrdinalIgnoreCase);
        IEnumerable<LearningToken> tokens = LearningSource switch
        {
            "keyboard" => KeyboardHotwords.Select(word => new LearningToken(word, "keyboard", UiText.Translate("从普通打字中学到；可以加入我的热词后编辑"))),
            "ignored" => SuppressedLearningWords.Select(word => new LearningToken(word, "ignored", UiText.Translate("已忽略；恢复后允许重新学习"))),
            _ => LearnedWords.Select(entry => new LearningToken(entry.Word, "voice", UiText.Current.IsEnglish ? $"{entry.Word} · learning frequency {entry.Frequency}; from term extraction and correction feedback" : $"{entry.Word} · 学习频次 {entry.Frequency}；来自术语提取与纠错反馈", entry))
        };
        tokens = tokens.Where(token => string.IsNullOrWhiteSpace(LearningSearch) || token.Word.Contains(LearningSearch.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .Select(token => token with { ManualEntry = manual.GetValueOrDefault(token.Word) });
        return LearningSort switch
        {
            "name" => tokens.OrderBy(token => token.Word, StringComparer.CurrentCultureIgnoreCase),
            "recent" => tokens.OrderByDescending(token => token.Entry?.UpdatedAtUtc),
            _ => LearningSource == "voice" ? tokens.OrderByDescending(token => token.Entry!.Frequency).ThenByDescending(token => token.Entry!.UpdatedAtUtc) : tokens
        };
    }
    public IEnumerable<LearningToken> VisibleLearningTokens => FilteredLearningTokens().Take(DisplayWordLimit);
    public string LearningResultCount => UiText.Translate(FilteredLearningTokens().Count() is var count && count > DisplayWordLimit ? $"{count} 个词 · 显示前 1000 个，可搜索全部词" : $"{count} 个词");
    public void RefreshLearningBrowser()
    {
        Changed(nameof(VisibleLearningTokens)); Changed(nameof(LearningResultCount));
    }
}
