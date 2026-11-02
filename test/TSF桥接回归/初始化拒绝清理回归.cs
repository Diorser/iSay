using System.Reflection;
using iSay.Desktop.Services;

internal static partial class Program
{
    private static async Task RunAbsentSessionCleanupScenarios()
    {
        const int denied = unchecked((int)0x80070005);
        await using (var host = new RecoveryHost { DropNextReply = TsfCommand.Begin, DropWithoutApply = true,
            QueryOverride = new(denied, TsfCompositionState.Committed, 9, 0) })
        {
            var unchanged = true;
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => unchanged);
            await MustThrow<TsfConfirmationPendingException>(() => session.BeginAsync(), "未接受的空初始化在原焦点未变时安排一次恢复");
            Check(host.BeginCount == 1 && host.UpdateCount == 0, "确认和空初始化重试分轮处理，不在查询轮发送正文");
            host.QueryOverride = null;
            Check((await session.UpdateAsync("本段恢复输入")).Applied && host.BeginCount == 2 && host.UpdateCount == 1 &&
                (await session.CommitAsync("本段恢复输入")).Applied,
                "原焦点未变时本段即可恢复出字，空初始化只补一次，正文和提交不重复");
        }
        await using (var host = new RecoveryHost { DropNextReply = TsfCommand.Begin, DropWithoutApply = true,
            QueryOverride = new(denied, TsfCompositionState.Committed, 9, 0) })
        {
            var unchanged = true;
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => unchanged);
            await MustThrow<TsfConfirmationPendingException>(() => session.BeginAsync(), "构造允许恢复但尚未重试的空初始化");
            unchanged = false;
            Check(!(await session.UpdateAsync("不能写往新位置")).Applied && host.BeginCount == 1 && host.UpdateCount == 0,
                "确认后用户切换焦点或打字，不重试空初始化也不向新位置发送正文");
        }
        foreach (var command in new[] { TsfCommand.Begin, TsfCommand.Update, TsfCommand.Commit, TsfCommand.Cancel })
        foreach (var state in new[] { TsfCompositionState.Idle, TsfCompositionState.Committed })
        {
            await using var host = new RecoveryHost();
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            if (command != TsfCommand.Begin) await session.BeginAsync();
            host.PendingNextBegin = command == TsfCommand.Begin;
            host.PendingNextUpdate = command == TsfCommand.Update;
            host.PendingNextCommit = command == TsfCommand.Commit;
            host.PendingNextCancel = command == TsfCommand.Cancel;
            host.PendingQueries = 99;
            try
            {
                if (command == TsfCommand.Begin) await session.BeginAsync();
                else if (command == TsfCommand.Update) await session.UpdateAsync("未确认正文");
                else if (command == TsfCommand.Commit) await session.CommitAsync("未确认正文");
                else await session.CancelAsync();
            }
            catch (TsfConfirmationPendingException) { }
            AgePending(session);
            host.QueryOverride = new(denied, state, 13, 0);
            var writes = host.BeginCount + host.UpdateCount + host.CommitCount + host.CancelCount;
            var result = await session.CancelAsync();
            Check(result.HResult == 0 && result.State == TsfCompositionState.Idle &&
                writes == host.BeginCount + host.UpdateCount + host.CommitCount + host.CancelCount,
                command + "迟到查询证明当前为其他已结束会话，清理只读放行，外观=" + state);
            Check(session.TryCreateLearningObserver() is null,
                "清理证明不能冒充原正文提交成功或启动手改学习：" + command + "/" + state);
        }
        foreach (var invalid in new[]
        {
            new TsfReply(denied, TsfCompositionState.Composing, 0, 0),
            new TsfReply(denied, TsfCompositionState.Committed, 0, 1),
            new TsfReply(unchecked((int)0x80004005), TsfCompositionState.Committed, 0, 0),
            new TsfReply(unchecked((int)0x8000000A), TsfCompositionState.Idle, 0, 0),
            new TsfReply(unchecked((int)0x800700AA), TsfCompositionState.Committed, 0, 0),
            new TsfReply(0, TsfCompositionState.Committed, 0, 0)
        })
        {
            await using var host = new RecoveryHost { PendingNextBegin = true, PendingQueries = 99 };
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await MustThrow<TsfConfirmationPendingException>(() => session.BeginAsync(), "构造未确认初始化");
            host.QueryOverride = invalid;
            await MustThrow<IOException>(() => session.CancelAsync(),
                "活动组合、非零序号或不确定错误不得误报已清理：" + invalid);
        }
        // 覆盖取消过程中等待被中断、只留下不确定标记的分支。
        await using (var host = new RecoveryHost())
        {
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await session.BeginAsync();
            typeof(TsfCompositionSession).GetField("uncertain", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
            host.QueryOverride = new(denied, TsfCompositionState.Committed, 17, 0);
            Check((await session.CancelAsync()).HResult == 0 && host.CancelCount == 0,
                "取消被中断后的只读清理同样识别已不存在的原会话，不取消其他文字范围");
        }
    }

    private static async Task CheckNativeAbsentSessionCleanup(TsfEndpoint endpoint)
    {
        var orphanId = Guid.NewGuid();
        var reply = await new TsfCompositionClient(endpoint).SendAsync(TsfCommand.Query, orphanId, 1, "");
        Check(reply.HResult == unchecked((int)0x80070005) && reply.State == TsfCompositionState.Committed && reply.AcceptedSequence == 0,
            "真实原生组件对未接受会话返回拒绝访问、已提交外观及零确认序号");
        foreach (var hasPending in new[] { false, true })
        {
            var session = new TsfCompositionSession(new(endpoint), orphanId);
            var fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(TsfCompositionSession).GetField("started", fields)!.SetValue(session, true);
            typeof(TsfCompositionSession).GetField("uncertain", fields)!.SetValue(session, true);
            if (hasPending)
            {
                // 只注入客户端丢失回复后的状态；真实宿主不伪造查询回复，也不操作用户窗口。
                var pendingType = typeof(TsfCompositionSession).GetNestedType("PendingWrite", BindingFlags.NonPublic)!;
                var pending = Activator.CreateInstance(pendingType, BindingFlags.Public | fields, null,
                    new object[] { TsfCommand.Begin, 1UL, "", System.Diagnostics.Stopwatch.GetTimestamp() }, null);
                typeof(TsfCompositionSession).GetField("pending", fields)!.SetValue(session, pending);
            }
            Check((await session.CancelAsync()).State == TsfCompositionState.Idle && session.TryCreateLearningObserver() is null,
                "真实原生已提交会话不被取消，失效初始化只读清理成功；有在途标记=" + hasPending);
        }
    }
}
