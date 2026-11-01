using System.Threading.Channels;

namespace iSay.Voice;

// 学习请求在单独队列执行；调用方无需等待它结束才提交文字或开始下段录音。
public sealed class VocabularyLearningCoordinator : IAsyncDisposable
{
    public const int MaximumPendingRequests = 32;
    public const int MaximumFinalTextLength = 65536;
    private readonly VocabularyLearningStore store;
    private readonly IVocabularyLearningService service;
    private readonly IModificationLearningService? modificationService;
    private readonly Func<bool>? isEnabled;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource generation = new();
    private readonly Channel<PendingLearning> queue;
    private readonly Task worker;
    private bool enabled;
    private bool disposed;
    private long generationNumber;
    private SubmissionEpoch submissionEpoch;

    public VocabularyLearningCoordinator(VocabularyLearningStore store, IVocabularyLearningService service,
        Func<bool>? isEnabled = null, IModificationLearningService? modificationService = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.modificationService = modificationService ?? service as IModificationLearningService;
        this.isEnabled = isEnabled;
        enabled = isEnabled?.Invoke() ?? false;
        submissionEpoch = new(generationNumber, generation.Token);
        queue = Channel.CreateBounded<PendingLearning>(new BoundedChannelOptions(MaximumPendingRequests)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        worker = Task.Run(ProcessQueueAsync);
    }

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (disposed || enabled == value) return;
            enabled = value;
            generationNumber++;
            var previous = generation;
            generation = new();
            Volatile.Write(ref submissionEpoch, new(generationNumber, generation.Token));
            previous.Cancel();
            previous.Dispose();
        }
    }

    public Task<VocabularyLearningResult> LearnFinalAsync(VocabularyNerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || !ValidText(request.FinalText) || !ValidContext(request.EventId, request.AppName))
            return Task.FromResult(new VocabularyLearningResult(VocabularyLearningStatus.InvalidRequest, ErrorCode: "invalid_final_request"));
        return Enqueue(request, null, cancellationToken);
    }

    // 调用方必须明确提供原文本与修正文本；本类不监听输入框，也不猜测用户修改范围。
    public Task<VocabularyLearningResult> LearnModificationAsync(VocabularyModifyPairRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || !ValidText(request.SourceText) || !ValidText(request.TargetText) ||
            !ValidContext(request.EventId, request.AppName))
            return Task.FromResult(new VocabularyLearningResult(VocabularyLearningStatus.InvalidRequest, ErrorCode: "invalid_modification_request"));
        if (modificationService is null)
            return Task.FromResult(new VocabularyLearningResult(VocabularyLearningStatus.ServiceFailed, ErrorCode: "modification_service_unavailable"));
        return Enqueue(null, request, cancellationToken);
    }

    private Task<VocabularyLearningResult> Enqueue(VocabularyNerRequest? final, VocabularyModifyPairRequest? modification,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) || cancellationToken.IsCancellationRequested)
            return Task.FromResult(new VocabularyLearningResult(VocabularyLearningStatus.Cancelled));
        if (!EnabledNow()) return Task.FromResult(new VocabularyLearningResult(VocabularyLearningStatus.Disabled));
        // 入队不取持久事务锁：即便词库正在等待磁盘或其他进程，也不阻塞录音结束键。
        var epoch = Volatile.Read(ref submissionEpoch);
        var completion = new TaskCompletionSource<VocabularyLearningResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingLearning(final, modification, epoch.Generation, epoch.Token, cancellationToken, completion);
        if (!queue.Writer.TryWrite(pending)) completion.SetResult(Volatile.Read(ref disposed) ?
            new(VocabularyLearningStatus.Cancelled) : new(VocabularyLearningStatus.QueueFull, ErrorCode: "learning_queue_full"));
        return completion.Task;
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var pending in queue.Reader.ReadAllAsync())
        {
            var result = await ProcessAsync(pending).ConfigureAwait(false);
            pending.Completion.TrySetResult(result);
        }
    }

    private async Task<VocabularyLearningResult> ProcessAsync(PendingLearning pending)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token,
            pending.GenerationToken, pending.RequestToken);
        try
        {
            lock (gate)
            {
                if (disposed || linked.IsCancellationRequested) return new(VocabularyLearningStatus.Cancelled);
                if (pending.Generation != generationNumber || !EnabledNow()) return new(VocabularyLearningStatus.Disabled);
            }
            var serviceTask = pending.Final is not null ? service.ExtractAsync(pending.Final, linked.Token) :
                modificationService!.ExtractModificationAsync(pending.Modification!, linked.Token);
            var response = await serviceTask.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (response is null || !response.Success)
                return new(VocabularyLearningStatus.ServiceFailed, ErrorCode: "ner_service_failed");
            if (string.IsNullOrWhiteSpace(response.ResponseJson))
                return new(VocabularyLearningStatus.InvalidResponse, ErrorCode: "empty_ner_response");
            lock (gate)
            {
                // 关闭开关和提交共用锁，服务即便忽略取消返回迟到结果也不能写入。
                if (disposed || linked.IsCancellationRequested) return new(VocabularyLearningStatus.Cancelled);
                if (pending.Generation != generationNumber || !EnabledNow()) return new(VocabularyLearningStatus.Disabled);
                return pending.Final is not null ? store.ApplyNerResponse(pending.Final.EventId, response.ResponseJson, linked.Token) :
                    store.ApplyModifyPairResponse(pending.Modification!.EventId, response.ResponseJson, linked.Token);
            }
        }
        catch (OperationCanceledException) { return new(VocabularyLearningStatus.Cancelled); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new(VocabularyLearningStatus.StorageFailed, ErrorCode: "learning_storage_failed"); }
        catch (Exception) { return new(VocabularyLearningStatus.ServiceFailed, ErrorCode: "ner_service_failed"); }
    }

    private bool EnabledNow()
    {
        if (!Volatile.Read(ref enabled)) return false;
        try { return isEnabled?.Invoke() ?? true; }
        catch { return false; }
    }

    private static bool ValidContext(string eventId, string appName) => VocabularyLearningStore.ValidEventId(eventId) &&
        appName is not null && appName.Length <= 256 && !appName.Any(char.IsControl);

    private static bool ValidText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumFinalTextLength) return false;
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (value == '\uFFFD' || char.IsControl(value) && value is not ('\n' or '\r' or '\t')) return false;
            if (char.IsHighSurrogate(value))
            { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false; }
            else if (char.IsLowSurrogate(value)) return false;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                enabled = false;
                generationNumber++;
                generation.Cancel();
                shutdown.Cancel();
                queue.Writer.TryComplete();
            }
        }
        await worker.ConfigureAwait(false);
        // Dispose可能被重复调用；TokenSource仅在worker结束后释放一次。
        lock (gate)
        {
            if (!sourcesDisposed)
            {
                sourcesDisposed = true;
                generation.Dispose();
                shutdown.Dispose();
            }
        }
    }

    private bool sourcesDisposed;
    private sealed record SubmissionEpoch(long Generation, CancellationToken Token);
    private sealed record PendingLearning(VocabularyNerRequest? Final, VocabularyModifyPairRequest? Modification, long Generation,
        CancellationToken GenerationToken, CancellationToken RequestToken,
        TaskCompletionSource<VocabularyLearningResult> Completion);
}
