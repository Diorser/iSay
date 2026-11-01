using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Threading;
using iSay.Voice;

namespace iSay.Desktop.Services;

public enum InputActivityKind { Keyboard, Mouse }

public sealed class InputActivityEventArgs(InputActivityKind kind, int virtualKey = 0) : EventArgs
{
    public InputActivityKind Kind { get; } = kind;
    public int VirtualKey { get; } = virtualKey;
    public bool IsInjected => false;
}

public sealed record DeliveryResult(bool Success, string Message, bool Paused = false, bool AwaitingHost = false)
{
    public static DeliveryResult Delivered(string message = "已输入") => new(true, message);
    public static DeliveryResult Retained(string message, bool awaitingHost = false) => new(false, message, true, awaitingHost);
}

/// <summary>输入诊断只含长度、匹配类型和身份类别，不包含正文、窗口标题或标识值。</summary>
public sealed record InputDeliveryDiagnostic(string Stage, string MatchKind, string FocusIdentity,
    int ExpectedLength, int ActualLength, long ElapsedMilliseconds)
{
    public bool? IsFinal { get; init; }
    public int? PreviousSnapshotLength { get; init; }
    public int? NextSnapshotLength { get; init; }
    public int? SentSnapshotLength { get; init; }
    public int? KeptPrefixLength { get; init; }
    public int? EditStart { get; init; }
    public int? RemovedLength { get; init; }
    public int? InsertedLength { get; init; }
}

internal sealed record InputTextSnapshot(string Text, int SelectionStart, int SelectionEnd);

internal sealed class TargetMovedBeforeWriteException(string message) : InvalidOperationException(message);

internal interface IInputTextTarget
{
    InputTextSnapshot Initial { get; }
    string DiagnosticIdentity => "injected-target";
    bool ConfirmFirstTextElement => false;
    bool IsCurrentFocus();
    IInputTextTarget? TryRecaptureOwned(string expectedText, bool allowUniqueSlot = false);
    InputTextSnapshot Read();
    void ReplaceOwned(int start, int length, string text, InputTextSnapshot expected, Func<bool>? stillValid = null);
}

/// <summary>全局录音热键和经过范围核验的文字输出。录音生命周期由上层管理。</summary>
public sealed class WindowsInputService : IDisposable
{
    private readonly BoundedInputExecutor _delivery = new();
    private readonly BoundedInputExecutor _clipboard = new(TimeSpan.FromMilliseconds(800), 0);
    private readonly Thread _hookThread;
    private readonly Dispatcher _hookDispatcher;
    private readonly GlobalHotkeyState _hotkey = new(Native.IsKeyDown);
    private readonly ConcurrentQueue<RecordingHotkeyAction> _hotkeyRequests = new();
    private readonly Native.HookProc _keyboardProc;
    private readonly Native.HookProc _mouseProc;
    private nint _keyboardHook;
    private nint _mouseHook;
    private int _registeredHotkeyId;
    private int _hotkeyRequestWorker;
    private nint _hotkeyCaptureWindow;
    private Action<int, bool>? _hotkeyCapture;
    private DeliverySession? _session;
    private DeliverySession? _retainedSession;
    private readonly object _sessionGate = new();
    private readonly Func<IInputTextTarget> _captureTarget;
    private readonly Func<bool> _hasHeldEditingModifier;
    private int _disposed;
    private int _recording;
    private long _physicalRevision;
    private long _sessionEpoch;

    public event EventHandler? ToggleRequested;
    public event EventHandler<RecordingHotkeyEventArgs>? RecordingRequested;
    public event EventHandler<InputActivityEventArgs>? InputActivity;
    public event EventHandler<InputDeliveryDiagnostic>? Diagnostic;

    // 钩子线程收到真实键鼠活动时同步递增；初始化恢复不能依赖可能迟到的界面事件。
    internal long PhysicalRevision => Interlocked.Read(ref _physicalRevision);

    internal bool ChromiumRangeSyncEnabled { get; set; }
    internal BoundedInputExecutor CompositionExecutor => _delivery;

    // 只读取焦点元素身份，不读取正文；键鼠发生变化后才在后台重新核验，正常快照不等待UIA。
    internal Func<CancellationToken, Task<bool>> CaptureCompositionFocusGuard(Func<bool> hasNativeFocus)
    {
        var capturedRevision = PhysicalRevision;
        var captured = ReadCompositionFocusAsync(capturedRevision, CancellationToken.None);
        long verifiedRevision = capturedRevision;
        return async token =>
        {
            if (!hasNativeFocus()) return false;
            var revision = PhysicalRevision;
            if (revision == capturedRevision || revision == Interlocked.Read(ref verifiedRevision)) return true;
            var original = await captured.WaitAsync(token).ConfigureAwait(false);
            if (original is null) return false;
            var current = await ReadCompositionFocusAsync(revision, token).ConfigureAwait(false);
            if (current is null || current.ProcessId != original.ProcessId || !current.RuntimeId.SequenceEqual(original.RuntimeId) ||
                PhysicalRevision != revision || !hasNativeFocus()) return false;
            Interlocked.Exchange(ref verifiedRevision, revision);
            return true;
        };
    }

    private sealed record CompositionFocusIdentity(int ProcessId, int[] RuntimeId);
    private async Task<CompositionFocusIdentity?> ReadCompositionFocusAsync(long revision, CancellationToken token)
    {
        try
        {
            return await _delivery.RunAsync<CompositionFocusIdentity?>(_ =>
            {
                if (PhysicalRevision != revision) return null;
                var element = AutomationElement.FocusedElement;
                if (element is null || element.Current.IsPassword) return null;
                var identity = new CompositionFocusIdentity(element.Current.ProcessId, element.GetRuntimeId());
                return PhysicalRevision == revision && identity.RuntimeId.Length != 0 ? identity : null;
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or ElementNotAvailableException or
            COMException or OperationCanceledException or ObjectDisposedException) { return null; }
    }

    public bool IsRecording
    {
        get => Volatile.Read(ref _recording) != 0;
        set => Volatile.Write(ref _recording, value ? 1 : 0);
    }

    public WindowsInputService(bool startHooks = true, string initialHotkey = "Alt", string initialMode = "toggle")
        : this(TextTarget.Capture, Native.HasHeldEditingModifier, startHooks, initialHotkey, initialMode) { }

    internal WindowsInputService(Func<IInputTextTarget> captureTarget, Func<bool> hasHeldEditingModifier, bool startHooks = false,
        string initialHotkey = "Alt", string initialMode = "toggle")
    {
        _captureTarget = captureTarget;
        _hasHeldEditingModifier = hasHeldEditingModifier;
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
        _hotkey.Configure(initialHotkey, initialMode);
        Dispatcher? dispatcher = null;
        Exception? startupError = null;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                if (startHooks)
                    EnableHotkeysCore(initialHotkey, initialMode);
            }
            catch (Exception error)
            {
                if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
                if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
                _keyboardHook = _mouseHook = 0;
                startupError = error;
            }
            finally { ready.Set(); }
            if (startupError is null)
                Dispatcher.Run();
        }) { IsBackground = true, Name = "iSay 全局热键" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("全局热键初始化超时。");
        if (startupError is not null)
        {
            throw new InvalidOperationException($"无法启动全局输入监听：{startupError.Message}", startupError);
        }
        _hookDispatcher = dispatcher!;
    }

    public string ConfiguredHotkey => _hotkey.Hotkey;
    public string ConfiguredHotkeyMode => _hotkey.Mode;
    public bool HotkeysEnabled => _keyboardHook != 0 && _mouseHook != 0;

    public void BeginHotkeyCapture(nint window, Action<int, bool> capture)
    {
        ThrowIfDisposed();
        _hookDispatcher.Invoke(() =>
        {
            _hotkey.Reset();
            _hotkeyCaptureWindow = window;
            _hotkeyCapture = capture;
        });
    }

    public void EndHotkeyCapture()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _hookDispatcher.Invoke(() =>
        {
            _hotkeyCapture = null;
            _hotkeyCaptureWindow = 0;
            _hotkey.Reset();
        });
    }

    public void EnableHotkeys(string hotkey, string mode = "toggle")
    {
        ThrowIfDisposed();
        _hookDispatcher.Invoke(() => EnableHotkeysCore(hotkey, mode));
    }

    public void DisableHotkeys()
    {
        ThrowIfDisposed();
        _hookDispatcher.Invoke(() =>
        {
            var stopHold = _hotkey.IsHoldActive;
            if (_registeredHotkeyId != 0) Native.UnregisterHotKey(0, _registeredHotkeyId);
            if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
            _registeredHotkeyId = 0;
            _keyboardHook = _mouseHook = 0;
            // 停用后看不到松开事件，必须清空抑制残留，恢复时不会吞掉第一次完整按键。
            _hotkey.Reset();
            if (stopHold) RaiseRecordingRequest(RecordingHotkeyAction.Stop);
        });
    }

    private void EnableHotkeysCore(string hotkey, string mode)
    {
        var definition = HotkeyDefinition.Parse(hotkey);
        var normalizedMode = HotkeyDefinition.NormalizeMode(mode);
        var installed = _keyboardHook == 0;
        if (installed) InstallHooks();
        try { ConfigureHotkeyCore(definition.Canonical, normalizedMode); }
        catch
        {
            // 从未启用的恢复实例遇到注册冲突时撤回本轮钩子，继续留在设置界面可重试。
            if (installed)
            {
                if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
                if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
                _keyboardHook = _mouseHook = 0;
            }
            throw;
        }
    }

    public void ConfigureHotkey(string hotkey, string mode = "toggle")
    {
        ThrowIfDisposed();
        _hookDispatcher.Invoke(() => ConfigureHotkeyCore(hotkey, mode));
    }

    private void ConfigureHotkeyCore(string hotkey, string mode)
    {
        var definition = HotkeyDefinition.Parse(hotkey);
        var normalizedMode = HotkeyDefinition.NormalizeMode(mode);
        var registration = 0;
        var reuse = _registeredHotkeyId != 0 && definition.Canonical == _hotkey.Hotkey;
        // 先占用新组合，再撤旧组合；注册失败不会使原来的录音键失效。
        if (_keyboardHook != 0 && !definition.IsModifierOnly && !reuse)
        {
            registration = _registeredHotkeyId == 0x4A31 ? 0x4A32 : 0x4A31;
            if (!Native.RegisterHotKey(0, registration, definition.Modifiers | 0x4000, (uint)definition.VirtualKey))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"快捷键 {definition.Canonical} 已被其他程序占用或由系统保留，请选择其他组合。原快捷键保持不变。");
        }
        var stopHold = _hotkey.IsHoldActive;
        _hotkey.Configure(definition.Canonical, normalizedMode);
        if (!reuse)
        {
            if (_registeredHotkeyId != 0) Native.UnregisterHotKey(0, _registeredHotkeyId);
            _registeredHotkeyId = registration;
        }
        if (stopHold) RaiseRecordingRequest(RecordingHotkeyAction.Stop);
    }

    public DeliveryResult BeginDelivery() => BeginDeliveryAsync().GetAwaiter().GetResult();

    internal async Task<Func<CancellationToken, Task<bool>>?> CaptureEmptyTargetGuardAsync(CancellationToken cancellationToken)
    {
        var revision = PhysicalRevision;
        try
        {
            var target = await _delivery.RunAsync<IInputTextTarget?>(_ =>
            {
                var captured = _captureTarget();
                return revision == PhysicalRevision && captured.IsCurrentFocus() && IsEmptyCaret(captured.Read()) ? captured : null;
            }, cancellationToken).ConfigureAwait(false);
            if (target is null) return null;
            return token => _delivery.RunAsync(_ => revision == PhysicalRevision && target.IsCurrentFocus() && IsEmptyCaret(target.Read()), token);
        }
        catch (Exception error) when (IsTargetError(error) || error is TimeoutException or OperationCanceledException) { return null; }
    }

    internal static bool IsEmptyCaret(InputTextSnapshot snapshot) => snapshot.SelectionStart == snapshot.SelectionEnd &&
        snapshot.SelectionStart >= 0 && snapshot.SelectionEnd <= snapshot.Text.Length && snapshot.Text.All(character => character is '\r' or '\n');
    public async Task<DeliveryResult> BeginDeliveryAsync(CancellationToken cancellationToken = default)
    {
        long epoch;
        lock (_sessionGate)
        {
            epoch = Interlocked.Increment(ref _sessionEpoch);
            _session = null;
            _retainedSession = null;
        }
        try
        {
            return await _delivery.RunAsync(token =>
            {
                var target = _captureTarget();
                token.ThrowIfCancellationRequested();
                lock (_sessionGate)
                {
                    if (epoch != Interlocked.Read(ref _sessionEpoch)) return DeliveryResult.Retained("输入位置已过期。");
                    _session = new DeliverySession(target);
                    _retainedSession = _session.Snapshot();
                }
                return DeliveryResult.Delivered("已记录输入位置");
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsTargetError(error) || error is TimeoutException or OperationCanceledException)
        {
            InvalidateSession(epoch, false);
            return DeliveryResult.Retained(error is TimeoutException ? error.Message : "当前没有可验证的编辑框，识别结果将保留在 iSay。");
        }
    }

    public Task<DeliveryResult> UpdateAsync(string cumulative, bool final = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cumulative);
        ThrowIfDisposed();
        return UpdateAtEpochAsync(cumulative, final, Interlocked.Read(ref _sessionEpoch), cancellationToken);
    }

    /// <summary>结束时只为已发送待确认的文字保留有界收尾时间，不复活旧段或重发未确认内容。</summary>
    public async Task<DeliveryResult> CompleteFinalAsync(string cumulative, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cumulative);
        ThrowIfDisposed();
        var epoch = Interlocked.Read(ref _sessionEpoch);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        while (true)
        {
            var result = await UpdateAtEpochAsync(cumulative, true, epoch, deadline.Token).ConfigureAwait(false);
            if (!result.AwaitingHost) return result;
            // 待确认返回只允许重读同一已发送范围；无法确认身份、光标或正文时下一次立即退出。
            if (deadline.IsCancellationRequested)
                return DeliveryResult.Retained("宿主尚未确认已发送文字，完整结果已保留，请从 iSay 复制或补填。");
        }
    }

    private async Task<DeliveryResult> UpdateAtEpochAsync(string cumulative, bool final, long epoch, CancellationToken cancellationToken)
    {
        try
        {
            return await _delivery.RunAsync(token => epoch == Interlocked.Read(ref _sessionEpoch)
                ? UpdateCore(cumulative, final, epoch, token)
                : DeliveryResult.Retained("旧录音结果已过期，未写入当前输入框。"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            InvalidateSession(epoch, true);
            return DeliveryResult.Retained("自动输入响应超时或已取消，完整文字已保留，请手动复制。");
        }
    }

    public void CancelDelivery() => CancelDeliveryCore(true);

    /// <summary>新录音或明确更换草稿时清除旧段归属，避免沿用上一段的显式补填范围。</summary>
    public void ResetDelivery() => CancelDeliveryCore(false);

    private void CancelDeliveryCore(bool retainForExplicit)
    {
        lock (_sessionGate)
        {
            Interlocked.Increment(ref _sessionEpoch);
            _session = null;
            if (!retainForExplicit) _retainedSession = null;
            // 只保留已发布的独立快照供用户显式补填，不复活自动输入或共享在途可变范围。
        }
    }

    public void Copy(string text) => CopyAsync(text).GetAwaiter().GetResult();
    public async Task<DeliveryResult> CopyAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _clipboard.RunAsync(token =>
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    try { if (Native.CopyClipboard(text)) return DeliveryResult.Delivered("已复制到剪贴板"); Thread.Sleep(35); }
                    catch (COMException) when (attempt < 4) { Thread.Sleep(35); }
                }
                return DeliveryResult.Retained("剪贴板正被其他程序占用，请稍后再复制。");
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is COMException or TimeoutException or OperationCanceledException) { return DeliveryResult.Retained("剪贴板正忙，文字已保留，请稍后复制。"); }
    }

    /// <summary>用户明确补填时复用本段范围，已交付文字不再次追加。</summary>
    public async Task<DeliveryResult> ExplicitPasteAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(text)) return DeliveryResult.Retained("没有可填入的文字。");
        if (IsRecording) return DeliveryResult.Retained("请先结束当前录音，再补填完整结果。");
        var epoch = Interlocked.Read(ref _sessionEpoch);
        try
        {
            return await _delivery.RunAsync(token =>
            {
                DeliverySession? retained;
                lock (_sessionGate)
                {
                    if (epoch != Interlocked.Read(ref _sessionEpoch)) return DeliveryResult.Retained("输入位置已过期。");
                    retained = _retainedSession?.Snapshot();
                }
                if (retained is not null)
                {
                    if (retained.Faulted) return DeliveryResult.Retained("上次输入未能确认，已保留完整结果，请复制后手动粘贴。");
                    if (!retained.IsRelated(text)) return DeliveryResult.Retained("此结果与原语音范围不一致，已保留文字，请复制后手动粘贴。");
                    var result = UpdateCore(text, true, epoch, token, retained);
                    lock (_sessionGate)
                    {
                        // 后台整理仍可能使用本段自动范围，显式补填后也必须接续同一份边界。
                        if (epoch == Interlocked.Read(ref _sessionEpoch) && _session is not null) _session = retained;
                    }
                    return result;
                }
                var target = _captureTarget();
                token.ThrowIfCancellationRequested();
                if (target.Initial.Text == text || target.Initial.SelectionStart == target.Initial.SelectionEnd &&
                    target.Initial.SelectionStart >= text.Length && target.Initial.Text.AsSpan(target.Initial.SelectionStart - text.Length, text.Length).SequenceEqual(text))
                    return DeliveryResult.Delivered("光标前已存在相同文字，未重复填入。");
                var session = new DeliverySession(target) { LastRequestedText = text };
                return UpdateCore(text, true, epoch, token, session);
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (IsTargetError(error) || error is TimeoutException or OperationCanceledException)
        {
            InvalidateSession(epoch, true);
            return DeliveryResult.Retained("当前输入位置无法确认，完整结果已保留，请复制后手动粘贴。");
        }
    }

    private void InvalidateSession(long epoch, bool uncertain)
    {
        lock (_sessionGate)
        {
            if (epoch != Interlocked.Read(ref _sessionEpoch)) return;
            Interlocked.Increment(ref _sessionEpoch);
            _session = null;
            if (uncertain && _retainedSession is not null) _retainedSession.Faulted = true;
        }
    }

    private void RetainSnapshot(DeliverySession session, long epoch, bool uncertain = false)
    {
        var snapshot = session.Snapshot();
        if (uncertain) snapshot.Faulted = true;
        lock (_sessionGate)
        {
            if (epoch == Interlocked.Read(ref _sessionEpoch) && Volatile.Read(ref _disposed) == 0) _retainedSession = snapshot;
        }
    }

    private DeliveryResult UpdateCore(string cumulative, bool final, long epoch, CancellationToken token, DeliverySession? explicitSession = null)
    {
        bool Valid() => !token.IsCancellationRequested && epoch == Interlocked.Read(ref _sessionEpoch) && Volatile.Read(ref _disposed) == 0;
        if (!Valid()) return DeliveryResult.Retained("过期输入已取消。");
        var session = explicitSession ?? Volatile.Read(ref _session);
        if (session is null)
            return DeliveryResult.Retained("未找到原输入位置，结果已保留，请手动粘贴。");
        if (session.Faulted)
            return DeliveryResult.Retained("输入范围已改变，结果已保留，请手动粘贴。");
        session.LastRequestedText = cumulative;
        RetainSnapshot(session, epoch);
        try
        {
            // 已经发送但尚未确认的文字只能重新读取，不能从旧快照再次发送累计全文。
            if (session.Pending is not null)
            {
                var pendingWait = Stopwatch.StartNew();
                while (session.Pending is not null)
                {
                    if (!Valid()) return DeliveryResult.Retained("输入已取消，结果保留。");
                    if (TryConfirmPending(session, epoch, false)) break;
                    // 录音中的重试立即返回；最终交付另留短读窗口，不能因结束计时器而错过已在途的宿主更新。
                    if (!final || pendingWait.ElapsedMilliseconds >= 400)
                    {
                        if (!TryConfirmPending(session, epoch))
                            return DeliveryResult.Retained(final ? "输入框尚未确认最终文字，完整转写已保留在 iSay。" :
                                "宿主尚未确认已发送文字；后续结果已保留，请从 iSay 复制或补填。", session.PendingCanAwaitHost);
                        break;
                    }
                    Thread.Sleep(15);
                }
            }
            var target = session.Target;
            if (!target.IsCurrentFocus())
            {
                var rebound = target.TryRecaptureOwned(session.Range.ExpectedText);
                if (rebound is null) return DeliveryResult.Retained("录音继续中；返回原输入位置后才会续写。若输入框已重建，请从 iSay 复制结果。");
                session.Target = target = rebound;
            }
            if (_hasHeldEditingModifier())
                return DeliveryResult.Retained("录音继续中；松开组合键或鼠标后续写。");

            var revision = Interlocked.Read(ref _physicalRevision);
            var snapshot = target.Read();
            if (!Valid()) return DeliveryResult.Retained("过期输入已取消。");
            if (!session.Range.CanUpdate(snapshot.Text, snapshot.SelectionStart, snapshot.SelectionEnd, out var reason))
                return DeliveryResult.Retained(reason);
            if (session.Range.DeliveredText == cumulative)
            {
                if (final && !session.FinalNoopReported)
                {
                    session.FinalNoopReported = true;
                    ReportFinalNoop(session, snapshot, cumulative.Length);
                }
                RetainSnapshot(session, epoch);
                return DeliveryResult.Delivered(final ? "最终文字已保留在原位置" : "已同步");
            }
            var initialDisplay = session.Range.ExpectedText;
            var firstAtEmptyCaret = !session.Range.HasCommitted && session.Range.ReplacementLength == 0 &&
                (session.Range.Start == 0 || session.Range.Start == initialDisplay.Length);
            var sentCumulative = cumulative;
            // 首字可能令网页编辑器重建控件。先确认一个真实的完整字素，再向已确认目标发送剩余增量。
            // 标准 Edit 用同步窗口消息，不需要这一步；也不能拆开 emoji、变音符或其他组合字符。
            if (firstAtEmptyCaret && target.ConfirmFirstTextElement && cumulative.Length > 0)
                sentCumulative = cumulative[..StringInfo.GetNextTextElementLength(cumulative)];
            if (!session.Range.TryPlanUpdate(sentCumulative, out var edit, out reason))
                return DeliveryResult.Retained(reason);

            // 先验证全文和光标，再仅选择本次语音拥有的范围。
            // 写入尚未确认时的取消或超时不得回退为从新光标重发全文。
            RetainSnapshot(session, epoch, true);
            var pendingWrite = new PendingWrite(sentCumulative, initialDisplay, firstAtEmptyCaret, revision, Stopwatch.GetTimestamp(),
                IsFinal: final, PreviousSnapshotLength: session.Range.DeliveredText.Length, NextSnapshotLength: cumulative.Length,
                KeptPrefixLength: session.Range.DeliveredText.Length - session.Range.ActiveDeliveredLength + edit.Start - session.Range.Start,
                EditStart: edit.Start, RemovedLength: edit.Length, InsertedLength: edit.Text.Length);
            target.ReplaceOwned(edit.Start, edit.Length, edit.Text, snapshot,
                () => Valid() && revision == Interlocked.Read(ref _physicalRevision) && !_hasHeldEditingModifier());
            session.Range.Commit(sentCumulative);
            session.Pending = pendingWrite;
            session.FinalNoopReported = false;
            if (final)
                ReportWriteDiagnostic(session, "final-write-sent", edit.Length == 0 ? "append-only" : edit.Text.Length == 0 ? "delete-only" :
                    pendingWrite.KeptPrefixLength == 0 ? "prefix-revision" : "tail-revision", true,
                    session.Range.ExpectedText.Length, snapshot.Text.Length, pendingWrite);

            // 等待宿主处理输入。短暂延迟不能永久禁用本段，后续更新仍先只读核验同一范围。
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 400)
            {
                if (!Valid()) return DeliveryResult.Retained("输入已取消，结果保留。");
                if (TryConfirmPending(session, epoch, false))
                {
                    if (sentCumulative != cumulative) return UpdateCore(cumulative, final, epoch, token, session);
                    return DeliveryResult.Delivered(final ? "已更新最终文字" : "已同步");
                }
                Thread.Sleep(15);
            }
            // 400 毫秒只是本次等待预算。保留已发出的精确边界，下次重读成功后仅补尚未发送的后缀。
            TryConfirmPending(session, epoch);
            if (session.Pending is null)
                return sentCumulative == cumulative ? DeliveryResult.Delivered(final ? "已更新最终文字" : "已同步")
                    : UpdateCore(cumulative, final, epoch, token, session);
            return DeliveryResult.Retained(final ? "输入框尚未确认最终文字，完整转写已保留在 iSay。" :
                "宿主尚未确认已发送文字；录音继续，后续文字保留，确认后自动续写。", session.PendingCanAwaitHost);
        }
        catch (TargetMovedBeforeWriteException)
        {
            RetainSnapshot(session, epoch);
            return DeliveryResult.Retained("录音继续中；等待手动操作结束并返回原语音末尾后续写。");
        }
        catch (Exception error) when (IsTargetError(error))
        {
            session.Faulted = true;
            RetainSnapshot(session, epoch);
            return DeliveryResult.Retained("无法验证语音文字范围，结果已保留，请手动粘贴。");
        }
    }

    private bool TryConfirmPending(DeliverySession session, long epoch, bool reportPending = true)
    {
        var pending = session.Pending!;
        var target = session.Target;
        InputTextSnapshot? received = null;
        var focused = false;
        var predictionDisappeared = false;
        try
        {
            focused = target.IsCurrentFocus();
            if (!focused)
            {
                // 控件重建仍必须通过目标实现的稳定身份核验；正文相同不能代替控件身份。
                var allowUniqueSlot = pending.FirstAtEmptyCaret && pending.PhysicalRevision == Interlocked.Read(ref _physicalRevision);
                var rebound = target.TryRecaptureOwned(session.Range.ExpectedText, allowUniqueSlot);
                if (rebound is null && pending.FirstAtEmptyCaret && pending.InitialDisplay.Length > 0 &&
                    pending.InitialDisplay != pending.Cumulative && pending.PhysicalRevision == Interlocked.Read(ref _physicalRevision))
                {
                    rebound = target.TryRecaptureOwned(pending.Cumulative, allowUniqueSlot);
                    predictionDisappeared = rebound is not null;
                }
                if (rebound is not null)
                {
                    session.Target = target = rebound;
                    focused = target.IsCurrentFocus();
                }
            }
            received = target.Read();
            // 灰色预测消失时只确认同一目标中已真实出现的精确本次文字，不删旧正文，也不补发首字。
            predictionDisappeared = (predictionDisappeared || pending.FirstAtEmptyCaret && pending.InitialDisplay.Length > 0 &&
                pending.InitialDisplay != pending.Cumulative) && received.Text == pending.Cumulative &&
                received.SelectionStart == pending.Cumulative.Length && received.SelectionEnd == pending.Cumulative.Length &&
                pending.PhysicalRevision == Interlocked.Read(ref _physicalRevision) && focused;
            if (received.Text == session.Range.ExpectedText || predictionDisappeared)
            {
                if (predictionDisappeared)
                {
                    session.Range = new OwnedTextRange("", 0, 0);
                    session.Range.Commit(pending.Cumulative);
                }
                session.Pending = null;
                if (pending.Reported || predictionDisappeared)
                    ReportDiagnostic(session, "confirmation-recovered", predictionDisappeared ? "prediction-removed" : "exact", focused,
                        session.Range.ExpectedText.Length, received.Text.Length, pending.StartedAt, pending);
                RetainSnapshot(session, epoch);
                return true;
            }
        }
        catch (Exception error) when (IsTargetError(error)) { }
        // 只能为同一焦点中的发送前原状或目标快照的精确在途前缀继续等待。
        // 已失焦、不可读或出现另一份正文时不进入额外收尾循环，也不借此重新写入。
        session.PendingCanAwaitHost = focused && received is not null &&
            (received.Text == pending.InitialDisplay || session.Range.ExpectedText.StartsWith(received.Text, StringComparison.Ordinal));
        if (reportPending)
        {
            session.Pending = pending with { Reported = true };
            ReportDiagnostic(session, "confirmation-pending", MatchKind(session.Range.ExpectedText, received?.Text), focused,
                session.Range.ExpectedText.Length, received?.Text.Length ?? -1, pending.StartedAt, pending);
            RetainSnapshot(session, epoch);
        }
        return false;
    }

    private void ReportDiagnostic(DeliverySession session, string stage, string match, bool focused, int expectedLength, int actualLength, long startedAt,
        PendingWrite? write = null)
    {
        var focusIdentity = session.Target.DiagnosticIdentity + (focused ? ":same-focus" : ":focus-changed");
        var signature = stage + "/" + match + "/" + focusIdentity;
        if (session.LastDiagnosticSignature == signature) return;
        session.LastDiagnosticSignature = signature;
        ReportWriteDiagnostic(session, stage, match, focused, expectedLength, actualLength, write, startedAt);
    }

    private void ReportWriteDiagnostic(DeliverySession session, string stage, string match, bool focused, int expectedLength, int actualLength,
        PendingWrite? write, long? startedAt = null)
    {
        var focusIdentity = session.Target.DiagnosticIdentity + (focused ? ":same-focus" : ":focus-changed");
        var diagnostic = new InputDeliveryDiagnostic(stage, match, focusIdentity, expectedLength, actualLength,
            (long)Stopwatch.GetElapsedTime(startedAt ?? write!.StartedAt).TotalMilliseconds)
        {
            IsFinal = write?.IsFinal,
            PreviousSnapshotLength = write?.PreviousSnapshotLength,
            NextSnapshotLength = write?.NextSnapshotLength,
            SentSnapshotLength = write?.Cumulative.Length,
            KeptPrefixLength = write?.KeptPrefixLength,
            EditStart = write?.EditStart,
            RemovedLength = write?.RemovedLength,
            InsertedLength = write?.InsertedLength
        };
        try { Diagnostic?.Invoke(this, diagnostic); }
        // 日志订阅方失败不能中断文字交付，也不能导致已发文字再次发送。
        catch (Exception) { }
    }

    private void ReportFinalNoop(DeliverySession session, InputTextSnapshot snapshot, int cumulativeLength)
    {
        var diagnostic = new InputDeliveryDiagnostic("final-confirmed-noop", "same-snapshot",
            session.Target.DiagnosticIdentity + ":same-focus", snapshot.Text.Length, snapshot.Text.Length, 0)
        {
            IsFinal = true, PreviousSnapshotLength = cumulativeLength, NextSnapshotLength = cumulativeLength,
            SentSnapshotLength = cumulativeLength, KeptPrefixLength = cumulativeLength,
            EditStart = session.Range.Start + session.Range.ActiveDeliveredLength, RemovedLength = 0, InsertedLength = 0
        };
        try { Diagnostic?.Invoke(this, diagnostic); }
        catch (Exception) { }
    }

    internal static string MatchKind(string expected, string? actual)
    {
        if (actual is null) return "unavailable";
        if (actual == expected) return "exact";
        if (actual == expected + "\r\n") return "extra-terminal-crlf";
        if (actual == expected + "\r") return "extra-terminal-cr";
        if (actual == expected + "\n") return "extra-terminal-lf";
        if (expected.StartsWith(actual, StringComparison.Ordinal)) return "short-prefix";
        if (actual.StartsWith(expected, StringComparison.Ordinal)) return "long-prefix";
        return actual.Length == expected.Length ? "same-length-different" : "different";
    }

    private static bool IsTargetError(Exception error) => error is InvalidOperationException or
        ElementNotAvailableException or UnauthorizedAccessException or COMException or Win32Exception or ArgumentException;

    private void InstallHooks()
    {
        var module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(13, _keyboardProc, module, 0);
        if (_keyboardHook == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _mouseHook = Native.SetWindowsHookEx(14, _mouseProc, module, 0);
        if (_mouseHook == 0)
        {
            Native.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code >= 0 && Volatile.Read(ref _disposed) == 0)
        {
            var keyboard = Marshal.PtrToStructure<Native.KeyboardData>(data);
            if ((keyboard.Flags & 0x10) == 0)
            {
                var down = message == 0x100 || message == 0x104;
                var up = message == 0x101 || message == 0x105;
                if (down || up)
                {
                    if (down) Interlocked.Increment(ref _physicalRevision);
                    var key = (int)keyboard.VirtualKey;
                    if (key == 0x12)
                        key = (keyboard.Flags & 1) == 0 ? 0xA4 : 0xA5;
                    else if (key == 0x11)
                        key = (keyboard.Flags & 1) == 0 ? 0xA2 : 0xA3;
                    else if (key == 0x10)
                        key = keyboard.ScanCode == 0x36 ? 0xA1 : 0xA0;
                    // 仅设置窗口前台录入时截获按键；保留原热键注册，既不误录音也不临时丢掉占用权。
                    if (_hotkeyCapture is { } capture && Native.GetForegroundWindow() == _hotkeyCaptureWindow)
                    {
                        capture(key, down);
                        return 1;
                    }
                    var decision = _hotkey.Process(key, down);
                    if (decision.MaskAltMenu)
                        Native.MaskAltMenu();
                    if (decision.Toggle)
                        RaiseRecordingRequest(RecordingHotkeyAction.Toggle);
                    else if (decision.Start)
                        RaiseRecordingRequest(RecordingHotkeyAction.Start);
                    else if (decision.Stop)
                        RaiseRecordingRequest(RecordingHotkeyAction.Stop);
                    else if (down && !decision.Suppress && key is not 0xA4 and not 0xA5)
                        RaiseActivity(new(InputActivityKind.Keyboard, key));
                    if (decision.Suppress)
                        return 1;
                }
            }
        }
        return Native.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private nint MouseHook(int code, nint message, nint data)
    {
        if (code >= 0 && Volatile.Read(ref _disposed) == 0 &&
            message is 0x201 or 0x204 or 0x207 or 0x20B or 0x20A or 0x20E)
        {
            var mouse = Marshal.PtrToStructure<Native.MouseData>(data);
            if ((mouse.Flags & 1) == 0)
            {
                Interlocked.Increment(ref _physicalRevision);
                RaiseActivity(new(InputActivityKind.Mouse));
            }
        }
        return Native.CallNextHookEx(_mouseHook, code, message, data);
    }

    private void RaiseRecordingRequest(RecordingHotkeyAction action)
    {
        // 单工作者按顺序转发意图；短按住时也必须先开始、再结束。
        _hotkeyRequests.Enqueue(action);
        if (Interlocked.CompareExchange(ref _hotkeyRequestWorker, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            do
            {
                while (_hotkeyRequests.TryDequeue(out var request))
                {
                    if (Volatile.Read(ref _disposed) != 0) continue;
                    try { RecordingRequested?.Invoke(this, new(request)); }
                    catch (Exception) { }
                    if (request == RecordingHotkeyAction.Toggle)
                    {
                        try { ToggleRequested?.Invoke(this, EventArgs.Empty); }
                        catch (Exception) { }
                    }
                }
                Interlocked.Exchange(ref _hotkeyRequestWorker, 0);
            } while (!_hotkeyRequests.IsEmpty && Interlocked.CompareExchange(ref _hotkeyRequestWorker, 1, 0) == 0);
        });
    }

    private void RaiseActivity(InputActivityEventArgs activity)
    {
        // 空闲时仍同步记录真实键鼠版本，但无需排队触发录音交付重试。
        if (!IsRecording || Volatile.Read(ref _disposed) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!IsRecording || Volatile.Read(ref _disposed) != 0) return;
            try { InputActivity?.Invoke(this, activity); }
            catch (Exception) { }
        });
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_sessionGate)
        {
            Interlocked.Increment(ref _sessionEpoch);
            _session = null;
            _retainedSession = null;
        }
        _delivery.Dispose(); _clipboard.Dispose();
        void Cleanup()
        {
            if (_registeredHotkeyId != 0) Native.UnregisterHotKey(0, _registeredHotkeyId);
            _registeredHotkeyId = 0;
            if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = _mouseHook = 0;
            _hookDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        if (_hookDispatcher.CheckAccess()) Cleanup();
        else if (!_hookDispatcher.HasShutdownStarted)
        {
            _hookDispatcher.BeginInvoke(Cleanup);
        }
        GC.SuppressFinalize(this);
    }

    private sealed record PendingWrite(string Cumulative, string InitialDisplay, bool FirstAtEmptyCaret,
        long PhysicalRevision, long StartedAt, bool Reported = false, bool IsFinal = false, int PreviousSnapshotLength = 0,
        int NextSnapshotLength = 0, int KeptPrefixLength = 0, int EditStart = 0, int RemovedLength = 0, int InsertedLength = 0);

    private sealed class DeliverySession
    {
        public DeliverySession(IInputTextTarget target) : this(target,
            new OwnedTextRange(target.Initial.Text, target.Initial.SelectionStart, target.Initial.SelectionEnd), false, "") { }
        private DeliverySession(IInputTextTarget target, OwnedTextRange range, bool faulted, string requested)
        {
            Target = target;
            Range = range;
            Faulted = faulted;
            LastRequestedText = requested;
        }
        public IInputTextTarget Target { get; set; }
        public OwnedTextRange Range { get; set; }
        public bool Faulted { get; set; }
        public string LastRequestedText { get; set; } = "";
        public PendingWrite? Pending { get; set; }
        public string LastDiagnosticSignature { get; set; } = "";
        public bool FinalNoopReported { get; set; }
        public bool PendingCanAwaitHost { get; set; }
        public bool IsRelated(string text) => text == LastRequestedText || text == Range.DeliveredText;
        public DeliverySession Snapshot() => new(Target, Range.Snapshot(), Faulted, LastRequestedText)
        {
            Pending = Pending,
            LastDiagnosticSignature = LastDiagnosticSignature,
            FinalNoopReported = FinalNoopReported,
            PendingCanAwaitHost = PendingCanAwaitHost
        };
    }

    internal static void VerifyWriteSnapshot(InputTextSnapshot expected, InputTextSnapshot actual)
    {
        if (actual != expected) throw new TargetMovedBeforeWriteException("正文或光标在规划后发生变化。");
    }

    internal static void VerifySelectedSnapshot(string expectedText, int start, int length, InputTextSnapshot actual)
    {
        if (actual.Text != expectedText || actual.SelectionStart != start || actual.SelectionEnd != start + length)
            throw new TargetMovedBeforeWriteException("写入前正文或语音选区发生变化。");
    }

    internal static bool CanRecaptureUniqueSlot(bool allowed, bool sameParent, bool sameHost, bool sameMetadata,
        bool originalUnique, bool candidateUnique, bool exactText, bool candidateFocused) =>
        allowed && sameParent && sameHost && sameMetadata && originalUnique && candidateUnique && exactText && candidateFocused;

    internal static InputTextSnapshot ResolveUiaSnapshot(string text, int start, int end, string? editableValue)
    {
        // 仅当同一控件的可写 ValuePattern 精确证明多出的结尾符号不属于输入值时才映射。
        // 无 ValuePattern、真实用户换行或其他正文差异均保持原样，不能使用 TrimEnd 模糊吞字。
        if (editableValue is not null && (text == editableValue + "\r\n" || text == editableValue + "\r" || text == editableValue + "\n") &&
            start >= 0 && end >= start && end <= text.Length)
            return new(editableValue, Math.Min(start, editableValue.Length), Math.Min(end, editableValue.Length));
        return new(text, start, end);
    }

    private sealed class TextTarget : IInputTextTarget
    {
        private readonly AutomationElement _element;
        private readonly nint _foreground;
        private readonly nint _nativeEdit;
        private readonly int _processId;
        private readonly string _automationId, _name, _className;
        private readonly ControlType _controlType;
        private readonly AutomationElement? _identityParent;
        private readonly bool _uniqueEditableSlot;
        public InputTextSnapshot Initial { get; }
        public string DiagnosticIdentity => _nativeEdit != 0 ? "native-handle" : _automationId.Length > 0 ? "stable-automation-id" :
            _uniqueEditableSlot ? "unique-edit-slot" : "runtime-element-only";
        public bool ConfirmFirstTextElement => _nativeEdit == 0;

        private TextTarget(AutomationElement element, nint foreground, nint nativeEdit)
        {
            _element = element;
            _foreground = foreground;
            _nativeEdit = nativeEdit;
            var current = element.Current;
            _processId = current.ProcessId;
            _automationId = current.AutomationId;
            _name = current.Name;
            _className = current.ClassName;
            _controlType = current.ControlType;
            _identityParent = TreeWalker.RawViewWalker.GetParent(element);
            _uniqueEditableSlot = _automationId.Length == 0 && IsOnlyDirectPeer(_identityParent, element, _controlType);
            Initial = Read();
        }

        public static TextTarget Capture()
        {
            var foreground = Native.GetForegroundWindow();
            var element = AutomationElement.FocusedElement ?? throw new InvalidOperationException("没有输入焦点。");
            var current = element.Current;
            if (current.ProcessId == Environment.ProcessId || current.IsPassword || !current.IsEnabled || !current.HasKeyboardFocus)
                throw new InvalidOperationException("当前目标不能接收语音。");
            var hasValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var value);
            if (hasValue && ((ValuePattern)value).Current.IsReadOnly)
                throw new InvalidOperationException("目标是只读控件。");
            var handle = new nint(current.NativeWindowHandle);
            var className = new StringBuilder(256);
            if (handle != 0) Native.GetClassName(handle, className, className.Capacity);
            // 仅对标准 Edit 使用窗口消息，RichEdit 与其他框架走 UIA 验证。
            var edit = className.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase) ? handle : 0;
            if (edit == 0)
            {
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
                    throw new InvalidOperationException("目标没有可验证的文字范围。");
                var isReadOnly = ((TextPattern)textPattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                if (isReadOnly is true || current.ControlType != ControlType.Edit && !hasValue && isReadOnly is not false)
                    throw new InvalidOperationException("不能确认目标是可编辑文字控件。");
            }
            var target = new TextTarget(element, foreground, edit);
            if (!target.IsCurrentFocus())
                throw new InvalidOperationException("输入目标在捕获期间发生变化。");
            return target;
        }

        public bool IsCurrentFocus()
        {
            if (Native.GetForegroundWindow() != _foreground)
                return false;
            try
            {
                var current = AutomationElement.FocusedElement;
                return current is not null && current.Current.ProcessId == _processId && Automation.Compare(_element, current);
            }
            catch (ElementNotAvailableException) { return false; }
        }

        public IInputTextTarget? TryRecaptureOwned(string expectedText, bool allowUniqueSlot = false)
        {
            // 稳定标识或首次写入期间未发生人工操作的唯一编辑槽才允许恢复，正文相同不能单独作为身份。
            if (_identityParent is null || Native.GetForegroundWindow() != _foreground) return null;
            if (_automationId.Length == 0 && (!allowUniqueSlot || !_uniqueEditableSlot)) return null;
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is null || focused.Current.AutomationId != _automationId || focused.Current.ProcessId != _processId) return null;
                var candidate = Capture();
                var sameHost = candidate._foreground == _foreground && candidate._processId == _processId;
                var sameMetadata = candidate._automationId == _automationId && candidate._name == _name &&
                    candidate._className == _className && candidate._controlType == _controlType;
                var sameParent = candidate._identityParent is not null && Automation.Compare(candidate._identityParent, _identityParent);
                if (_automationId.Length == 0)
                    return CanRecaptureUniqueSlot(allowUniqueSlot, sameParent, sameHost, sameMetadata, _uniqueEditableSlot,
                        candidate._uniqueEditableSlot, candidate.Initial.Text == expectedText, candidate.IsCurrentFocus()) ? candidate : null;
                if (!sameHost || !sameMetadata || !sameParent || candidate.Initial.Text != expectedText) return null;
                var siblings = _identityParent.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, _automationId));
                return siblings.Count == 1 && Automation.Compare(siblings[0], candidate._element) ? candidate : null;
            }
            catch (Exception error) when (IsTargetError(error)) { return null; }
        }

        private static bool IsOnlyDirectPeer(AutomationElement? parent, AutomationElement element, ControlType type)
        {
            if (parent is null || type != ControlType.Edit && type != ControlType.Document) return false;
            // 连只读的同类兄弟也按歧义处理；不通过筛掉不确定控件来制造“唯一”。
            var peers = parent.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
            return peers.Count == 1 && Automation.Compare(peers[0], element);
        }

        public InputTextSnapshot Read()
        {
            if (_nativeEdit != 0)
            {
                var length = (int)Native.SendMessage(_nativeEdit, 0x000E, 0, 0);
                if (length is < 0 or > 1_000_000)
                    throw new InvalidOperationException("输入框过大。");
                var text = new StringBuilder(length + 1);
                Native.SendMessageText(_nativeEdit, 0x000D, text.Capacity, text);
                Native.SendMessageSelection(_nativeEdit, 0x00B0, out var start, out var end);
                return new(text.ToString(), (int)start, (int)end);
            }
            var pattern = (TextPattern)_element.GetCurrentPattern(TextPattern.Pattern);
            var document = pattern.DocumentRange;
            var content = document.GetText(-1);
            if (content.Length > 1_000_000)
                throw new InvalidOperationException("输入框过大。");
            var selection = pattern.GetSelection();
            if (selection.Length != 1)
                throw new InvalidOperationException("选区不唯一。");
            var before = document.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
            var through = document.Clone();
            through.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.End);
            var selectionStart = before.GetText(-1).Length;
            var selectionEnd = through.GetText(-1).Length;
            string? editableValue = null;
            if ((content.EndsWith('\r') || content.EndsWith('\n')) &&
                _element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) && valueObject is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
                editableValue = valuePattern.Current.Value;
            return ResolveUiaSnapshot(content, selectionStart, selectionEnd, editableValue);
        }

        public void ReplaceSelection(string text) => ReplaceOwned(Initial.SelectionStart, Initial.SelectionEnd - Initial.SelectionStart, text, Initial);

        public void ReplaceOwned(int start, int length, string text, InputTextSnapshot expected, Func<bool>? stillValid = null)
        {
            if (!IsCurrentFocus())
                throw new TargetMovedBeforeWriteException("输入目标已改变。");
            var original = Read();
            VerifyWriteSnapshot(expected, original);
            if (start < 0 || length < 0 || start + length > original.Text.Length)
                throw new TargetMovedBeforeWriteException("输入范围越界。");
            if (stillValid is not null && !stillValid())
                throw new TargetMovedBeforeWriteException("检测到同时发生的手动操作。");
            if (length == 0 && text.Length == 0) return;
            if (_nativeEdit != 0)
            {
                Native.SendMessage(_nativeEdit, 0x00B1, start, start + length);
                var selected = Read();
                VerifySelectedSnapshot(expected.Text, start, length, selected);
                if (!IsCurrentFocus() || stillValid is not null && !stillValid())
                    throw new TargetMovedBeforeWriteException("输入位置已改变。");
                // 字符串窗口消息由 Windows 处理，避免发送有延迟的逐字符累计更新。
                Native.SendMessageString(_nativeEdit, 0x00C2, 1, text);
                return;
            }
            // 常态增量已经位于正确光标处，免去全文范围移动和 Select 往返；仍保留焦点与人工操作核验。
            if (original.SelectionStart == start && original.SelectionEnd == start + length)
            {
                if (!IsCurrentFocus() || stillValid is not null && !stillValid()) throw new TargetMovedBeforeWriteException("输入位置已改变。");
                Native.TypeUnicode(text);
                return;
            }
            var pattern = (TextPattern)_element.GetCurrentPattern(TextPattern.Pattern);
            var document = pattern.DocumentRange;
            var owned = document.Clone();
            owned.MoveEndpointByRange(TextPatternRangeEndpoint.End, owned, TextPatternRangeEndpoint.Start);
            owned.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, start);
            owned.MoveEndpointByRange(TextPatternRangeEndpoint.End, owned, TextPatternRangeEndpoint.Start);
            owned.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, length);
            var before = document.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, owned, TextPatternRangeEndpoint.Start);
            var after = document.Clone();
            after.MoveEndpointByRange(TextPatternRangeEndpoint.Start, owned, TextPatternRangeEndpoint.End);
            // 某些宿主按字素、某些按 UTF-16 移动；遇到 emoji 或换行时用精确前缀定位再核验。
            if (before.GetText(-1) != original.Text[..start] || owned.GetText(-1) != original.Text.Substring(start, length) ||
                after.GetText(-1) != original.Text[(start + length)..])
            {
                var beginPoint = PointAtUtf16Offset(document, original.Text, start);
                var endPoint = PointAtUtf16Offset(document, original.Text, start + length);
                owned = document.Clone();
                owned.MoveEndpointByRange(TextPatternRangeEndpoint.Start, beginPoint, TextPatternRangeEndpoint.Start);
                owned.MoveEndpointByRange(TextPatternRangeEndpoint.End, endPoint, TextPatternRangeEndpoint.Start);
                if (owned.GetText(-1) != original.Text.Substring(start, length)) throw new TargetMovedBeforeWriteException("宿主不能精确定位语音范围。");
            }
            owned.Select();
            // Chromium 的 Select 返回时，选区更新可能仍在宿主线程队列中。
            // 只等待同一正文内的精确选区落地，期间任何焦点或人工编辑变化都立即拒绝写入。
            var selectionWait = Stopwatch.StartNew();
            while (true)
            {
                var selected = Read();
                if (!IsCurrentFocus() || selected.Text != original.Text || stillValid is not null && !stillValid())
                    throw new TargetMovedBeforeWriteException("选区在写入前发生变化。");
                if (selected.SelectionStart == start && selected.SelectionEnd == start + length) break;
                if (selectionWait.ElapsedMilliseconds >= 250)
                    throw new TargetMovedBeforeWriteException("宿主尚未确认语音选区。");
                Thread.Sleep(10);
            }
            Native.TypeUnicode(text);
        }

        private static TextPatternRange PointAtUtf16Offset(TextPatternRange document, string text, int offset)
        {
            var wanted = text[..offset];
            var low = 0;
            var high = text.Length;
            var units = StringInfo.ParseCombiningCharacters(wanted).Length;
            while (low <= high)
            {
                var probe = document.Clone();
                probe.MoveEndpointByRange(TextPatternRangeEndpoint.End, probe, TextPatternRangeEndpoint.Start);
                probe.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, units);
                var prefix = probe.GetText(-1);
                if (prefix == wanted)
                {
                    probe.MoveEndpointByRange(TextPatternRangeEndpoint.Start, probe, TextPatternRangeEndpoint.End);
                    return probe;
                }
                if (!text.StartsWith(prefix, StringComparison.Ordinal)) throw new TargetMovedBeforeWriteException("定位期间正文已变化。");
                if (prefix.Length < offset) low = units + 1;
                else high = units - 1;
                units = low + (high - low) / 2;
            }
            throw new TargetMovedBeforeWriteException("光标位置不能映射到完整字符边界。");
        }
    }

    private static class Native
    {
        internal delegate nint HookProc(int code, nint message, nint data);
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public int X, Y; public uint MouseDataValue, Flags, Time; public nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion
        {
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public MouseInput Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint window, int id);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? module);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        internal static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, StringBuilder name, int length);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] private static extern nint TimedMessage(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint TimedText(nint window, uint message, int length, StringBuilder text, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] private static extern nint TimedSelection(nint window, uint message, out uint start, out uint end, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint TimedString(nint window, uint message, int undo, string text, uint flags, uint timeout, out nint result);
        internal static nint SendMessage(nint window, uint message, nint wParam, nint lParam) { if (TimedMessage(window,message,wParam,lParam,3,200,out var result)==0) throw new Win32Exception("目标窗口无响应。"); return result; }
        internal static nint SendMessageText(nint window, uint message, int length, StringBuilder text) { if (TimedText(window,message,length,text,3,200,out var result)==0) throw new Win32Exception("目标窗口无响应。"); return result; }
        internal static nint SendMessageSelection(nint window, uint message, out uint start, out uint end) { if (TimedSelection(window,message,out start,out end,3,200,out var result)==0) throw new Win32Exception("目标窗口无响应。"); return result; }
        internal static nint SendMessageString(nint window, uint message, int undo, string text) { if (TimedString(window,message,undo,text,3,200,out var result)==0) throw new Win32Exception("目标窗口无响应。"); return result; }
        [DllImport("user32.dll")] private static extern bool OpenClipboard(nint window);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
        [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
        [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
        [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
        internal static bool CopyClipboard(string text)
        {
            // 消息窗口仅用于剪贴板所有权，不显示、不激活任何前台窗口。
            var owner = CreateWindowEx(0,"STATIC","iSay 剪贴板",0,0,0,0,0,new nint(-3),0,0,0);
            try
            {
                if (!OpenClipboard(owner)) return false;
                try
                {
                    var bytes = Encoding.Unicode.GetBytes(text + '\0');
                    var memory = GlobalAlloc(2, (nuint)bytes.Length);
                    if (memory == 0) throw new Win32Exception();
                    var transferred = false;
                    try
                    {
                        var pointer = GlobalLock(memory); if (pointer == 0) throw new Win32Exception();
                        try { Marshal.Copy(bytes,0,pointer,bytes.Length); } finally { GlobalUnlock(memory); }
                        if (!EmptyClipboard()) return false;
                        transferred = SetClipboardData(13,memory) != 0;
                        return transferred;
                    }
                    finally { if (!transferred) GlobalFree(memory); }
                }
                finally { CloseClipboard(); }
            }
            finally { if (owner != 0) DestroyWindow(owner); }
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);

        internal static void MaskAltMenu()
        {
            var input = new[] { Key(0xA2, 0, 0), Key(0xA2, 0, 2) };
            SendInput((uint)input.Length, input, Marshal.SizeOf<Input>());
        }

        internal static bool HasHeldEditingModifier() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x01, 0x02, 0x04 }
            .Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

        internal static void TypeUnicode(string text)
        {
            var input = new Input[text.Length == 0 ? 2 : text.Length * 2];
            if (text.Length == 0)
            {
                input[0] = Key(0x08, 0, 0);
                input[1] = Key(0x08, 0, 2);
            }
            else
            {
                for (var i = 0; i < text.Length; i++)
                {
                    input[i * 2] = Key(0, text[i], 4);
                    input[i * 2 + 1] = Key(0, text[i], 6);
                }
            }
            if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "宿主拒绝接收文字，可能需要相同权限。");
        }

        private static Input Key(ushort key, ushort scan, uint flags) => new()
        {
            Type = 1,
            Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, ScanCode = scan, Flags = flags } }
        };
    }
}
