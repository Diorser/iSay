using System.Threading.Channels;

namespace iSay.Desktop.Services;

/// <summary>诊断写盘使用独立有界队列，不能拖住语音回调或界面；拥堵时丢弃诊断，不丢转写。</summary>
public sealed class BackgroundDiagnosticQueue
{
    private readonly Channel<Action> queue;
    private readonly Task worker;
    private long rejected, failed;
    public long Rejected => Interlocked.Read(ref rejected);
    public long Failed => Interlocked.Read(ref failed);

    public BackgroundDiagnosticQueue(int capacity = 128)
    {
        queue = Channel.CreateBounded<Action>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        worker = Task.Run(async () =>
        {
            await foreach (var write in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { write(); }
                catch (Exception) { Interlocked.Increment(ref failed); }
            }
        });
    }

    public bool TryEnqueue(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (queue.Writer.TryWrite(write)) return true;
        Interlocked.Increment(ref rejected);
        return false;
    }

    public async Task<bool> CompleteAsync(TimeSpan timeout)
    {
        queue.Writer.TryComplete();
        try { await worker.WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }
}
