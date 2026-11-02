using System.Text.Json;
using iSay.Desktop.Services;

internal static partial class Program
{
    private static async Task<int> RunBackgroundSoak()
    {
        try
        {
            for (var round = 0; round < 100; round++)
            {
                await using var host = new RecoveryHost();
                var focused = true; var unchanged = true;
                TsfCompositionSession Create() => new(new(host.Endpoint), Guid.NewGuid(), () => unchanged,
                    canWriteOriginal: _ => Task.FromResult(focused), guardInitialChromiumBatch: round % 5 == 0);
                var session = Create(); var initial = $"第{round}段开头"; var final = initial + "，继续说话直到结束。";
                await session.UpdateAsync(initial);
                var expectedBegins = 1;
                switch (round % 5)
                {
                    case 0:
                        host.TerminateAfterFirstBatchQueries = 1;
                        await Confirm(() => session.UpdateAsync(final));
                        break;
                    case 1:
                        focused = false; unchanged = false;
                        var writes = host.UpdateCount;
                        await session.UpdateAsync(final);
                        if (host.UpdateCount != writes) throw new InvalidOperationException("失焦期间仍发正文");
                        focused = true; host.TerminateNextUpdate = true;
                        await Confirm(() => session.UpdateAsync(final));
                        break;
                    case 2:
                        host.PendingNextUpdate = true; host.PendingQueries = 1; host.TerminatedWhilePending = true;
                        await Confirm(() => session.UpdateAsync(final));
                        break;
                    case 3:
                        host.PendingNextUpdate = true;
                        try { await session.UpdateAsync(final); } catch (TsfConfirmationPendingException) { }
                        AgePending(session); host.ResetOnNextQuery = true;
                        await session.CancelAsync(); session = Create(); expectedBegins = 2;
                        await Confirm(() => session.UpdateAsync(final));
                        break;
                    case 4:
                        host.BusyCancelsRemaining = 1;
                        var busy = await session.CancelAsync();
                        if (!busy.IsPending) throw new InvalidOperationException("未复现取消忙碌");
                        await session.CancelAsync(); session = Create(); expectedBegins = 2;
                        await Confirm(() => session.UpdateAsync(final));
                        break;
                }
                var result = await Confirm(() => session.CommitAsync(final));
                Check(result.Applied && host.Text == final && host.BeginCount == expectedBegins && host.CommitCount == 1,
                    $"后台稳定性第{round + 1}轮：恢复后正文完整、不追加重复全文、提交一次");
            }
            await using (var idle = new RecoveryHost())
            {
                var session = new TsfCompositionSession(new(idle.Endpoint), Guid.NewGuid(), () => true, guardInitialChromiumBatch: true);
                await session.UpdateAsync("空闲前的首批文字");
                for (var step = 0; step < 4; step++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    Console.WriteLine($"后台隔离会话已空闲{(step + 1) * 30}秒，未操作系统窗口。");
                }
                var final = "空闲前的首批文字，空闲后继续说话。";
                Check((await session.UpdateAsync(final)).Applied && (await session.CommitAsync(final)).Applied &&
                    idle.BeginCount == 1 && idle.CommitCount == 1 && idle.Text == final,
                    "真实后台空闲120秒后原会话继续出字并唯一提交");
            }
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../后台稳定性循环结果.json"));
            File.WriteAllText(output, JsonSerializer.Serialize(new { 时间=DateTimeOffset.Now, 范围="隔离命名管道；100轮恢复循环；真实空闲120秒；非系统休眠测试", 测试=Checks }, new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine($"通过 {Checks.Count} 项后台稳定性循环；{output}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task<TsfReply> Confirm(Func<Task<TsfReply>> operation)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try { return await operation(); }
            catch (TsfConfirmationPendingException) { }
        }
        throw new InvalidOperationException("后台恢复超过八次只读确认预算");
    }
}
