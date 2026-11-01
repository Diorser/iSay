namespace iSay.Voice;

// 使用独立的自家普通用户词库，保持原版自然候选提交和热词筛选规则。
public sealed class NativeKeyboardVocabularyBackend : IKeyboardVocabularyBackend
{
    private readonly NativeVocabularyDictionary dictionary;

    public NativeKeyboardVocabularyBackend(NativeVocabularyDictionaryOptions options)
        => dictionary = new NativeVocabularyDictionary(options);

    public Task PrepareAsync(CancellationToken cancellationToken = default)
        => dictionary.PrepareAsync(cancellationToken);

    public Task<bool> CommitCandidateAsync(string reading, string word, CancellationToken cancellationToken = default)
        => dictionary.CommitCandidateAsync(reading, word, cancellationToken);

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!await dictionary.SaveAsync(cancellationToken).ConfigureAwait(false))
            throw new IOException("原生键盘学习词库保存失败。");
    }

    public async Task<IReadOnlyList<string>> GetHotwordsAsync(CancellationToken cancellationToken = default)
    {
        var result = await dictionary.GetHotwordsAsync(cancellationToken).ConfigureAwait(false);
        if (result.NativeStatus is not (2 or 3))
            throw new IOException("原生键盘词库筛选失败，保留已经确认的快照。");
        return result.Words;
    }

    public ValueTask DisposeAsync() => dictionary.DisposeAsync();
}
