using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace iSay.Voice.Engines;

public static class AsrVocabularyContext
{
    public const int MaximumWords = 256;
    public const int MaximumWordLength = 64;
    public const int MaximumUtf8Bytes = 16 * 1024;

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? words)
    {
        if (words is null) return Array.Empty<string>();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in words)
        {
            if (!TryNormalizeWord(candidate, out var word) || !seen.Add(word)) continue;
            result.Add(word);
            if (result.Count == MaximumWords) break;
        }
        return result.AsReadOnly();
    }

    public static string BuildBase64(IEnumerable<string>? words)
    {
        // 兼容入口只编码已选词表，不再次套选择预算，避免两批词被误当成同一来源后缩减。
        return Encode(Normalize(words)).Base64;
    }

    public static AsrVocabularySnapshot CreateSnapshot(IEnumerable<VocabularySelectionCandidate>? candidates, long version = 0)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version), "词表版本不能为负数。");
        var selection = OfficialVocabularySelection.Select(candidates);
        var encoded = Encode(selection.Words);
        if (encoded.Count < selection.Words.Count) selection = selection.KeepPrefix(encoded.Count);
        return new(selection, encoded.Base64, Convert.ToHexString(SHA256.HashData(encoded.Payload)), version);
    }

    internal static bool TryNormalizeWord(string? candidate, out string word)
    {
        word = candidate?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(word) || word.Length > MaximumWordLength) return false;
        for (var index = 0; index < word.Length; index++)
        {
            var character = word[index];
            if (char.IsControl(character)) return false;
            if (!char.IsSurrogate(character)) continue;
            if (!char.IsHighSurrogate(character) || index + 1 == word.Length || !char.IsLowSurrogate(word[++index])) return false;
        }
        return true;
    }

    private static EncodedContext Encode(IReadOnlyList<string> normalized)
    {
        if (normalized.Count == 0) return new("", [], 0);
        var prefix = Encoding.UTF8.GetBytes("{\"chat\":[],\"hotwords\":[");
        var suffix = Encoding.UTF8.GetBytes("],\"trackingInfo\":{\"start_type\":\"shortcut_long\"}}");
        var length = prefix.Length + suffix.Length;
        var selected = new List<byte[]>();
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        foreach (var word in normalized)
        {
            // 内部频次参与选择；已确认的官方普通启动路径在请求字段中填0、类别为other。
            var item = JsonSerializer.SerializeToUtf8Bytes(new { word, @class = "other", frequency = 0 }, options);
            var additional = item.Length + (selected.Count > 0 ? 1 : 0);
            if (additional > MaximumUtf8Bytes - length) break;
            selected.Add(item);
            length += additional;
        }
        if (selected.Count == 0) return new("", [], 0);
        using var payload = new MemoryStream(length);
        payload.Write(prefix);
        for (var index = 0; index < selected.Count; index++)
        {
            if (index > 0) payload.WriteByte((byte)',');
            payload.Write(selected[index]);
        }
        payload.Write(suffix);
        var bytes = payload.ToArray();
        return new(Convert.ToBase64String(bytes), bytes, selected.Count);
    }

    private sealed record EncodedContext(string Base64, byte[] Payload, int Count);
}

public sealed class AsrVocabularySnapshot
{
    internal AsrVocabularySnapshot(VocabularySelectionResult selection, string base64, string fingerprint, long version)
    {
        Selection = selection;
        Base64 = base64;
        Fingerprint = fingerprint;
        Version = version;
    }

    public IReadOnlyList<string> Words => Selection.Words;
    public string Base64 { get; }
    public string Fingerprint { get; }
    public long Version { get; }
    public VocabularySelectionResult Selection { get; }
}
