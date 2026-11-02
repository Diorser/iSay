using iSay.Desktop.Services;

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
    }
}
