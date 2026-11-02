using System.Diagnostics;
using iSay.Desktop.Services;

internal static partial class Program
{
    private static async Task RunInitialBatchGuardTests()
    {
        await using (var host = new RecoveryHost { TerminateAfterFirstBatchQueries = 4 })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true,
                guardInitialChromiumBatch: true);
            Check((await delivery.UpdateAsync("测试")).Applied && host.QueryCount == 0,
                "首批节奏保护不延迟第一批文字，不增加首字查询");
            Check((await delivery.UpdateAsync("测试一下")).Applied && host.InitialRecoveryCount == 1 && host.UpdateCount == 1 && host.BeginCount == 1,
                "宿主迟到结束首批时先只读观察再恢复，不抢写第二笔累计正文");
            var queries = host.QueryCount;
            Check((await delivery.UpdateAsync("测试一下继续说话")).Applied && host.QueryCount == queries,
                "首批恢复后正常实时更新不再经过节奏保护");
            Check((await delivery.CommitAsync("测试一下继续说话")).Applied && host.CommitCount == 1,
                "节奏保护后最终提交仍只提交一次");
        }
        await using (var host = new RecoveryHost { TerminateAfterFirstBatchQueries = 3 })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true,
                guardInitialChromiumBatch: true);
            await delivery.UpdateAsync("今");
            Check((await delivery.CommitAsync("今天")).Applied && host.InitialRecoveryCount == 1 && host.CommitCount == 1,
                "首批后立即结束同样先观察原组合，不在竞态窗口重打全文");
        }
        await using (var host = new RecoveryHost())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true,
                guardInitialChromiumBatch: true);
            await delivery.UpdateAsync("首字");
            using var cancellation = new CancellationTokenSource(25);
            await MustThrow<OperationCanceledException>(() => delivery.UpdateAsync("不能迟到写入", cancellation.Token),
                "首批观察可以取消，取消后不发送待交付正文");
            Check(host.UpdateCount == 1 && host.Text == "首字", "首批观察取消保留已经显示的文字");
        }
        await using (var host = new RecoveryHost())
        {
            var focus = true;
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => focus,
                canWriteOriginal: _ => Task.FromResult(focus), guardInitialChromiumBatch: true);
            await delivery.UpdateAsync("原框");
            focus = false;
            Check(!(await delivery.UpdateAsync("不能写到其他框")).Applied && host.UpdateCount == 1,
                "首批期间失焦不会把新快照写到其他位置");
        }
        await using (var host = new RecoveryHost { Text = "原选区" })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true,
                guardInitialChromiumBatch: true);
            await delivery.UpdateAsync("替换选区");
            Check((await delivery.UpdateAsync("替换选区继续")).Applied && host.QueryCount == 0,
                "初始非空选区不进入空框首批节奏保护");
        }
    }
}
