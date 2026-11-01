using System.Text;
using System.Text.Json;

namespace iSay.Voice;

public sealed record RecognitionVocabularyEntry(string Word, DateTimeOffset AddedAtUtc, bool Preferred = false);
public sealed record VocabularyImportSummary(int AddedCount, int ExistingCount, int RejectedCount);
public sealed record VocabularySourceSummary(string Source, int ReadCount, int WordCount,
    int CorrectionCount, int RejectedCount, bool Supported);
public sealed record VocabularyImportResult(IReadOnlyList<string> Words,
    IReadOnlyList<PersonalCorrectionEntry> Corrections, int RejectedCount,
    IReadOnlyList<VocabularySourceSummary> Sources, string? Warning = null)
{
    public int UnsupportedCount => Sources.Count(source => !source.Supported);
}

// 纯识别词与纠错对分开保存；导入和增删不会自行开启联网热词。
public sealed class RecognitionVocabularyStore
{
    public const int MaximumEntries = 1000;
    // 兼容旧词库，超过新增上限的已有记录仍完整读取，不按损坏文件处理。
    private const int LegacyReadLimit = 5000;
    public const int MaximumPreferredEntries = 256;
    public const int MaximumWordLength = 64;
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    private readonly object gate = new();
    private readonly string path;
    private List<RecognitionVocabularyEntry> entries;

    public RecognitionVocabularyStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        path = Path.GetFullPath(filePath);
        entries = ReadEntries();
    }

    public IReadOnlyList<RecognitionVocabularyEntry> Snapshot()
    {
        lock (gate) return entries.ToArray();
    }

    public bool Add(string word)
    {
        var normalized = NormalizeWord(word);
        if (normalized is null) return false;
        lock (gate)
        {
            if (entries.Count >= MaximumEntries || entries.Any(entry =>
                string.Equals(entry.Word, normalized, StringComparison.OrdinalIgnoreCase))) return false;
            Save(entries.Append(new(normalized, DateTimeOffset.UtcNow)).ToList());
            return true;
        }
    }

    public bool Remove(string word)
    {
        lock (gate)
        {
            var next = entries.Where(entry => !string.Equals(entry.Word, word,
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == entries.Count) return false;
            Save(next);
            return true;
        }
    }

    public bool Rename(RecognitionVocabularyEntry expected, string word)
    {
        var normalized = NormalizeWord(word);
        if (normalized is null) return false;
        lock (gate)
        {
            var index = entries.FindIndex(entry => entry == expected);
            if (index < 0 || entries.Where((_, i) => i != index).Any(entry => string.Equals(entry.Word, normalized, StringComparison.OrdinalIgnoreCase))) return false;
            var next = entries.ToList();
            next[index] = expected with { Word = normalized };
            Save(next);
            return true;
        }
    }

    public IReadOnlyList<RecognitionVocabularyEntry> RemoveMany(IEnumerable<RecognitionVocabularyEntry> selected)
    {
        lock (gate)
        {
            // 按完整快照匹配，迟到的界面操作不能删除已经被修改的词。
            var requested = selected.ToHashSet();
            var removed = entries.Where(requested.Contains).ToArray();
            if (removed.Length > 0) Save(entries.Where(entry => !requested.Contains(entry)).ToList());
            return removed;
        }
    }

    public int Restore(IEnumerable<RecognitionVocabularyEntry> removed)
    {
        lock (gate)
        {
            var next = entries.ToList();
            var count = 0;
            foreach (var entry in removed)
            {
                if (NormalizeWord(entry.Word) != entry.Word || next.Count >= MaximumEntries ||
                    next.Any(value => string.Equals(value.Word, entry.Word, StringComparison.OrdinalIgnoreCase))) continue;
                if (entry.Preferred && next.Count(value => value.Preferred) >= MaximumPreferredEntries) continue;
                next.Add(entry); count++;
            }
            if (count > 0) Save(next);
            return count;
        }
    }

    public bool SetPreferred(string word, bool preferred)
    {
        var normalized = NormalizeWord(word);
        if (normalized is null) return false;
        lock (gate)
        {
            var index = entries.FindIndex(entry => string.Equals(entry.Word, normalized,
                StringComparison.OrdinalIgnoreCase));
            if (index < 0 || entries[index].Preferred == preferred) return false;
            // 超限拒绝本次设置，不静默替换其他优先词；取消优先始终不受容量限制。
            if (preferred && entries.Count(entry => entry.Preferred) >= MaximumPreferredEntries) return false;
            var next = entries.ToList();
            next[index] = next[index] with { Preferred = preferred };
            Save(next);
            return true;
        }
    }

    public VocabularyImportSummary Import(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        lock (gate)
        {
            var next = entries.ToList();
            var seen = new HashSet<string>(next.Select(entry => entry.Word), StringComparer.OrdinalIgnoreCase);
            var added = 0; var existing = 0; var rejected = 0; var examined = 0;
            foreach (var word in words)
            {
                // 来自文件的序列有容量上限；额外防止调用者传入无限序列。
                if (++examined > MaximumEntries * 4) { rejected++; break; }
                var normalized = NormalizeWord(word);
                if (normalized is null) { rejected++; continue; }
                if (seen.Contains(normalized)) { existing++; continue; }
                if (next.Count >= MaximumEntries) { rejected++; continue; }
                next.Add(new(normalized, DateTimeOffset.UtcNow)); seen.Add(normalized); added++;
            }
            if (added > 0) Save(next);
            return new(added, existing, rejected);
        }
    }

    internal static string? NormalizeWord(string? value)
    {
        var word = value?.Trim();
        if (string.IsNullOrEmpty(word) || word.Length > MaximumWordLength ||
            word.Any(character => char.IsControl(character) || character == '\uFFFD' ||
                character is '\uFFFE' or '\uFFFF')) return null;
        for (var index = 0; index < word.Length; index++)
        {
            if (char.IsHighSurrogate(word[index]))
            {
                if (index + 1 >= word.Length || !char.IsLowSurrogate(word[++index])) return null;
            }
            else if (char.IsLowSurrogate(word[index])) return null;
        }
        return word;
    }

    private List<RecognitionVocabularyEntry> ReadEntries()
    {
        if (!File.Exists(path)) return [];
        var bytes = VocabularyFileIO.ReadLimited(path, MaximumFileBytes);
        List<RecognitionVocabularyEntry>? values;
        try { values = JsonSerializer.Deserialize<List<RecognitionVocabularyEntry>>(bytes); }
        catch (JsonException error) { throw new InvalidDataException("识别词库文件格式损坏，原文件未修改。", error); }
        if (values is null || values.Count > LegacyReadLimit || values.Count(entry => entry is not null && entry.Preferred) > MaximumPreferredEntries ||
            values.Any(entry => entry is null ||
            NormalizeWord(entry.Word) != entry.Word) || values.Select(entry => entry.Word)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
            throw new InvalidDataException("识别词库内容或容量不正确，原文件未修改。");
        return values;
    }

    private void Save(List<RecognitionVocabularyEntry> next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("识别词库超出允许容量，原文件未修改。");
        VocabularyFileIO.WriteAtomic(path, bytes, true);
        entries = next;
    }
}

internal static class VocabularyFileIO
{
    public static byte[] ReadLimited(string path, int maximumBytes)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = source.Length;
        if (length < 0 || length > maximumBytes) throw new InvalidDataException("词库文件超出允许容量，未读取内容。");
        var bytes = new byte[(int)length]; source.ReadExactly(bytes);
        if (source.Length != length) throw new IOException("词库正在变化，请稍后重试，未导入内容。");
        return bytes;
    }

    public static void WriteAtomic(string path, byte[] bytes, bool overwrite)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, fullPath, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
