using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace iSay.Voice;

// 主清单的原子替换是唯一提交点；原生资料与精确事件回执总在同一代次发布。
public sealed class KeyboardVocabularyStore : IDisposable
{
    public const int MaximumBatchSize = 64;
    public const int MaximumRecentEvents = 8192;
    public const int MaximumArchiveSegments = 4096;
    public const int MaximumArchiveBytes = 2 * 1024 * 1024;
    public const int MaximumManifestBytes = 128 * 1024 * 1024;
    public const int MaximumProfileFiles = 4096;
    public const long MaximumProfileBytes = 64L * 1024 * 1024;
    public const int MaximumProfileDirectories = 4096;
    private const int FilterBytes = 16384;
    private const int FilterRounds = 7;
    private readonly string root, manifestPath, profileRoot, archiveRoot, leaseRoot;
    private readonly object stateGate = new();
    private CancellationTokenSource operationCancellation = new();
    private bool enabled = true, disposed;
    private long generation;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 16
    };

    public KeyboardVocabularyStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        root = Path.GetFullPath(rootDirectory);
        if (root.StartsWith(@"\\", StringComparison.Ordinal) ||
            Path.TrimEndingDirectorySeparator(root) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!))
            throw new ArgumentException("键盘词库必须使用独立本地目录。", nameof(rootDirectory));
        manifestPath = Path.Combine(root, "发布.json"); profileRoot = Path.Combine(root, "配置");
        archiveRoot = Path.Combine(root, "事件分卷");
        leaseRoot = Path.Combine(root, "快照租约");
    }

    public void SetEnabled(bool value)
    {
        CancellationTokenSource? previous = null;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (enabled == value) return;
            enabled = value; generation++;
            previous = operationCancellation; operationCancellation = new();
        }
        previous.Cancel(); previous.Dispose();
    }

    public Task<KeyboardVocabularyApplyResult> ApplyBatchAsync(IReadOnlyList<KeyboardCandidateCommit> commits,
        Func<string, IKeyboardVocabularyBackend> backendFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commits); ArgumentNullException.ThrowIfNull(backendFactory);
        if (commits.Count is < 1 or > MaximumBatchSize)
            return Task.FromResult(new KeyboardVocabularyApplyResult(KeyboardVocabularyStatus.InvalidRequest, ErrorCode: "batch_size"));
        var snapshot = commits.ToArray();
        if (snapshot.Any(item => item is null || item.CommitId == Guid.Empty || item.SourceClsid == Guid.Empty ||
            !ValidText(item.Reading, 256) || !ValidText(item.Word, 64)))
            return Task.FromResult(new KeyboardVocabularyApplyResult(KeyboardVocabularyStatus.InvalidRequest, ErrorCode: "commit_fields"));
        long epoch; CancellationToken operationToken;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!enabled) return Task.FromResult(new KeyboardVocabularyApplyResult(KeyboardVocabularyStatus.Disabled));
            epoch = generation; operationToken = operationCancellation.Token;
        }
        return Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationToken);
            return await ApplyCoreAsync(snapshot, backendFactory, epoch, linked.Token).ConfigureAwait(false);
        });
    }

    public Task<KeyboardVocabularySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            using var serial = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            var document = Load(cancellationToken);
            FileStream? lease = null;
            var directory = document.Profile is null ? null : ProfilePath(document.Profile.Name);
            if (directory is not null)
            {
                VerifyProfile(document.Profile!, cancellationToken);
                var path = LeasePath(document.Profile!.Name); RejectReparse(path);
                lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            return new KeyboardVocabularySnapshot(document.Words, document.AcceptedCount, document.RejectedCount,
                ProcessedCount(document), document.Revision, directory, document.Profile?.Fingerprint ?? "", lease);
        }, cancellationToken);

    public Task<KeyboardVocabularyApplyResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource previous; CancellationToken operationToken; long epoch;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            epoch = ++generation; previous = operationCancellation; operationCancellation = new();
            operationToken = operationCancellation.Token;
        }
        previous.Cancel(); previous.Dispose();
        return Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationToken);
            try
            {
                using var serial = await AcquireAsync(linked.Token).ConfigureAwait(false);
                var document = Load(linked.Token, false);
                if (document.Revision == long.MaxValue) return Capacity("revision_capacity");
                document.Profile = null; document.Words.Clear(); document.Revision++;
                Publish(document, epoch, false, linked.Token);
                CleanupProfiles(null);
                return new KeyboardVocabularyApplyResult(KeyboardVocabularyStatus.Applied, Revision: document.Revision);
            }
            catch (OperationCanceledException) { return new KeyboardVocabularyApplyResult(KeyboardVocabularyStatus.Cancelled); }
            catch (CapacityException error) { return Capacity(error.Code); }
            catch (Exception error) when (StorageError(error))
            { return new KeyboardVocabularyApplyResult(KeyboardVocabularyStatus.StorageFailed, ErrorCode: "clear_storage"); }
        });
    }

    private async Task<KeyboardVocabularyApplyResult> ApplyCoreAsync(KeyboardCandidateCommit[] commits,
        Func<string, IKeyboardVocabularyBackend> factory, long epoch, CancellationToken token)
    {
        string? working = null; var retained = false; var backendReleased = true; long revision = 0;
        KeyboardVocabularyApplyResult result;
        try
        {
            result = await TransactAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result = new(KeyboardVocabularyStatus.Cancelled, Revision: revision); }
        catch (CapacityException error) { result = Capacity(error.Code); }
        catch (Exception error) when (StorageError(error))
        { result = new(KeyboardVocabularyStatus.StorageFailed, Revision: revision, ErrorCode: "keyboard_storage"); }
        finally
        {
            if (working is not null)
            {
                if (!backendReleased)
                {
                    retained = true;
                    // Dispose 失败时无法证明原生进程已退出；留外置标记，后续事务也不擅自回收其工作目录。
                    try { var marker = RetainedPath(Path.GetFileName(working)); if (!File.Exists(marker)) WriteDurable(marker, []); }
                    catch (Exception error) when (StorageError(error)) { }
                }
                else retained = !TryDeleteProfile(working);
            }
        }
        return result with { TemporaryProfileRetained = retained };

        async Task<KeyboardVocabularyApplyResult> TransactAsync()
        {
            using var serial = await AcquireAsync(token).ConfigureAwait(false);
            var document = Load(token); revision = document.Revision;
            var incoming = new Dictionary<Guid, (KeyboardCandidateCommit Commit, string Hash)>();
            var already = 0;
            foreach (var commit in commits)
            {
                var hash = ContentHash(commit);
                if (incoming.TryGetValue(commit.CommitId, out var same))
                {
                    if (same.Hash != hash) return new(KeyboardVocabularyStatus.Conflict, Revision: revision, ErrorCode: "event_content_conflict");
                    already++; continue;
                }
                var previous = FindReceipt(document, commit.CommitId, token);
                if (previous is not null)
                {
                    if (previous.ContentHash != hash) return new(KeyboardVocabularyStatus.Conflict, Revision: revision, ErrorCode: "event_content_conflict");
                    already++; continue;
                }
                incoming.Add(commit.CommitId, (commit, hash));
            }
            if (incoming.Count == 0) return new(KeyboardVocabularyStatus.AlreadyProcessed, AlreadyProcessedCount: already, Revision: revision);
            if (document.Revision == long.MaxValue || document.AcceptedCount > long.MaxValue - incoming.Count ||
                document.RejectedCount > long.MaxValue - incoming.Count) return Capacity("counter_capacity");
            if (ProcessedCount(document) + incoming.Count > (long)MaximumArchiveSegments * MaximumRecentEvents + MaximumRecentEvents)
                return Capacity("event_archive_capacity");
            CleanupProfiles(document.Profile?.Name);
            if (Directory.EnumerateDirectories(profileRoot).Take(MaximumProfileDirectories).Count() >= MaximumProfileDirectories)
                return Capacity("retained_profile_capacity");
            working = Path.Combine(profileRoot, "代次-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(working); RejectReparse(working);
            if (document.Profile is not null) CloneProfile(document.Profile, working, token);
            var accepted = 0; var rejected = 0;
            var receipts = new List<Receipt>();
            IReadOnlyList<string> selected;
            try
            {
                var backend = factory(working) ?? throw new InvalidOperationException("键盘候选后端为空。");
                backendReleased = false;
                try
                {
                    await backend.PrepareAsync(token).ConfigureAwait(false);
                    foreach (var entry in incoming.Values)
                    {
                        token.ThrowIfCancellationRequested();
                        var success = await backend.CommitCandidateAsync(entry.Commit.Reading, entry.Commit.Word, token).ConfigureAwait(false);
                        receipts.Add(new(entry.Commit.CommitId, entry.Hash, success));
                        if (success) accepted++; else rejected++;
                    }
                    selected = (await backend.GetHotwordsAsync(token).ConfigureAwait(false))?.ToArray()
                        ?? throw new InvalidDataException("键盘后端返回空引用词表。");
                    await backend.SaveAsync(token).ConfigureAwait(false);
                }
                finally { await backend.DisposeAsync().ConfigureAwait(false); backendReleased = true; }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return new(KeyboardVocabularyStatus.BackendFailed, Revision: revision, ErrorCode: "candidate_backend"); }
            token.ThrowIfCancellationRequested();
            if (selected.Count > 5000 || selected.Any(word => !ValidText(word, 64)) ||
                selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
                return new(KeyboardVocabularyStatus.BackendFailed, Revision: revision, ErrorCode: "backend_hotwords");
            // 只有 Save 与 Dispose 都完成才核验文件；后端不得把仍在写入的资料当成可发布快照。
            var profile = Inventory(working, token);
            if ((accepted > 0 || selected.Count > 0) && profile.Files.Count == 0)
                return new(KeyboardVocabularyStatus.BackendFailed, Revision: revision, ErrorCode: "backend_profile_empty");
            using (var lease = new FileStream(LeasePath(profile.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None)) lease.Flush(true);
            document.Profile = profile; document.Words = selected.ToList();
            document.AcceptedCount += accepted; document.RejectedCount += rejected; document.Revision++;
            document.Events.AddRange(receipts); RollArchives(document, token);
            Publish(document, epoch, true, token);
            working = null;
            CleanupProfiles(profile.Name);
            return new(KeyboardVocabularyStatus.Applied, accepted, rejected, already, document.Revision);
        }
    }

    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        RejectReparse(root); Directory.CreateDirectory(root); RejectReparse(root);
        RejectReparse(profileRoot); RejectReparse(archiveRoot); RejectReparse(leaseRoot);
        Directory.CreateDirectory(profileRoot); Directory.CreateDirectory(archiveRoot); Directory.CreateDirectory(leaseRoot);
        var path = Path.Combine(root, ".事务锁");
        var started = Environment.TickCount64;
        for (;;)
        {
            token.ThrowIfCancellationRequested(); RejectReparse(path);
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                RejectReparse(path); return stream;
            }
            catch (IOException) when (Environment.TickCount64 - started < 15000) { await Task.Delay(20, token).ConfigureAwait(false); }
        }
    }

    private Document Load(CancellationToken token, bool verifyProfile = true)
    {
        RejectReparse(manifestPath);
        if (!File.Exists(manifestPath)) return new();
        var document = Deserialize<Document>(ReadBounded(manifestPath, MaximumManifestBytes, token));
        if (document.Version != 1 || document.Revision < 0 || document.AcceptedCount < 0 || document.RejectedCount < 0 ||
            document.Words is null || document.Events is null || document.Archives is null ||
            document.Words.Count > 5000 || document.Words.Any(word => !ValidText(word, 64)) ||
            document.Words.Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Words.Count ||
            document.Events.Count > MaximumRecentEvents || document.Archives.Count > MaximumArchiveSegments)
            throw new InvalidDataException("键盘词库清单无效。");
        ValidateReceipts(document.Events);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long accepted = document.Events.LongCount(item => item.Accepted), rejected = document.Events.Count - accepted;
        foreach (var archive in document.Archives)
        {
            token.ThrowIfCancellationRequested();
            if (archive is null || !Hex(archive.Hash) || archive.Name != archive.Hash + ".json" || !names.Add(archive.Name) ||
                archive.Length is < 1 or > MaximumArchiveBytes || archive.Count != MaximumRecentEvents ||
                archive.Accepted < 0 || archive.Accepted > archive.Count || !Hex(archive.FilterHash))
                throw new InvalidDataException("键盘事件分卷引用无效。");
            var filter = DecodeFilter(archive);
            document.Filters.Add(archive.Name, filter);
            var path = ArchivePath(archive.Name); RejectReparse(path);
            if (!File.Exists(path) || new FileInfo(path).Length != archive.Length) throw new InvalidDataException("键盘事件分卷缺失或长度异常。");
            accepted = checked(accepted + archive.Accepted); rejected = checked(rejected + archive.Count - archive.Accepted);
        }
        if (accepted != document.AcceptedCount || rejected != document.RejectedCount)
            throw new InvalidDataException("键盘事件统计不一致。");
        if (document.Profile is null && document.Words.Count != 0) throw new InvalidDataException("无配置却含热词。");
        if (document.Profile is not null && verifyProfile) VerifyProfile(document.Profile, token);
        return document;
    }

    private Receipt? FindReceipt(Document document, Guid id, CancellationToken token)
    {
        var found = document.Events.FirstOrDefault(item => item.CommitId == id);
        var key = SHA256.HashData(id.ToByteArray());
        foreach (var archive in document.Archives)
        {
            token.ThrowIfCancellationRequested();
            var filter = document.Filters[archive.Name];
            if (!FilterContains(filter, key)) continue;
            if (!document.ArchiveCache.TryGetValue(archive.Name, out var receipts))
            {
                var bytes = ReadBounded(ArchivePath(archive.Name), MaximumArchiveBytes, token);
                if (bytes.Length != archive.Length || Hash(bytes) != archive.Hash) throw new InvalidDataException("键盘分卷校验失败。");
                receipts = Deserialize<List<Receipt>>(bytes); ValidateReceipts(receipts);
                if (receipts.Count != archive.Count || receipts.LongCount(item => item.Accepted) != archive.Accepted ||
                    !BuildFilter(receipts).SequenceEqual(filter)) throw new InvalidDataException("键盘分卷内容无效。");
                // 只缓存本次独占事务中已核验的分卷，最多八卷；不跨事务信任旧的磁盘身份。
                if (document.ArchiveCache.Count == 8) document.ArchiveCache.Clear();
                document.ArchiveCache.Add(archive.Name, receipts);
            }
            var match = receipts.FirstOrDefault(item => item.CommitId == id);
            if (match is null) continue;
            if (found is not null) throw new InvalidDataException("键盘事件出现重复历史记录。");
            found = match;
        }
        return found;
    }

    private void RollArchives(Document document, CancellationToken token)
    {
        while (document.Events.Count > MaximumRecentEvents)
        {
            token.ThrowIfCancellationRequested();
            if (document.Archives.Count == MaximumArchiveSegments) throw new CapacityException("event_archive_capacity");
            var events = document.Events.Take(MaximumRecentEvents).ToList();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(events, JsonOptions);
            if (bytes.Length > MaximumArchiveBytes) throw new CapacityException("archive_bytes");
            var hash = Hash(bytes); var name = hash + ".json"; var path = ArchivePath(name); RejectReparse(path);
            if (File.Exists(path))
            {
                if (!ReadBounded(path, MaximumArchiveBytes, token).SequenceEqual(bytes)) throw new InvalidDataException("同名键盘分卷损坏。");
            }
            else WriteDurable(path, bytes);
            var filter = BuildFilter(events);
            document.Archives.Add(new(name, hash, bytes.Length, events.Count, events.LongCount(item => item.Accepted),
                Convert.ToBase64String(filter), Hash(filter)));
            document.Events.RemoveRange(0, events.Count);
        }
    }

    private void Publish(Document document, long epoch, bool requireEnabled, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumManifestBytes) throw new CapacityException("manifest_bytes");
        var temporary = Path.Combine(root, "发布-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteDurable(temporary, bytes); RejectReparse(manifestPath);
            lock (stateGate)
            {
                token.ThrowIfCancellationRequested();
                if (disposed || generation != epoch || requireEnabled && !enabled) throw new OperationCanceledException(token);
                File.Move(temporary, manifestPath, true);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private Profile Inventory(string directory, CancellationToken token)
    {
        var files = EnumerateProfile(directory, token);
        var entries = new List<ProfileFile>();
        foreach (var path in files)
        {
            var bytes = ReadBounded(path, MaximumProfileBytes, token);
            entries.Add(new(Path.GetRelativePath(directory, path).Replace('\\', '/'), bytes.LongLength, Hash(bytes)));
        }
        entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return new(Path.GetFileName(directory), Hash(JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions)), entries);
    }

    private void VerifyProfile(Profile profile, CancellationToken token)
    {
        if (!ValidProfileName(profile.Name) || !Hex(profile.Fingerprint) || profile.Files is null ||
            profile.Files.Count > MaximumProfileFiles || Hash(JsonSerializer.SerializeToUtf8Bytes(profile.Files, JsonOptions)) != profile.Fingerprint)
            throw new InvalidDataException("键盘配置引用无效。");
        var path = ProfilePath(profile.Name); RejectReparse(path);
        if (!Directory.Exists(path)) throw new InvalidDataException("键盘配置目录缺失。");
        var actual = Inventory(path, token);
        if (actual.Fingerprint != profile.Fingerprint) throw new InvalidDataException("已发布键盘配置被修改。");
        var lease = LeasePath(profile.Name); RejectReparse(lease);
        if (!File.Exists(lease) || new FileInfo(lease).Length != 0) throw new InvalidDataException("键盘配置租约无效。");
    }

    private void CloneProfile(Profile profile, string destination, CancellationToken token)
    {
        VerifyProfile(profile, token);
        var source = ProfilePath(profile.Name);
        foreach (var file in profile.Files)
        {
            token.ThrowIfCancellationRequested();
            var from = Path.GetFullPath(Path.Combine(source, file.Name));
            var to = Path.GetFullPath(Path.Combine(destination, file.Name));
            Within(from, source); Within(to, destination); RejectReparse(from); RejectReparse(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!); RejectReparse(to);
            var bytes = ReadBounded(from, MaximumProfileBytes, token);
            if (bytes.LongLength != file.Length || Hash(bytes) != file.Hash) throw new InvalidDataException("克隆键盘配置时发现变更。");
            WriteDurable(to, bytes);
        }
    }

    private static List<string> EnumerateProfile(string directory, CancellationToken token)
    {
        RejectReparse(directory); var files = new List<string>(); var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0)); var examined = 0; long size = 0;
        while (pending.TryPop(out var current))
        {
            if (current.Depth > 32) throw new InvalidDataException("键盘配置过深。");
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                token.ThrowIfCancellationRequested(); Within(path, directory); RejectReparse(path);
                if (++examined > MaximumProfileFiles) throw new CapacityException("profile_files");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((path, current.Depth + 1));
                else
                {
                    var length = new FileInfo(path).Length;
                    if (length > MaximumProfileBytes || (size += length) > MaximumProfileBytes) throw new CapacityException("profile_bytes");
                    files.Add(path);
                }
            }
        }
        return files;
    }

    private void CleanupProfiles(string? keep)
    {
        try
        {
            RejectReparse(profileRoot);
            foreach (var path in Directory.EnumerateDirectories(profileRoot).Take(MaximumProfileDirectories))
                if (ValidProfileName(Path.GetFileName(path)) && Path.GetFileName(path) != keep) _ = TryDeleteProfile(path);
        }
        catch (Exception error) when (StorageError(error)) { }
    }

    private bool TryDeleteProfile(string path)
    {
        try
        {
            Within(path, profileRoot); RejectReparse(path);
            if (!ValidProfileName(Path.GetFileName(path))) return false;
            if (!Directory.Exists(path)) return true;
            var retained = RetainedPath(Path.GetFileName(path)); RejectReparse(retained);
            if (File.Exists(retained)) return false;
            var leasePath = LeasePath(Path.GetFileName(path)); RejectReparse(leasePath);
            using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var files = EnumerateProfile(path, CancellationToken.None);
            // 先证明所有正文文件可独占，避免活动后端仍写入时删除半套资料。
            var handles = new List<FileStream>();
            try { foreach (var file in files) handles.Add(new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)); }
            finally { foreach (var handle in handles) handle.Dispose(); }
            foreach (var file in files) File.Delete(file);
            var directories = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(value => value.Length).ToArray();
            foreach (var directory in directories) { RejectReparse(directory); Directory.Delete(directory); }
            lease.Dispose(); File.Delete(leasePath); Directory.Delete(path); return true;
        }
        catch (Exception error) when (StorageError(error)) { return false; }
    }

    private static byte[] BuildFilter(IEnumerable<Receipt> receipts)
    {
        var result = new byte[FilterBytes];
        foreach (var receipt in receipts)
        {
            var hash = SHA256.HashData(receipt.CommitId.ToByteArray());
            for (var index = 0; index < FilterRounds; index++)
            { var bit = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(index * 4)) % (FilterBytes * 8); result[bit / 8] |= (byte)(1 << (int)(bit % 8)); }
        }
        return result;
    }
    private static bool FilterContains(byte[] filter, byte[] hash)
    {
        for (var index = 0; index < FilterRounds; index++)
        { var bit = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(index * 4)) % (FilterBytes * 8); if ((filter[bit / 8] & (1 << (int)(bit % 8))) == 0) return false; }
        return true;
    }
    private static byte[] DecodeFilter(Archive archive)
    {
        if (archive.Filter is null || archive.Filter.Length != ((FilterBytes + 2) / 3) * 4) throw new InvalidDataException("键盘事件过滤器长度无效。");
        byte[] filter;
        try { filter = Convert.FromBase64String(archive.Filter); } catch (FormatException) { throw new InvalidDataException("键盘事件过滤器编码无效。"); }
        if (filter.Length != FilterBytes || Hash(filter) != archive.FilterHash) throw new InvalidDataException("键盘事件过滤器校验失败。");
        return filter;
    }
    private static void ValidateReceipts(List<Receipt> receipts)
    {
        if (receipts.Count > MaximumRecentEvents || receipts.Any(item => item is null || item.CommitId == Guid.Empty || !Hex(item.ContentHash)) ||
            receipts.Select(item => item.CommitId).Distinct().Count() != receipts.Count) throw new InvalidDataException("键盘事件回执无效。");
    }
    private static string ContentHash(KeyboardCandidateCommit commit)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, commit.Reading, commit.Word, commit.SourceClsid }, JsonOptions);
        return Hash(canonical);
    }
    private static long ProcessedCount(Document document) => document.AcceptedCount + document.RejectedCount;
    private static bool ValidText(string? text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text != text.Trim() || text.Any(char.IsControl)) return false;
        try { _ = StrictUtf8.GetByteCount(text); return true; } catch (EncoderFallbackException) { return false; }
    }
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool ValidProfileName(string? value) => value is { Length: 35 } && value.StartsWith("代次-", StringComparison.Ordinal) && Guid.TryParseExact(value[3..], "N", out _);
    private string ProfilePath(string name) { if (!ValidProfileName(name)) throw new InvalidDataException("非法键盘配置名。"); return Path.Combine(profileRoot, name); }
    private string LeasePath(string name) { if (!ValidProfileName(name)) throw new InvalidDataException("非法键盘租约名。"); return Path.Combine(leaseRoot, name + ".lock"); }
    private string RetainedPath(string name) { if (!ValidProfileName(name)) throw new InvalidDataException("非法键盘保留名。"); return Path.Combine(leaseRoot, name + ".待确认结束"); }
    private string ArchivePath(string name) { if (name.Length != 69 || !Hex(name[..64]) || !name.EndsWith(".json", StringComparison.Ordinal)) throw new InvalidDataException("非法键盘分卷名。"); return Path.Combine(archiveRoot, name); }
    private static void Within(string path, string directory)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("键盘词库路径越界。");
    }
    private static void RejectReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("键盘词库拒绝重解析路径。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static byte[] ReadBounded(string path, long maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RejectReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum || stream.Length > int.MaxValue) throw new CapacityException("file_bytes");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); token.ThrowIfCancellationRequested(); return bytes;
    }
    private static T Deserialize<T>(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        ValidateJson(json.RootElement);
        return json.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException("空键盘词库文档。");
    }
    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("重复键盘词库字段。"); ValidateJson(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateJson(item);
    }
    private static void WriteDurable(string path, byte[] bytes)
    {
        RejectReparse(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(true);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool StorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OverflowException;
    private static KeyboardVocabularyApplyResult Capacity(string code) => new(KeyboardVocabularyStatus.CapacityExceeded, ErrorCode: code);
    public void Dispose()
    {
        CancellationTokenSource? previous;
        lock (stateGate) { if (disposed) return; disposed = true; generation++; previous = operationCancellation; }
        previous.Cancel(); previous.Dispose();
    }
    private sealed class CapacityException(string code) : IOException { public string Code { get; } = code; }
    private sealed class Document
    {
        [JsonRequired]
        public int Version { get; set; } = 1;
        [JsonRequired]
        public long Revision { get; set; }
        [JsonRequired]
        public long AcceptedCount { get; set; }
        [JsonRequired]
        public long RejectedCount { get; set; }
        [JsonRequired]
        public List<string> Words { get; set; } = [];
        [JsonRequired]
        public Profile? Profile { get; set; }
        [JsonRequired]
        public List<Receipt> Events { get; set; } = [];
        [JsonRequired]
        public List<Archive> Archives { get; set; } = [];
        [JsonIgnore]
        public Dictionary<string, byte[]> Filters { get; } = new(StringComparer.Ordinal);
        [JsonIgnore]
        public Dictionary<string, List<Receipt>> ArchiveCache { get; } = new(StringComparer.Ordinal);
    }
    private sealed record Receipt(Guid CommitId, string ContentHash, bool Accepted);
    private sealed record Archive(string Name, string Hash, int Length, int Count, long Accepted, string Filter, string FilterHash);
    private sealed record Profile(string Name, string Fingerprint, List<ProfileFile> Files);
    private sealed record ProfileFile(string Name, long Length, string Hash);
}
