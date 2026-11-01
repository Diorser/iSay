using System.Text.Json.Serialization;

namespace iSay.Voice;

public sealed class VoiceSettings
{
    public int Version { get; set; } = 1;
    public string UiLanguage { get; set; } = "zh-CN";
    public bool AutoCheckUpdates { get; set; } = true;
    public string SkippedUpdateVersion { get; set; } = "";
    public string MicrophoneId { get; set; } = "default";
    public string Hotkey { get; set; } = "RightAlt";
    public string HotkeyMode { get; set; } = "toggle";
    public bool StartAtLogin { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool ShowFloatingBar { get; set; } = true;
    public bool MuteSystemAudioWhileRecording { get; set; } = true;
    public int MaxRecordingSeconds { get; set; } = 600;
    public int NoNewTextTimeoutSeconds { get; set; } = 20;
    public string Engine { get; set; } = "native";
    public bool SmartFormattingEnabled { get; set; } = true;
    public string FormattingBackend { get; set; } = "auto";
    public string FormattingStyle { get; set; } = "standard";
    public string PunctuationMode { get; set; } = "all";
    public string LatinSpacing { get; set; } = "preserve";
    public string ApiBaseUrl { get; set; } = "";
    public string ApiModel { get; set; } = "";
    [JsonIgnore] public string ApiKey { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    public string ProxyMode { get; set; } = "system";
    public string ProxyUrl { get; set; } = "";
    public string OutputMode { get; set; } = "live";
    public string InputBackend { get; set; } = "tsf";
    // 选区同步候选尚未通过节点重建压力测试，默认关闭；不可随正式升级自动启用。
    public bool ChromiumRangeSyncEnabled { get; set; }
    public string FocusPolicy { get; set; } = "pause-delivery";
    public bool SaveHistory { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 30;
    public bool SaveAudio { get; set; }
    public int AudioRetentionDays { get; set; } = 30;
    public bool AudioRetentionEnabled { get; set; }
    public bool AsrVocabularyEnabled { get; set; }
    public bool LearnVocabularyEnabled { get; set; }
    // 与官方体验改进门控分开保存；升级不得替用户开启语音正文的额外联网学习。
    public bool AutoVocabularyLearningEnabled { get; set; }
    // 普通候选提交只在本地学习，与语音正文的联网学习分开控制。
    public bool KeyboardLearningEnabled { get; set; }
    [JsonIgnore] public IReadOnlyList<PersonalCorrectionEntry> PersonalCorrections { get; set; } = [];
    public bool RemoveFillers { get; set; }
    public string CustomVocabulary { get; set; } = "";
    public string FormattingInstructions { get; set; } = "";
    public bool SoundFeedback { get; set; }
    public string AsrBaseUrl { get; set; } = "";
    public string AsrModel { get; set; } = "whisper-1";
    [JsonIgnore] public string AsrApiKey { get; set; } = "";
    public string AsrApiKeyProtected { get; set; } = "";
    public string NativeRuntimePath { get; set; } = "";

    public void Validate()
    {
        // 仅在读取旧配置时接受历史标识；落盘统一使用中性名称。
        if (Engine == "doubao-native") Engine = "native";
        if (FormattingBackend == "doubao-native") FormattingBackend = "native";
        if (UiLanguage is not ("zh-CN" or "en-US")) UiLanguage = "zh-CN";
        if (SkippedUpdateVersion.Length > 0 && !System.Version.TryParse(SkippedUpdateVersion, out _)) SkippedUpdateVersion = "";
        Hotkey = HotkeyDefinition.Parse(Hotkey).Canonical;
        if (!new[] { "toggle", "hold" }.Contains(HotkeyMode)) throw new ArgumentException("录音快捷键模式无效。");
        if (MaxRecordingSeconds is < 10 or > 3600) throw new ArgumentException("单次录音上限应为10至3600秒。");
        if (NoNewTextTimeoutSeconds != 0 && (NoNewTextTimeoutSeconds is < 5 or > 600))
            throw new ArgumentException("无新文字自动结束应为5至600秒，填写0可关闭。");
        if (HistoryRetentionDays is < 1 or > 3650) throw new ArgumentException("历史保留时间应为1至3650天。");
        if (AudioRetentionDays is < 1 or > 3650) throw new ArgumentException("录音保留时间应为1至3650天。");
        if (!new[] { "all", "spaces", "no-final" }.Contains(PunctuationMode)) throw new ArgumentException("标点偏好无效。");
        if (!new[] { "preserve", "compact", "spaced" }.Contains(LatinSpacing)) throw new ArgumentException("中英数字间距偏好无效。");
        if (!new[] { "native", "openai-compatible" }.Contains(Engine)) throw new ArgumentException("识别引擎设置无效。");
        if (!new[] { "live", "on-stop", "clipboard" }.Contains(OutputMode)) throw new ArgumentException("输出方式设置无效。");
        if (!new[] { "tsf", "compatibility" }.Contains(InputBackend)) throw new ArgumentException("输入模式设置无效。");
        if (!new[] { "pause-delivery", "stop-recording" }.Contains(FocusPolicy)) throw new ArgumentException("焦点策略设置无效。");
        if (!new[] { "system", "direct", "custom" }.Contains(ProxyMode)) throw new ArgumentException("代理模式设置无效。");
        if (!new[] { "standard", "literal", "paragraphs" }.Contains(FormattingStyle)) throw new ArgumentException("整理风格设置无效。");
        if (!new[] { "auto", "native", "custom-api" }.Contains(FormattingBackend)) throw new ArgumentException("整理服务设置无效。");
        if (ProxyMode == "custom" && !ValidUrl(ProxyUrl)) throw new ArgumentException("请填写有效的HTTP或SOCKS代理地址。");
        if (!string.IsNullOrWhiteSpace(ApiBaseUrl) && !ValidHttpUrl(ApiBaseUrl)) throw new ArgumentException("智能整理接口地址无效。");
        if (!string.IsNullOrWhiteSpace(AsrBaseUrl) && !ValidHttpUrl(AsrBaseUrl)) throw new ArgumentException("语音识别接口地址无效。");
    }

    private static bool ValidUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && new[] { "http", "https", "socks5", "socks4" }.Contains(uri.Scheme);
    private static bool ValidHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

public sealed record MicrophoneInfo(string Id, string Name);
public sealed record HistoryEntry(string Id, DateTimeOffset CreatedAt, string RawText, string FormattedText, string Status, string? AudioPath = null)
{
    public string Preview => (string.IsNullOrWhiteSpace(FormattedText) ? RawText : FormattedText).Replace("\n", " ");
    public override string ToString() => $"{CreatedAt:MM-dd HH:mm}  {Preview[..Math.Min(Preview.Length, 80)]}";
}
