namespace iSay.Voice;

public sealed record VocabularyNerRequest(string EventId, string FinalText, string AppName);
public sealed record VocabularyModifyPairRequest(string EventId, string SourceText, string TargetText, string AppName);
public sealed record VocabularyServiceResponse(bool Success, string? ResponseJson, string? ErrorCode = null);

public interface IVocabularyLearningService
{
    Task<VocabularyServiceResponse> ExtractAsync(VocabularyNerRequest request, CancellationToken cancellationToken);
}

public interface IModificationLearningService
{
    Task<VocabularyServiceResponse> ExtractModificationAsync(VocabularyModifyPairRequest request,
        CancellationToken cancellationToken);
}

[Flags]
public enum VocabularyLearningSource
{
    None = 0,
    FinalNer = 1,
    ServiceModifyPair = 2,
    ExplicitUserCorrection = 4
}

public sealed record VocabularyLearnedEntry(string Word, long Frequency, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, long NerObservationCount, long CorrectionAcceptedCount,
    long CorrectionRejectedCount, VocabularyLearningSource Sources);
public sealed record VocabularyLearningStatistics(int EntryCount, int SuppressedCount, long ProcessedEventCount);
public sealed record VocabularyLearningSnapshot(IReadOnlyList<VocabularyLearnedEntry> Entries,
    IReadOnlyList<string> SuppressedWords, VocabularyLearningStatistics Statistics);

public enum VocabularyLearningStatus
{
    Applied, AlreadyApplied, NoTerms, Disabled, Cancelled, QueueFull, ServiceFailed,
    InvalidRequest, InvalidResponse, Conflict, CapacityExceeded, StorageFailed
}

// 结果仅携带计数和固定错误分类，不携带个人语音正文或服务错误正文。
public sealed record VocabularyLearningResult(VocabularyLearningStatus Status, int AddedCount = 0,
    int UpdatedCount = 0, int DecrementedCount = 0, int RejectedCount = 0, string? ErrorCode = null);
