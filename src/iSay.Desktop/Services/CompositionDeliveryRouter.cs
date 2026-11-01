using System.IO;
using System.Diagnostics;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("iSay.CompositionApp.Tests")]

namespace iSay.Desktop.Services;

internal interface ICompatibilityInputDelivery
{
    Task<DeliveryResult> BeginAsync(CancellationToken cancellationToken);
    Task<DeliveryResult> UpdateAsync(string text, CancellationToken cancellationToken);
    Task<DeliveryResult> CommitAsync(string text, CancellationToken cancellationToken);
    void Cancel();
}

/// <summary>每段录音固定输入后端和目标；组合输入失败后不向普通键入转发同一段文字。</summary>
internal sealed class CompositionDeliveryRouter
{
    private readonly ICompatibilityInputDelivery compatibility;
    private readonly Func<Guid, ICompositionSession?> captureComposition;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Queue<DeliverySession> cleanupBacklog = new();
    private DeliverySession? active;
    private readonly Func<Guid, CancellationToken, Task<ICompositionSession?>>? captureEmptyComposition;
    public event Action<TsfPipeDiagnostic>? PipeDiagnostic;

    public CompositionDeliveryRouter(WindowsInputService input)
    {
        compatibility = new CompatibilityDelivery(input);
        ICompositionSession? CreateComposition(Guid id, Func<CancellationToken, Task<bool>>? emptyGuard = null)
        {
            var revision = input.PhysicalRevision;
            var endpoint = TsfEndpoint.CaptureForeground();
            var isChatGpt = endpoint is not null && RequiresInitialBatchGuard(endpoint);
            var rangeSync = endpoint is not null && input.ChromiumRangeSyncEnabled && ChromiumCompositionRangeSync.Supports(endpoint) ?
                new ChromiumCompositionRangeSync(input.CompositionExecutor, endpoint, endpoint.StillHasOriginalFocus, () => input.PhysicalRevision) : null;
            var focusGuard = endpoint is null ? null : input.CaptureCompositionFocusGuard(endpoint.StillHasOriginalFocus);
            return endpoint is null ? null : new TsfCompositionSession(new TsfCompositionClient(endpoint, value => PipeDiagnostic?.Invoke(value)), id,
                () => input.PhysicalRevision == revision && endpoint.StillHasOriginalFocus(),
                () =>
                {
                    var committedRevision = input.PhysicalRevision;
                    var originalFocusAtCommit = endpoint.StillHasOriginalFocus();
                    return () => originalFocusAtCommit && input.PhysicalRevision == committedRevision && endpoint.StillHasOriginalFocus();
                }, async token => await focusGuard!(token).ConfigureAwait(false) && (emptyGuard is null || await emptyGuard(token).ConfigureAwait(false)),
                rangeSync is null ? null : rangeSync.PrepareAsync, rangeSync?.Ready,
                guardInitialChromiumBatch: isChatGpt,
                prepareCommittedRange: !isChatGpt ? null : async (expected, token) =>
                {
                    // 仅后台整理写回时选择已核对的自有整段；连续出字不启用历史范围同步实验。
                    var finalRange = new ChromiumCompositionRangeSync(input.CompositionExecutor, endpoint,
                        endpoint.StillHasOriginalFocus, () => input.PhysicalRevision, allowUniqueEditor: true);
                    await finalRange.Ready.WaitAsync(token).ConfigureAwait(false);
                    return await finalRange.PrepareAsync(expected, token).ConfigureAwait(false);
                },
                refineParagraphs: !isChatGpt ? null : async (expected, text, guard, token) =>
                {
                    var finalRange = new ChromiumCompositionRangeSync(input.CompositionExecutor, endpoint,
                        endpoint.StillHasOriginalFocus, () => input.PhysicalRevision, allowUniqueEditor: true);
                    return await finalRange.RefineParagraphsAsync(expected, text, guard, token).ConfigureAwait(false);
                });
        }
        captureComposition = id => CreateComposition(id);
        captureEmptyComposition = async (id, token) =>
        {
            var endpoint = TsfEndpoint.CaptureForeground();
            if (endpoint is null) return null;
            Func<CancellationToken, Task<bool>>? guard;
            if (ChromiumCompositionRangeSync.Supports(endpoint))
            {
                var probe = new ChromiumCompositionRangeSync(input.CompositionExecutor, endpoint, endpoint.StillHasOriginalFocus,
                    () => input.PhysicalRevision, allowUniqueEditor: true);
                guard = await probe.CaptureEmptyGuardAsync(token).ConfigureAwait(false);
            }
            else guard = await input.CaptureEmptyTargetGuardAsync(token).ConfigureAwait(false);
            if (guard is null || !endpoint.StillHasOriginalFocus()) return null;
            return CreateComposition(id, guard);
        };
    }

    private static bool RequiresInitialBatchGuard(TsfEndpoint endpoint)
    {
        // 本轮只对已实测的 ChatGPT 桌面 Chromium 宿主启用首批保护，其他程序保持原有交付节奏。
        if (!ChromiumCompositionRangeSync.Supports(endpoint)) return false;
        try
        {
            using var process = Process.GetProcessById((int)endpoint.ProcessId);
            return process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal CompositionDeliveryRouter(ICompatibilityInputDelivery compatibility, Func<Guid, ICompositionSession?> captureComposition,
        Func<Guid, CancellationToken, Task<ICompositionSession?>>? captureEmptyComposition = null)
    {
        this.compatibility = compatibility;
        this.captureComposition = captureComposition;
        this.captureEmptyComposition = captureEmptyComposition;
    }

    public bool UsesComposition(Guid session) => Volatile.Read(ref active) is { } current && current.Id == session && current.UseComposition;
    public bool RefinementRecreatedComposition(Guid session) => Volatile.Read(ref active) is { } current &&
        current.Id == session && current.Composition?.RefinementRecreatedComposition == true;
    public ITsfLearningObserver? TryCreateLearningObserver(Guid session) => Volatile.Read(ref active) is { } current &&
        current.Id == session && current.UseComposition && current.Committed && !current.Blocked ? current.Composition?.TryCreateLearningObserver() : null;

    public async Task<DeliveryResult> BeginAsync(Guid session, bool useComposition, CancellationToken cancellationToken = default)
    {
        if (session == Guid.Empty) throw new ArgumentException("输入会话不能为空。", nameof(session));
        // 工厂在首次等待前捕获当前目标，不能等到识别首字或网络整理完成后再决定输入位置。
        ICompositionSession? composition = null;
        if (useComposition)
        {
            try { composition = captureComposition(session); }
            catch (Exception error) when (IsDeliveryError(error)) { }
        }
        var next = new DeliverySession(session, useComposition, composition, cancellationToken);
        var previous = Interlocked.Exchange(ref active, next);
        previous?.Cancellation.Cancel();
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (previous is not null) cleanupBacklog.Enqueue(previous);
            if (!await DrainCleanupAsync().ConfigureAwait(false))
            {
                next.Blocked = true;
                return DeliveryResult.Retained("上一段组合尚未确认结束；新段继续转写并保留在 iSay，暂不向输入框写字。");
            }
            if (!ReferenceEquals(Volatile.Read(ref active), next)) return Expired();
            if (useComposition && composition is null)
            {
                next.Blocked = true;
                return DeliveryResult.Retained("当前输入框未启用组合输入，录音继续，完整转写保留在 iSay。可在设置中手动选择兼容输入。");
            }
            var result = await InvokeAsync(next, token => useComposition ? BeginCompositionAsync(composition!, token) : compatibility.BeginAsync(token), cancellationToken).ConfigureAwait(false);
            next.Begun = result.Success;
            next.Blocked |= !result.Success && !result.AwaitingHost;
            if (!result.Success && result.AwaitingHost && next.RetryAt == 0)
                next.RetryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
            return result;
        }
        finally { serial.Release(); }
    }

    public Task<DeliveryResult> UpdateAsync(Guid session, string text, CancellationToken cancellationToken = default) => DeliverAsync(session, text, false, cancellationToken);
    public async Task<DeliveryResult> CommitAsync(Guid session, string text, CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            var result = await DeliverAsync(session, text, true, cancellationToken).ConfigureAwait(false);
            if (!result.Success && !result.AwaitingHost && !string.IsNullOrEmpty(text) && !cancellationToken.IsCancellationRequested &&
                await TryAdoptEmptyFinalTargetAsync(session, cancellationToken).ConfigureAwait(false)) continue;
            if (!result.AwaitingHost || cancellationToken.IsCancellationRequested || Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= 2800) return result;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryAdoptEmptyFinalTargetAsync(Guid session, CancellationToken cancellationToken)
    {
        if (captureEmptyComposition is null) return false;
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref active);
            if (current is null || current.Id != session || !current.UseComposition || current.Committed ||
                current.FinalTargetAttempted || current.WaitingConfirmation || cleanupBacklog.Count != 0 ||
                current.Token.IsCancellationRequested || current.Composition?.MayHaveWrittenText == true) return false;
            current.FinalTargetAttempted = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(1800));
            // 只有明确没有发过正文，且旧空组合已确认结束，才能在用户结束时接住当前空输入框。
            if (current.Composition is { } previous)
            {
                var cancelled = await previous.CancelAsync(deadline.Token).ConfigureAwait(false);
                if (cancelled.HResult != 0 || cancelled.State is not (TsfCompositionState.Idle or TsfCompositionState.Committed)) return false;
            }
            var replacement = await captureEmptyComposition(Guid.NewGuid(), deadline.Token).ConfigureAwait(false);
            if (replacement is null || !ReferenceEquals(active, current) || current.Token.IsCancellationRequested) return false;
            current.Composition = replacement; current.Blocked = false; current.WaitingConfirmation = false;
            current.RetryAt = 0; current.LastText = ""; current.LastUpdateSucceeded = false;
            var begin = await InvokeAsync(current, token => BeginCompositionAsync(replacement, token), deadline.Token).ConfigureAwait(false);
            current.Begun = begin.Success;
            current.Blocked |= !begin.Success && !begin.AwaitingHost;
            return begin.Success || begin.AwaitingHost;
        }
        catch (Exception error) when (IsDeliveryError(error)) { return false; }
        finally { serial.Release(); }
    }

    public async Task<DeliveryResult> RefineAsync(Guid session, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            var result = await RefineCommittedAsync(session, text, cancellationToken).ConfigureAwait(false);
            if (!result.AwaitingHost || cancellationToken.IsCancellationRequested || Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= 2800) return result;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DeliveryResult> RefineCommittedAsync(Guid session, string text, CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref active);
        if (current is null || current.Id != session) return Expired();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Token);
        try { await serial.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Expired(); }
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref active), current) || current.Cancellation.IsCancellationRequested) return Expired();
            if (!current.UseComposition || !current.Committed || current.Blocked)
                return DeliveryResult.Retained("智能整理已保留在 iSay；原输入范围未能安全更新。");
            if (current.RefinementCompleted)
                return current.LastText == text ? DeliveryResult.Delivered("智能整理已更新原输入范围") :
                    DeliveryResult.Retained("该段整理已处理，后续结果保留在 iSay。");
            if (current.RefinementText is not null && current.RefinementText != text)
                return DeliveryResult.Retained("原整理命令仍待确认，新结果保留在 iSay。");
            current.RefinementText = text;
            var result = await InvokeAsync(current, async token =>
            {
                // 仅修订已提交且仍可核验的原段；ChatGPT 在已验证的自有选区上新建事务，绝不捕获新光标补发。
                var reply = await current.Composition!.RefineAsync(text, token).ConfigureAwait(false);
                if (reply.HResult == 0 && reply.State == TsfCompositionState.Committed && reply.TextLength == text.Length)
                    return DeliveryResult.Delivered("智能整理已更新原输入范围");
                return DeliveryResult.Retained("智能整理已保留在 iSay；输入位置或文字已变化，或当前组件不支持安全更新。");
            }, linked.Token).ConfigureAwait(false);
            if (!ReferenceEquals(Volatile.Read(ref active), current)) return Expired();
            if (result.Success)
            {
                current.RefinementCompleted = true;
                current.LastText = text;
                current.WaitingConfirmation = false;
            }
            return result;
        }
        finally { serial.Release(); }
    }

    private async Task<DeliveryResult> DeliverAsync(Guid session, string text, bool final, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var current = Volatile.Read(ref active);
        if (current is null || current.Id != session) return Expired();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Token);
        try { await serial.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Expired(); }
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref active), current) || current.Cancellation.IsCancellationRequested) return Expired();
            if (current.Blocked) return DeliveryResult.Retained("原组合输入已不可用，完整转写保留在 iSay；不会改用普通键入重发。");
            if (current.RetryAt != 0 && Stopwatch.GetTimestamp() < current.RetryAt)
                // 最终提交在安全重试冷却期间也要继续有界收尾，不能误判为无需等待并放弃提交。
                return DeliveryResult.Retained(current.WaitingConfirmation ? "正在只读确认原组合命令，完整转写继续保留。" : "组合输入暂不可写，录音和转写继续。", final || current.WaitingConfirmation);
            if (current.Committed)
                return text == current.LastText ? DeliveryResult.Delivered("最终文字已在原输入位置提交") : DeliveryResult.Retained("该段文字已提交，后续整理结果保留在 iSay。");
            if (!final && current.LastUpdateSucceeded && text == current.LastText) return DeliveryResult.Delivered("组合文字已同步");
            var result = await InvokeAsync(current, async token =>
            {
                if (!current.UseComposition)
                    return final ? await compatibility.CommitAsync(text, token).ConfigureAwait(false) : await compatibility.UpdateAsync(text, token).ConfigureAwait(false);
                var reply = final ? await current.Composition!.CommitAsync(text, token).ConfigureAwait(false) : await current.Composition!.UpdateAsync(text, token).ConfigureAwait(false);
                if (reply.State is TsfCompositionState.HostTerminated or TsfCompositionState.Rejected) current.Blocked = true;
                return ConvertReply(reply, final);
            }, linked.Token, final && !current.UseComposition ? 2600 : 1200).ConfigureAwait(false);
            if (!ReferenceEquals(Volatile.Read(ref active), current)) return Expired();
            current.LastUpdateSucceeded = result.Success;
            if (result.Success)
            {
                current.Begun = true; current.RetryAt = 0; current.WaitingConfirmation = false;
                current.LastText = text;
                if (final) current.Committed = true;
            }
            return result;
        }
        finally { serial.Release(); }
    }

    public async Task CancelAsync(Guid session, CancellationToken cancellationToken = default)
    {
        var current = Volatile.Read(ref active);
        if (current is null || current.Id != session) return;
        if (!ReferenceEquals(Interlocked.CompareExchange(ref active, null, current), current)) return;
        current.Cancellation.Cancel();
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { cleanupBacklog.Enqueue(current); await DrainCleanupAsync().ConfigureAwait(false); }
        finally { serial.Release(); }
    }

    private static async Task<DeliveryResult> BeginCompositionAsync(ICompositionSession composition, CancellationToken token)
    {
        var reply = await composition.BeginAsync(token).ConfigureAwait(false);
        return reply.State == TsfCompositionState.FocusUnavailable ?
            DeliveryResult.Retained("原输入位置暂不可初始化；仅在原焦点及键鼠状态未变时稍后重试，完整转写保留。", true) : ConvertReply(reply, false);
    }

    private async Task<DeliveryResult> InvokeAsync(DeliverySession session, Func<CancellationToken, Task<DeliveryResult>> operation,
        CancellationToken cancellationToken, int milliseconds = 1200)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
        deadline.CancelAfter(milliseconds);
        try { return await operation(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (TsfConfirmationPendingException error)
        {
            if (error.Recovered) session.Begun = true;
            session.WaitingConfirmation = true;
            session.RetryAt = error.Recovered ? 0 : Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
            return DeliveryResult.Retained(error.Message, true);
        }
        catch (TsfTransportException error) when (!error.MayHaveReachedHost)
        {
            session.RetryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
            return DeliveryResult.Retained(session.Begun ? "原组合连接暂不可用，录音继续，稍后只向原范围同步。" :
                "组合连接暂不可用；仅在原焦点及键鼠状态未变时重试初始化，完整转写保留。", !session.Begun);
        }
        catch (Exception error) when (IsDeliveryError(error))
        {
            if (session.Token.IsCancellationRequested || cancellationToken.IsCancellationRequested) return Expired();
            // 发送后确认不明时不能推定没有写入，更不能切换后端发送第二份。
            session.Blocked = true;
            return DeliveryResult.Retained("组合输入响应未能确认，完整转写已保留在 iSay；不会重发已发送文字。");
        }
    }

    private async Task<bool> DrainCleanupAsync()
    {
        while (cleanupBacklog.TryPeek(out var previous))
        {
            if (!await CleanupAsync(previous).ConfigureAwait(false)) return false;
            cleanupBacklog.Dequeue();
        }
        return true;
    }

    private async Task<bool> CleanupAsync(DeliverySession session)
    {
        var cleaned = false;
        try
        {
            if (!session.UseComposition) { compatibility.Cancel(); return cleaned = true; }
            if (session.Composition is null || session.Committed) return cleaned = true;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(2600));
            while (true)
            {
                // 取消仅结束已有组合并保留正文，不能用取消旧段去删除用户内容。
                // 原编辑仍占锁时，Busy/在途不能视为成功；新 Begin 必须等待旧范围明确结束。
                try
                {
                    var reply = await session.Composition.CancelAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (reply.State == TsfCompositionState.HostTerminated && reply.HResult != unchecked((int)0x800700AA) && reply.HResult != unchecked((int)0x8000000A) ||
                        reply.HResult == 0 && reply.State is TsfCompositionState.Idle or TsfCompositionState.Committed)
                        return cleaned = true;
                    if (reply.HResult != unchecked((int)0x800700AA) && reply.HResult != unchecked((int)0x8000000A) && reply.State != TsfCompositionState.FocusUnavailable)
                        return false;
                }
                catch (TsfConfirmationPendingException) { }
                catch (TsfTransportException error) when (!error.MayHaveReachedHost && error.CanQuery) { }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (IsDeliveryError(error)) { return false; }
        finally { if (cleaned) session.Cancellation.Dispose(); }
    }

    private static DeliveryResult ConvertReply(TsfReply reply, bool final)
    {
        if (reply.HResult == 0 && reply.State == (final ? TsfCompositionState.Committed : TsfCompositionState.Composing))
            return DeliveryResult.Delivered(final ? "组合文字已提交" : "组合文字已同步");
        return DeliveryResult.Retained(reply.State == TsfCompositionState.FocusUnavailable ?
            "录音继续；返回原输入位置后续写，完整转写保留在 iSay。" :
            "宿主已结束或拒绝原组合，完整转写保留在 iSay；不会重发全文。");
    }
    private static DeliveryResult Expired() => DeliveryResult.Retained("该段输入已结束，文字保留在 iSay。");
    private static bool IsDeliveryError(Exception error) => error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException;

    private sealed class DeliverySession
    {
        public DeliverySession(Guid id, bool useComposition, ICompositionSession? composition, CancellationToken cancellationToken)
        {
            Id = id; UseComposition = useComposition; Composition = composition;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Token = Cancellation.Token;
        }
        public Guid Id { get; }
        public bool UseComposition { get; }
        public ICompositionSession? Composition { get; set; }
        public bool FinalTargetAttempted { get; set; }
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token { get; }
        public bool Blocked { get; set; }
        public bool Begun { get; set; }
        public bool WaitingConfirmation { get; set; }
        public long RetryAt { get; set; }
        public bool Committed { get; set; }
        public bool RefinementCompleted { get; set; }
        public string? RefinementText { get; set; }
        public bool LastUpdateSucceeded { get; set; }
        public string LastText { get; set; } = "";
    }
    private sealed class CompatibilityDelivery(WindowsInputService input) : ICompatibilityInputDelivery
    {
        public Task<DeliveryResult> BeginAsync(CancellationToken token) => input.BeginDeliveryAsync(token);
        public Task<DeliveryResult> UpdateAsync(string text, CancellationToken token) => input.UpdateAsync(text, false, token);
        public Task<DeliveryResult> CommitAsync(string text, CancellationToken token) => input.CompleteFinalAsync(text, token);
        public void Cancel() => input.CancelDelivery();
    }
}
