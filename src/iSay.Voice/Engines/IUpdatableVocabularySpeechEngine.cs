namespace iSay.Voice.Engines;

public interface IUpdatableVocabularySpeechEngine
{
    // 只在没有录音的段间更新下一段词表；不支持或正在录音时返回false，调用方可择机完整重建。
    Task<bool> TryUpdateVocabularyAsync(IReadOnlyList<string> words, CancellationToken cancellationToken = default);
}
