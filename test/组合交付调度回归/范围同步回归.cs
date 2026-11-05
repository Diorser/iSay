using iSay.Desktop.Services;

internal static class 范围同步回归
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await using (var host = new 初字恢复回归.初字宿主())
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), rangeReady: ready.Task);
            var begin = session.BeginAsync();
            await Task.Delay(30);
            check(!begin.IsCompleted && host.Begins == 0, "范围捕获尚未就绪时不与Begin交错");
            ready.SetResult();
            check((await begin).Applied && host.Begins == 1, "范围捕获完成后只建立一次组合");
        }
        await using (var host = new 初字恢复回归.初字宿主())
        {
            var calls = 0; var finishes = new List<bool>(); var allowed = false;
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true,
                prepareRange: (text, token) =>
                {
                    calls++;
                    return Task.FromResult<CompositionRangePreparation?>(new(allowed, applied => { finishes.Add(applied); return Task.CompletedTask; }));
                });
            await session.UpdateAsync("首批");
            check(calls == 0 && host.Queries == 0, "首批未确认前不选择预测占位文字");
            check(!(await session.UpdateAsync("后续")).Applied && host.Text == "首批" && calls == 1,
                "同步未就绪不发送累计正文");
            check(finishes.SequenceEqual(new[] { false }), "未就绪同步也执行清理回调");
            allowed = true;
            check((await session.UpdateAsync("后续")).Applied && host.Text == "后续" && finishes.Last(),
                "暂缓后仍可沿原会话继续同步而不永久锁死");
            host.FailNextWrite = true;
            check(!(await session.UpdateAsync("失败写入")).Applied && !finishes.Last() && host.Text == "后续",
                "原生写入失败时恢复选区且不确认新快照");
            var count = finishes.Count;
            check((await session.CommitAsync("完成")).Applied && host.Text == "完成" && finishes.Count == count + 1 && finishes.Last(),
                "正常提交同步原范围并完成唯一提交");
        }
        await using (var host = new 初字恢复回归.初字宿主())
        {
            var calls = 0;
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true,
                prepareRange: (_, _) => { calls++; return Task.FromResult<CompositionRangePreparation?>(new(true)); });
            await session.UpdateAsync("首批"); host.QueryTerminates = true;
            check((await session.UpdateAsync("首批继续")).Applied && host.Recoveries == 1 && calls == 0,
                "已终止组合先走首批恢复，不先选择破坏折叠光标");
        }
        await using (var host = new 初字恢复回归.初字宿主())
        {
            var finished = 0; var appliedFinish = true;
            using var cancellation = new CancellationTokenSource();
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true,
                prepareRange: (_, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult<CompositionRangePreparation?>(new(true, applied =>
                    { finished++; appliedFinish = applied; return Task.CompletedTask; }));
                });
            await session.UpdateAsync("原文");
            try { await session.UpdateAsync("取消后不写", cancellation.Token); }
            catch (OperationCanceledException) { }
            check(finished == 1 && !appliedFinish && host.Text == "原文", "同步后取消必清理选区且不改正文");
        }
        await using (var host = new 初字恢复回归.初字宿主())
        {
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await session.UpdateAsync("原文"); await session.UpdateAsync("继续"); await session.CommitAsync("继续");
            check(host.Queries == 0 && host.Text == "继续" && host.Commits == 1, "未启用增强的原生输入路径不增加查询或额外提交");
        }
    }
}
