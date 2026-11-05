using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using iSay.Desktop.Services;

internal static class 初字恢复回归
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var initial in new[] { "我", "你好", "😀", "首个快照可以超过八个字符" })
        {
            await using var host = new 初字宿主();
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await delivery.BeginAsync(); await delivery.UpdateAsync(initial); host.TerminateNextWrite = true;
            var reply = await delivery.UpdateAsync(initial + "继续说");
            check(reply.Applied && host.Text == initial + "继续说" && host.Recoveries == 1 && host.Begins == 1,
                "首个非空快照长度" + initial.Length + "被结束后仅恢复原范围一次，不重发Begin");
            host.TerminateNextWrite = true;
            check(!(await delivery.UpdateAsync(initial + "第二次结束")).Applied && host.Recoveries == 1,
                "首个快照长度" + initial.Length + "恢复后再次结束不重复恢复");
        }
        await using (var host = new 初字宿主())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await delivery.BeginAsync(); await delivery.UpdateAsync("首字"); host.TerminateNextWrite = true;
            check((await delivery.CommitAsync("最终完整文字")).State == TsfCompositionState.Committed &&
                host.Text == "最终完整文字" && host.Recoveries == 1 && host.Commits == 1 && host.Begins == 1,
                "结束时首次发现首快照被宿主结束，恢复原范围后唯一Commit");
        }
        foreach (var reason in new[] { "物理输入版本变化", "原焦点变化", "未提供原目标验证" })
        {
            await using var host = new 初字宿主();
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), reason == "未提供原目标验证" ? null : () => false);
            await delivery.BeginAsync(); await delivery.UpdateAsync("首字"); host.TerminateNextWrite = true;
            check(!(await delivery.UpdateAsync("禁止恢复")).Applied && host.Recoveries == 0 && host.Begins == 1 && host.Text == "首字",
                reason + "时禁止恢复或从新光标重建");
        }
        await using (var host = new 初字宿主())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await delivery.BeginAsync(); await delivery.UpdateAsync("首字"); await delivery.UpdateAsync("第二个成功快照"); host.TerminateNextWrite = true;
            check(!(await delivery.UpdateAsync("第三个快照")).Applied && host.Recoveries == 0 && host.Text == "第二个成功快照",
                "已经成功更新两个非空快照的正常组合终止不能走初字恢复");
        }
        await using (var host = new 初字宿主 { RejectRecovery = true })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await delivery.BeginAsync(); await delivery.UpdateAsync("首字"); host.TerminateNextWrite = true;
            check(!(await delivery.UpdateAsync("旧组件不支持")).Applied && !(await delivery.CommitAsync("保留全文")).Applied &&
                host.Recoveries == 1 && host.Text == "首字" && host.Begins == 1 && host.Commits == 0,
                "旧组件拒绝命令7时保留转写，不补发Begin或普通全文输入");
        }
        foreach (var final in new[] { false, true })
        {
            await using var host = new 初字宿主 { DropRecoveryReply = true };
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await delivery.BeginAsync(); await delivery.UpdateAsync("首字"); host.TerminateNextWrite = true;
            var reply = await delivery.ApplyAsync("恢复回复丢失仍不重发", final);
            check(reply.Applied && host.Recoveries == 1 && host.Queries == 1 && host.Text == "恢复回复丢失仍不重发" && host.Commits == (final ? 1 : 0),
                (final ? "最终" : "实时") + "恢复回复丢失后只查确切序号，再按原意图完成");
        }
        await using (var host = new 初字宿主 { RecoveryPending = true })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await delivery.BeginAsync(); await delivery.UpdateAsync("首字"); host.TerminateNextWrite = true;
            try { await delivery.UpdateAsync("原恢复快照"); throw new InvalidOperationException("恢复在途应等待确认"); }
            catch (TsfConfirmationPendingException) { }
            try { await delivery.UpdateAsync("等待期间的新快照"); throw new InvalidOperationException("确认和新快照应分开调度"); }
            catch (TsfConfirmationPendingException error) { check(error.Recovered, "恢复在途只查原序号，确认后另轮发送最新快照"); }
            check((await delivery.CommitAsync("等待期间的新快照")).State == TsfCompositionState.Committed &&
                host.Recoveries == 1 && host.Queries == 1 && host.Text == "等待期间的新快照" && host.Commits == 1,
                "恢复在途经查询证明后可提交新快照，恢复命令始终只有一次");
        }
        await using (var host = new 初字宿主 { FailRecoveryWhileActive = true })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await delivery.BeginAsync(); await delivery.UpdateAsync("首字"); host.TerminateNextWrite = true;
            var failed = await delivery.UpdateAsync("恢复时编辑失败");
            check(failed.HResult == unchecked((int)0x80004005) && failed.State == TsfCompositionState.Composing && !failed.Applied,
                "恢复建立组合后编辑及本机清理失败，诚实保留活动状态而不冒充宿主终止");
            check((await delivery.QueryAsync()).Applied && !(await delivery.UpdateAsync("查询后也不能继续写")).Applied &&
                !(await delivery.CommitAsync("失败后不能提交全文")).Applied && !(await delivery.BeginAsync()).Applied &&
                host.Recoveries == 1 && host.Begins == 1 && host.Commits == 0 && host.Text == "首字",
                "失败组合只读查询成功不能解除写入禁令，不再恢复或重建Begin");
            host.BusyCancelsRemaining = 1;
            check(!(await delivery.CancelAsync()).Applied && (await delivery.CancelAsync()).State == TsfCompositionState.Committed && host.Cancels == 2,
                "恢复失败仍活动的组合保留Cancel能力，Busy后可再次明确清理");
        }
        await using (var host = new 初字宿主 { FailRecoveryWhileActive = true })
        {
            var originalId = Guid.NewGuid(); var nextId = Guid.NewGuid();
            var original = new TsfCompositionSession(new(host.Endpoint), originalId, () => true);
            var next = new TsfCompositionSession(new(host.Endpoint), nextId, () => true);
            var router = new CompositionDeliveryRouter(new CompatibilityMemory(), id => id == originalId ? original : next);
            await router.BeginAsync(originalId, true); await router.UpdateAsync(originalId, "首字"); host.TerminateNextWrite = true;
            await router.UpdateAsync(originalId, "恢复时编辑失败"); host.BusyCancelsRemaining = 1;
            check((await router.BeginAsync(nextId, true)).Success && host.Cancels == 2 && host.Begins == 2 && host.Recoveries == 1 && host.Text == "首字",
                "下一段先经调度清理队列结束恢复失败的活动组合，Busy解除前不抢建新组合");
        }
    }

    // 只模拟当前用户命名管道协议，不注册输入法，不建立窗口，也不读取麦克风。
    internal sealed class 初字宿主 : IAsyncDisposable
    {
        private static int nextThread = 880000;
        private readonly CancellationTokenSource stop = new();
        private readonly Task server;
        private TsfCompositionState state = TsfCompositionState.Idle;
        private ulong accepted;
        public TsfEndpoint Endpoint { get; } = new((uint)Environment.ProcessId, (uint)Interlocked.Increment(ref nextThread));
        public string Text = "";
        public int Begins, Recoveries, Commits, Queries, Cancels, BusyCancelsRemaining;
        public bool TerminateNextWrite, RejectRecovery, DropRecoveryReply, RecoveryPending, FailRecoveryWhileActive;
        public bool QueryTerminates, FailNextWrite;
        public 初字宿主() => server = ServeAsync();

        private async Task ServeAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stop.Token);
                    var header = new byte[40]; await pipe.ReadExactlyAsync(header, stop.Token);
                    var command = (TsfCommand)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
                    var sequence = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
                    var payload = new byte[length * 2]; if (payload.Length != 0) await pipe.ReadExactlyAsync(payload, stop.Token);
                    var text = Encoding.Unicode.GetString(payload);
                    var operation = 0;
                    if (command == TsfCommand.Query) { Queries++; if (QueryTerminates) state = TsfCompositionState.HostTerminated; }
                    else if (command == TsfCommand.Begin) { Begins++; state = TsfCompositionState.Composing; accepted = sequence; }
                    else if (command == TsfCommand.RecoverInitial)
                    {
                        Recoveries++;
                        if (RejectRecovery) operation = unchecked((int)0x80070057);
                        else if (FailRecoveryWhileActive) { state = TsfCompositionState.Composing; operation = unchecked((int)0x80004005); }
                        else
                        {
                            Text = text; state = TsfCompositionState.Composing; accepted = sequence;
                            if (DropRecoveryReply) { DropRecoveryReply = false; continue; }
                            if (RecoveryPending) { RecoveryPending = false; operation = unchecked((int)0x8000000A); }
                        }
                    }
                    else if (command == TsfCommand.Cancel)
                    {
                        Cancels++;
                        if (BusyCancelsRemaining-- > 0) operation = unchecked((int)0x800700AA);
                        else { state = TsfCompositionState.Committed; accepted = sequence; }
                    }
                    else if (FailNextWrite) { FailNextWrite = false; operation = unchecked((int)0x80004005); }
                    else if (TerminateNextWrite) { TerminateNextWrite = false; state = TsfCompositionState.HostTerminated; operation = unchecked((int)0x80040504); }
                    else if (state == TsfCompositionState.HostTerminated) operation = unchecked((int)0x80040504);
                    else
                    {
                        Text = text; accepted = sequence;
                        if (command == TsfCommand.Commit) { Commits++; state = TsfCompositionState.Committed; }
                    }
                    var response = new byte[32];
                    BinaryPrimitives.WriteUInt32LittleEndian(response, TsfCompositionClient.Magic);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), TsfCompositionClient.Version);
                    BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(8), operation);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), (uint)state);
                    BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(16), Text.Length);
                    BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(24), accepted);
                    await pipe.WriteAsync(response, stop.Token);
                    // 等客户端读取后关闭，再释放本次连接，避免模拟服务本身制造回复截断。
                    var done = new byte[1];
                    try { await pipe.ReadExactlyAsync(done, stop.Token); }
                    catch (EndOfStreamException) { }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); await server; stop.Dispose(); }
    }
}
