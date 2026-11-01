using System.Collections.Concurrent;

namespace iSay.Desktop.Services;

// 将可能卡在宿主 COM 提供方的调用与界面隔离，超时任务失效且挂起线程数量有上限。
public sealed class BoundedInputExecutor : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly TimeSpan timeout;
    private readonly int maximumAbandoned;
    private readonly List<Worker> workers = [];
    private Worker? active;
    private bool disposed;
    public BoundedInputExecutor(TimeSpan? timeout = null, int maximumAbandoned = 2)
    {
        this.timeout = timeout ?? TimeSpan.FromMilliseconds(1000);
        this.maximumAbandoned = maximumAbandoned;
    }
    public int WorkerCount { get { lock (sync) return workers.Count(x => x.Thread.IsAlive); } }
    public async Task<T> RunAsync<T>(Func<CancellationToken, T> callback, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        Worker? worker = null;
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                workers.RemoveAll(x => !x.Thread.IsAlive);
                if (active is null)
                {
                    if (workers.Count >= maximumAbandoned + 1) throw new TimeoutException("目标输入程序连续无响应；录音继续，文字保留在 iSay。请重启 iSay 后重试自动输入。");
                    active = new Worker(); workers.Add(active);
                }
                worker = active;
            }
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = completion.Task.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            worker.Queue.Add(() =>
            {
                try { token.ThrowIfCancellationRequested(); completion.TrySetResult(callback(token)); }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            try { return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                lock (sync)
                {
                    if (ReferenceEquals(active, worker)) { active = null; worker.Queue.CompleteAdding(); }
                }
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("目标输入程序响应超时；录音继续，完整结果保留在 iSay。");
            }
        }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true;
            foreach (var worker in workers) worker.Queue.CompleteAdding();
        }
        shutdown.Cancel();
    }
    private sealed class Worker
    {
        public BlockingCollection<Action> Queue { get; } = new();
        public Thread Thread { get; }
        public Worker()
        {
            Thread = new Thread(() => { foreach (var action in Queue.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "iSay 有界输入工作线程" };
            // UIA客户端按微软建议使用独立MTA线程，避免与目标的界面线程相互等待。
            Thread.SetApartmentState(ApartmentState.MTA); Thread.Start();
        }
    }
}
