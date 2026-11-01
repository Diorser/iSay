using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using iSay.Voice.Engines;

namespace iSay.Voice;

public sealed class NativeDictionaryVocabularyProjection
{
    internal NativeDictionaryVocabularyProjection(IEnumerable<string> originalWords, IEnumerable<string> unsupportedManualWords,
        IEnumerable<VocabularySelectionCandidate> manualCandidates, IEnumerable<VocabularySelectionCandidate> learnedCandidates,
        string fingerprint, uint? nativeStatus, string? failureCode, bool retained)
    {
        OriginalWords = Array.AsReadOnly(originalWords.ToArray());
        UnsupportedManualWords = Array.AsReadOnly(unsupportedManualWords.ToArray());
        ManualCandidates = Array.AsReadOnly(manualCandidates.ToArray());
        LearnedCandidates = Array.AsReadOnly(learnedCandidates.ToArray());
        SourceFingerprint = fingerprint;
        NativeStatus = nativeStatus;
        FailureCode = failureCode;
        TemporaryDataRetained = retained;
    }

    public IReadOnlyList<string> OriginalWords { get; }
    public IReadOnlyList<string> UnsupportedManualWords { get; }
    public IReadOnlyList<VocabularySelectionCandidate> ManualCandidates { get; }
    public IReadOnlyList<VocabularySelectionCandidate> LearnedCandidates { get; }
    public bool HasOriginalSelection => NativeStatus == 2 && OriginalWords.Count > 0 && FailureCode is null;
    public string SourceFingerprint { get; }
    public uint? NativeStatus { get; }
    public string? FailureCode { get; }
    public bool TemporaryDataRetained { get; }
}

// 只在自家可重建临时词典中投影手工来源，不把NER词伪装成普通用户词或伪造首批门票。
public sealed class NativeDictionaryVocabularyProjector
{
    public const int MaximumCandidatesPerSource = 5000;
    private const long MaximumCleanupBytes = 64L * 1024 * 1024;
    private readonly Func<string, NativeVocabularyDictionaryOptions> createOptions;
    private readonly string projectionRoot;

    public NativeDictionaryVocabularyProjector(Func<string, NativeVocabularyDictionaryOptions> createOptions, string projectionRoot)
    {
        this.createOptions = createOptions ?? throw new ArgumentNullException(nameof(createOptions));
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionRoot);
        this.projectionRoot = Path.GetFullPath(projectionRoot);
        if (this.projectionRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
            Path.TrimEndingDirectorySeparator(this.projectionRoot) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(this.projectionRoot)!))
            throw new ArgumentException("词典投影必须指定独立的本地工作目录。", nameof(projectionRoot));
    }

    public Task<NativeDictionaryVocabularyProjection> BuildAsync(IReadOnlyList<VocabularySelectionCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureCandidates(candidates);
        cancellationToken.ThrowIfCancellationRequested();
        // 指纹、目录与原生请求均在后台执行；调用者自行负责调度和结果缓存。
        return Task.Run(() => BuildCoreAsync(snapshot, null, cancellationToken), cancellationToken);
    }

    public Task<NativeDictionaryVocabularyProjection> BuildAsync(IReadOnlyList<VocabularySelectionCandidate> candidates,
        KeyboardVocabularySnapshot keyboardSnapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyboardSnapshot);
        ObjectDisposedException.ThrowIf(keyboardSnapshot.IsDisposed, keyboardSnapshot);
        var snapshot = CaptureCandidates(candidates);
        cancellationToken.ThrowIfCancellationRequested();
        // 调用期间由调用方持有快照租约，后台复制不能追着正在改变的词库读取。
        return Task.Run(() => BuildCoreAsync(snapshot, keyboardSnapshot, cancellationToken), cancellationToken);
    }

    public static string ComputeSourceFingerprint(IReadOnlyList<VocabularySelectionCandidate> candidates)
        => Fingerprint(CaptureCandidates(candidates));

    private async Task<NativeDictionaryVocabularyProjection> BuildCoreAsync(VocabularySelectionCandidate[] snapshot,
        KeyboardVocabularySnapshot? keyboardSnapshot, CancellationToken cancellationToken)
    {
        if (keyboardSnapshot is not null) ObjectDisposedException.ThrowIf(keyboardSnapshot.IsDisposed, keyboardSnapshot);
        var fingerprint = Fingerprint(snapshot);
        if (keyboardSnapshot is not null)
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint + ":" + keyboardSnapshot.Revision + ":" + keyboardSnapshot.ProfileFingerprint)));
        var manual = Normalize(snapshot, VocabularyCandidateSource.Manual);
        var learned = Normalize(snapshot, VocabularyCandidateSource.Learned);
        cancellationToken.ThrowIfCancellationRequested();
        if (manual.Count == 0 && keyboardSnapshot is not null)
        {
            // 没有手工覆盖时，事务快照已经保存了真实 getter 结果，无需为每个打字批次重载整个词典。
            return new(keyboardSnapshot.Words, [], manual, learned, fingerprint,
                keyboardSnapshot.Words.Count == 0 ? (uint)3 : 2, null, false);
        }
        if (manual.Count == 0 && keyboardSnapshot?.ProfileDirectory is null) return new([], [], manual, learned, fingerprint, null, null, false);

        var temporary = Path.Combine(projectionRoot, "投影-" + Guid.NewGuid().ToString("N"));
        var created = false;
        var retained = false;
        IReadOnlyList<string> original = [];
        var unsupported = new List<string>();
        uint? nativeStatus = null;
        string? failureCode = null;
        try
        {
            RejectReparseAncestors(projectionRoot);
            Directory.CreateDirectory(projectionRoot);
            RejectReparseAncestors(projectionRoot);
            if (Directory.Exists(temporary) || File.Exists(temporary)) throw new IOException("词典投影临时目录已被占用。");
            Directory.CreateDirectory(temporary);
            created = true;
            RejectReparseAncestors(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            if (keyboardSnapshot?.ProfileDirectory is { } sourceProfile)
                CopyKeyboardBase(sourceProfile, temporary, cancellationToken);
            var options = createOptions(temporary) ?? throw new IOException("词典投影缺少运行选项。");
            if (!string.Equals(Path.GetFullPath(options.DataDirectory), temporary, StringComparison.OrdinalIgnoreCase))
                throw new IOException("词典投影不能改写其他资料目录。");
            await using (var dictionary = new NativeVocabularyDictionary(options))
            {
                await dictionary.PrepareAsync(cancellationToken).ConfigureAwait(false);
                foreach (var candidate in manual)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var added = candidate.Word.Length <= 4
                        ? await dictionary.AddUserWordAsync(candidate.Word, cancellationToken).ConfigureAwait(false)
                        : await dictionary.AddCommonPhraseAsync("", candidate.Word, cancellationToken).ConfigureAwait(false);
                    // 不把计数循环写成使用次数；每个手工词只投影一次，筛选顺序完整交给真实getter。
                    if (!added) unsupported.Add(candidate.Word);
                }
                var selected = await dictionary.GetHotwordsAsync(cancellationToken).ConfigureAwait(false);
                nativeStatus = selected.NativeStatus;
                if (selected.Available) original = selected.Words;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { failureCode = "native_dictionary_projection_timeout"; original = []; nativeStatus = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            JsonException or DecoderFallbackException or System.ComponentModel.Win32Exception)
        { failureCode = "native_dictionary_projection_unavailable"; original = []; nativeStatus = null; }
        finally
        {
            if (created) retained = !TryCleanup(temporary);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(original, unsupported, manual, learned, fingerprint, nativeStatus, failureCode, retained);
    }

    private static void CopyKeyboardBase(string sourceProfile, string destination, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourceProfile);
        if (source.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(source) == source ||
            source.Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("键盘词库基础目录无效。");
        RejectReparseAncestors(source);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((source, 0));
        var examined = 0;
        long bytes = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++examined > NativeVocabularyDictionary.MaximumDataEntries) throw new IOException("键盘词库文件数量超过投影上限。");
                RejectReparseAncestors(entry);
                var relative = Path.GetRelativePath(source, entry);
                var target = Path.GetFullPath(Path.Combine(destination, relative));
                if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("键盘词库复制超出投影目录。");
                if (Directory.Exists(entry))
                {
                    if (current.Depth >= NativeVocabularyDictionary.MaximumDataDepth) throw new IOException("键盘词库目录过深。");
                    Directory.CreateDirectory(target);
                    pending.Push((entry, current.Depth + 1));
                }
                else
                {
                    if (relative.Equals("iSay词典占用.lock", StringComparison.OrdinalIgnoreCase)) continue;
                    bytes += new FileInfo(entry).Length;
                    if (bytes > MaximumCleanupBytes) throw new IOException("键盘词库超过投影容量。");
                    File.Copy(entry, target, false);
                }
            }
        }
    }

    private static VocabularySelectionCandidate[] CaptureCandidates(IReadOnlyList<VocabularySelectionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > MaximumCandidatesPerSource * 2) throw new ArgumentException("词典投影候选超过数量上限。", nameof(candidates));
        var snapshot = candidates.ToArray();
        var manual = 0;
        var learned = 0;
        long characterCount = 0;
        foreach (var item in snapshot)
        {
            if (item is null || !Enum.IsDefined(item.Source)) throw new ArgumentException("词典投影候选来源无效。", nameof(candidates));
            characterCount += item.Word?.Length ?? 0;
            if (characterCount > 1024 * 1024) throw new ArgumentException("词典投影来源正文超过容量。", nameof(candidates));
            if (item.Source == VocabularyCandidateSource.Manual) manual++; else learned++;
        }
        if (manual > MaximumCandidatesPerSource || learned > MaximumCandidatesPerSource)
            throw new ArgumentException("每类词典投影候选最多5000项。", nameof(candidates));
        return snapshot;
    }

    private static IReadOnlyList<VocabularySelectionCandidate> Normalize(VocabularySelectionCandidate[] snapshot, VocabularyCandidateSource source)
    {
        var normalized = new Dictionary<string, VocabularySelectionCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot)
        {
            if (item.Source != source || source == VocabularyCandidateSource.Learned && item.Frequency <= 0) continue;
            var word = RecognitionVocabularyStore.NormalizeWord(item.Word);
            if (word is null) continue;
            var candidate = item with { Word = word, Frequency = Math.Max(1, item.Frequency) };
            if (!normalized.TryGetValue(word, out var previous)) { normalized.Add(word, candidate); continue; }
            normalized[word] = new(candidate.Preferred && !previous.Preferred ? candidate.Word : previous.Word,
                previous.Frequency > long.MaxValue - candidate.Frequency ? long.MaxValue : previous.Frequency + candidate.Frequency,
                Nullable.Compare(previous.LastUpdatedAtUtc, candidate.LastUpdatedAtUtc) >= 0 ? previous.LastUpdatedAtUtc : candidate.LastUpdatedAtUtc,
                previous.Preferred || candidate.Preferred, source);
        }
        return Array.AsReadOnly(normalized.Values.ToArray());
    }

    private static string Fingerprint(VocabularySelectionCandidate[] snapshot)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("iSay.DictionaryProjection.1");
            writer.Write(snapshot.Length);
            foreach (var candidate in snapshot)
            {
                // 逐UTF16单元编码，连无效输入的原始单元也区分，不因替换字符产生指纹碰撞。
                writer.Write(candidate.Word is not null);
                if (candidate.Word is not null)
                {
                    writer.Write(candidate.Word.Length);
                    foreach (var character in candidate.Word) writer.Write((ushort)character);
                }
                writer.Write(candidate.Frequency);
                writer.Write((int)candidate.Source);
                writer.Write(candidate.Preferred);
                writer.Write(candidate.LastUpdatedAtUtc.HasValue);
                if (candidate.LastUpdatedAtUtc.HasValue) writer.Write(candidate.LastUpdatedAtUtc.Value.UtcTicks);
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    private bool TryCleanup(string temporary)
    {
        try
        {
            if (!string.Equals(Path.GetDirectoryName(temporary), projectionRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(temporary).StartsWith("投影-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(temporary)[3..], "N", out _)) return false;
            RejectReparseAncestors(temporary);
            var lease = Path.Combine(temporary, "iSay词典占用.lock");
            if (File.Exists(lease))
            {
                RejectReparseAncestors(lease);
                // Dispose后仍被持有意味着无法证实子进程退出；此时保留工作目录。
                using var available = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            var files = new List<string>();
            var directories = new List<string>();
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((temporary, 0));
            long totalBytes = 0;
            while (pending.TryPop(out var current))
            {
                directories.Add(current.Path);
                foreach (var file in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    if (files.Count + directories.Count + pending.Count >= NativeVocabularyDictionary.MaximumDataEntries) return false;
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (current.Depth >= NativeVocabularyDictionary.MaximumDataDepth) return false;
                        pending.Push((file, current.Depth + 1));
                    }
                    else
                    {
                        totalBytes += new FileInfo(file).Length;
                        if (totalBytes > MaximumCleanupBytes) return false;
                        files.Add(file);
                    }
                }
            }
            // 删除逐项执行且每项复查路径；不对递归删除传入外部计算路径。
            foreach (var file in files) { RejectReparseAncestors(file); File.Delete(file); }
            foreach (var directory in directories.AsEnumerable().Reverse()) { RejectReparseAncestors(directory); Directory.Delete(directory, false); }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static void RejectReparseAncestors(string target)
    {
        for (var current = Path.GetFullPath(target); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("词典投影目录不允许重解析点。");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }
}
