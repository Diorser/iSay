using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using System.Text.RegularExpressions;

namespace iSay.Desktop;

/// <summary>界面文案统一入口，切换语言时刷新现有绑定。</summary>
public sealed partial class UiText : INotifyPropertyChanged
{
    private sealed record TextPair(string Chinese, string English);
    private static readonly Lazy<Dictionary<string, string>> EnglishStrings = new(BuildEnglishStrings);
    private static Dictionary<string, string> BuildEnglishStrings() => Strings.Values
        .GroupBy(pair => pair.Chinese, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().English, StringComparer.Ordinal);
    private string language = "zh-CN";
    public static UiText Current { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;
    public string Language => language;
    public bool IsEnglish => language == "en-US";
    public string this[string key] => Strings.TryGetValue(key, out var pair) ? IsEnglish ? pair.English : pair.Chinese : key;

    public void SetLanguage(string? value)
    {
        var normalized = value?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en-US" : "zh-CN";
        if (language == normalized) return;
        language = normalized;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string Translate(string text)
    {
        if (!Current.IsEnglish || string.IsNullOrEmpty(text)) return Neutralize(text);
        text = Neutralize(text);
        if (EnglishStrings.Value.TryGetValue(text, out var english)) return english;
        if (Additional.TryGetValue(text, out var translated)) return translated;
        return TranslateStatus(text);
    }

    private static string Neutralize(string value) => value
        .Replace("豆包内置整理", "内置智能整理", StringComparison.Ordinal)
        .Replace("豆包本机语音适配器", "内置语音适配器", StringComparison.Ordinal)
        .Replace("原版豆包词库", "原有词库", StringComparison.Ordinal)
        .Replace("豆包原版资料", "原有安装资料", StringComparison.Ordinal)
        .Replace("本机豆包语音词库", "本机已有语音词库", StringComparison.Ordinal);

    private static string TranslateStatus(string value)
    {
        // 只转换已知的完整状态模板，避免改写用户词条、识别正文或服务原始错误。
        var patterns = new (string Pattern, string Replacement)[]
        {
            (@"^(\d+) 个自动学词 · (\d+) 个已忽略词$", "$1 learned words · $2 ignored words"),
            (@"^(\d+) / 1000 个热词 · (\d+) 个优先词(?: · 匹配 (\d+) 个)?$", "$1 / 1000 hotwords · $2 priority words · $3 matching"),
            (@"^(\d+) 个词 · 显示前 1000 个，可搜索全部词$", "$1 words · first 1,000 shown; search covers all words"),
            (@"^(\d+) 个词$", "$1 words"),
            (@"^共 (\d+) 条 · 当前 (\d+)–(\d+) 条$", "$1 entries · showing $2–$3"),
            (@"^删除选中（(\d+)）$", "Delete selected ($1)"),
        };
        foreach (var (pattern, replacement) in patterns)
            if (Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
                return Regex.Replace(value, pattern, replacement, RegexOptions.CultureInvariant).Replace(" ·  matching", "", StringComparison.Ordinal);
        var hint = Regex.Match(value, @"^(?:按住 (.+) 说话，松开结束|按 (.+) 开始，再按结束)，(结束时输入到当前光标|结束后复制到剪贴板|边说边输入)。$", RegexOptions.CultureInvariant);
        if (hint.Success)
        {
            var gesture = hint.Groups[1].Success ? $"Hold {hint.Groups[1].Value} to speak; release to stop" : $"Press {hint.Groups[2].Value} to start and again to stop";
            return gesture + (hint.Groups[3].Value switch
            {
                "结束时输入到当前光标" => ". Insert at the cursor when finished.",
                "结束后复制到剪贴板" => ". Copy to the clipboard when finished.",
                _ => ". Type while speaking."
            });
        }
        return value;
    }
}

public sealed class LExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding($"[{Key}]")
    {
        Source = UiText.Current,
        Mode = BindingMode.OneWay
    }.ProvideValue(serviceProvider);
}
