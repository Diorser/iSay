namespace iSay.Voice;

// 原生识别、整理、学习的网络初始化不能重叠；暖进程和正常请求不持有此锁。
internal static class NativeSdkInitializationGate
{
    private static readonly SemaphoreSlim gate = new(1, 1);

    internal static async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }
}
