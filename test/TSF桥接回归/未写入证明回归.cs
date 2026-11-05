using iSay.Desktop.Services;
using System.Buffers.Binary;

internal static partial class Program
{
    private static async Task RunUnwrittenProofTests()
    {
        await using (var host = new RecoveryHost { FocusUnavailable = true })
        {
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => false);
            await session.UpdateAsync("没有目标时说话");
            Check(!session.MayHaveWrittenText && host.UpdateCount == 0, "无输入焦点的Begin失败能证明尚未发送正文");
        }
        await using (var host = new RecoveryHost())
        {
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await session.BeginAsync(); host.FocusUnavailable = true;
            await session.UpdateAsync("正文未被接受");
            Check(!session.MayHaveWrittenText && host.Text.Length == 0 && host.UpdateCount == 1, "明确的焦点拒绝及未接受序号证明没有写入");
            host.FocusUnavailable = false; await session.UpdateAsync("已写入正文");
            host.FocusUnavailable = true; await session.UpdateAsync("后续失焦");
            Check(session.MayHaveWrittenText && host.Text == "已写入正文", "先前已有成功正文时后来的焦点拒绝不能清除已写记录");
        }
        await using (var host = new RecoveryHost())
        {
            var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await session.BeginAsync(); host.DropNextReply = TsfCommand.Update;
            await session.UpdateAsync("回复丢失但已写入");
            Check(session.MayHaveWrittenText && host.Text.Length > 0, "写入后丢失回复仍保留可能写入标志，不能重发全文");
        }
        foreach (var acceptedFirstUpdate in new[] { false, true })
        {
            var endpoint = Endpoint();
            var priorSequence = 1UL;
            var priorLength = 0;
            var server = Serve(endpoint, acceptedFirstUpdate ? 4 : 3, request =>
            {
                if (request.Command == TsfCommand.Update && request.Text == "首批被宿主拒绝")
                {
                    var rejected = Reply(request, TsfCompositionState.Composing, unchecked((int)0x8007051A));
                    BinaryPrimitives.WriteUInt32LittleEndian(rejected.AsSpan(16), (uint)priorLength);
                    BinaryPrimitives.WriteUInt64LittleEndian(rejected.AsSpan(24), priorSequence);
                    return rejected;
                }
                priorLength = request.Text.Length;
                priorSequence = request.Sequence;
                return Reply(request, request.Command == TsfCommand.Cancel ? TsfCompositionState.Committed : TsfCompositionState.Composing);
            });
            var session = new TsfCompositionSession(new(endpoint), Guid.NewGuid());
            await session.BeginAsync();
            if (acceptedFirstUpdate) await session.UpdateAsync("已经显示的字");
            Check(!(await session.UpdateAsync("首批被宿主拒绝")).Applied && session.MayHaveWrittenText == acceptedFirstUpdate,
                acceptedFirstUpdate ? "范围不匹配不能抹掉先前已写正文" : "零字且未接受的范围不匹配可证明首批没有写入");
            Check(session.CanProbeEmptyFinalTarget != acceptedFirstUpdate,
                "只读备用空框探测与实际写入资格分离，已经确认正文则无需探测");
            Check((await session.CancelAsync()).Applied, "范围不匹配后仍能结束旧组合");
            await server;
        }
        {
            var endpoint = Endpoint();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new ManualResetEventSlim();
            var server = Serve(endpoint, 2, request =>
            {
                if (request.Command == TsfCommand.Update) { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(2)); }
                return Reply(request, TsfCompositionState.Composing);
            });
            var session = new TsfCompositionSession(new(endpoint), Guid.NewGuid());
            await session.BeginAsync();
            var pending = session.UpdateAsync("首批还在路上");
            await entered.Task;
            Check(session.MayHaveWrittenText && session.CanProbeEmptyFinalTarget,
                "首笔回复未返回时只可探测结束目标，仍不能推定可补填全文");
            release.Set(); await pending; await server;
            Check(!session.CanProbeEmptyFinalTarget && session.MayHaveWrittenText,
                "首笔确认成功即关闭备用空框探测，不造成第二份正文");
        }
    }
}
