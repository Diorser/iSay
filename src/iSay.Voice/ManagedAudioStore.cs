namespace iSay.Voice;

/// <summary>只管理本应用数据目录下按会话编号命名的录音，清理采用可恢复的移动。</summary>
public sealed class ManagedAudioStore
{
    private readonly string root;
    public string RecordingDirectory => Path.Combine(root, "录音");
    public string ArchiveDirectory => Path.Combine(root, "录音归档");
    private readonly object gate = new();

    public ManagedAudioStore(string directory) => root = Path.GetFullPath(directory);

    public int ArchiveExpired(int retentionDays, DateTimeOffset now)
    {
        if (retentionDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(retentionDays), "录音保留时间应为1至3650天。");
        lock (gate)
        {
            EnsureDirectorySafe(root);
            if (!Directory.Exists(RecordingDirectory)) return 0;
            EnsureDirectorySafe(RecordingDirectory);
            var count = 0;
            foreach (var path in Directory.EnumerateFiles(RecordingDirectory, "*.wav", SearchOption.TopDirectoryOnly))
                if (IsManagedName(path) && File.GetLastWriteTimeUtc(path) < now.UtcDateTime.AddDays(-retentionDays) && ArchiveOne(path)) count++;
            return count;
        }
    }

    public bool ArchiveRecording(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        lock (gate)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), RecordingDirectory, StringComparison.OrdinalIgnoreCase) || !IsManagedName(path)) return false;
            return ArchiveOne(path);
        }
    }

    private bool ArchiveOne(string path)
    {
        EnsureDirectorySafe(root); EnsureDirectorySafe(RecordingDirectory);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        EnsureDirectorySafe(ArchiveDirectory);
        Directory.CreateDirectory(ArchiveDirectory);
        var target = Path.Combine(ArchiveDirectory, Path.GetFileName(path));
        // 已有归档绝不覆盖；保留源文件供用户自行处理。
        if (File.Exists(target)) return false;
        File.Move(path, target, false);
        return true;
    }

    private static bool IsManagedName(string path) =>
        string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _);

    private static void EnsureDirectorySafe(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("录音目录包含链接或重解析点，已停止管理以保留文件。");
    }
}
