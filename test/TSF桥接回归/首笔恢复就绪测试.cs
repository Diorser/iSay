using System.Buffers.Binary;
using System.Diagnostics;
using iSay.Desktop.Services;

internal static partial class Program
{
    private static async Task RunInitialReadinessTests()
    {
        foreach (var scenario in new[] { "稍后就绪", "等待超时", "键鼠已变" })
        {
            var endpoint = Endpoint();
            var requests = new List<Request>();
            var unchanged = true;
            var attempts = 0;
            var server = Serve(endpoint, scenario == "稍后就绪" ? 6 : scenario == "等待超时" ? 5 : 4, request =>
            {
                requests.Add(request);
                var hr = 0;
                var state = TsfCompositionState.Composing;
                if (request.Command == TsfCommand.Update && request.Text != "今")
                { hr = unchecked((int)0x80040504); state = TsfCompositionState.HostTerminated; }
                if (request.Command == TsfCommand.RecoverInitial && (++attempts == 1 || scenario != "稍后就绪"))
                {
                    hr = unchecked((int)0x80070015); state = TsfCompositionState.HostTerminated;
                    if (scenario == "键鼠已变") unchanged = false;
                }
                if (request.Command == TsfCommand.Commit) state = TsfCompositionState.Committed;
                var result = Reply(request, state, hr);
                if (hr != 0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 1);
                    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), 2);
                }
                return result;
            });
            var session = new TsfCompositionSession(new(endpoint), Guid.NewGuid(), () => unchanged);
            Check((await session.UpdateAsync("今")).Applied, scenario + "建立首笔基线");
            var next = await session.UpdateAsync("今天继续说话。");
            if (scenario == "键鼠已变")
                Check(next.HResult == unchecked((int)0x80070015) && next.State == TsfCompositionState.HostTerminated, "键鼠变化不进入首笔自动等待窗口");
            else
            {
                Check(next.HResult == 1 && next.State == TsfCompositionState.FocusUnavailable, scenario + "明确未写入的未就绪不永久封锁");
                if (scenario == "等待超时") await Task.Delay(750);
                next = await session.UpdateAsync("今天继续说话。");
                if (scenario == "稍后就绪")
                {
                    Check(next.Applied && requests.Count(r => r.Command == TsfCommand.Begin) == 1 && attempts == 2,
                        "首笔选区就绪后只恢复原组合，不另发Begin或累计全文追加");
                    Check((await session.CommitAsync("今天继续说话。")).Applied, "首笔就绪恢复后能正常唯一提交");
                }
                else Check(next.HResult == unchecked((int)0x80070015) && next.State == TsfCompositionState.HostTerminated, "首笔未就绪等待有界，不无限尝试恢复");
            }
            await server;
        }
    }
}
