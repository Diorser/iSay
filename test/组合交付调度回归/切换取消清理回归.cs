using System.Reflection;
using iSay.Desktop.Services;

internal static class 切换取消清理回归
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var cancelOnly in new[] { false, true })
        {
            var previous = new CompositionMemory();
            var selected = previous;
            var router = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => selected);
            var id = Guid.NewGuid();
            await router.BeginAsync(id, true); await router.UpdateAsync(id, "已经显示的旧段");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            selected = new CompositionMemory();
            try
            {
                if (cancelOnly) await router.CancelAsync(id, cancelled.Token);
                else await router.BeginAsync(Guid.NewGuid(), true, cancelled.Token);
            }
            catch (OperationCanceledException) { }
            selected = new CompositionMemory();
            var next = Guid.NewGuid();
            check((await router.BeginAsync(next, true)).Success && previous.Cancels == 1 && previous.Text == "已经显示的旧段",
                (cancelOnly ? "取消录音" : "切换录音") + "在等待串行锁前被取消，也不能丢失旧会话清理责任");
            check((await router.UpdateAsync(next, "下一段仍可写入")).Success &&
                (await router.CommitAsync(next, "下一段仍可写入")).Success,
                "取消竞态后新段正常输入且唯一提交");
        }

        // 控制调度锁复现取消发生在等待期间，而不是仅测试已取消令牌。
        var old = new CompositionMemory(); var target = old;
        var waitingRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => target);
        var oldId = Guid.NewGuid(); await waitingRouter.BeginAsync(oldId, true);
        var serial = (SemaphoreSlim)typeof(CompositionDeliveryRouter).GetField("serial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(waitingRouter)!;
        await serial.WaitAsync();
        using var token = new CancellationTokenSource();
        target = new CompositionMemory();
        var switching = waitingRouter.BeginAsync(Guid.NewGuid(), true, token.Token);
        token.Cancel(); serial.Release();
        try { await switching; } catch (OperationCanceledException) { }
        target = new CompositionMemory();
        check((await waitingRouter.BeginAsync(Guid.NewGuid(), true)).Success && old.Cancels == 1,
            "串行锁等待中取消不会留下阻塞下一段的孤儿组合");

        var cancelTarget = new CompositionMemory();
        var cancelRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => cancelTarget);
        var cancelId = Guid.NewGuid(); await cancelRouter.BeginAsync(cancelId, true);
        var cancelSerial = (SemaphoreSlim)typeof(CompositionDeliveryRouter).GetField("serial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cancelRouter)!;
        await cancelSerial.WaitAsync(); using var cancelToken = new CancellationTokenSource();
        var cancelling = cancelRouter.CancelAsync(cancelId, cancelToken.Token);
        cancelToken.Cancel(); cancelSerial.Release();
        try { await cancelling; } catch (OperationCanceledException) { }
        var original = cancelTarget; cancelTarget = new CompositionMemory();
        check((await cancelRouter.BeginAsync(Guid.NewGuid(), true)).Success && original.Cancels == 1,
            "结束清理等待锁时取消，下一段仍能接管旧会话清理");

        var captured = new List<CompositionMemory>();
        var burst = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => { var item=new CompositionMemory(); captured.Add(item); return item; });
        await burst.BeginAsync(Guid.NewGuid(), true);
        var burstSerial = (SemaphoreSlim)typeof(CompositionDeliveryRouter).GetField("serial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(burst)!;
        await burstSerial.WaitAsync();
        var cancellations = new List<CancellationTokenSource>(); var switchingTasks = new List<Task>();
        for (var attempt=0; attempt<48; attempt++)
        {
            var cancellation=new CancellationTokenSource(); cancellations.Add(cancellation);
            var operation=burst.BeginAsync(Guid.NewGuid(), true, cancellation.Token);
            switchingTasks.Add(IgnoreCancellation(operation));
            if (attempt%3==0) cancellation.Cancel();
        }
        burstSerial.Release();
        await Task.WhenAll(switchingTasks).WaitAsync(TimeSpan.FromSeconds(10));
        var finalId=Guid.NewGuid();
        check((await burst.BeginAsync(finalId, true)).Success && captured.SkipLast(1).All(x=>x.Cancels==1),
            "四十八次交错切换与取消后，每个旧会话都恰好清理一次");
        check((await burst.UpdateAsync(finalId, "密集切换后的完整文字")).Success && (await burst.CommitAsync(finalId, "密集切换后的完整文字")).Success,
            "密集切换后新录音仍可出字和提交");
        foreach(var cancellation in cancellations) cancellation.Dispose();
    }
    private static async Task IgnoreCancellation(Task operation) { try { await operation; } catch (OperationCanceledException) { } }
}
