namespace iSay.Voice;

public sealed record KeyboardCandidateCommit(Guid CommitId, string Reading, string Word, Guid SourceClsid);

// 后端必须使用真正的候选提交入口；未匹配候选返回 false，不能改用管理添词。
public interface IKeyboardVocabularyBackend : IAsyncDisposable
{
    Task PrepareAsync(CancellationToken cancellationToken = default);
    Task<bool> CommitCandidateAsync(string reading, string word, CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetHotwordsAsync(CancellationToken cancellationToken = default);
}

public enum KeyboardVocabularyStatus
{
    Applied, AlreadyProcessed, Disabled, Cancelled, Conflict, InvalidRequest, CapacityExceeded, BackendFailed, StorageFailed
}

public sealed record KeyboardVocabularyApplyResult(KeyboardVocabularyStatus Status, int AcceptedCount = 0,
    int RejectedCount = 0, int AlreadyProcessedCount = 0, long Revision = 0, string? ErrorCode = null,
    bool TemporaryProfileRetained = false);

// 克隆 ProfileDirectory 期间必须保留此租约；结束后 Dispose，旧代次才可回收。
public sealed class KeyboardVocabularySnapshot : IDisposable
{
    private FileStream? lease;
    private readonly string? profileDirectory;
    private int disposed;
    internal KeyboardVocabularySnapshot(IEnumerable<string> words, long accepted, long rejected, long processed,
        long revision, string? directory, string fingerprint, FileStream? lease)
    {
        Words = Array.AsReadOnly(words.ToArray()); AcceptedCount = accepted; RejectedCount = rejected;
        ProcessedCount = processed; Revision = revision; profileDirectory = directory;
        ProfileFingerprint = fingerprint; this.lease = lease;
    }
    public IReadOnlyList<string> Words { get; }
    public long AcceptedCount { get; }
    public long RejectedCount { get; }
    public long ProcessedCount { get; }
    public long Revision { get; }
    // 元数据在释放后仍可展示，目录访问则必须处于租约有效期内；不得与复制并发 Dispose。
    public string? ProfileDirectory
    {
        get { ObjectDisposedException.ThrowIf(IsDisposed, this); return profileDirectory; }
    }
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;
    public string ProfileFingerprint { get; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref lease, null)?.Dispose();
    }
}
