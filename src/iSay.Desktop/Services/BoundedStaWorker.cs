using System.Windows.Threading;

namespace iSay.Desktop.Services;

/// <summary>将可能卡在外部 COM 提供方的调用隔离在有界 STA 工作线程中。</summary>
public sealed class BoundedStaWorker : IDisposable
{
    private readonly object sync = new();
    private readonly string name;
    private readonly int maximumAbandoned;
    private readonly List<Worker> abandoned = [];
    private Worker? current;
    private bool disposed;

    public BoundedStaWorker(string name, int maximumAbandoned = 2)
    {
        this.name = name;
        this.maximumAbandoned = maximumAbandoned;
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, T> action, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Worker worker;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            abandoned.RemoveAll(item => !item.Thread.IsAlive);
            if (abandoned.Count >= maximumAbandoned) throw new TimeoutException("输入提供方多次无响应，自动写入已暂停；录音和转写仍可继续。");
            worker = current ??= new Worker(name);
        }
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(timeout);
        DispatcherOperation<T>? operation = null;
        try
        {
            var dispatcher = await worker.Ready.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            operationCancellation.Token.ThrowIfCancellationRequested();
            operation = dispatcher.InvokeAsync(() => action(operationCancellation.Token), DispatcherPriority.Normal, operationCancellation.Token);
            return await operation.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            operationCancellation.Cancel();
            if (operation is not null && !operation.Task.IsCompleted)
            {
                // Abort 只能撤回尚未运行的任务；已经卡住的线程退出后会自行结束。
                operation.Abort();
                if (!operation.Task.IsCompleted) Abandon(worker);
                _ = operation.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
            else if (!worker.Ready.Task.IsCompleted) Abandon(worker);
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("输入目标响应超时，结果已保留；不会补发旧文字。", error);
            throw;
        }
    }

    private void Abandon(Worker worker)
    {
        lock (sync)
        {
            if (ReferenceEquals(current, worker)) current = null;
            if (!abandoned.Contains(worker)) abandoned.Add(worker);
        }
        worker.RequestShutdown();
    }

    public void Dispose()
    {
        Worker[] workers;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            workers = abandoned.Concat(current is null ? [] : new[] { current }).ToArray();
            current = null;
        }
        // 不等待不受控制的 UIA 提供方，否则退出也会卡住。
        foreach (var worker in workers) worker.RequestShutdown();
    }

    private sealed class Worker
    {
        public Thread Thread { get; }
        public TaskCompletionSource<Dispatcher> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int stopping;
        public Worker(string name)
        {
            Thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                Ready.TrySetResult(dispatcher);
                if (Volatile.Read(ref stopping) == 0) Dispatcher.Run();
            }) { IsBackground = true, Name = name };
            Thread.SetApartmentState(ApartmentState.STA);
            Thread.Start();
        }
        public void RequestShutdown()
        {
            Interlocked.Exchange(ref stopping, 1);
            if (Ready.Task.IsCompletedSuccessfully)
            {
                var dispatcher = Ready.Task.Result;
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        }
    }
}
