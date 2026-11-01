using System.Text;
using System.Text.Json;

namespace iSay.Voice;

public static class VocabularyInterchange
{
    private sealed record ExportDocument(int Version, string[] Words, PersonalCorrectionEntry[] Corrections);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static VocabularyImportResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.Equals(Path.GetFileName(path), "asr_user_ner_dict.dat", StringComparison.OrdinalIgnoreCase))
            return InstalledVocabularyReader.ReadFile(path);
        var bytes = VocabularyFileIO.ReadLimited(path, RecognitionVocabularyStore.MaximumFileBytes);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("词库不是有效的 UTF-8 文件，未导入任何内容。", error); }
        var words = new List<string>(); var corrections = new List<PersonalCorrectionEntry>(); var rejected = 0;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            ExportDocument? document;
            try { document = JsonSerializer.Deserialize<ExportDocument>(text); }
            catch (JsonException error) { throw new InvalidDataException("词库 JSON 格式损坏，未导入任何内容。", error); }
            if (document is null || document.Version != 1 || document.Words is null || document.Corrections is null ||
                document.Words.Length > RecognitionVocabularyStore.MaximumEntries ||
                document.Corrections.Length > PersonalVocabularyStore.MaximumEntries)
                throw new InvalidDataException("词库版本、字段或容量不受支持，未导入任何内容。");
            foreach (var word in document.Words)
                if (RecognitionVocabularyStore.NormalizeWord(word) is { } normalized) words.Add(normalized); else rejected++;
            foreach (var correction in document.Corrections)
            {
                if (correction is not null && TryCorrection(correction.Mistaken, correction.Corrected) is { } valid)
                    corrections.Add(valid);
                else rejected++;
            }
        }
        else if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            var lines = text.Split('\n');
            if (lines.Length > RecognitionVocabularyStore.MaximumEntries + PersonalVocabularyStore.MaximumEntries)
                throw new InvalidDataException("词库行数超出允许容量，未导入任何内容。");
            foreach (var line in lines)
            {
                var value = line.Trim();
                if (value.Length == 0 || value.StartsWith('#')) continue;
                if (value.Contains("=>", StringComparison.Ordinal))
                {
                    var pair = value.Split("=>", 2, StringSplitOptions.TrimEntries);
                    if (TryCorrection(pair[0], pair[1]) is { } correction) corrections.Add(correction); else rejected++;
                }
                else if (RecognitionVocabularyStore.NormalizeWord(value) is { } word) words.Add(word);
                else rejected++;
            }
        }
        else throw new InvalidDataException("仅支持 iSay JSON、UTF-8 文本词库或已核验的已支持的识别词库文件。");
        var uniqueWords = words.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var uniqueCorrections = corrections.DistinctBy(entry => entry.Mistaken).ToArray();
        if (uniqueWords.Length > RecognitionVocabularyStore.MaximumEntries ||
            uniqueCorrections.Length > PersonalVocabularyStore.MaximumEntries)
            throw new InvalidDataException("词库有效条目超出容量，未导入任何内容。");
        return new(uniqueWords, uniqueCorrections, rejected,
            [new(Path.GetFileName(path), words.Count + corrections.Count + rejected,
                uniqueWords.Length, uniqueCorrections.Length, rejected, true)]);
    }

    // 导出不覆盖已有文件，避免选错路径破坏原词库；界面应让用户另选文件名。
    public static void Write(string path, IEnumerable<string> words, IEnumerable<PersonalCorrectionEntry> corrections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(words); ArgumentNullException.ThrowIfNull(corrections);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导出文件请使用 .json 扩展名，以保留纯热词和纠错对的区别。");
        var selectedWords = words.Take(RecognitionVocabularyStore.MaximumEntries + 1).ToArray();
        var selectedCorrections = corrections.Take(PersonalVocabularyStore.MaximumEntries + 1).ToArray();
        if (selectedWords.Length > RecognitionVocabularyStore.MaximumEntries ||
            selectedCorrections.Length > PersonalVocabularyStore.MaximumEntries ||
            selectedWords.Any(word => RecognitionVocabularyStore.NormalizeWord(word) != word) ||
            selectedCorrections.Any(entry => entry is null || TryCorrection(entry.Mistaken, entry.Corrected) is null))
            throw new InvalidDataException("导出词库内容或容量不正确，未写入文件。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ExportDocument(1,
            selectedWords.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), selectedCorrections),
            new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > RecognitionVocabularyStore.MaximumFileBytes) throw new InvalidDataException("导出词库超出允许容量，未写入文件。");
        VocabularyFileIO.WriteAtomic(path, bytes, false);
    }

    private static PersonalCorrectionEntry? TryCorrection(string? mistaken, string? corrected)
    {
        var source = RecognitionVocabularyStore.NormalizeWord(mistaken);
        var target = RecognitionVocabularyStore.NormalizeWord(corrected);
        return source is not null && target is not null && source != target && !target.Contains("=>", StringComparison.Ordinal)
            ? new(source, target, 1, DateTimeOffset.UtcNow) : null;
    }
}
