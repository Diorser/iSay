using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace iSay.Voice;

// 自动学词使用独立文件；原始语音、NER全文和服务身份均不持久化。
public sealed class VocabularyLearningStore
{
    public const int MaximumEntries = 5000;
    public const int MaximumSuppressedEntries = 20000;
    // 仅为旧版单文件读取上限；新版在独立分卷中精确保留历史事件，不以十万条作为终身学习上限。
    public const int MaximumProcessedEvents = 100000;
    public const int MaximumActiveProcessedEvents = 4096;
    public const int MaximumArchiveSegments = 2048;
    public const int MaximumArchiveBytes = 2 * 1024 * 1024;
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    private readonly string path;
    private readonly string mutexName;
    private readonly string archiveDirectory;
    private readonly TimeProvider timeProvider;
    private readonly object cacheGate = new();
    private CachedSnapshot? cache;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    public VocabularyLearningStore(string filePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        path = Path.GetFullPath(filePath);
        archiveDirectory = path + ".学习账本";
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        mutexName = "iSay.VocabularyLearning." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        WithDocument(PublishSnapshot, CancellationToken.None);
    }

    public IReadOnlyList<VocabularyLearnedEntry> Snapshot() => ReadSnapshot().Entries;

    public IReadOnlyList<string> SnapshotSuppressed() => ReadSnapshot().SuppressedWords;

    public VocabularyLearningStatistics GetStatistics() => ReadSnapshot().Statistics;

    public VocabularyLearningSnapshot CaptureSnapshot()
    {
        // 三种视图来自同一个不可变缓存状态，避免界面跨事务拼成互相矛盾的词表。
        var snapshot = ReadSnapshot();
        return new(snapshot.Entries, snapshot.SuppressedWords, snapshot.Statistics);
    }

    public IReadOnlyList<string> GetRecognitionVocabulary(int maximumWords = 256)
    {
        if (maximumWords is < 0 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(maximumWords));
        return Array.AsReadOnly(ReadSnapshot().RankedWords.Take(maximumWords).ToArray());
    }

    public bool Remove(string word)
    {
        var normalized = RecognitionVocabularyStore.NormalizeWord(word);
        if (normalized is null) return false;
        return WithDocument(document =>
        {
            var index = document.Entries.FindIndex(entry => SameWord(entry.Word, normalized));
            if (index < 0) return false;
            AddSuppression(document, document.Entries[index].Word);
            document.Entries.RemoveAt(index);
            Save(document, CancellationToken.None);
            return true;
        }, CancellationToken.None);
    }

    public void Clear() => WithDocument(document =>
    {
        if (document.Entries.Count == 0) return false;
        foreach (var entry in document.Entries) AddSuppression(document, entry.Word);
        document.Entries.Clear();
        Save(document, CancellationToken.None);
        return true;
    }, CancellationToken.None);

    public bool RestoreSuppressed(string word) => WithDocument(document =>
    {
        var removed = document.SuppressedWords.RemoveAll(value => SameWord(value, word));
        if (removed == 0) return false;
        Save(document, CancellationToken.None);
        return true;
    }, CancellationToken.None);

    public VocabularyLearningResult ApplyNerResponse(string eventId, string responseJson,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidEventId(eventId)) return new(VocabularyLearningStatus.InvalidRequest, ErrorCode: "invalid_event_id");
        List<LearningDelta> deltas;
        try { deltas = ParseNer(responseJson); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(VocabularyLearningStatus.InvalidResponse, ErrorCode: "invalid_ner_response"); }
        return Apply(eventId, deltas, VocabularyLearningSource.FinalNer, cancellationToken);
    }

    // 只接受已确认的服务响应 results[{src,dst}]，不从输入框手改差异猜测修改对。
    public VocabularyLearningResult ApplyModifyPairResponse(string eventId, string responseJson,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidEventId(eventId)) return new(VocabularyLearningStatus.InvalidRequest, ErrorCode: "invalid_event_id");
        List<LearningDelta> deltas;
        try { deltas = ParseModifyPairs(responseJson); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(VocabularyLearningStatus.InvalidResponse, ErrorCode: "invalid_modify_response"); }
        return Apply(eventId, deltas, VocabularyLearningSource.ServiceModifyPair, cancellationToken);
    }

    private VocabularyLearningResult Apply(string eventId, List<LearningDelta> deltas,
        VocabularyLearningSource source, CancellationToken cancellationToken)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new { Source = source, Deltas = deltas });
        var contentHash = Convert.ToHexString(SHA256.HashData(canonical));
        return WithDocument<VocabularyLearningResult>(document =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingEvent = document.Events.FirstOrDefault(entry => entry.EventId == eventId)
                ?? FindArchivedEvent(document, eventId, cancellationToken);
            if (existingEvent is not null)
                return new(existingEvent.ContentHash == contentHash ? VocabularyLearningStatus.AlreadyApplied :
                    VocabularyLearningStatus.Conflict, ErrorCode: existingEvent.ContentHash == contentHash ? null : "event_content_conflict");
            if (document.Archives.Count >= MaximumArchiveSegments && document.Events.Count >= MaximumActiveProcessedEvents)
                return new(VocabularyLearningStatus.CapacityExceeded, ErrorCode: "event_archive_capacity");
            var now = timeProvider.GetUtcNow();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var decremented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var suppressed = new HashSet<string>(document.SuppressedWords, StringComparer.OrdinalIgnoreCase);
            var next = document.Entries.ToDictionary(entry => entry.Word, StringComparer.OrdinalIgnoreCase);
            var rejected = 0;
            foreach (var delta in deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (suppressed.Contains(delta.Word)) { rejected++; continue; }
                next.TryGetValue(delta.Word, out var entry);
                if (entry is null)
                {
                    // 来源词尚不存在时不新建负计数；只为正向实体结果创建词条。
                    if (delta.Amount <= 0) continue;
                    if (next.Count >= MaximumEntries)
                        return new(VocabularyLearningStatus.CapacityExceeded, ErrorCode: "word_capacity");
                    entry = new(delta.Word, 0, now, now, 0, 0, 0, VocabularyLearningSource.None);
                    added.Add(delta.Word);
                }
                else updated.Add(entry.Word);
                var amount = delta.Amount;
                var frequency = amount >= 0 ? SaturatingAdd(entry.Frequency, amount) :
                    entry.Frequency > -amount ? entry.Frequency + amount : 0;
                if (amount < 0) decremented.Add(entry.Word);
                next[entry.Word] = entry with
                {
                    Frequency = frequency,
                    UpdatedAtUtc = now >= entry.UpdatedAtUtc ? now : entry.UpdatedAtUtc,
                    NerObservationCount = SaturatingAdd(entry.NerObservationCount, delta.NerObservations),
                    CorrectionAcceptedCount = SaturatingAdd(entry.CorrectionAcceptedCount, delta.Accepted),
                    CorrectionRejectedCount = SaturatingAdd(entry.CorrectionRejectedCount, delta.Rejected),
                    Sources = entry.Sources | source
                };
            }
            document.Entries = next.Values.ToList();
            document.Events.Add(new(eventId, contentHash, source, now));
            Save(document, cancellationToken);
            return new(added.Count + updated.Count > 0 ? VocabularyLearningStatus.Applied : VocabularyLearningStatus.NoTerms,
                added.Count, updated.Count, decremented.Count, rejected);
        }, cancellationToken);
    }

    private static List<LearningDelta> ParseNer(string json)
    {
        using var document = ParseResponse(json);
        var words = new Dictionary<string, LearningDelta>(StringComparer.OrdinalIgnoreCase);
        var examined = 0;
        foreach (var result in Results(document))
        {
            RequireObject(result);
            if (!result.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
                !result.TryGetProperty("words", out var values) || values.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("实体词响应缺少已确认字段。");
            foreach (var value in values.EnumerateArray())
            {
                if (++examined > MaximumEntries) throw new InvalidDataException("实体词响应超出容量。");
                RequireObject(value);
                var word = ReadWord(value, "word", false);
                if (!value.TryGetProperty("freq", out var frequency) || frequency.ValueKind != JsonValueKind.Number ||
                    !frequency.TryGetInt64(out var amount) || amount <= 0)
                    throw new InvalidDataException("实体词频次无效。");
                if (words.TryGetValue(word, out var existing))
                    words[word] = existing with { Amount = SaturatingAdd(existing.Amount, amount) };
                else words[word] = new(word, amount, 1, 0, 0);
            }
        }
        return words.Values.OrderBy(value => value.Word, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<LearningDelta> ParseModifyPairs(string json)
    {
        using var document = ParseResponse(json);
        var deltas = new List<LearningDelta>();
        foreach (var result in Results(document))
        {
            RequireObject(result);
            var source = ReadWord(result, "src", true);
            var target = ReadWord(result, "dst", true);
            if (source == target) continue;
            if (source.Length > 0) deltas.Add(new(source, -1, 0, 0, 1));
            if (target.Length > 0) deltas.Add(new(target, 1, 0, 1, 0));
        }
        return deltas;
    }

    private static JsonDocument ParseResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes)
            throw new InvalidDataException("学习响应为空或过大。");
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        try { ValidateUniqueProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static JsonElement.ArrayEnumerator Results(JsonDocument document)
    {
        var root = document.RootElement;
        RequireObject(root);
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() > MaximumEntries) throw new InvalidDataException("学习结果集合无效。");
        return results.EnumerateArray();
    }

    private static string ReadWord(JsonElement value, string property, bool allowEmpty)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("词条字段类型不正确。");
        var word = item.GetString()!;
        if (allowEmpty && word.Length == 0) return word;
        if (RecognitionVocabularyStore.NormalizeWord(word) != word)
            throw new InvalidDataException("词条包含无效文本或超出长度。");
        return word;
    }

    private T WithDocument<T>(Func<LearningDocument, T> action, CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try
            {
                var index = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], TimeSpan.FromSeconds(10));
                if (index == 1) throw new OperationCanceledException(cancellationToken);
                if (index == WaitHandle.WaitTimeout) throw new IOException("自动词库正忙，未覆盖文件。");
                acquired = true;
            }
            catch (AbandonedMutexException) { acquired = true; }
            cancellationToken.ThrowIfCancellationRequested();
            return action(Read());
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private LearningDocument Read()
    {
        if (!File.Exists(path)) return new();
        var bytes = VocabularyFileIO.ReadLimited(path, MaximumFileBytes);
        LearningDocument? document;
        bool hasArchives;
        try
        {
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            ValidateUniqueProperties(parsed.RootElement);
            hasArchives = parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("Archives", out _);
            document = JsonSerializer.Deserialize<LearningDocument>(bytes, JsonOptions);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("自动词库文件格式损坏，原文件未修改。", error); }
        if (document is null || document.Version is not (1 or 2) || document.Entries is null || document.Events is null ||
            document.Version == 2 && !hasArchives ||
            document.Archives is null || document.Archives.Count > MaximumArchiveSegments ||
            document.Version == 1 && document.Archives.Count != 0 ||
            document.SuppressedWords is null || document.Entries.Count > MaximumEntries ||
            document.Events.Count > (document.Version == 1 ? MaximumProcessedEvents : MaximumActiveProcessedEvents) || document.SuppressedWords.Count > MaximumSuppressedEntries ||
            document.Entries.Any(entry => entry is null || RecognitionVocabularyStore.NormalizeWord(entry.Word) != entry.Word ||
                entry.Frequency < 0 || entry.NerObservationCount < 0 || entry.CorrectionAcceptedCount < 0 ||
                entry.CorrectionRejectedCount < 0 || entry.CreatedAtUtc > entry.UpdatedAtUtc ||
                entry.Sources == VocabularyLearningSource.None || (entry.Sources & ~KnownSources) != 0) ||
            document.SuppressedWords.Any(word => RecognitionVocabularyStore.NormalizeWord(word) != word) ||
            document.Events.Any(entry => !ValidEvent(entry)) ||
            document.Archives.Any(archive => !ValidArchive(archive)) ||
            document.Archives.Select(archive => archive.Hash).Distinct(StringComparer.Ordinal).Count() != document.Archives.Count ||
            document.Entries.Select(entry => entry.Word).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Entries.Count ||
            document.SuppressedWords.Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.SuppressedWords.Count ||
            document.Events.Select(entry => entry.EventId).Distinct(StringComparer.Ordinal).Count() != document.Events.Count ||
            document.Entries.Any(entry => document.SuppressedWords.Contains(entry.Word, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("自动词库内容、版本或容量不正确，原文件未修改。");
        foreach (var archive in document.Archives)
        {
            var file = ArchivePath(archive.Hash);
            EnsureNoReparsePoints(file);
            if (!File.Exists(file) || new FileInfo(file).Length != archive.Bytes)
                throw new InvalidDataException("学习历史分卷缺失或长度不符，原词库未修改。");
        }
        return document;
    }

    private void Save(LearningDocument document, CancellationToken cancellationToken)
    {
        // 先持久化不可变分卷，再一次原子提交主文件引用；中断留下的孤卷不可见，也不参与判重。
        while (document.Events.Count > MaximumActiveProcessedEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Archives.Count >= MaximumArchiveSegments)
                throw new InvalidDataException("学习历史账本达到存储上限，原词库未修改。");
            var count = MaximumActiveProcessedEvents;
            var events = document.Events.Take(count).ToArray();
            var archiveBytes = JsonSerializer.SerializeToUtf8Bytes(new EventArchive { Events = events }, JsonOptions);
            if (archiveBytes.Length > MaximumArchiveBytes) throw new InvalidDataException("学习历史分卷超出容量，原词库未修改。");
            var hash = Convert.ToHexString(SHA256.HashData(archiveBytes));
            var target = ArchivePath(hash);
            Directory.CreateDirectory(archiveDirectory);
            EnsureNoReparsePoints(archiveDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(target)) VocabularyFileIO.WriteAtomic(target, archiveBytes, false);
            else if (!SHA256.HashData(ReadArchiveBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(archiveBytes)))
                throw new InvalidDataException("已有学习历史分卷损坏，原词库未修改。");
            EnsureNoReparsePoints(target);
            var filter = BuildFilter(events);
            document.Archives.Add(new(hash, events.Length, archiveBytes.Length, Convert.ToBase64String(filter), Convert.ToHexString(SHA256.HashData(filter))));
            document.Events.RemoveRange(0, count);
        }
        document.Version = 2;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("自动词库超出容量，原文件未修改。");
        cancellationToken.ThrowIfCancellationRequested();
        // 原子替换是事务提交点；提交开始后的取消不撤回已完成的文件替换。
        VocabularyFileIO.WriteAtomic(path, bytes, true);
        PublishSnapshot(document);
    }

    private CachedSnapshot ReadSnapshot()
    {
        var fingerprint = GetFingerprint();
        lock (cacheGate)
            if (cache is not null && cache.Fingerprint == fingerprint) return cache;
        // 只读视图按文件指纹重载；每个修改事务仍在跨进程锁内完整重读，不使用缓存做增量写入。
        return WithDocument(PublishSnapshot, CancellationToken.None);
    }

    private CachedSnapshot PublishSnapshot(LearningDocument document)
    {
        var entries = Array.AsReadOnly(document.Entries.ToArray());
        var snapshot = new CachedSnapshot(GetFingerprint(), entries,
            Array.AsReadOnly(document.SuppressedWords.ToArray()),
            document.Entries.Where(entry => entry.Frequency > 0).OrderByDescending(entry => entry.Frequency)
                .ThenByDescending(entry => entry.UpdatedAtUtc).ThenBy(entry => entry.Word, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Word).ToArray(),
            new(document.Entries.Count, document.SuppressedWords.Count,
                document.Events.Count + document.Archives.Sum(archive => (long)archive.Count)));
        lock (cacheGate) cache = snapshot;
        return snapshot;
    }

    private FileFingerprint GetFingerprint()
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists ? new(true, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : new(false, 0, 0, 0);
    }

    private static void AddSuppression(LearningDocument document, string word)
    {
        if (document.SuppressedWords.Contains(word, StringComparer.OrdinalIgnoreCase)) return;
        if (document.SuppressedWords.Count >= MaximumSuppressedEntries)
            throw new InvalidDataException("已忽略词条达到容量，原文件未修改。");
        document.SuppressedWords.Add(word);
    }

    private static void RequireObject(JsonElement value)
    { if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("响应对象类型不正确。"); }

    private static void ValidateUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("JSON包含重复字段，未处理。");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUniqueProperties(item);
        else if (value.ValueKind == JsonValueKind.String)
            _ = value.GetString();
    }

    internal static bool ValidEventId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
    private static bool SameWord(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static long SaturatingAdd(long first, long second) => first > long.MaxValue - second ? long.MaxValue : first + second;
    private static bool ValidEvent(ProcessedLearningEvent? entry) => entry is not null && ValidEventId(entry.EventId) &&
        entry.ContentHash is { Length: 64 } && entry.ContentHash.All(Uri.IsHexDigit) &&
        entry.Source is VocabularyLearningSource.FinalNer or VocabularyLearningSource.ServiceModifyPair;

    private const int FilterBytes = 8192;
    private static bool ValidArchive(ArchiveReference? archive)
    {
        if (archive is null || archive.Hash is not { Length: 64 } || !archive.Hash.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F') ||
            archive.Count is < 1 or > MaximumActiveProcessedEvents || archive.Bytes is < 1 or > MaximumArchiveBytes || archive.Filter is null ||
            archive.FilterHash is not { Length: 64 } || !archive.FilterHash.All(Uri.IsHexDigit))
            return false;
        try
        {
            var filter = Convert.FromBase64String(archive.Filter);
            return filter.Length == FilterBytes && Convert.ToHexString(SHA256.HashData(filter)) == archive.FilterHash;
        }
        catch (FormatException) { return false; }
    }

    private static byte[] BuildFilter(IEnumerable<ProcessedLearningEvent> events)
    {
        var filter = new byte[FilterBytes];
        foreach (var entry in events) FilterEvent(filter, entry.EventId, true);
        return filter;
    }

    private static bool FilterEvent(byte[] filter, string eventId, bool add)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(eventId));
        for (var index = 0; index < 8; index += 2)
        {
            var bit = hash[index] | hash[index + 1] << 8;
            var mask = (byte)(1 << (bit & 7));
            if (add) filter[bit >> 3] |= mask;
            else if ((filter[bit >> 3] & mask) == 0) return false;
        }
        return true;
    }

    private ProcessedLearningEvent? FindArchivedEvent(LearningDocument document, string eventId, CancellationToken cancellationToken)
    {
        ProcessedLearningEvent? found = null;
        foreach (var reference in document.Archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 过滤器只有否定预筛作用；命中后始终重新读取并核验不可变分卷，不以缓存代替哈希校验。
            var filter = Convert.FromBase64String(reference.Filter);
            if (!FilterEvent(filter, eventId, false)) continue;
            var bytes = ReadArchiveBytes(ArchivePath(reference.Hash));
            if (bytes.Length != reference.Bytes || Convert.ToHexString(SHA256.HashData(bytes)) != reference.Hash)
                throw new InvalidDataException("学习历史分卷校验失败，原词库未修改。");
            EventArchive? archive;
            try
            {
                using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                ValidateUniqueProperties(parsed.RootElement);
                archive = JsonSerializer.Deserialize<EventArchive>(bytes, JsonOptions);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            { throw new InvalidDataException("学习历史分卷格式损坏，原词库未修改。", error); }
            if (archive is null || archive.Version != 1 || archive.Events is null || archive.Events.Length != reference.Count ||
                archive.Events.Any(entry => !ValidEvent(entry)) ||
                archive.Events.Select(entry => entry.EventId).Distinct(StringComparer.Ordinal).Count() != archive.Events.Length ||
                !BuildFilter(archive.Events).AsSpan().SequenceEqual(filter))
                throw new InvalidDataException("学习历史分卷内容不一致，原词库未修改。");
            var candidate = archive.Events.FirstOrDefault(entry => entry.EventId == eventId);
            if (candidate is not null)
            {
                if (found is not null) throw new InvalidDataException("学习历史存在重复事件，原词库未修改。");
                found = candidate;
            }
        }
        return found;
    }

    private string ArchivePath(string hash)
    {
        if (hash.Length != 64 || !hash.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw new InvalidDataException("学习历史分卷路径无效。");
        var candidate = Path.GetFullPath(Path.Combine(archiveDirectory, hash + ".json"));
        if (!string.Equals(Path.GetDirectoryName(candidate), archiveDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("学习历史分卷超出资料目录。");
        EnsureNoReparsePoints(archiveDirectory);
        return candidate;
    }

    private static byte[] ReadArchiveBytes(string file)
    {
        EnsureNoReparsePoints(file);
        // 独占写入共享限制避免读取期间被其他写者替换为不同正文；每次仍完整校验SHA256。
        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is < 1 or > MaximumArchiveBytes) throw new InvalidDataException("学习历史分卷超出允许容量。");
        var bytes = new byte[(int)source.Length];
        source.ReadExactly(bytes);
        if (source.Length != bytes.Length) throw new IOException("学习历史分卷正在变化，原词库未修改。");
        return bytes;
    }

    private static void EnsureNoReparsePoints(string file)
    {
        for (var current = Path.GetFullPath(file); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("学习历史目录不允许符号链接或重解析点。");
    }
    private const VocabularyLearningSource KnownSources = VocabularyLearningSource.FinalNer |
        VocabularyLearningSource.ServiceModifyPair | VocabularyLearningSource.ExplicitUserCorrection;

    private sealed class LearningDocument
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public List<VocabularyLearnedEntry> Entries { get; set; } = [];
        [JsonRequired] public List<ProcessedLearningEvent> Events { get; set; } = [];
        [JsonRequired] public List<string> SuppressedWords { get; set; } = [];
        public List<ArchiveReference> Archives { get; set; } = [];
    }
    private sealed record ArchiveReference(string Hash, int Count, int Bytes, string Filter, string FilterHash);
    private sealed class EventArchive
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public ProcessedLearningEvent[] Events { get; set; } = [];
    }
    private sealed record ProcessedLearningEvent(string EventId, string ContentHash,
        VocabularyLearningSource Source, DateTimeOffset AppliedAtUtc);
    private sealed record LearningDelta(string Word, long Amount, long NerObservations, long Accepted, long Rejected);
    private sealed record FileFingerprint(bool Exists, long Length, long LastWriteTicks, long CreationTicks);
    private sealed record CachedSnapshot(FileFingerprint Fingerprint, IReadOnlyList<VocabularyLearnedEntry> Entries,
        IReadOnlyList<string> SuppressedWords, string[] RankedWords, VocabularyLearningStatistics Statistics);
}
