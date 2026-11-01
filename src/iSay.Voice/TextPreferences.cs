using System.Text;
using System.Text.RegularExpressions;

namespace iSay.Voice;

/// <summary>只调整语句标点及汉字与英文数字的边界，不改写网址、数值、代码或英文词间空格。</summary>
public static class TextPreferences
{
    private static readonly Regex ProtectedText = new(
        """
        "(?:[A-Za-z]:\\|\\\\)[^"\r\n]+"|'(?:[A-Za-z]:\\|\\\\)[^'\r\n]+'|(?:https?://|ftp://|www\.)[^\s<>"“”，。！？；：、]+|[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}|[0-9]+(?:[.,:/\-][0-9]+)+|\.[0-9]+|(?:[A-Za-z]:\\|\\\\)[^\s<>"“”，。！？；：、]+
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private const string SentencePunctuation = "，。！？；：、,.!?;:…";
    private const string ClosingMarks = "\"'”’）)]}〉》」』】";

    public static string Apply(string text, VoiceSettings settings)
    {
        if (text.Length == 0 || settings.PunctuationMode == "all" && settings.LatinSpacing == "preserve") return text;
        var protectedPositions = Protect(text);
        var value = settings.PunctuationMode switch
        {
            "spaces" => ReplaceSentencePunctuation(text, protectedPositions),
            "no-final" => RemoveFinalPunctuation(text, protectedPositions),
            _ => text
        };
        return settings.LatinSpacing == "preserve" ? value : AdjustSpacing(value, settings.LatinSpacing);
    }

    private static bool[] Protect(string text)
    {
        var mask = new bool[text.Length];
        foreach (Match match in ProtectedText.Matches(text)) Array.Fill(mask, true, match.Index, match.Length);
        // 成对反引号内的代码整体保留；未闭合代码从开头保护到当前快照末尾。
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '`') continue;
            var count = 1;
            while (index + count < text.Length && text[index + count] == '`') count++;
            var marker = new string('`', count);
            var close = text.IndexOf(marker, index + count, StringComparison.Ordinal);
            var end = close < 0 ? text.Length : close + count;
            Array.Fill(mask, true, index, end - index);
            index = end - 1;
        }
        return mask;
    }

    private static string ReplaceSentencePunctuation(string text, bool[] mask)
    {
        var result = new StringBuilder(text.Length);
        var pendingSpace = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (!mask[index] && SentencePunctuation.Contains(character)) { pendingSpace = true; continue; }
            if (pendingSpace && result.Length > 0 && !char.IsWhiteSpace(result[^1]) && !char.IsWhiteSpace(character)) result.Append(' ');
            pendingSpace = false;
            result.Append(character);
        }
        return result.ToString();
    }

    private static string RemoveFinalPunctuation(string text, bool[] mask)
    {
        var remove = new bool[text.Length];
        for (var end = text.Length; end >= 0; end--)
        {
            if (end != text.Length && text[end] != '\n') continue;
            var index = end - 1;
            while (index >= 0 && (text[index] is ' ' or '\t' or '\r' || ClosingMarks.Contains(text[index]))) index--;
            while (index >= 0 && !mask[index] && SentencePunctuation.Contains(text[index])) remove[index--] = true;
        }
        var result = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++) if (!remove[index]) result.Append(text[index]);
        return result.ToString();
    }

    private static string AdjustSpacing(string text, string mode)
    {
        // 先标记保护区；金额小数、URL以及代码内部原有空格不受边界规则影响。
        var protectedPositions = Protect(text);
        var runes = text.EnumerateRunes().ToArray();
        var offsets = new int[runes.Length];
        for (var index = 1; index < offsets.Length; index++) offsets[index] = offsets[index - 1] + runes[index - 1].Utf16SequenceLength;
        var result = new StringBuilder(text.Length + 16);
        for (var index = 0; index < runes.Length; index++)
        {
            if (mode == "compact" && (runes[index].Value is ' ' or '\t') && !protectedPositions[offsets[index]])
            {
                var next = index + 1;
                while (next < runes.Length && (runes[next].Value is ' ' or '\t') && !protectedPositions[offsets[next]]) next++;
                if (index > 0 && next < runes.Length && Boundary(runes[index - 1], runes[next])) { index = next - 1; continue; }
            }
            if (mode == "spaced" && index > 0 && Boundary(runes[index - 1], runes[index]) &&
                !(protectedPositions[offsets[index - 1]] && protectedPositions[offsets[index]])) result.Append(' ');
            result.Append(runes[index].ToString());
        }
        return result.ToString();
    }

    private static bool Boundary(Rune left, Rune right) => Han(left.Value) && LatinOrDigit(right.Value) || LatinOrDigit(left.Value) && Han(right.Value);
    private static bool LatinOrDigit(int value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
    private static bool Han(int value) => value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF || value == 0x3007;
}
