using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using iSay.Desktop.Services;

// 真实命名管道复现暂态终止回复；不创建窗口，不访问用户输入框。
internal static class 旧会话暂态清理回归
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using var host = new 隔离宿主();
        var compatibility = new CompatibilityMemory();
        var router = new CompositionDeliveryRouter(compatibility,
            id => new TsfCompositionSession(new(host.Endpoint), id));
        var diagnostics = new List<string>();
        router.CleanupDiagnostic += diagnostics.Add;
        var first = Guid.NewGuid();
        check((await router.BeginAsync(first, true)).Success, "隔离管道旧录音成功建立");
        host.DelayNextUpdate = true;
        check((await router.UpdateAsync(first, "旧段只写一次")).AwaitingHost, "旧更新在途时只保留后续快照");
        await Task.Delay(270);
        check((await router.UpdateAsync(first, "不能重发的旧全文")).AwaitingHost,
            "暂态终止状态不会封禁路由且不重发全文");
        // 超过常规正文确认预算，下一段必须只读核对旧序号，再结束旧组合。
        await Task.Delay(2600);
        var next = Guid.NewGuid();
        check((await router.BeginAsync(next, true)).Success && host.Begins == 2 && host.Cancels == 1 && host.Updates == 1,
            "确认预算耗尽后仍清理旧段并放行下一次录音，不重发旧正文");
        check((await router.UpdateAsync(next, "新段正常出字")).Success &&
            (await router.CommitAsync(next, "新段正常出字")).Success && host.Updates == 2 && host.Commits == 1 &&
            host.Text == "新段正常出字" && compatibility.Total == 0,
            "后续新录音正常交付和提交，未切换普通键入或追加旧全文");
        check(diagnostics.Any(x => x.Contains("清理已确认")) && diagnostics.All(x => !x.Contains("旧段只写一次")),
            "旧会话清理有诊断且不记录语音正文");
    }

    private sealed class 隔离宿主 : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        private ulong accepted;
        private ulong deferredSequence;
        private string? deferredText;
        private int pendingQueries = 2;
        private TsfCompositionState state;
        public TsfEndpoint Endpoint { get; } = new((uint)Environment.ProcessId, 998721);
        public bool DelayNextUpdate;
        public int Begins, Updates, Cancels, Commits;
        public string Text = "";
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
                    await pipe.ReadExactlyAsync(bytes, stop.Token);
                    var command = (TsfCommand)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                    var sequence = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
                    var text = Encoding.Unicode.GetString(bytes);
                    var hr = 0;
                    if (command == TsfCommand.Query && deferredText is not null)
                    {
                        if (pendingQueries-- > 0) hr = unchecked((int)0x8000000A);
                        else { Text = deferredText; accepted = deferredSequence; deferredText = null; }
                    }
                    else if (command != TsfCommand.Query)
                    {
                        if (command == TsfCommand.Begin) { Begins++; Text = ""; }
                        if (command == TsfCommand.Update) Updates++;
                        if (command == TsfCommand.Cancel) Cancels++;
                        if (command == TsfCommand.Commit) Commits++;
                        if (command == TsfCommand.Update && DelayNextUpdate)
                        {
                            DelayNextUpdate = false; deferredText = text; deferredSequence = sequence;
                            hr = unchecked((int)0x8000000A);
                        }
                        else
                        {
                            accepted = sequence;
                            if (command is TsfCommand.Update or TsfCommand.Commit) Text = text;
                            state = command is TsfCommand.Commit or TsfCommand.Cancel ? TsfCompositionState.Committed : TsfCompositionState.Composing;
                        }
                    }
                    var reply = new byte[32];
                    BinaryPrimitives.WriteUInt32LittleEndian(reply, TsfCompositionClient.Magic);
                    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), 1);
                    BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(8), hr);
                    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), (uint)(hr != 0 ? TsfCompositionState.HostTerminated : state));
                    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(16), (uint)Text.Length);
                    BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(24), accepted);
                    await pipe.WriteAsync(reply, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (IOException) { }
            }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); await serving; stop.Dispose(); }
    }
}
