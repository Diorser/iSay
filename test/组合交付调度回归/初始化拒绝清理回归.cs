using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using iSay.Desktop.Services;

// 复现初始化未被接受、只读查询返回旧的已提交会话；全程不创建窗口或发送系统按键。
internal static class 初始化拒绝清理回归
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using var host = new 隔离宿主();
        var compatibility = new CompatibilityMemory();
        var router = new CompositionDeliveryRouter(compatibility, id => new TsfCompositionSession(new(host.Endpoint), id));
        var first = Guid.NewGuid();
        check(!(await router.BeginAsync(first, true)).Success, "未接受的空初始化不会冒报成功");
        var next = Guid.NewGuid();
        check((await router.BeginAsync(next, true)).Success && host.Begins == 2 && host.Cancels == 0,
            "查询证明旧初始化不属于当前已提交会话后放行新录音，不取消别人的范围");
        check((await router.UpdateAsync(next, "新录音正常输入")).Success &&
            (await router.CommitAsync(next, "新录音正常输入")).Success && host.Text == "新录音正常输入" &&
            host.Updates == 1 && host.Commits == 1 && compatibility.Total == 0,
            "初始化失败不再封锁后续交付，也不重发旧正文或退回粘贴");
        await router.CancelAsync(next);
    }

    private sealed class 隔离宿主 : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        private Guid owner = Guid.NewGuid();
        private ulong accepted = 8;
        private TsfCompositionState state = TsfCompositionState.Committed;
        public TsfEndpoint Endpoint { get; } = new((uint)Environment.ProcessId, 998723);
        public int Begins, Cancels, Updates, Commits;
        public string Text = "先前已提交的其他文字";
        public 隔离宿主() => serving = ServeAsync();
        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                    var header = new byte[40]; await pipe.ReadExactlyAsync(header, stop.Token);
                    var bytes = new byte[checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) * 2)];
                    if (bytes.Length != 0) await pipe.ReadExactlyAsync(bytes, stop.Token);
                    var command = (TsfCommand)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                    var session = new Guid(header.AsSpan(16, 16));
                    var sequence = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
                    var hr = 0;
                    if (command == TsfCommand.Begin && ++Begins == 1)
                    {
                        // 原生请求排队后连接丢失，Begin 未执行，仍保留先前已提交会话。
                        continue;
                    }
                    if (command == TsfCommand.Query) hr = owner != session ? unchecked((int)0x80070005) : 0;
                    else
                    {
                        if (command == TsfCommand.Cancel) Cancels++;
                        if (command == TsfCommand.Update) Updates++;
                        if (command == TsfCommand.Commit) Commits++;
                        owner = session; accepted = sequence;
                        if (command is TsfCommand.Update or TsfCommand.Commit) Text = Encoding.Unicode.GetString(bytes);
                        state = command is TsfCommand.Commit or TsfCommand.Cancel ? TsfCompositionState.Committed : TsfCompositionState.Composing;
                    }
                    var reply = new byte[32];
                    BinaryPrimitives.WriteUInt32LittleEndian(reply, TsfCompositionClient.Magic);
                    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), 1);
                    BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(8), hr);
                    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), (uint)state);
                    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(16), (uint)Text.Length);
                    BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(24), session == owner ? accepted : 0);
                    await pipe.WriteAsync(reply, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (IOException) { }
            }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); await serving; stop.Dispose(); }
    }
}
