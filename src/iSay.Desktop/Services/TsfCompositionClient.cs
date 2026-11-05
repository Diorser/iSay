using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace iSay.Desktop.Services;

internal enum TsfCommand : uint { Begin = 1, Update = 2, Commit = 3, Cancel = 4, Query = 5, ResetUnsupported = 6, RecoverInitial = 7, RefineCommitted = 8, QueryRefinementSupport = 9, RecoverOwnedRange = 17 }
internal enum TsfCompositionState : uint { Idle, Composing, FocusUnavailable, HostTerminated, Committed, Rejected }
internal sealed record TsfReply(int HResult, TsfCompositionState State, int TextLength, ulong AcceptedSequence)
{
    // S_FALSE 表示命令没有完成；不能把“保留原文”当作已写入。
    public bool Applied => HResult == 0 && State is TsfCompositionState.Composing or TsfCompositionState.Committed or TsfCompositionState.Idle;
    // 宿主状态是编辑过程中的快照；在途或忙碌回复不能作为永久终态缓存。
    public bool IsPending => HResult is unchecked((int)0x8000000A) or unchecked((int)0x800700AA);
    public bool IsDefinitiveTerminal => !IsPending && State is TsfCompositionState.HostTerminated or TsfCompositionState.Rejected;
}

// 可选的编辑器范围同步；退出任何未确认写入的路径时恢复原选区，不把同步动作当作文字交付成功。
internal sealed class CompositionRangePreparation(bool canWrite, Func<bool, Task>? finish = null)
{
    public bool CanWrite { get; } = canWrite;
    public Task FinishAsync(bool applied) => finish?.Invoke(applied) ?? Task.CompletedTask;
}

/// <summary>管道诊断只含阶段、时长和长度，不记录正文或窗口身份。</summary>
internal sealed record TsfPipeDiagnostic(Guid SessionId, TsfCommand Command, ulong Sequence, int TextLength,
    string Stage, double QueueMilliseconds, double ConnectMilliseconds, double WriteMilliseconds, double ReplyMilliseconds,
    double TotalMilliseconds, TsfCompositionState? State, int? HResult, ulong? AcceptedSequence);

internal sealed class TsfTransportException(string stage, bool mayHaveReachedHost, bool canQuery, Exception inner)
    : IOException("组合输入通信失败，阶段=" + stage, inner)
{
    public bool MayHaveReachedHost { get; } = mayHaveReachedHost;
    public bool CanQuery { get; } = canQuery;
}

internal sealed class TsfConfirmationPendingException(string message, bool recovered = false) : IOException(message)
{
    public bool Recovered { get; } = recovered;
}

internal sealed record TsfEndpoint(uint ProcessId, uint ThreadId)
{
    public nint FocusWindow { get; init; }
    public string PipeName => $"iSay.Tsf.v1.{WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法获取当前用户身份。")}.{ProcessId}.{ThreadId}";

    public static TsfEndpoint? CaptureForeground()
    {
        var thread = GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        if (thread == 0 || process == 0 || process == Environment.ProcessId) return null;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Focus == 0) return null;
        var focusedThread = GetWindowThreadProcessId(info.Focus, out var focusedProcess);
        return focusedProcess != 0 && focusedProcess != Environment.ProcessId && focusedThread != 0 ? new(focusedProcess, focusedThread) { FocusWindow = info.Focus } : null;
    }

    internal bool StillHasOriginalFocus()
    {
        if (FocusWindow == 0) return false;
        var current = CaptureForeground();
        return current is not null && current.ProcessId == ProcessId && current.ThreadId == ThreadId && current.FocusWindow == FocusWindow;
    }

    internal bool HasKnownUnsafeRefinementUndo()
    {
        if (FocusWindow == 0) return false;
        var thread = GetWindowThreadProcessId(FocusWindow, out var process);
        if (thread != ThreadId || process != ProcessId) return false;
        var name = new StringBuilder(256);
        return GetClassNameW(FocusWindow, name, name.Capacity) > 0 && IsKnownUnsafeRefinementClass(name.ToString());
    }

    internal static bool IsKnownUnsafeRefinementClass(string name) =>
        name.Equals("RICHEDIT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MSFTEDIT", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("RICHEDIT20", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("RICHEDIT50", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("RichEditD2D", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("WindowsForms10.RichEdit", StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}

/// <summary>只和已在目标线程激活的 TSF 服务通信，不注入进程，也不切换系统输入法。</summary>
internal sealed class TsfCompositionClient(TsfEndpoint endpoint, Action<TsfPipeDiagnostic>? diagnostic = null)
{
    internal const uint Magic = 0x46545349, Version = 1;
    internal const int MaximumTextLength = 1024 * 1024;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly TsfEndpoint target = endpoint;
    private readonly long? ownerStartedUtcTicks = ReadOwnerStartTime(endpoint.ProcessId);

    private static long? ReadOwnerStartTime(uint processId)
    {
        try { using var owner = Process.GetProcessById(checked((int)processId)); return owner.StartTime.ToUniversalTime().Ticks; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    internal bool OwnerHasExited()
    {
        try
        {
            using var owner = Process.GetProcessById(checked((int)target.ProcessId));
            return owner.HasExited || ownerStartedUtcTicks is { } started && owner.StartTime.ToUniversalTime().Ticks != started;
        }
        catch (ArgumentException) { return true; }
        // 无权读取不是进程退出的证明；不能仅凭管道超时丢弃仍存活的原组合。
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal bool HasKnownUnsafeRefinementUndo() => target.HasKnownUnsafeRefinementUndo();
    internal ITsfLearningObserver CreateLearningObserver() => new TsfLearningClient(target);

    public async Task<TsfReply> SendAsync(TsfCommand command, Guid session, ulong sequence, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(command) || session == Guid.Empty || sequence == 0 || text.Length > MaximumTextLength || text.Contains('\0'))
            throw new ArgumentException("组合输入命令、会话、序号或正文长度无效。");
        // 不允许无效代理项穿过协议后变成替代字符，导致双方正文快照不一致。
        var encoding = new UnicodeEncoding(false, false, true);
        var payload = encoding.GetBytes(text);
        var requestedAt = Stopwatch.GetTimestamp();
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        var enteredAt = Stopwatch.GetTimestamp();
        var connectedAt = enteredAt; var writtenAt = enteredAt; var repliedAt = enteredAt;
        var stage = "connect"; var mayHaveReachedHost = false;
        TsfReply? result = null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", target.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(100, cancellationToken).ConfigureAwait(false);
            connectedAt = Stopwatch.GetTimestamp();
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverProcess) || serverProcess != target.ProcessId)
                throw new UnauthorizedAccessException("组合输入端点不属于已记录的目标进程。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
            var header = new byte[40];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), Version);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)command);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)text.Length);
            session.TryWriteBytes(header.AsSpan(16, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), sequence);
            stage = "write";
            // WriteAsync 即便抛错也可能已发送半帧；从这里开始只能查询确认，不能假定零写入。
            mayHaveReachedHost = true;
            await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            if (payload.Length != 0) await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            writtenAt = Stopwatch.GetTimestamp(); stage = "reply";
            var response = new byte[32];
            await pipe.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            repliedAt = Stopwatch.GetTimestamp(); stage = "validate";
            if (BinaryPrimitives.ReadUInt32LittleEndian(response) != Magic || BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4)) != Version ||
                BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(20)) != 0)
                throw new InvalidDataException("组合输入回复格式无效。");
            var state = (TsfCompositionState)BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(12));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(16));
            var accepted = BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(24));
            if (!Enum.IsDefined(state) || length > MaximumTextLength || accepted > sequence)
                throw new InvalidDataException("组合输入回复中的状态、长度或序号无效。");
            result = new TsfReply(BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(8)), state, (int)length, accepted);
            if (command is not TsfCommand.Query and not TsfCommand.QueryRefinementSupport && result.Applied && accepted != sequence) throw new InvalidDataException("组合输入回复未确认当前命令。");
            stage = "completed";
            return result;
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (error is InvalidDataException or UnauthorizedAccessException) throw;
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new TsfTransportException(stage, mayHaveReachedHost, error is not InvalidDataException and not UnauthorizedAccessException, error);
        }
        finally
        {
            var endedAt = Stopwatch.GetTimestamp();
            serial.Release();
            if (connectedAt == enteredAt) connectedAt = endedAt;
            if (writtenAt == enteredAt) writtenAt = stage == "write" ? endedAt : connectedAt;
            if (repliedAt == enteredAt) repliedAt = stage == "reply" ? endedAt : writtenAt;
            try
            {
                diagnostic?.Invoke(new(session, command, sequence, text.Length, stage,
                    Stopwatch.GetElapsedTime(requestedAt, enteredAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(enteredAt, connectedAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(connectedAt, writtenAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(writtenAt, repliedAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(requestedAt, endedAt).TotalMilliseconds, result?.State, result?.HResult, result?.AcceptedSequence));
            }
            catch (Exception) { }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint processId);
}

/// <summary>同一录音段绑定同一目标和递增序号；遇到不确定结果绝不改用普通键入重发。</summary>
internal interface ICompositionSession
{
    // 保留正文可能已写入的事实；不能用后来的拒绝回复抹掉此前已确认的正文。
    bool MayHaveWrittenText => true;
    bool RefinementRecreatedComposition => false;
    ITsfLearningObserver? TryCreateLearningObserver() => null;
    Task<TsfReply> BeginAsync(CancellationToken cancellationToken = default);
    Task<TsfReply> UpdateAsync(string text, CancellationToken cancellationToken = default);
    Task<TsfReply> CommitAsync(string text, CancellationToken cancellationToken = default);
    // 旧实现不能安全改写已提交范围时直接拒绝，不建立新组合或退回普通键入。
    Task<TsfReply> RefineAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TsfReply(unchecked((int)0x80004001), TsfCompositionState.Rejected, 0, 0));
    Task<TsfReply> CancelAsync(CancellationToken cancellationToken = default);
    Task<TsfReply> QueryAsync(CancellationToken cancellationToken = default);
}

internal sealed class TsfCompositionSession(TsfCompositionClient client, Guid session, Func<bool>? canRetryBegin = null,
    Func<Func<bool>>? captureRefinementGuard = null, Func<CancellationToken, Task<bool>>? canWriteOriginal = null,
    Func<string, CancellationToken, Task<CompositionRangePreparation?>>? prepareRange = null,
    Task? rangeReady = null, bool guardInitialChromiumBatch = false,
    Func<string, CancellationToken, Task<CompositionRangePreparation?>>? prepareCommittedRange = null,
    Func<string, string, Func<bool>, CancellationToken, Task<bool>>? refineParagraphs = null,
    Func<CancellationToken, Task<bool>>? canBegin = null) : ICompositionSession
{
    private bool mayHaveWrittenText;
    public bool MayHaveWrittenText => mayHaveWrittenText;
    public bool RefinementRecreatedComposition => replacementLearningSession is not null || paragraphRefined;
    public ITsfLearningObserver? TryCreateLearningObserver() => !committedSuccessfully || paragraphRefined ? null : replacementLearningSession is { } actual ?
        new RefinementLearningObserver(client.CreateLearningObserver(), session, actual) : client.CreateLearningObserver();
    private Guid? replacementLearningSession;
    private bool paragraphRefined;
    private const int PendingHResult = unchecked((int)0x8000000A);
    private const int ConfirmationBudgetMilliseconds = 2500;
    private readonly SemaphoreSlim serial = new(1, 1);
    private ulong sequence;
    private bool started, completed, uncertain, terminated, beginAttempted, initialRecoveryAttempted;
    private bool absentBeginRetryAttempted;
    private long initialRecoveryWaitStartedAt;
    private bool committedSuccessfully, refinementAttempted;
    private Func<bool>? refinementGuard;
    private string? refinementText;
    private int acknowledgedNonemptyUpdates;
    private long firstUpdateAcknowledgedAt;
    private bool beganEmpty, initialBatchGuardCompleted;
    private PendingWrite? pending;
    private string? acknowledgedText;
    private TsfReply? writeFailure;
    private TsfReply last = new(0, TsfCompositionState.Idle, 0, 0);

    public async Task<TsfReply> BeginAsync(CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await BeginCore(cancellationToken).ConfigureAwait(false); }
        finally { serial.Release(); }
    }

    private async Task<TsfReply> BeginCore(CancellationToken cancellationToken)
    {
        if (pending is not null) return await ConfirmPendingCore(cancellationToken).ConfigureAwait(false);
        if (writeFailure is not null) return writeFailure;
        if (completed || uncertain || terminated) return Rejected();
        if (started) return last;
        // 只有原焦点及同步键鼠版本均未变化才重试空 Begin；不能把迟到结果送往同线程的另一输入框。
        if (beginAttempted && canRetryBegin is not null && !canRetryBegin()) { terminated = true; return last = Rejected(); }
        beginAttempted = true;
        // 避免无障碍树首次初始化与空组合建立交错，造成宿主把灰色预测纳入初始组合缓存。
        if (rangeReady is not null) await rangeReady.WaitAsync(cancellationToken).ConfigureAwait(false);
        // 结束时接入的新框在探测和清理旧段后仍可能被改动，建立组合前也要再验证。
        if (canBegin is not null && !await canBegin(cancellationToken).ConfigureAwait(false)) return last = Rejected();
        return await SubmitCore(TsfCommand.Begin, "", cancellationToken).ConfigureAwait(false);
    }

    public Task<TsfReply> UpdateAsync(string text, CancellationToken cancellationToken = default) => ApplyAsync(text, false, cancellationToken);
    public Task<TsfReply> CommitAsync(string text, CancellationToken cancellationToken = default) => ApplyAsync(text, true, cancellationToken);

    public async Task<TsfReply> RefineAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 已可能发送的整理命令只查询原序号；焦点变化也不能把未知结果推定为未写入。
            if (pending is { Command: TsfCommand.RefineCommitted } previous)
            {
                var confirmation = await ConfirmPendingCore(cancellationToken).ConfigureAwait(false);
                return previous.Text == text ? confirmation : Rejected();
            }
            if (!completed || !committedSuccessfully || uncertain || terminated || writeFailure is not null) return Rejected();
            if (refinementAttempted)
                return refinementText == text && acknowledgedText == text && last.HResult == 0 && last.State == TsfCompositionState.Committed ? last : Rejected();
            // 此处使用提交前固定的版本，不能沿用录音开始时的版本；结束热键本身也属于真实按键。
            if (refinementGuard is null || !refinementGuard()) return Rejected();
            if (text == acknowledgedText) return last;
            if (prepareCommittedRange is not null)
            {
                // 已实测 ChatGPT 会把无组合的 SetText 修订重复应用；只在结束后核验自有整段范围。
                // 实时更新不选择正文；已有前后文、用户改字或选区无法确认时保留整理结果，绝不盲写。
                refinementAttempted = true;
                refinementText = text;
                return await RefineSelectedCore(text, cancellationToken).ConfigureAwait(false);
            }
            if (client.HasKnownUnsafeRefinementUndo())
            {
                // 只有目标线程中真正加载的组件确认支持 TOM 撤销事务，才对 RichEdit 放行整理。
                var support = await client.SendAsync(TsfCommand.QueryRefinementSupport, session, Math.Max(1, sequence), "", cancellationToken).ConfigureAwait(false);
                if (support.HResult != 0 || support.State != TsfCompositionState.Committed || !refinementGuard()) return Rejected();
            }
            refinementAttempted = true;
            refinementText = text;
            return await SubmitCore(TsfCommand.RefineCommitted, text, cancellationToken).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }

    private async Task<TsfReply> RefineSelectedCore(string text, CancellationToken token)
    {
        var expected = acknowledgedText;
        if (string.IsNullOrEmpty(expected)) return Rejected();
        if (expected.IndexOfAny(['\r', '\n']) >= 0 || text.IndexOfAny(['\r', '\n']) >= 0)
        {
            // 完整段落只写回一次，不再作为持续组合发送，不以原范围继续学习整理造成的改动。
            if (refineParagraphs is null) return Rejected();
            // 外部段落事务不沿用旧原生范围；先停止该段观察，防止把 AI 整理误学为用户纠错。
            if (expected.Length > TsfLearningClient.MaximumTextLength) return Rejected();
            var observer = client.CreateLearningObserver();
            // 原生取消只接受已绑定的学习会话；先精确绑定已提交原文，不能把“未绑定”拒绝误当成已停用。
            var armed = await observer.ArmAsync(session, expected, token).ConfigureAwait(false);
            if (armed.HResult != 0 || armed.State != TsfLearningState.Watching || !refinementGuard!()) return Rejected();
            var stopped = await observer.CancelAsync(session, token).ConfigureAwait(false);
            if (stopped.HResult != 0 || stopped.State is not (TsfLearningState.Disabled or TsfLearningState.Discarded) || !refinementGuard!()) return Rejected();
            paragraphRefined = true;
            if (!await refineParagraphs(expected, text, refinementGuard!, token).ConfigureAwait(false)) return Rejected();
            acknowledgedText = text;
            return last = new(0, TsfCompositionState.Committed, text.Length, last.AcceptedSequence);
        }
        var preparation = await prepareCommittedRange!(expected, token).ConfigureAwait(false);
        if (preparation is not { CanWrite: true }) return Rejected();
        var applied = false;
        var replacementSession = Guid.NewGuid();
        var replacement = new TsfCompositionSession(client, replacementSession, refinementGuard,
            canWriteOriginal: _ => Task.FromResult(refinementGuard!()));
        try
        {
            if (!refinementGuard!()) return Rejected();
            // 独立 Begin 先让宿主在已核对原文上建立编辑状态，再于下一次锁内修订并提交。
            // 这与实时输入共用生产组合协议，不发模拟按键、不使用剪贴板，也不改原生组件。
            async Task<TsfReply> Confirm(Func<Task<TsfReply>> operation)
            {
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    try { return await operation().ConfigureAwait(false); }
                    catch (TsfConfirmationPendingException) when (timer.ElapsedMilliseconds < 2000)
                    { await Task.Delay(10, token).ConfigureAwait(false); }
                }
            }
            var begin = await Confirm(() => replacement.BeginAsync(token)).ConfigureAwait(false);
            if (!begin.Applied || begin.TextLength != expected.Length || !refinementGuard!()) return Rejected();
            // 让宿主先处理组合更新及末尾光标，再单独结束同文组合，避免一把提交时保留旧全选状态。
            var updated = await Confirm(() => replacement.UpdateAsync(text, token)).ConfigureAwait(false);
            if (!updated.Applied || updated.TextLength != text.Length || !refinementGuard!()) return Rejected();
            var result = await Confirm(() => replacement.CommitAsync(text, token)).ConfigureAwait(false);
            applied = result.HResult == 0 && result.State == TsfCompositionState.Committed && result.TextLength == text.Length;
            if (applied) { acknowledgedText = text; last = result; replacementLearningSession = replacementSession; }
            return result;
        }
        finally
        {
            if (!applied)
            {
                // 失败只清理本次组合；不尝试从光标处重发全文或退回不安全的原写回方式。
                try { await replacement.CancelAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (IsUncertain(error)) { }
            }
            await preparation.FinishAsync(applied).ConfigureAwait(false);
        }
    }

    public async Task<TsfReply> CancelAsync(CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (completed) return last;
            if (client.OwnerHasExited())
            {
                // 目标进程退出或进程号已复用，旧组合不可能继续写入，不再等待消失的管道。
                pending = null; uncertain = false; completed = true;
                return last = new(0, TsfCompositionState.Idle, 0, 0);
            }
            if (!started && !uncertain) { completed = true; return last = new(0, TsfCompositionState.Idle, 0, sequence); }
            if (terminated && !last.IsPending) return last;
            // 清理只确认原会话，不重发正文；暂态的终止外观不能永久卡住后续所有录音。
            if (terminated) { terminated = false; uncertain = true; }
            // 即使上次回复丢失，也允许明确结束该会话；Cancel 不写正文，不重新建立范围。
            // 原 Cancel 已经可能执行时只查原序号；Busy 表示尚未接受取消，不能冒充已清理。
            if (pending is not null)
            {
                var confirmation = await ConfirmPendingCore(cancellationToken, true).ConfigureAwait(false);
                if (completed || confirmation.IsDefinitiveTerminal) return confirmation;
            }
            if (uncertain && pending is null)
            {
                // 上一轮清理被调用方取消时，空 Cancel 也可能已生效；先只读证明终态再决定是否仍需取消。
                last = await client.SendAsync(TsfCommand.Query, session, Math.Max(1, sequence), "", cancellationToken).ConfigureAwait(false);
                if (TryCompleteAbsentSessionCleanup(last)) return last;
                if (last.State == TsfCompositionState.HostTerminated && last.HResult != PendingHResult && last.HResult != unchecked((int)0x800700AA))
                { terminated = true; return last; }
                if (last.HResult == 0 && last.State is TsfCompositionState.Idle or TsfCompositionState.Committed)
                { completed = true; uncertain = false; return last; }
                if (last.HResult != 0) return last;
            }
            return await SubmitCore(TsfCommand.Cancel, "", cancellationToken).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }

    public async Task<TsfReply> QueryAsync(CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pending is not null) return await ConfirmPendingCore(cancellationToken).ConfigureAwait(false);
            if (completed || !started || terminated) return last;
            var reply = await client.SendAsync(TsfCommand.Query, session, Math.Max(1, sequence), "", cancellationToken).ConfigureAwait(false);
            last = reply;
            if (reply.IsDefinitiveTerminal) terminated = true;
            return reply;
        }
        finally { serial.Release(); }
    }

    public async Task<TsfReply> ApplyAsync(string text, bool final, CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = final ? TsfCommand.Commit : TsfCommand.Update;
            // 焦点切换时先保留转写，不向仍未处理焦点通知的宿主发送正文；回原元素后再核验原范围。
            if (!completed && canWriteOriginal is not null && !await canWriteOriginal(cancellationToken).ConfigureAwait(false))
                return new(1, TsfCompositionState.FocusUnavailable, last.TextLength, last.AcceptedSequence);
            if (pending is { } previous)
            {
                var confirmation = await ConfirmPendingCore(cancellationToken).ConfigureAwait(false);
                if (terminated)
                    return previous.Command is TsfCommand.Update or TsfCommand.Commit ?
                        await RecoverInitialCore(text, final, confirmation, cancellationToken).ConfigureAwait(false) : confirmation;
                if ((previous.Command == command || previous.Command is TsfCommand.RecoverInitial or TsfCommand.RecoverOwnedRange && !final) && previous.Text == text) return confirmation;
                // 确认和下一次写入分开调度，单次调用不串接两个完整管道等待预算。
                throw new TsfConfirmationPendingException("上一条组合命令已确认，下一轮将同步最新转写。", true);
            }
            if (writeFailure is not null) return writeFailure;
            if (terminated && !completed && !uncertain) return await RecoverInitialCore(text, final, last, cancellationToken).ConfigureAwait(false);
            if (completed || uncertain || terminated) return Rejected();
            if (!final && started && acknowledgedText == text && last.HResult == 0 && last.State == TsfCompositionState.Composing) return last;
            if (!started)
            {
                var begin = await BeginCore(cancellationToken).ConfigureAwait(false);
                if (!begin.Applied) return begin;
            }
            if (guardInitialChromiumBatch && beganEmpty && acknowledgedNonemptyUpdates == 1 && !initialBatchGuardCompleted)
            {
                // 首批返回成功后，Chromium 仍可能正在移除预测节点；这时连续覆盖会把旧缓存追加到真实正文。
                // 只在首批窗口只读观察组合状态，不选中文本、不重写正文；一旦确认终止就走已有首批原范围恢复。
                var observed = await ObserveInitialBatchAsync(cancellationToken).ConfigureAwait(false);
                if (observed is not null)
                    return observed.State == TsfCompositionState.HostTerminated ?
                        await RecoverInitialCore(text, final, observed, cancellationToken).ConfigureAwait(false) : observed;
                if (canWriteOriginal is not null && !await canWriteOriginal(cancellationToken).ConfigureAwait(false))
                    return new(1, TsfCompositionState.FocusUnavailable, last.TextLength, last.AcceptedSequence);
            }
            CompositionRangePreparation? preparation = null;
            var applied = false;
            var unchangedFinal = final && acknowledgedText == text;
            try
            {
                if (prepareRange is not null && !string.IsNullOrEmpty(acknowledgedText))
                {
                    // 宿主已结束组合时必须沿用原范围恢复，不能先选中正文破坏恢复所需的折叠光标。
                    var state = await client.SendAsync(TsfCommand.Query, session, Math.Max(1, sequence), "", cancellationToken).ConfigureAwait(false);
                    if (state.IsPending) throw new TsfConfirmationPendingException("原编辑仍在处理中，只读等待，不重发正文。");
                    if (state.State == TsfCompositionState.HostTerminated && state.HResult != PendingHResult)
                    {
                        terminated = true;
                        return await RecoverInitialCore(text, final, state, cancellationToken).ConfigureAwait(false);
                    }
                    if (state.HResult != 0 || state.State != TsfCompositionState.Composing) return state;
                    preparation = await prepareRange(acknowledgedText, cancellationToken).ConfigureAwait(false);
                    if (preparation is { CanWrite: false })
                        return new(1, TsfCompositionState.FocusUnavailable, last.TextLength, last.AcceptedSequence);
                }
                var reply = await SubmitCore(command, text, cancellationToken).ConfigureAwait(false);
                applied = reply.Applied;
                // 相同正文的结束只提交而不重写，宿主可能保留同步选区；确认结束后单独清理它。
                // 普通更新已把光标移到新正文末尾，不能再发送迟到的旧选区恢复动作。
                if (unchangedFinal && reply.Applied && reply.State == TsfCompositionState.Committed && preparation is not null)
                {
                    await preparation.FinishAsync(false).ConfigureAwait(false);
                    preparation = null;
                }
                // 同步后才发生的组合结束，先恢复折叠光标，再按已有合同恢复，不放宽用户选区检查。
                if (reply.State == TsfCompositionState.HostTerminated && preparation is not null)
                {
                    await preparation.FinishAsync(false).ConfigureAwait(false);
                    preparation = null;
                }
                return reply.State == TsfCompositionState.HostTerminated ?
                    await RecoverInitialCore(text, final, reply, cancellationToken).ConfigureAwait(false) : reply;
            }
            finally
            {
                if (preparation is not null) await preparation.FinishAsync(applied).ConfigureAwait(false);
            }
        }
        finally { serial.Release(); }
    }

    private async Task<TsfReply?> ObserveInitialBatchAsync(CancellationToken token)
    {
        // 此窗口是避免首批竞态的有界节奏保护，不代表渲染端已确认正文；仍须真实宿主压力验收。
        const int observationWindowMilliseconds = 150;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (canWriteOriginal is not null && !await canWriteOriginal(token).ConfigureAwait(false))
                return new(1, TsfCompositionState.FocusUnavailable, last.TextLength, last.AcceptedSequence);
            var observed = await client.SendAsync(TsfCommand.Query, session, Math.Max(1, sequence), "", token).ConfigureAwait(false);
            if (observed.IsPending) throw new TsfConfirmationPendingException("首批编辑仍在处理中，只读等待原组合。");
            if (observed.State is TsfCompositionState.HostTerminated or TsfCompositionState.Rejected)
            {
                last = observed; terminated = true; initialBatchGuardCompleted = true;
                return observed;
            }
            if (observed.HResult != 0 || observed.State != TsfCompositionState.Composing) return observed;
            if (observed.AcceptedSequence != last.AcceptedSequence || observed.TextLength != acknowledgedText?.Length)
                throw new InvalidDataException("首批观察的序号或长度不一致，停止后续正文写入。");
            if (Stopwatch.GetElapsedTime(firstUpdateAcknowledgedAt).TotalMilliseconds >= observationWindowMilliseconds)
            {
                initialBatchGuardCompleted = true;
                return null;
            }
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    private async Task<TsfReply> RecoverInitialCore(string text, bool final, TsfReply failure, CancellationToken cancellationToken)
    {
        // 无物理输入的首快照沿用旧命令；有键鼠活动时须额外证明回到了原UIA元素，且原生原范围逐字不变。
        if (failure.State != TsfCompositionState.HostTerminated || acknowledgedNonemptyUpdates < 1 ||
            string.IsNullOrEmpty(acknowledgedText)) return failure;
        var unchangedInput = canRetryBegin?.Invoke() == true;
        if (canWriteOriginal is not null)
        {
            if (!await canWriteOriginal(cancellationToken).ConfigureAwait(false))
                return new(1, TsfCompositionState.FocusUnavailable, last.TextLength, last.AcceptedSequence);
        }
        else if (!unchangedInput) return failure;
        var firstRecovery = unchangedInput && !initialRecoveryAttempted && acknowledgedNonemptyUpdates == 1;
        var command = firstRecovery ? TsfCommand.RecoverInitial : TsfCommand.RecoverOwnedRange;
        if (firstRecovery && initialRecoveryWaitStartedAt == 0) initialRecoveryWaitStartedAt = Stopwatch.GetTimestamp();
        terminated = false;
        var recovered = await SubmitCore(command, text, cancellationToken).ConfigureAwait(false);
        if (firstRecovery && recovered.HResult == unchecked((int)0x80070015) && recovered.State == TsfCompositionState.HostTerminated &&
            canRetryBegin?.Invoke() == true && Stopwatch.GetElapsedTime(initialRecoveryWaitStartedAt).TotalMilliseconds < 700)
        {
            // 仅处理原生确认“首笔选区尚未就绪且未写入”的情况；下一轮仍复核原范围和光标。
            // 不把这一次焦点通知竞态交给上层永久封锁，也不循环发送正文或重建新目标。
            terminated = true; last = failure;
            return new(1, TsfCompositionState.FocusUnavailable, failure.TextLength, failure.AcceptedSequence);
        }
        initialRecoveryAttempted = true;
        if (canWriteOriginal is not null && recovered.HResult == 1 && recovered.State == TsfCompositionState.FocusUnavailable)
        {
            // 核验之后焦点仍可能再次改变；这次没有写入，不把短暂失焦永久记成范围损坏。
            terminated = true; last = failure;
            return recovered;
        }
        if (recovered.HResult != 0 || recovered.State is not (TsfCompositionState.Composing or TsfCompositionState.FocusUnavailable))
        {
            // 恢复失败后禁止继续写字；新组合及清理都失败时仍可能活动，必须保留真实状态供取消重试。
            // 只读查询不能清除本次写入失败，也不能把查询成功误报成正文已同步。
            var failureReply = recovered.HResult == 0 ? recovered with { HResult = unchecked((int)0x8007139F) } : recovered;
            if (recovered.State is TsfCompositionState.HostTerminated or TsfCompositionState.Rejected) terminated = true;
            else writeFailure = failureReply;
            return last = failureReply;
        }
        return final && recovered.State == TsfCompositionState.Composing ?
            await SubmitCore(TsfCommand.Commit, text, cancellationToken).ConfigureAwait(false) : recovered;
    }

    private async Task<TsfReply> SubmitCore(TsfCommand command, string text, CancellationToken cancellationToken)
    {
        // 只在第一次真正发出提交前捕获；等待旧快照确认的调用不能提前固定版本。
        if (command == TsfCommand.Commit && refinementGuard is null && captureRefinementGuard is not null)
        {
            var originalGuard = captureRefinementGuard();
            // 先固定用户编辑版本；RichEdit 另在发送整理前探测组件能力，旧版 DLL 不会被误放行。
            refinementGuard = originalGuard;
        }
        var request = new PendingWrite(command, ++sequence, text, Stopwatch.GetTimestamp());
        var previouslyWritten = mayHaveWrittenText;
        try
        {
            if (text.Length > 0) mayHaveWrittenText = true;
            last = await client.SendAsync(command, session, request.Sequence, text, cancellationToken).ConfigureAwait(false);
            // 原生端的失焦拒绝和普通更新的范围不匹配都在 SetText 前返回。只信任未接受的零正文回复。
            // 恢复命令、回复丢失以及任何曾经写入的会话都不能据此判为空。
            if (!previouslyWritten && last.TextLength == 0 && last.AcceptedSequence < request.Sequence &&
                (last.HResult == 1 && last.State == TsfCompositionState.FocusUnavailable ||
                 command is (TsfCommand.Update or TsfCommand.Commit) && last.HResult == unchecked((int)0x8007051A) &&
                 last.State == TsfCompositionState.Composing)) mayHaveWrittenText = false;
            if (command is TsfCommand.Commit or TsfCommand.RefineCommitted && last.HResult == 0 &&
                last.State == TsfCompositionState.Committed && last.TextLength != text.Length)
                throw new InvalidDataException("提交回复的正文长度不一致，未继续改写输入框。");
            if (last.HResult == PendingHResult)
            {
                pending = request; uncertain = true;
                throw new TsfConfirmationPendingException("宿主正在编辑，只读等待原命令确认。");
            }
            RecordAcknowledgement(request, last);
            return last;
        }
        catch (TsfConfirmationPendingException) { throw; }
        catch (TsfTransportException error) when (!error.MayHaveReachedHost) { throw; }
        catch (TsfTransportException error) when (error.CanQuery)
        {
            pending = request; uncertain = true;
            // 快速断连通常仍有充足单次预算；立即只读核验，避免先空等250毫秒拖慢首字。
            // 慢命令不串接第二个完整等待，交还录音计时器稍后确认。
            if (Stopwatch.GetElapsedTime(request.StartedAt).TotalMilliseconds < 100)
                return await ConfirmPendingCore(cancellationToken).ConfigureAwait(false);
            throw new TsfConfirmationPendingException("组合命令已可能写入，正在只读核验原序号。");
        }
        catch (Exception error) when (IsUncertain(error)) { uncertain = true; throw; }
    }

    private async Task<TsfReply> ConfirmPendingCore(CancellationToken cancellationToken, bool allowExpiredCleanupQuery = false)
    {
        var expected = pending!;
        var remaining = ConfirmationBudgetMilliseconds - Stopwatch.GetElapsedTime(expected.StartedAt).TotalMilliseconds;
        // 长时间待机或失焦后，允许一次有界的迟到只读确认；仍不确定则保留正文。
        // 清理可再次确认，正文恢复最多追加一次查询，不能无限延长原命令预算。
        if (remaining <= 0 && (allowExpiredCleanupQuery || !expected.LateConfirmationProbed))
        {
            if (!allowExpiredCleanupQuery) expected.LateConfirmationProbed = true;
            remaining = 700;
        }
        if (remaining <= 0)
            throw new IOException("原组合命令未在确认预算内得到证明，完整转写继续保留。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        TsfReply reply;
        try { reply = await client.SendAsync(TsfCommand.Query, session, expected.Sequence, "", deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("原组合命令未在确认预算内得到证明，完整转写继续保留。"); }
        catch (TsfTransportException error) when (error.CanQuery)
        { throw new TsfConfirmationPendingException("原组合命令暂未确认，只读查询稍后继续。"); }
        if (reply.IsPending) throw new TsfConfirmationPendingException("宿主编辑仍未结束，只读等待原命令，不重发正文。");
        if (allowExpiredCleanupQuery && TryCompleteAbsentSessionCleanup(reply)) return last;
        if (!allowExpiredCleanupQuery && expected.Command == TsfCommand.Begin && !started && !mayHaveWrittenText &&
            !absentBeginRetryAttempted && IsAbsentSessionCleanupProof(reply) && canRetryBegin?.Invoke() == true)
        {
            // 只重试确证未接受的空初始化；下一轮仍核验原焦点和键鼠版本，绝不重发已经可能写入的正文。
            absentBeginRetryAttempted = true; pending = null; uncertain = false;
            last = new(0, TsfCompositionState.Idle, 0, 0);
            throw new TsfConfirmationPendingException("空初始化未被接受，下一轮仅在原焦点未变时重试一次。", true);
        }
        if (reply.IsDefinitiveTerminal)
        {
            pending = null; uncertain = false; terminated = true; last = reply; return reply;
        }
        var expectedState = expected.Command == TsfCommand.Cancel ? reply.State is TsfCompositionState.Committed or TsfCompositionState.Idle :
            expected.Command is TsfCommand.Commit or TsfCommand.RefineCommitted ? reply.State == TsfCompositionState.Committed :
            reply.State is TsfCompositionState.Composing or TsfCompositionState.FocusUnavailable;
        // 依靠原会话和确切命令序号证明原快照；相同长度或旧序号都不足以解除不确定状态。
        // Begin 保留原选区、Cancel 保留已显示正文，二者并不以请求的空正文替换范围。
        var expectedLength = expected.Command is TsfCommand.Begin or TsfCommand.Cancel || reply.TextLength == expected.Text.Length;
        if (reply.HResult == 0 && reply.AcceptedSequence == expected.Sequence && expectedLength && expectedState)
        {
            pending = null; uncertain = false; last = reply;
            RecordAcknowledgement(expected, reply);
            return reply;
        }
        if (reply.HResult == PendingHResult || reply.HResult == 0 && reply.AcceptedSequence < expected.Sequence)
            throw new TsfConfirmationPendingException("宿主尚未确认原命令，只读等待，不重发正文。");
        throw new IOException("原组合回复的序号、长度或状态不一致，未继续改写输入框。");
    }

    private bool TryCompleteAbsentSessionCleanup(TsfReply reply)
    {
        // 原生查询在待授锁或重入时先返回在途/忙碌；会话不匹配才返回拒绝访问且确认序号为零。
        // 初始化取消后可能仍保留上一段的已提交状态，不能把这类回复永久留在全局清理队列。
        // 仅用于清理，且要求没有活动组合；绝不将它作为旧正文写入成功的证明，也不向其他会话发取消。
        if (!IsAbsentSessionCleanupProof(reply)) return false;
        pending = null; uncertain = false; completed = true;
        last = new(0, TsfCompositionState.Idle, 0, 0);
        return true;
    }

    private static bool IsAbsentSessionCleanupProof(TsfReply reply) => reply.AcceptedSequence == 0 &&
        (reply.HResult == 0 && reply.State == TsfCompositionState.Idle ||
        reply.HResult == unchecked((int)0x80070005) && reply.State is TsfCompositionState.Idle or TsfCompositionState.Committed);

    private void RecordAcknowledgement(PendingWrite request, TsfReply reply)
    {
        if (reply.IsDefinitiveTerminal) terminated = true;
        if (reply.HResult == 0 && reply.AcceptedSequence == request.Sequence)
        {
            if (request.Command == TsfCommand.Begin && reply.State is TsfCompositionState.Composing or TsfCompositionState.FocusUnavailable)
            { started = true; beganEmpty = reply.TextLength == 0; }
            if (request.Command == TsfCommand.Commit && reply.State == TsfCompositionState.Committed && reply.TextLength == request.Text.Length)
            { completed = true; committedSuccessfully = true; }
            if (request.Command == TsfCommand.Cancel && reply.State is TsfCompositionState.Committed or TsfCompositionState.Idle)
            { completed = true; pending = null; uncertain = false; }
            if (request.Command is TsfCommand.Update or TsfCommand.Commit or TsfCommand.RecoverInitial or TsfCommand.RecoverOwnedRange or TsfCommand.RefineCommitted &&
                reply.State is TsfCompositionState.Composing or TsfCompositionState.Committed or TsfCompositionState.FocusUnavailable)
            {
                acknowledgedText = request.Text;
                if (request.Command == TsfCommand.Update && request.Text.Length != 0)
                {
                    if (acknowledgedNonemptyUpdates == 0) firstUpdateAcknowledgedAt = Stopwatch.GetTimestamp();
                    acknowledgedNonemptyUpdates++;
                }
            }
            if (request.Command is TsfCommand.RecoverInitial or TsfCommand.RecoverOwnedRange && reply.State is TsfCompositionState.Composing or TsfCompositionState.FocusUnavailable)
            { started = true; terminated = false; }
        }
    }

    private TsfReply Rejected() => new(unchecked((int)0x8007139F), TsfCompositionState.Rejected, last.TextLength, sequence);
    private static bool IsUncertain(Exception error) => error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException;
    private sealed record PendingWrite(TsfCommand Command, ulong Sequence, string Text, long StartedAt)
    {
        public bool LateConfirmationProbed { get; set; }
    }
}
