namespace iSay.Desktop.Services;

/// <summary>按提交次序后台保存不可变快照，旧快照绝不会在新快照之后覆盖文件。</summary>
public sealed class OrderedPersistenceQueue
{
    private readonly object gate = new();
    private Task tail = Task.CompletedTask;
    private int pending;
    public int Pending => Volatile.Read(ref pending);

    public Task Enqueue(Action save)
    {
        ArgumentNullException.ThrowIfNull(save);
        lock (gate)
        {
            var previous = tail;
            Interlocked.Increment(ref pending);
            tail = Task.Run(async () =>
            {
                try
                {
                    // 单次保存失败不会堵死后续保存；每个返回任务仍向调用者报告本次错误。
                    try { await previous.ConfigureAwait(false); } catch (Exception) { }
                    save();
                }
                finally { Interlocked.Decrement(ref pending); }
            });
            return tail;
        }
    }

    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        Task current;
        lock (gate) current = tail;
        try { await current.WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }
}
