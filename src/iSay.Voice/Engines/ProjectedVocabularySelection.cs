namespace iSay.Voice.Engines;

public static class ProjectedVocabularySelection
{
    public static IReadOnlyList<string> Select(NativeDictionaryVocabularyProjection projection,
        IReadOnlyList<VocabularySelectionCandidate>? learnedOverride = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var learned = learnedOverride ?? projection.LearnedCandidates;
        if (!projection.HasOriginalSelection)
            return OfficialVocabularySelection.Select(projection.ManualCandidates.Concat(learned)).Words;

        // 明确置顶是产品覆盖；原生列表内部不再按自家频次重排。NER 增强始终保留独立来源。
        var preferred = OfficialVocabularySelection.Select(projection.ManualCandidates.Where(item => item.Preferred)).Words;
        var unsupported = new HashSet<string>(projection.UnsupportedManualWords, StringComparer.OrdinalIgnoreCase);
        var supplemental = OfficialVocabularySelection.Select(projection.ManualCandidates.Where(item => unsupported.Contains(item.Word))
            .Concat(learned)).Words;
        return BoundWords(preferred.Concat(projection.OriginalWords).Concat(supplemental));
    }

    public static IReadOnlyList<string> SelectWithNativeFallback(IReadOnlyList<VocabularySelectionCandidate> candidates,
        IReadOnlyList<string> originalWords)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(originalWords);
        if (originalWords.Count == 0) return OfficialVocabularySelection.Select(candidates).Words;
        // 新的手工投影尚未完成时，仍可直接使用上一份已经证实的原生键盘词表顺序。
        var preferred = OfficialVocabularySelection.Select(candidates.Where(item => item.Source == VocabularyCandidateSource.Manual && item.Preferred)).Words;
        var remaining = OfficialVocabularySelection.Select(candidates).Words;
        return BoundWords(preferred.Concat(originalWords).Concat(remaining));
    }

    private static IReadOnlyList<string> BoundWords(IEnumerable<string> candidates)
    {
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shortUnits = 0;
        var extendedUnits = 0;
        foreach (var candidate in candidates)
        {
            if (!AsrVocabularyContext.TryNormalizeWord(candidate, out var word) || !seen.Add(word)) continue;
            if (words.Count == AsrVocabularyContext.MaximumWords) break;
            var shortWord = word.Length is >= 2 and <= 4;
            if (word.Length > (shortWord ? OfficialVocabularySelection.ShortBatchUtf16Budget - shortUnits
                : OfficialVocabularySelection.ExtendedBatchUtf16Budget - extendedUnits)) continue;
            words.Add(word);
            if (shortWord) shortUnits += word.Length; else extendedUnits += word.Length;
        }
        return words.AsReadOnly();
    }
}
