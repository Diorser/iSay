using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace iSay.Voice;

public sealed record PersonalCorrectionEntry(string Mistaken, string Corrected, int UseCount, DateTimeOffset UpdatedAtUtc);

// 只由用户明确的“记住纠错”动作调用，不监视输入框，不读取第三方词库或历史。
public sealed class PersonalVocabularyStore
{
    public const int MaximumEntries = 512;
    public const int MaximumTermLength = 64;
    private const int MaximumFileBytes = 512 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private List<PersonalCorrectionEntry> _entries;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PersonalVocabularyStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _path = Path.GetFullPath(filePath);
        _entries = ReadEntries();
    }

    public IReadOnlyList<PersonalCorrectionEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public bool RememberCorrection(string mistaken, string corrected)
    {
        mistaken = Normalize(mistaken);
        corrected = Normalize(corrected);
        if (mistaken.Length == 0 || corrected.Length == 0 || mistaken == corrected) return false;
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(v => v.Mistaken == mistaken);
            var next = _entries.Where(v => v.Mistaken != mistaken).ToList();
            next.Insert(0, new(mistaken, corrected, existing?.Corrected == corrected
                ? Math.Clamp(existing.UseCount, 0, 999_999) + 1 : 1, DateTimeOffset.UtcNow));
            Save(next.Take(MaximumEntries).ToList());
            return true;
        }
    }

    public bool RemoveCorrection(string mistaken, string corrected)
    {
        lock (_gate)
        {
            var next = _entries.Where(v => v.Mistaken != mistaken || v.Corrected != corrected).ToList();
            if (next.Count == _entries.Count) return false;
            Save(next);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) Save([]);
    }

    public bool Update(PersonalCorrectionEntry expected, string mistaken, string corrected)
    {
        mistaken = Normalize(mistaken); corrected = Normalize(corrected);
        if (mistaken.Length == 0 || corrected.Length == 0 || mistaken == corrected) return false;
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry == expected);
            if (index < 0 || _entries.Where((_, i) => i != index).Any(entry => entry.Mistaken == mistaken)) return false;
            var next = _entries.ToList();
            next[index] = expected with { Mistaken = mistaken, Corrected = corrected, UpdatedAtUtc = DateTimeOffset.UtcNow };
            Save(next); return true;
        }
    }

    public bool Restore(PersonalCorrectionEntry entry)
    {
        if (Normalize(entry.Mistaken) != entry.Mistaken || Normalize(entry.Corrected) != entry.Corrected ||
            entry.Mistaken.Length == 0 || entry.Corrected.Length == 0 || entry.Mistaken == entry.Corrected) return false;
        lock (_gate)
        {
            if (_entries.Count >= MaximumEntries || _entries.Any(value => value.Mistaken == entry.Mistaken)) return false;
            Save(_entries.Prepend(entry).ToList()); return true;
        }
    }

    public IReadOnlyList<string> GetRecognitionVocabulary()
    {
        lock (_gate) return Engines.AsrVocabularyContext.Normalize(_entries.Select(v => v.Corrected));
    }

    public string ApplyCorrections(string text)
    {
        PersonalCorrectionEntry[] entries;
        lock (_gate) entries = _entries.ToArray();
        return ApplyCorrections(text, entries);
    }

    public static string ApplyCorrections(string text, IReadOnlyList<PersonalCorrectionEntry> entries)
    {
        if (string.IsNullOrEmpty(text) || entries.Count == 0) return text;
        var map = entries.Where(v => v is not null && Normalize(v.Mistaken) == v.Mistaken
                && Normalize(v.Corrected) == v.Corrected && v.Mistaken.Length > 0 && v.Corrected.Length > 0
                && v.Mistaken != v.Corrected).DistinctBy(v => v.Mistaken).Take(MaximumEntries)
            .ToDictionary(v => v.Mistaken, v => v.Corrected, StringComparer.Ordinal);
        if (map.Count == 0) return text;
        // 一次匹配只替换原文，避免甲→乙、乙→丙在同一遍产生级联；较长术语优先。
        var patterns = map.Keys.OrderByDescending(v => v.Length).Select(v =>
            (IsAsciiWord(v[0]) ? @"(?<![A-Za-z0-9_])" : "") + Regex.Escape(v)
            + (IsAsciiWord(v[^1]) ? @"(?![A-Za-z0-9_])" : ""));
        return Regex.Replace(text, string.Join("|", patterns), m => map[m.Value], RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
    }

    private List<PersonalCorrectionEntry> ReadEntries()
    {
        if (!File.Exists(_path)) return [];
        var info = new FileInfo(_path);
        if (info.Length > MaximumFileBytes) throw new InvalidDataException("个人纠错文件超出允许容量，原文件未修改。");
        var values = JsonSerializer.Deserialize<List<PersonalCorrectionEntry>>(File.ReadAllText(_path, Encoding.UTF8))
            ?? throw new InvalidDataException("个人纠错文件格式不正确，原文件未修改。");
        return values.Where(v => v is not null && Normalize(v.Mistaken) == v.Mistaken
            && Normalize(v.Corrected) == v.Corrected && v.Mistaken.Length > 0 && v.Corrected.Length > 0
            && v.Mistaken != v.Corrected).DistinctBy(v => v.Mistaken).Take(MaximumEntries).ToList();
    }

    private void Save(List<PersonalCorrectionEntry> entries)
    {
        var parent = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, _path, true);
            _entries = entries;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Normalize(string? term)
    {
        var value = term?.Trim() ?? "";
        return value.Length is > 0 and <= MaximumTermLength && !value.Any(char.IsControl) ? value : "";
    }

    private static bool IsAsciiWord(char value) => char.IsAsciiLetterOrDigit(value) || value == '_';
}
