using System.Text.Json;

namespace iSay.Updates;

public sealed record UpdateInstallResult(string Version, long Sequence, string TransactionDirectory);

public static class ApplicationUpdateInstaller
{
    public static async Task<UpdateInstallResult> InstallAsync(UpdateConfiguration configuration, UpdateInstallRequest request,
        string installDirectory, string currentVersion, CancellationToken cancellationToken = default)
    {
        var root = UpdateSafety.PrepareDirectory(installDirectory);
        if (Path.GetPathRoot(root)!.TrimEnd('\\', '/').Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新安装目录不能是磁盘根目录。");
        var stateRoot = UpdateSafety.PrepareDirectory(Path.Combine(root, ".update-state"));
        var lockPath = Path.Combine(stateRoot, "install.lock");
        UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(root)!, lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await RecoverCoreAsync(root, cancellationToken);
        var statePath = Path.Combine(stateRoot, "installed.json");
        var previous = ReadState(statePath);
        var effective = JsonSerializer.Deserialize<UpdateConfiguration>(JsonSerializer.Serialize(configuration, UpdateJson.Options), UpdateJson.Options)!;
        effective.MinimumSequence = Math.Max(effective.MinimumSequence, previous?.Sequence ?? 0);
        if (!File.Exists(request.EnvelopePath) || new FileInfo(request.EnvelopePath).Length > ManifestCodec.MaximumEnvelopeBytes)
            throw new InvalidDataException("待安装清单不存在或超出上限。");
        var release = ManifestCodec.Verify(await File.ReadAllTextAsync(request.EnvelopePath, cancellationToken), effective);
        if (release.Manifest.Sequence <= effective.MinimumSequence) throw new InvalidDataException("安装更新的序号必须递增。");
        var version = UpdateVersions.Parse(release.Manifest.Version);
        if (version <= UpdateVersions.Parse(currentVersion) || previous is not null && version <= UpdateVersions.Parse(previous.Version))
            throw new InvalidDataException("更新不能安装相同或更旧版本。");
        if (request.PackagePaths is null || request.PackagePaths.Count != release.Manifest.Components.Count)
            throw new InvalidDataException("更新包数量与签名清单不符。");
        var transactionRoot = UpdateSafety.PrepareDirectory(Path.Combine(stateRoot, "transactions"));
        var transaction = UpdateSafety.PrepareDirectory(Path.Combine(transactionRoot, Guid.NewGuid().ToString("N")));
        var stage = UpdateSafety.PrepareDirectory(Path.Combine(transaction, "stage"));
        var backup = UpdateSafety.PrepareDirectory(Path.Combine(transaction, "backup"));
        var journal = new InstallJournal { Version = release.Manifest.Version, Sequence = release.Manifest.Sequence, PreviousState = previous };
        try
        {
            foreach (var component in release.Manifest.Components)
            {
                if (!request.PackagePaths.TryGetValue(component.Id, out var package)) throw new InvalidDataException("缺少更新组件包。");
                await SafeArchive.ExtractAsync(component, package, stage, effective, cancellationToken);
                foreach (var file in component.Files)
                {
                    var target = UpdateSafety.ResolveInside(root, file.Path);
                    if (Directory.Exists(target)) throw new InvalidDataException("更新目标被目录占用。");
                    var original = File.Exists(target);
                    journal.Files.Add(new JournalFile
                    {
                        Path = file.Path, Size = file.Size, Sha256 = file.Sha256, HadOriginal = original,
                        OriginalSize = original ? new FileInfo(target).Length : 0,
                        OriginalSha256 = original ? await HashFileAsync(target, cancellationToken) : null
                    });
                }
            }
            journal.Status = "Installing";
            await SaveJournalAsync(transaction, journal, cancellationToken);
        }
        catch
        {
            TryDeleteTree(transactionRoot, transaction);
            throw;
        }
        try
        {
            foreach (var file in journal.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = UpdateSafety.ResolveInside(root, file.Path);
                var saved = UpdateSafety.ResolveInside(backup, file.Path);
                var source = UpdateSafety.ResolveInside(stage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (file.HadOriginal)
                {
                    await UpdateSafety.VerifyFileAsync(target, file.OriginalSize, file.OriginalSha256!, cancellationToken);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    // 移走旧目录项后落位新文件，不覆盖写入可能指向别处的硬链接。
                    File.Move(target, saved);
                }
                File.Move(source, target);
            }
            journal.Status = "Committing";
            await SaveJournalAsync(transaction, journal, CancellationToken.None);
            var installed = new InstalledState { Version = journal.Version, Sequence = journal.Sequence };
            await UpdateSafety.WriteTextAtomicAsync(statePath, JsonSerializer.Serialize(installed, UpdateJson.Options), CancellationToken.None);
            journal.Status = "Completed";
            await SaveJournalAsync(transaction, journal, CancellationToken.None);
            PruneCompletedTransactions(transactionRoot, transaction);
            return new(journal.Version, journal.Sequence, transaction);
        }
        catch (Exception failure)
        {
            try { await RollbackAsync(root, transaction, journal); }
            catch (Exception rollback) { throw new AggregateException("应用更新失败，自动回滚未完成；已保留备份与事务记录，需恢复后再更新。", failure, rollback); }
            throw new IOException("应用更新失败，已恢复原文件；未结束任何其他进程。", failure);
        }
    }

    public static async Task RecoverAsync(string installDirectory, CancellationToken cancellationToken = default)
    {
        var root = UpdateSafety.PrepareDirectory(installDirectory);
        var state = UpdateSafety.PrepareDirectory(Path.Combine(root, ".update-state"));
        var lockPath = Path.Combine(state, "install.lock");
        UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(root)!, lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await RecoverCoreAsync(root, cancellationToken);
    }

    public static long ReadInstalledSequence(string installDirectory) => ReadState(Path.Combine(installDirectory, ".update-state", "installed.json"))?.Sequence ?? 0;

    private static async Task RecoverCoreAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(installDirectory);
        var transactions = Path.Combine(root, ".update-state", "transactions");
        UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(root)!, transactions);
        if (!Directory.Exists(transactions)) return;
        foreach (var directory in Directory.EnumerateDirectories(transactions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new InvalidDataException("更新事务目录名称无效。");
            var journalPath = Path.Combine(directory, "journal.json");
            UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(root)!, journalPath);
            if (!File.Exists(journalPath)) continue;
            if (new FileInfo(journalPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("更新事务记录超出限额。");
            var journal = JsonSerializer.Deserialize<InstallJournal>(await File.ReadAllTextAsync(journalPath, cancellationToken), UpdateJson.Options)
                ?? throw new InvalidDataException("更新事务记录为空。");
            ValidateJournal(journal);
            if (journal.Status is "Completed" or "RolledBack") continue;
            var state = ReadState(Path.Combine(root, ".update-state", "installed.json"));
            if (journal.Status == "Committing" && state?.Sequence == journal.Sequence)
            {
                foreach (var file in journal.Files)
                    await UpdateSafety.VerifyFileAsync(UpdateSafety.ResolveInside(root, file.Path), file.Size, file.Sha256, cancellationToken);
                journal.Status = "Completed";
                await SaveJournalAsync(directory, journal, cancellationToken);
            }
            else await RollbackAsync(root, directory, journal);
        }
    }

    private static async Task RollbackAsync(string root, string transaction, InstallJournal journal)
    {
        ValidateJournal(journal);
        journal.Status = "RollingBack";
        await SaveJournalAsync(transaction, journal, CancellationToken.None);
        foreach (var file in journal.Files.AsEnumerable().Reverse())
        {
            var target = UpdateSafety.ResolveInside(root, file.Path);
            var saved = UpdateSafety.ResolveInside(Path.Combine(transaction, "backup"), file.Path);
            if (File.Exists(saved))
            {
                if (!file.HadOriginal) throw new InvalidDataException("新增文件出现不应存在的旧版备份。");
                await UpdateSafety.VerifyFileAsync(saved, file.OriginalSize, file.OriginalSha256!, CancellationToken.None);
                if (File.Exists(target))
                {
                    await UpdateSafety.VerifyFileAsync(target, file.Size, file.Sha256, CancellationToken.None);
                    File.Delete(target);
                }
                File.Move(saved, target);
            }
            else if (!file.HadOriginal && File.Exists(target))
            {
                await UpdateSafety.VerifyFileAsync(target, file.Size, file.Sha256, CancellationToken.None);
                File.Delete(target);
            }
            else if (file.HadOriginal)
                await UpdateSafety.VerifyFileAsync(target, file.OriginalSize, file.OriginalSha256!, CancellationToken.None);
        }
        var statePath = Path.Combine(root, ".update-state", "installed.json");
        var state = ReadState(statePath);
        if (state?.Sequence == journal.Sequence)
        {
            if (journal.PreviousState is null) File.Delete(statePath);
            else await UpdateSafety.WriteTextAtomicAsync(statePath, JsonSerializer.Serialize(journal.PreviousState, UpdateJson.Options), CancellationToken.None);
        }
        journal.Status = "RolledBack";
        await SaveJournalAsync(transaction, journal, CancellationToken.None);
        TryDeleteTree(transaction, Path.Combine(transaction, "stage"));
    }

    private static InstalledState? ReadState(string path)
    {
        UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(Path.GetFullPath(path))!, path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("已安装更新状态超出限额。");
        var state = JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(path), UpdateJson.Options) ?? throw new InvalidDataException("已安装更新状态为空。");
        if (state.Sequence < 1) throw new InvalidDataException("已安装更新序号无效。");
        UpdateVersions.Parse(state.Version);
        return state;
    }

    private static void ValidateJournal(InstallJournal journal)
    {
        if (journal.Status is not ("Installing" or "Committing" or "RollingBack" or "Completed" or "RolledBack") || journal.Sequence < 1 || journal.Files is null || journal.Files.Count > 20000)
            throw new InvalidDataException("更新事务状态无效。");
        UpdateVersions.Parse(journal.Version);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in journal.Files)
        {
            if (file is null || file.Size < 0 || !paths.Add(file.Path)) throw new InvalidDataException("更新事务文件无效。");
            UpdateSafety.ValidateApplicationPath(file.Path);
            UpdateSafety.RequireHash(file.Sha256);
            if (file.HadOriginal)
            {
                if (file.OriginalSize < 0) throw new InvalidDataException("原文件长度无效。");
                UpdateSafety.RequireHash(file.OriginalSha256!);
            }
        }
    }

    private static void PruneCompletedTransactions(string root, string keep)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (directory.Equals(keep, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                try
                {
                    var path = Path.Combine(directory, "journal.json");
                    UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(root)!, path);
                    if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) continue;
                    var old = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(path), UpdateJson.Options);
                    if (old?.Status == "Completed") TryDeleteTree(root, directory);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteTree(string root, string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新清理目录越界。");
            UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(full)!, full);
            if (!Directory.Exists(full)) return;
            CheckTree(full);
            Directory.Delete(full, true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }

        static void CheckTree(string current)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新清理不允许经过链接。");
                if ((attributes & FileAttributes.Directory) != 0) CheckTree(entry);
            }
        }
    }

    private static Task SaveJournalAsync(string transaction, InstallJournal journal, CancellationToken token) =>
        UpdateSafety.WriteTextAtomicAsync(Path.Combine(transaction, "journal.json"), JsonSerializer.Serialize(journal, UpdateJson.Options), token);

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token));
    }

    private sealed class InstalledState
    {
        public string Version { get; set; } = "";
        public long Sequence { get; set; }
    }
    private sealed class InstallJournal
    {
        public string Status { get; set; } = "Installing";
        public string Version { get; set; } = "";
        public long Sequence { get; set; }
        public InstalledState? PreviousState { get; set; }
        public List<JournalFile> Files { get; set; } = [];
    }
    private sealed class JournalFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public bool HadOriginal { get; set; }
        public long OriginalSize { get; set; }
        public string? OriginalSha256 { get; set; }
    }
}

public static class UpdateVersions
{
    public static Version Parse(string version)
    {
        if (!Version.TryParse(version, out var parsed)) throw new InvalidDataException("更新版本号无效。");
        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
    }
}
