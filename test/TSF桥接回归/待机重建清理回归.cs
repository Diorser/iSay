using System.Diagnostics;
using System.Reflection;
using iSay.Desktop.Services;

internal static partial class Program
{
    private static void AgePending(TsfCompositionSession session)
    {
        var pending = typeof(TsfCompositionSession).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        pending.GetType().GetProperty("StartedAt")!.SetValue(pending,
            Stopwatch.GetTimestamp() - checked(Stopwatch.Frequency * 60L * 60 * 12));
    }
    private static async Task RunLongFocusGapScenario()
    {
        await using var host = new RecoveryHost();
        var focus = true;
        var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true,
            canWriteOriginal: _ => Task.FromResult(focus));
        await session.BeginAsync(); host.PendingNextUpdate = true;
        await MustThrow<TsfConfirmationPendingException>(() => session.UpdateAsync("失焦前的原正文"), "失焦前模拟编辑尚未确认");
        AgePending(session); focus = false;
        Check((await session.UpdateAsync("离开期间继续说话")).State == TsfCompositionState.FocusUnavailable && host.QueryCount == 0,
            "模拟十二小时离开原框时不查询或发送新正文");
        focus = true;
        await MustThrow<TsfConfirmationPendingException>(() => session.UpdateAsync("回到原框后的完整正文"), "长时间回焦先只读确认原编辑");
        Check((await session.UpdateAsync("回到原框后的完整正文")).Applied && host.UpdateCount == 2 && host.BeginCount == 1,
            "长时间回焦确认后沿用原组合续写，不因旧确认计时过期而卡死");
        Check((await session.CommitAsync("回到原框后的完整正文")).Applied && host.CommitCount == 1,
            "长时间回焦最终提交一次");
    }

    private static async Task<int> RunLifetimeHost(string readyPath, string stopName)
    {
        await using var host = new RecoveryHost(new((uint)Environment.ProcessId, 0x77000001));
        using var stop = EventWaitHandle.OpenExisting(stopName);
        File.WriteAllText(readyPath, Environment.ProcessId.ToString());
        return await Task.Run(() => stop.WaitOne(TimeSpan.FromSeconds(25))) ? 0 : 2;
    }
    private static async Task RunExitedOwnerScenario()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../退出宿主证据", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var readyPath = Path.Combine(directory, "就绪.txt");
        var stopName = "Local\\iSayLifecycleTest_" + Guid.NewGuid().ToString("N");
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--lifetime-host", readyPath, stopName }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (!File.Exists(readyPath)) { if (process.HasExited) throw new InvalidOperationException("隔离宿主提前退出"); await Task.Delay(15, deadline.Token); }
            var session = new TsfCompositionSession(new(new((uint)process.Id, 0x77000001)), Guid.NewGuid());
            Check((await session.UpdateAsync("旧进程中的输入")).Applied, "独立进程旧输入组合正常建立");
            stop.Set(); await process.WaitForExitAsync(deadline.Token);
            var watch = Stopwatch.StartNew(); var result = await session.CancelAsync(deadline.Token);
            Check(result.HResult == 0 && result.State == TsfCompositionState.Idle && watch.ElapsedMilliseconds < 500,
                "确认旧宿主进程已经退出时立即释放清理，不让消失的管道挡住所有后续输入");
        }
        finally { stop.Set(); await process.WaitForExitAsync(deadline.Token); }
    }
    private static async Task RunStandbyResetScenarios()
    {
        foreach (var pendingCommand in new[] { TsfCommand.Begin, TsfCommand.Update, TsfCommand.Commit, TsfCommand.Cancel })
        {
            await using var host = new RecoveryHost();
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            if (pendingCommand != TsfCommand.Begin) await session.BeginAsync();
            if (pendingCommand == TsfCommand.Begin) host.PendingNextBegin = true;
            if (pendingCommand == TsfCommand.Update) host.PendingNextUpdate = true;
            if (pendingCommand == TsfCommand.Commit) host.PendingNextCommit = true;
            if (pendingCommand == TsfCommand.Cancel) host.PendingNextCancel = true;
            // 查询保持暂态，确保仍有待确认命令。
            host.PendingQueries = 3;
            try
            {
                if (pendingCommand == TsfCommand.Begin) await session.BeginAsync();
                else if (pendingCommand == TsfCommand.Update) await session.UpdateAsync("待机前未确认正文");
                else if (pendingCommand == TsfCommand.Commit) await session.CommitAsync("待机前最终正文");
                else await session.CancelAsync();
            }
            catch (TsfConfirmationPendingException) { }
            var pending = typeof(TsfCompositionSession).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session);
            Check(pending is not null, "待机前存在未确认的" + pendingCommand);
            AgePending(session);
            host.ResetOnNextQuery = true;
            var cleared = await session.CancelAsync();
            Check(cleared.HResult == 0 && cleared.State == TsfCompositionState.Idle && host.CancelCount <= 1,
                "模拟十二小时待机后宿主重置，" + pendingCommand + "可只读释放旧会话，不向新上下文发取消或正文");
            var next = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            Check((await next.UpdateAsync("唤醒后的新录音")).Applied && (await next.CommitAsync("唤醒后的新录音")).Applied,
                "宿主重置后下一段正常出字和提交");
        }
    }
}
