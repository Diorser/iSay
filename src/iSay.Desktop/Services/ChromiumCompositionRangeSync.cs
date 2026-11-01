using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using AutomationElement = Interop.UIAutomationClient.IUIAutomationElement;
using TextPatternRange = Interop.UIAutomationClient.IUIAutomationTextRange;


namespace iSay.Desktop.Services;

/// <summary>只核对原输入框的已确认语音；实时范围同步实验与最终整理分别启用，不按相似文字寻找目标。</summary>
internal sealed class ChromiumCompositionRangeSync
{
    private readonly BoundedInputExecutor executor;
    private readonly TsfEndpoint endpoint;
    private readonly Func<bool> hasFocus;
    private readonly Func<long> revision;
    private readonly long initialRevision;
    private readonly Task<AutomationElement?> original;
    private readonly bool allowUniqueEditor;
    private IUIAutomation? automation;
    private IUIAutomation Automation => automation ??= new CUIAutomation();
    private bool established;
    private bool restoreFailed;
    private string? originalBefore, originalAfter;
    private bool unsupportedPattern;
    private bool retryRead;
    internal Action<string, int, int>? Diagnostic { get; set; }
    internal Task Ready => original;

    internal async Task<Func<CancellationToken, Task<bool>>?> CaptureEmptyGuardAsync(CancellationToken token)
    {
        var captured = await original.WaitAsync(token).ConfigureAwait(false);
        if (captured is null) return null;
        async Task<bool> CheckEmpty(CancellationToken cancellation)
        {
            try
            {
                return await executor.RunAsync(_ =>
                {
                    if (revision() != initialRevision || !hasFocus()) return false;
                    var focused = FocusedInOriginalWindow();
                    if (focused is null || Automation.CompareElements(captured, focused) == 0 || focused.CurrentIsPassword != 0 ||
                        focused.GetCurrentPattern(10014) is not IUIAutomationTextPattern pattern) return false;
                    var actual = pattern.DocumentRange.GetText(2);
                    if (!actual.All(character => character is '\r' or '\n')) return false;
                    // 多取一个字符以排除前两个换行之后仍有正文的输入框。
                    actual = pattern.DocumentRange.GetText(TsfCompositionClient.MaximumTextLength + 1);
                    var selections = pattern.GetSelection();
                    return actual.All(character => character is '\r' or '\n') && selections.Length == 1 &&
                        selections.GetElement(0).CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                            selections.GetElement(0), TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) == 0 &&
                        revision() == initialRevision && hasFocus();
                }, cancellation).ConfigureAwait(false);
            }
            catch (Exception error) when (IsHostError(error)) { return false; }
        }
        return await CheckEmpty(token).ConfigureAwait(false) ? CheckEmpty : null;
    }

    internal static bool Supports(TsfEndpoint endpoint)
    {
        var name = new StringBuilder(256);
        var thread = GetWindowThreadProcessId(endpoint.FocusWindow, out var process);
        return thread == endpoint.ThreadId && process == endpoint.ProcessId &&
            GetClassNameW(endpoint.FocusWindow, name, name.Capacity) > 0 &&
            name.ToString() is "Chrome_RenderWidgetHostHWND" or "Chrome_WidgetWin_1";
    }

    internal ChromiumCompositionRangeSync(BoundedInputExecutor executor, TsfEndpoint endpoint, Func<bool> hasFocus, Func<long> revision,
        bool allowUniqueEditor = false)
    {
        this.executor = executor; this.endpoint = endpoint; this.hasFocus = hasFocus; this.revision = revision;
        this.allowUniqueEditor = allowUniqueEditor;
        initialRevision = revision();
        original = CaptureAsync();
    }

    private AutomationElement? FocusedInOriginalWindow()
    {
        if (!hasFocus()) return null;
        var handle = allowUniqueEditor ? GetAncestor(endpoint.FocusWindow, 2) : endpoint.FocusWindow;
        if (handle == 0) return null;
        if (GetWindowThreadProcessId(handle, out var owner) == 0 || owner != endpoint.ProcessId)
        {
            // 嵌入式 WebView 的顶层窗口属于宿主进程，此时仍只从原浏览器窗口查找。
            handle = endpoint.FocusWindow;
            if (GetWindowThreadProcessId(handle, out owner) == 0 || owner != endpoint.ProcessId) return null;
        }
        var root = Automation.ElementFromHandle(handle);
        var focused = root?.FindFirst(TreeScope.TreeScope_Subtree, Automation.CreatePropertyCondition(30008, true));
        Diagnostic?.Invoke("焦点类型", focused?.CurrentControlType ?? 0, root?.CurrentControlType ?? 0);
        if (!allowUniqueEditor || focused is null || focused.CurrentControlType is not (50030 or 50032)) return focused;
        // 当前 ChatGPT 将焦点标在根文档，而非编辑节点。只在最终整理中允许唯一可编辑槽，绝不按相似正文找框。
        var editors = root!.FindAll(TreeScope.TreeScope_Subtree, Automation.CreatePropertyCondition(30003, 50004));
        Diagnostic?.Invoke("编辑槽数量", editors.Length, 0);
        if (editors.Length != 1) return null;
        var editor = editors.GetElement(0);
        Diagnostic?.Invoke("编辑槽可用性", editor.CurrentIsEnabled, editor.CurrentIsKeyboardFocusable);
        return editor.CurrentIsEnabled != 0 && editor.CurrentIsKeyboardFocusable != 0 && editor.CurrentIsPassword == 0 ? editor : null;
    }

    private async Task<AutomationElement?> CaptureAsync()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var value = await executor.RunAsync<AutomationElement?>(token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (revision() != initialRevision) return null;
                    var element = FocusedInOriginalWindow();
                    if (element is null || element.CurrentIsPassword != 0 || revision() != initialRevision)
                    { Diagnostic?.Invoke("未捕获原元素", attempt, 0); return null; }
                    var raw = element.GetCurrentPattern(10014);
                    if (raw is not IUIAutomationTextPattern) { unsupportedPattern = true; Diagnostic?.Invoke("不支持文本模式", 0, 0); }
                    else
                    {
                        var pattern = (IUIAutomationTextPattern)raw;
                        var selections = pattern.GetSelection();
                        if (selections.Length == 1)
                        {
                            var before = pattern.DocumentRange.Clone();
                            before.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, selections.GetElement(0), TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                            var after = pattern.DocumentRange.Clone();
                            after.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, selections.GetElement(0), TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
                            originalBefore = before.GetText(TsfCompositionClient.MaximumTextLength + 1);
                            originalAfter = after.GetText(TsfCompositionClient.MaximumTextLength + 1);
                        }
                    }
                    return revision() == initialRevision ? element : null;
                }).ConfigureAwait(false);
                if (value is not null) return value;
                if (revision() != initialRevision || !hasFocus()) break;
                await Task.Delay(20).ConfigureAwait(false);
            }
            catch (Exception error) when (IsHostError(error)) { Diagnostic?.Invoke("捕获异常", error.HResult, 0); break; }
        }
        return null;
    }

    internal async Task<CompositionRangePreparation?> PrepareAsync(string expected, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            retryRead = false;
            var result = await TryPrepareAsync(expected, token).ConfigureAwait(false);
            if (!retryRead || attempt == 9) return result;
            // 仅重读迟到的无障碍快照；没有选区同步或正文写入，不能据旧缓存继续发送累计快照。
            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }

    private async Task<CompositionRangePreparation?> TryPrepareAsync(string expected, CancellationToken token)
    {
        if (restoreFailed || !hasFocus()) return new(false);
        var captured = await original.WaitAsync(token).ConfigureAwait(false);
        if (captured is null) { Diagnostic?.Invoke("无原元素", 0, 0); return new(false); }
        if (unsupportedPattern) return null;
        try
        {
            return await executor.RunAsync<CompositionRangePreparation?>(activeToken =>
            {
                var currentRevision = revision();
                activeToken.ThrowIfCancellationRequested();
                var focused = FocusedInOriginalWindow();
                if (focused is null || Automation.CompareElements(captured, focused) == 0 || focused.CurrentIsPassword != 0 ||
                    focused.GetCurrentPattern(10014) is not IUIAutomationTextPattern) return new(false);
                var pattern = (IUIAutomationTextPattern)focused.GetCurrentPattern(10014);
                var document = pattern.DocumentRange;
                var maximumRead = Math.Min(TsfCompositionClient.MaximumTextLength + 1,
                    expected.Length + (originalBefore?.Length ?? 0) + (originalAfter?.Length ?? 0) + 4);
                var actual = document.GetText(maximumRead);
                var trailer = actual == expected ? 0 : actual == expected + "\n" ? 1 : actual == expected + "\r\n" ? 2 : -1;
                // 只有整个输入框都属于本段语音时才启用；已启用后发生手打或其他改动则保留转写，绝不覆盖它。
                if (trailer < 0)
                {
                    Diagnostic?.Invoke("正文不符", expected.Length, actual.Length);
                    // 只有初始前后文仍逐字对应，才证明这是未参与增强的有前文输入框；不能把缓存迟到当成可直接写入。
                    if (!established && originalBefore is not null && originalAfter is not null &&
                        originalBefore.Length + originalAfter.Length != 0 && actual == originalBefore + expected + originalAfter) return null;
                    retryRead = currentRevision == initialRevision;
                    return new(false);
                }
                // 只裁掉已核对的虚拟换行，不把UTF16长度当成无障碍字符单位。
                var owned = ExactRange(document, expected);
                if (owned is null) return new(false);
                var selected = pattern.GetSelection();
                if (selected.Length != 1 || selected.GetElement(0).CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, selected.GetElement(0), TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) != 0)
                    return new(false);
                if (currentRevision != initialRevision &&
                    selected.GetElement(0).CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, owned, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) != 0) return new(false);
                var previous = selected.GetElement(0).Clone();
                activeToken.ThrowIfCancellationRequested();
                var finalFocus = FocusedInOriginalWindow();
                if (revision() != currentRevision || !hasFocus() || finalFocus is null || Automation.CompareElements(focused, finalFocus) == 0) return new(false);
                try
                {
                    owned.Select();
                    // Select 只表示动作已发送；宿主无障碍快照确认原选区之前不能发送累计正文。
                    var confirmed = false;
                    var until = System.Diagnostics.Stopwatch.StartNew();
                    do
                    {
                        activeToken.ThrowIfCancellationRequested();
                        if (revision() != currentRevision || !hasFocus()) break;
                        var observed = pattern.GetSelection();
                        var observedText = observed.Length == 1 ? observed.GetElement(0).GetText(expected.Length + 4) : "";
                        if (observed.Length == 1 && observedText == expected) { confirmed = true; break; }
                        activeToken.WaitHandle.WaitOne(10);
                        // 同文节点重建后旧无障碍范围可能已失效，须重新取得本元素的文档范围而非重复使用旧对象。
                        var freshDocument = pattern.DocumentRange;
                        var freshText = freshDocument.GetText(expected.Length + 4);
                        if (freshText != expected && freshText != expected + "\n" && freshText != expected + "\r\n") break;
                        var freshOwned = ExactRange(freshDocument, expected);
                        if (freshOwned is null) break;
                        activeToken.ThrowIfCancellationRequested();
                        var retryFocus = FocusedInOriginalWindow();
                        if (revision() != currentRevision || retryFocus is null || Automation.CompareElements(focused, retryFocus) == 0) break;
                        owned = freshOwned;
                        owned.Select();
                    } while (until.ElapsedMilliseconds < 400);
                    if (!confirmed)
                    {
                        RestoreCore(focused, previous, owned, expected);
                        Diagnostic?.Invoke("选区未确认", expected.Length, actual.Length);
                        return new(false);
                    }
                    // 宿主在 Select 内卡住导致调用方超时时，迟到线程必须自行恢复，不能留下失去所有者的整段选区。
                    if (activeToken.IsCancellationRequested)
                    {
                        RestoreCore(focused, previous, owned, expected);
                        return new(false);
                    }
                }
                catch
                {
                    try { RestoreCore(focused, previous, owned, expected); } catch { restoreFailed = true; }
                    throw;
                }
                established = true;
                Diagnostic?.Invoke("原范围已同步", expected.Length, actual.Length);
                return new(true, applied => applied ? Task.CompletedTask : RestoreAsync(focused, previous, owned, expected));
            }, token).ConfigureAwait(false);

        }
        catch (Exception error) when (IsHostError(error)) { return new(false); }
    }

    private static TextPatternRange? ExactRange(TextPatternRange document, string expected)
    {
        var result = document.Clone();
        // UIA字符单位不等于UTF16长度；从完整原文裁掉虚拟换行，避免表情和组合字符越界。
        for (var trim = 0; trim <= 2; trim++)
        {
            if (result.GetText(expected.Length + 4) == expected) return result;
            if (trim != 2) result.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, -1);
        }
        return null;
    }

    internal async Task<bool> RefineParagraphsAsync(string expected, string text, Func<bool> guard, CancellationToken token,
        Func<string, Func<bool>, CancellationToken, IParagraphPasteLease?>? acquire = null)
    {
        var captured = await original.WaitAsync(token).ConfigureAwait(false);
        if (captured is null || !guard()) { Diagnostic?.Invoke("段落未捕获", 0, 0); return false; }
        try
        {
            return await executor.RunAsync(activeToken =>
            {
                bool Valid() => !activeToken.IsCancellationRequested && guard() && revision() == initialRevision && hasFocus();
                var focused = FocusedInOriginalWindow();
                if (!Valid() || focused is null || Automation.CompareElements(captured, focused) == 0) { Diagnostic?.Invoke("段落焦点变化", 0, 0); return false; }
                var pattern = focused.GetCurrentPattern(10014) as IUIAutomationTextPattern;
                if (pattern is null) return false;
                var document = pattern.DocumentRange;
                var owned = ExactRange(document, expected);
                var selection = pattern.GetSelection();
                if (owned is null || selection.Length != 1) { Diagnostic?.Invoke("段落原文选区", owned is null ? 0 : 1, selection.Length); return false; }
                var previous = selection.GetElement(0).Clone();
                // 提交后的虚拟段落光标可落在无障碍文档末尾之外；用户移动由提交时冻结的键鼠版本拒绝。
                if (previous.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, previous, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) != 0)
                { Diagnostic?.Invoke("段落原光标", previous.GetText(100).Length, previous.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, owned, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End)); return false; }
                var sent = false;
                try
                {
                    if (!Valid()) return false;
                    owned.Select();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    bool Selected()
                    {
                        if (!Valid() || ExactRange(pattern.DocumentRange, expected) is null) return false;
                        var selected = pattern.GetSelection();
                        return selected.Length == 1 && selected.GetElement(0).GetText(expected.Length + 4) == expected;
                    }
                    while (!Selected())
                    {
                        if (!Valid() || timer.ElapsedMilliseconds >= 180) { Diagnostic?.Invoke("段落选区未确认", 0, 0); return false; }
                        activeToken.WaitHandle.WaitOne(5);
                    }
                    using var lease = (acquire ?? RefinementClipboardLease.TryCreate)(text, Valid, activeToken);
                    if (lease is RefinementClipboardLease nativeLease) nativeLease.Diagnostic = Diagnostic;
                    if (lease is null || !Selected()) { Diagnostic?.Invoke("段落粘贴未准备", lease is null ? 0 : 1, 0); return false; }
                    // 从这一刻起结果可能已写入；失败也不重发，不移动选区试图补救未知的粘贴。
                    sent = true;
                    if (!lease.Paste(Valid, activeToken)) { sent = lease.MayHaveSent; Diagnostic?.Invoke("段落粘贴未发送", 0, 0); return false; }
                    timer.Restart();
                    var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
                    // 不同 Chromium 编辑器对空段落的无障碍投影不同，逐个精确核对合法表示，不裁剪正文。
                    var representations = new[] { normalized, ParagraphProjection(text) }.Distinct(StringComparer.Ordinal).ToArray();
                    do
                    {
                        if (!Valid()) return false;
                        var finalDocument = pattern.DocumentRange;
                        var visible = representations.FirstOrDefault(candidate => ExactRange(finalDocument, candidate) is not null);
                        var finalSelection = pattern.GetSelection();
                        if (visible is not null && finalSelection.Length == 1 &&
                            finalSelection.GetElement(0).CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, finalSelection.GetElement(0), TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) == 0)
                        {
                            // 宿主虚拟段落边界可能不属于 DocumentRange；按光标两侧实际文字核验，不按偏移猜位置。
                            var beforeCaret = finalDocument.Clone();
                            beforeCaret.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, finalSelection.GetElement(0), TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                            var afterCaret = finalDocument.Clone();
                            afterCaret.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, finalSelection.GetElement(0), TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
                            var beforeText = beforeCaret.GetText(visible.Length + 4);
                            var afterText = afterCaret.GetText(4);
                            if ((beforeText == visible || beforeText == visible + "\n" || beforeText == visible + "\r\n" || beforeText + afterText == visible) &&
                                afterText is "" or "\n" or "\r\n") return true;
                        }
                        activeToken.WaitHandle.WaitOne(5);
                    } while (timer.ElapsedMilliseconds < 500);
                    Diagnostic?.Invoke("段落写回未确认", 0, 0); return false;
                }
                finally
                {
                    // 仅未发写入且仍为自有选区时恢复；键鼠变化后不能覆盖用户的新选区。
                    if (!sent && Valid()) RestoreCore(captured, previous, owned, expected);
                }
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsHostError(error)) { Diagnostic?.Invoke("段落异常", error.HResult, 0); return false; }
    }

    private static string ParagraphProjection(string text)
    {
        // Chromium 无障碍正文在内部空段落连续出现时少一个分隔符，首尾空段落仍保留。
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        // 行首缩进在无障碍流中归入前一段末尾；只用于核验，实际段落和空格不作修改。
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "\\n([ \\t]+)", "$1\n");
        return System.Text.RegularExpressions.Regex.Replace(normalized, "(?<=[^\\n])\\n{2,}(?=[^\\n])", match => match.Value[1..]);
    }
    private async Task RestoreAsync(AutomationElement target, TextPatternRange previous, TextPatternRange owned, string expected)
    {
        try
        {
            // 清理不沿用已经取消的用户令牌；仅恢复仍为本次同步选区的原元素，不能抢回焦点。
            await executor.RunAsync(activeToken =>
            {
                activeToken.ThrowIfCancellationRequested();
                return RestoreCore(target, previous, owned, expected);
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (IsHostError(error)) { restoreFailed = true; }
    }

    private bool RestoreCore(AutomationElement target, TextPatternRange previous, TextPatternRange owned, string expected)
    {
        var focused = FocusedInOriginalWindow();
        if (focused is null || Automation.CompareElements(target, focused) == 0) return false;
        var pattern = (IUIAutomationTextPattern)focused.GetCurrentPattern(10014);
        var selected = pattern.GetSelection();
        if (owned.GetText(expected.Length + 1) != expected || selected.Length != 1 || selected.GetElement(0).GetText(expected.Length + 1) != expected) return false;
        previous.Select();
        return true;
    }

    private static bool IsHostError(Exception error) => error is InvalidOperationException or COMException or
        InvalidCastException or TimeoutException or OperationCanceledException or ObjectDisposedException;

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder text, int capacity);
}
