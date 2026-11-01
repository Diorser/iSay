namespace iSay.Desktop.Services;

/// <summary>将驱动停止、回调收尾和释放串行放到后台，超时后保留原任务，禁止并发释放设备。</summary>
public sealed class AudioDeviceShutdown
{
    private int pending;
    public bool IsPending => Volatile.Read(ref pending) != 0;

    public async Task RunAsync(Action stop, Task stopped, Action release, TimeSpan timeout)
    {
        Interlocked.Increment(ref pending);
        var cleanup = Task.Run(async () =>
        {
            try
            {
                try
                {
                    stop();
                    await stopped.ConfigureAwait(false);
                }
                finally { release(); }
            }
            finally { Interlocked.Decrement(ref pending); }
        });
        // 等待期限仅约束调用方；驱动没返回时不再发第二次停止或释放。
        _ = cleanup.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await cleanup.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException error) { throw new TimeoutException("麦克风驱动尚未完成停止或释放，转写已保留；界面可继续使用，设备恢复前暂不开始新录音。", error); }
    }
}
