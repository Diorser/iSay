namespace iSay.Voice.Engines;

public enum VocabularyCandidateSource
{
    Manual,
    Learned
}

public sealed record VocabularySelectionCandidate(
    string Word,
    long Frequency = 1,
    DateTimeOffset? LastUpdatedAtUtc = null,
    bool Preferred = false,
    VocabularyCandidateSource Source = VocabularyCandidateSource.Manual);

public sealed class VocabularySelectionResult
{
    internal VocabularySelectionResult(IEnumerable<VocabularySelectionCandidate> items, int examinedCount,
        int rejectedCount, bool inputTruncated)
    {
        var copy = items.ToArray();
        Items = Array.AsReadOnly(copy);
        Words = Array.AsReadOnly(copy.Select(item => item.Word).ToArray());
        ExaminedCount = examinedCount;
        RejectedCount = rejectedCount;
        InputTruncated = inputTruncated;
        ShortUtf16Units = copy.Where(item => item.Word.Length is >= 2 and <= 4).Sum(item => item.Word.Length);
        ExtendedUtf16Units = copy.Where(item => item.Word.Length is < 2 or > 4).Sum(item => item.Word.Length);
    }

    public IReadOnlyList<VocabularySelectionCandidate> Items { get; }
    public IReadOnlyList<string> Words { get; }
    public int ExaminedCount { get; }
    public int RejectedCount { get; }
    public bool InputTruncated { get; }
    public int ShortUtf16Units { get; }
    public int ExtendedUtf16Units { get; }
    public string PolicyDescription => OfficialVocabularySelection.PolicyDescription;

    internal VocabularySelectionResult KeepPrefix(int count) => new(Items.Take(count), ExaminedCount,
        RejectedCount + Items.Count - count, InputTruncated);
}

public static class OfficialVocabularySelection
{
    public const int ShortBatchUtf16Budget = 500;
    public const int ExtendedBatchUtf16Budget = 500;
    public const int MaximumLearnedWordLength = 50;
    public const int MaximumCandidates = 8192;
    public const string PolicyDescription = "字段与两批UTF16长度预算采用已确认的官方路径；候选来源、字符过滤、优先/手工/频次/更新时间排序为iSay自家策略，未宣称等同官方精确权重。短批按长度2–4分类，扩展批保留单字、英文及手工最长64单元；首批为空仍可用扩展词。";

    public static VocabularySelectionResult Select(IEnumerable<VocabularySelectionCandidate>? candidates)
    {
        if (candidates is null) return new([], 0, 0, false);
        var merged = new Dictionary<string, RankedCandidate>(StringComparer.OrdinalIgnoreCase);
        var examined = 0;
        var rejected = 0;
        var truncated = false;
        foreach (var candidate in candidates)
        {
            if (examined == MaximumCandidates) { truncated = true; break; }
            var index = examined++;
            if (candidate is null || !Enum.IsDefined(candidate.Source)
                || candidate.Source == VocabularyCandidateSource.Learned && candidate.Frequency <= 0
                || !AsrVocabularyContext.TryNormalizeWord(candidate.Word, out var word)
                || candidate.Source == VocabularyCandidateSource.Learned && word.Length > MaximumLearnedWordLength)
            {
                rejected++;
                continue;
            }
            var normalized = candidate with { Word = word, Frequency = Math.Max(1, candidate.Frequency) };
            if (!merged.TryGetValue(word, out var existing))
            {
                merged.Add(word, new(normalized, index));
                continue;
            }
            // 同词的计数饱和相加；更新时间只表示本地更新，不能冒称用户最近使用时间。
            var prior = existing.Candidate;
            var preferred = prior.Preferred || normalized.Preferred;
            var manual = prior.Source == VocabularyCandidateSource.Manual || normalized.Source == VocabularyCandidateSource.Manual;
            var spelling = normalized.Source == VocabularyCandidateSource.Manual && prior.Source != VocabularyCandidateSource.Manual
                || normalized.Preferred && !prior.Preferred ? normalized.Word : prior.Word;
            var frequency = prior.Frequency > long.MaxValue - normalized.Frequency ? long.MaxValue : prior.Frequency + normalized.Frequency;
            var updated = Nullable.Compare(prior.LastUpdatedAtUtc, normalized.LastUpdatedAtUtc) >= 0
                ? prior.LastUpdatedAtUtc : normalized.LastUpdatedAtUtc;
            merged[word] = new(new(spelling, frequency, updated, preferred,
                manual ? VocabularyCandidateSource.Manual : VocabularyCandidateSource.Learned), existing.Index);
            rejected++;
        }

        var shortBatch = new List<VocabularySelectionCandidate>();
        var extendedBatch = new List<VocabularySelectionCandidate>();
        var shortUnits = 0;
        var extendedUnits = 0;
        foreach (var ranked in merged.Values.OrderByDescending(item => item.Candidate.Preferred)
            .ThenBy(item => item.Candidate.Source)
            .ThenByDescending(item => item.Candidate.Frequency)
            .ThenByDescending(item => item.Candidate.LastUpdatedAtUtc)
            .ThenBy(item => item.Index))
        {
            var item = ranked.Candidate;
            var shortWord = item.Word.Length is >= 2 and <= 4;
            var used = shortWord ? shortUnits : extendedUnits;
            var limit = shortWord ? ShortBatchUtf16Budget : ExtendedBatchUtf16Budget;
            if (shortBatch.Count + extendedBatch.Count == AsrVocabularyContext.MaximumWords || item.Word.Length > limit - used)
            {
                // 超预算后继续寻找可容纳的词是自家策略，不冒称官方提前退出分支。
                rejected++;
                continue;
            }
            if (shortWord) { shortBatch.Add(item); shortUnits += item.Word.Length; }
            else { extendedBatch.Add(item); extendedUnits += item.Word.Length; }
        }
        return new(shortBatch.Concat(extendedBatch), examined, rejected, truncated);
    }

    private sealed record RankedCandidate(VocabularySelectionCandidate Candidate, int Index);
}
