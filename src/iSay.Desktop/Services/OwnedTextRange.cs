using System.Globalization;

namespace iSay.Desktop.Services;

/// <summary>记录本次语音拥有的范围，任何无法验证的变化都不会触发累计全文重发。</summary>
public sealed class OwnedTextRange
{
    private string _before;
    private string _after;
    private string _originalSelection;
    private string _sealedPrefix = "";
    private string? _expectedText;
    private bool _hasCommitted;
    public int Start => _before.Length;
    public string DeliveredText { get; private set; } = "";
    public bool HasCommitted => _hasCommitted;
    public int ActiveDeliveredLength => _hasCommitted ? DeliveredText.Length - _sealedPrefix.Length : 0;
    public string ExpectedText => _expectedText ??= _before + (_hasCommitted ? DeliveredText[_sealedPrefix.Length..] : _originalSelection) + _after;

    public OwnedTextRange(string initialText, int selectionStart, int selectionEnd)
    {
        if (selectionStart < 0 || selectionEnd < selectionStart || selectionEnd > initialText.Length)
            throw new ArgumentException("初始选区无效。");
        _before = initialText[..selectionStart];
        _originalSelection = initialText[selectionStart..selectionEnd];
        _after = initialText[selectionEnd..];
    }

    private OwnedTextRange(OwnedTextRange source)
    {
        _before = source._before;
        _after = source._after;
        _originalSelection = source._originalSelection;
        _sealedPrefix = source._sealedPrefix;
        _expectedText = source._expectedText;
        _hasCommitted = source._hasCommitted;
        DeliveredText = source.DeliveredText;
    }

    internal OwnedTextRange Snapshot() => new(this);

    public bool CanUpdate(string actualText, int selectionStart, int selectionEnd, out string reason)
    {
        if (selectionStart < 0 || selectionEnd < selectionStart || selectionEnd > actualText.Length)
        {
            reason = "不能确认当前文字光标，录音继续，完整结果保留在 iSay。";
            return false;
        }
        if (!string.Equals(actualText, ExpectedText, StringComparison.Ordinal))
        {
            if (!TryRebaseOutsideEdit(actualText, selectionStart, selectionEnd))
            {
                reason = "语音范围内有手动编辑，录音继续，完整结果保留在 iSay；撤销该编辑或另起一段后可恢复自动输入。";
                return false;
            }
        }
        var expectedStart = _hasCommitted ? Start + ActiveDeliveredLength : Start;
        var expectedEnd = _hasCommitted ? expectedStart : Start + _originalSelection.Length;
        if (selectionStart != expectedStart || selectionEnd != expectedEnd)
        {
            reason = "光标已移动；返回语音文字末尾后才会续写。";
            return false;
        }
        reason = "";
        return true;
    }

    public int ReplacementLength => _hasCommitted ? ActiveDeliveredLength : _originalSelection.Length;

    public bool TryPlanUpdate(string cumulative, out OwnedTextEdit edit, out string reason)
    {
        ArgumentNullException.ThrowIfNull(cumulative);
        if (!cumulative.StartsWith(_sealedPrefix, StringComparison.Ordinal) || !IsTextElementBoundary(cumulative, _sealedPrefix.Length))
        {
            edit = default;
            reason = "识别或整理修订涉及手打前的语音，已保留完整结果；不会覆盖手打文字或重发全文。";
            return false;
        }
        if (!_hasCommitted)
        {
            // 初始非空选区属于用户明确选中的内容，必须完整替换，不能把其中相同的字当成已交付语音。
            edit = new(Start, ReplacementLength, cumulative);
            reason = "";
            return true;
        }
        var previous = DeliveredText.AsSpan(_sealedPrefix.Length);
        var next = cumulative.AsSpan(_sealedPrefix.Length);
        var common = CommonTextElementPrefix(previous, next);
        // 稳定前文保持不动，只修订直到原语音末尾的尾段。
        // 不保留共同后缀做中间替换，键入后光标自然位于新的语音末尾，下一次仍可精确续写。
        edit = new(Start + common, previous.Length - common, next[common..].ToString());
        reason = "";
        return true;
    }

    private static int CommonTextElementPrefix(ReadOnlySpan<char> previous, ReadOnlySpan<char> next)
    {
        var common = 0;
        while (common < previous.Length && common < next.Length && previous[common] == next[common]) common++;
        var boundary = 0;
        while (boundary < common)
        {
            var oldLength = StringInfo.GetNextTextElementLength(previous[boundary..]);
            var newLength = StringInfo.GetNextTextElementLength(next[boundary..]);
            // 即使看起来是纯追加，组合音符、肤色或连接符也可能扩展旧末尾字素。
            // 只有新旧两份快照都承认的完整字素边界才能保留，不能拆代理对或组合序列。
            if (oldLength != newLength || boundary + oldLength > common) break;
            boundary += oldLength;
        }
        return boundary;
    }

    private static bool IsTextElementBoundary(string text, int offset)
    {
        if (offset == 0 || offset == text.Length) return true;
        var boundary = 0;
        while (boundary < offset) boundary += StringInfo.GetNextTextElementLength(text.AsSpan(boundary));
        return boundary == offset;
    }

    private bool TryRebaseOutsideEdit(string actual, int selectionStart, int selectionEnd)
    {
        // 只接纳一段精确位于语音范围外的变化，不按相似度寻找或删除累计全文。
        var expected = ExpectedText;
        var prefix = 0;
        while (prefix < expected.Length && prefix < actual.Length && expected[prefix] == actual[prefix]) prefix++;
        var oldEnd = expected.Length;
        var newEnd = actual.Length;
        while (oldEnd > prefix && newEnd > prefix && expected[oldEnd - 1] == actual[newEnd - 1]) { oldEnd--; newEnd--; }
        var ownedEnd = Start + ReplacementLength;
        var suffix = 0;
        while (suffix < expected.Length && suffix < actual.Length && expected[^(suffix + 1)] == actual[^(suffix + 1)]) suffix++;
        // 重复字符可能让同一最小改动既能落在语音内，也能落在边界外；这种歧义不能自动接管。
        var earliestChange = Math.Min(prefix, Math.Min(expected.Length, actual.Length) - suffix);

        // 用户在语音末尾打字时，封存之前的语音，后续只追加尚未交付的新语音。
        // 后面的识别若改写封存前缀，必须暂停，不能越过手打边界做全文替换。
        if ((_hasCommitted || _originalSelection.Length == 0) && prefix == ownedEnd && earliestChange == prefix && oldEnd == prefix && newEnd > prefix &&
            selectionStart == newEnd && selectionEnd == newEnd)
        {
            _before = actual[..newEnd];
            _after = actual[newEnd..];
            _originalSelection = "";
            _sealedPrefix = DeliveredText;
            _expectedText = null;
            return true;
        }
        if (oldEnd <= Start)
        {
            var newStart = Start + newEnd - oldEnd;
            _before = actual[..newStart];
            _expectedText = null;
            return string.Equals(actual, ExpectedText, StringComparison.Ordinal);
        }
        if (earliestChange >= ownedEnd)
        {
            _after = actual[ownedEnd..];
            _expectedText = null;
            return string.Equals(actual, ExpectedText, StringComparison.Ordinal);
        }
        return false;
    }

    public void Commit(string text)
    {
        if (!text.StartsWith(_sealedPrefix, StringComparison.Ordinal) || !IsTextElementBoundary(text, _sealedPrefix.Length))
            throw new InvalidOperationException("语音更新不能越过手打边界。");
        DeliveredText = text;
        _hasCommitted = true;
        _expectedText = null;
    }
}

public readonly record struct OwnedTextEdit(int Start, int Length, string Text);
