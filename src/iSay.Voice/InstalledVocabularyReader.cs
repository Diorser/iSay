using System.Buffers.Binary;
using System.Text;

namespace iSay.Voice;

// 只读已经核验的 ASR 实体词库，不打开拼音、网址、邮箱库或官方登录配置。
public static class InstalledVocabularyReader
{
    public const string SupportedFileName = "asr_user_ner_dict.dat";
    private const uint SupportedDictionaryType = 12036;
    private const uint SupportedVersion = 20251128;
    private const int MaximumSourceBytes = 4 * 1024 * 1024;

    public static VocabularyImportResult ReadDefault() => ReadDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoubaoIme", "dict"));

    public static VocabularyImportResult ReadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(Path.GetFullPath(directory), SupportedFileName);
        if (!File.Exists(path)) return Unsupported("未找到已支持的已支持的识别实体词库；可以使用 UTF-8 文本手动导入。");
        return ReadFile(path);
    }

    public static VocabularyImportResult ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(Path.GetFileName(path), SupportedFileName, StringComparison.OrdinalIgnoreCase))
            return Unsupported("该文件不是已核验的已支持的识别实体词库，未读取其他词库。");
        try
        {
            var before = new FileInfo(path); var modified = before.LastWriteTimeUtc; var length = before.Length;
            var bytes = VocabularyFileIO.ReadLimited(path, MaximumSourceBytes);
            // 官方程序可能保持文件句柄，最后修改时间尚未刷新；连续两次快照一致才解析。
            if (!bytes.AsSpan().SequenceEqual(VocabularyFileIO.ReadLimited(path, MaximumSourceBytes)))
                throw new IOException("词库正在变化，请稍后重试；未导入任何内容。");
            var result = Parse(bytes);
            before.Refresh();
            if (!before.Exists || before.Length != length || before.LastWriteTimeUtc != modified)
                throw new IOException("词库正在变化，请稍后重试；未导入任何内容。");
            return result;
        }
        catch (InvalidDataException error) { return Unsupported(error.Message); }
    }

    private static VocabularyImportResult Parse(byte[] bytes)
    {
        if (bytes.Length < 64) throw Invalid();
        var header = new uint[16];
        for (var index = 0; index < header.Length; index++)
            header[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4, 4));
        // 类型、版本和全部区间均锁定已确认格式，未知版本不尝试猜测解码。
        if (header[0] != SupportedDictionaryType || header[1] != SupportedVersion || header[2] != 64 ||
            header[3] != bytes.Length || header[4] != 64 || header[5] != 120 || header[6] != 184 ||
            header[8] is 0 or > 20000 || header[9] > header[8] || header[7] != header[8] * 4 ||
            header[10] != header[6] + header[7] || header[11] > MaximumSourceBytes ||
            (ulong)header[10] + header[11] != (ulong)bytes.Length || header[12] > header[11] ||
            header[13] != 2 || header[14] != 2 || header[15] != 16) throw Invalid();
        var count = (int)header[9]; var cursor = (int)header[12]; var dataOffset = (int)header[10];
        var offsets = new List<int>(count);
        for (var index = 0; index < header[8]; index++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)header[6] + index * 4, 4));
            if (index < count)
            {
                if (offset >= cursor) throw Invalid();
                offsets.Add((int)offset);
            }
            else if (offset != 0) throw Invalid();
        }
        if (count == 0)
        {
            if (cursor != 0) throw Invalid();
            return new([], [], 0, [new(SupportedFileName, 0, 0, 0, 0, true)]);
        }
        offsets.Sort();
        if (offsets[0] != 0 || offsets.Distinct().Count() != count) throw Invalid();
        var words = new List<string>(count); var rejected = 0;
        for (var index = 0; index < count; index++)
        {
            var start = offsets[index]; var end = index + 1 < count ? offsets[index + 1] : cursor;
            var record = bytes.AsSpan(dataOffset + start, end - start);
            if (record.Length < 22) throw Invalid();
            var syllableBytes = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
            var payload = 18 + syllableBytes;
            if (syllableBytes == 0 || syllableBytes % 2 != 0 || payload + 4 > record.Length) throw Invalid();
            var payloadBytes = BinaryPrimitives.ReadUInt16LittleEndian(record[payload..]);
            var wordBytes = BinaryPrimitives.ReadUInt16LittleEndian(record[(payload + 2)..]);
            if (wordBytes == 0 || wordBytes % 2 != 0 || payloadBytes != wordBytes + 2 ||
                payload + 4 + wordBytes != record.Length || wordBytes != syllableBytes) throw Invalid();
            var decoded = new char[wordBytes / 2];
            for (var character = 0; character < decoded.Length; character++)
            {
                var encoded = BinaryPrimitives.ReadUInt16LittleEndian(record[(payload + 4 + character * 2)..]);
                decoded[character] = (char)((((encoded << 7) | (encoded >> 9)) & 0xFFFF) ^ 0x65C9);
            }
            var value = new string(decoded);
            if (RecognitionVocabularyStore.NormalizeWord(value) == value) words.Add(value); else rejected++;
        }
        var unique = words.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(unique, [], rejected, [new(SupportedFileName, count, unique.Length, 0, rejected, true)],
            "仅预览本机已有识别实体词；尚未导入，也不会自动启用联网热词。");
    }

    private static InvalidDataException Invalid() => new("词库格式、版本或记录校验未通过，未猜测解析，原文件未修改。");
    private static VocabularyImportResult Unsupported(string message) => new([], [], 0,
        [new(SupportedFileName, 0, 0, 0, 0, false)], message);
}
