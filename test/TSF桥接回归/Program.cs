using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using iSay.Desktop.Services;

internal static partial class Program
{
    private static readonly List<object> Checks = [];
    private static uint nextThread = 0x70000000;
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--lifetime-host") return await RunLifetimeHost(args[1], args[2]);
        if (args.Length == 1 && args[0] == "--soak") return await RunBackgroundSoak();
        try
        {
            await RunUnwrittenProofTests();
            await RunInitialReadinessTests();
            await RunInitialBatchGuardTests();
            await RunSelectedRefinementTests();
            await RunStopEmptyTargetGuards();
            var session = Guid.NewGuid();
            var endpoint = Endpoint();
            var requests = new List<Request>();
            var server = Serve(endpoint, 4, request => { requests.Add(request); return Reply(request, request.Command == TsfCommand.Commit ? TsfCompositionState.Committed : TsfCompositionState.Composing); });
            var delivery = new TsfCompositionSession(new(endpoint), session);
            Check((await delivery.ApplyAsync("今", false)).Applied, "桥接先开始组合再发送首字");
            Check((await delivery.ApplyAsync("今天🙂准备出门", false)).Applied, "中文和完整代理对正文按UTF16原样传递");
            Check((await delivery.ApplyAsync("今天🙂准备出门。", true)).Applied, "结束命令和更新命令明确分离");
            await server;
            Check(requests.Select(x => x.Command).SequenceEqual(new[] { TsfCommand.Begin, TsfCommand.Update, TsfCommand.Update, TsfCommand.Commit }), "同一段只发一个Begin与一个Commit");
            Check(requests.All(x => x.Session == session) && requests.Select(x => x.Sequence).SequenceEqual(new ulong[] { 1, 2, 3, 4 }), "同段会话固定且序号严格递增");
            Check(requests[2].Text == "今天🙂准备出门", "协议不破坏中文和emoji");
            Check(!(await delivery.ApplyAsync("不能再输入", true)).Applied && requests.Count == 4, "结束后再次调用不会再次发送正文");

            await InvalidReply("错误魔数被拒绝", response => response[0] ^= 1);
            await InvalidReply("不支持的版本被拒绝", response => BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 9));
            await InvalidReply("保留字段非零被拒绝", response => response[20] = 1);
            await InvalidReply("未知状态被拒绝", response => BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), 42));
            await InvalidReply("越界长度被拒绝", response => BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(16), 1024 * 1024 + 1));
            await InvalidReply("未来序号被拒绝", response => BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(24), 10));
            await InvalidReply("未确认当前命令不能报成功", response => BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(24), 0));

            endpoint = Endpoint();
            server = Serve(endpoint, 1, request => Reply(request, TsfCompositionState.FocusUnavailable, 1));
            var focused = await new TsfCompositionClient(endpoint).SendAsync(TsfCommand.Update, session, 1, "保留原文");
            await server;
            Check(!focused.Applied && focused.State == TsfCompositionState.FocusUnavailable, "S_FALSE失焦回复保持未交付状态");

            endpoint = Endpoint(); var seen = 0;
            server = Serve(endpoint, 2, request => { seen++; return Reply(request, request.Command == TsfCommand.Begin ? TsfCompositionState.Composing : TsfCompositionState.HostTerminated, request.Command == TsfCommand.Begin ? 0 : 1); });
            delivery = new(new(endpoint), session);
            var terminated = await delivery.ApplyAsync("宿主中止后不得重新追加", false); await server;
            var afterTerminate = await delivery.ApplyAsync("宿主中止后不得重新追加全文", false);
            Check(!terminated.Applied && !afterTerminate.Applied && seen == 2, "宿主终止后不新建组合或退回普通键入");

            endpoint = Endpoint();
            server = Serve(endpoint, 2, request => Reply(request, TsfCompositionState.Composing), truncateLast: true);
            delivery = new(new(endpoint), session);
            await MustThrow<IOException>(() => delivery.ApplyAsync("可能已发送", false), "半截回复不得当成功");
            await server;
            await MustThrow<TsfConfirmationPendingException>(() => delivery.ApplyAsync("不得再发送", false), "不确定发送结果之后仅只读确认，不自动重发正文");

            var client = new TsfCompositionClient(Endpoint());
            await MustThrow<ArgumentException>(() => client.SendAsync(TsfCommand.Begin, Guid.Empty, 1, ""), "空会话不进入管道");
            await MustThrow<ArgumentException>(() => client.SendAsync(TsfCommand.Update, session, 0, ""), "零序号不进入管道");
            await MustThrow<ArgumentException>(() => client.SendAsync((TsfCommand)0, session, 1, ""), "未知命令不进入管道");
            await MustThrow<ArgumentException>(() => client.SendAsync(TsfCommand.Update, session, 1, new string('中', 1024 * 1024 + 1)), "过长正文不分配协议写入");
            await MustThrow<EncoderFallbackException>(() => client.SendAsync(TsfCommand.Update, session, 1, "\ud800"), "无效代理项拒绝发送");
            await MustThrow<ArgumentException>(() => client.SendAsync(TsfCommand.Update, session, 1, "中\0文"), "空字符拒绝发送以保持协议正文一致");

            endpoint = new((uint)Environment.ProcessId + 123456, ++nextThread);
            using (var spoof = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                var accept = spoof.WaitForConnectionAsync();
                await MustThrow<UnauthorizedAccessException>(() => new TsfCompositionClient(endpoint).SendAsync(TsfCommand.Query, session, 1, ""), "同用户伪造目标PID的管道被拒绝");
                await accept;
            }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await MustThrow<OperationCanceledException>(() => client.SendAsync(TsfCommand.Query, session, 1, "", canceled.Token), "已取消命令不等待管道");
            }
            await RunRecoveryScenarios();
            await RunStandbyResetScenarios();
            await RunAbsentSessionCleanupScenarios();
            await RunLongFocusGapScenario();
            await RunExitedOwnerScenario();
            await RunFocusRecoveryScenarios();
            if (args.Length == 3 && args[0] == "--native") await RunNativeHost(args[1], args[2]);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TSF桥接回归结果.json"));
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                时间 = DateTimeOffset.Now,
                原生组件哈希 = args.Length == 3 && args[0] == "--native" ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))) : null,
                范围 = "真实当前用户命名管道；独立隐藏TSF宿主；不注入进程、不注册输入法、不操作用户输入框",
                测试 = Checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"通过 {Checks.Count} 项；{output}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static TsfEndpoint Endpoint() => new((uint)Environment.ProcessId, ++nextThread);
    private static async Task RunRecoveryScenarios()
    {
        await using (var host = new RecoveryHost())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await delivery.BeginAsync(); host.PendingNextUpdate = true;
            await MustThrow<TsfConfirmationPendingException>(() => delivery.UpdateAsync("待确认的旧段"), "旧Update返回等待时暂停正文发送");
            host.PendingQueries = 1; host.TerminatedWhilePending = true;
            await MustThrow<TsfConfirmationPendingException>(() => delivery.UpdateAsync("不能重发的文字"),
                "HostTerminated与E_PENDING同时出现时仍需只读确认，不能缓存为永久终态");
            // 超过正常正文确认预算后，清理仍应允许只读查询原序号并结束旧范围。
            await Task.Delay(2600); host.TerminatedWhilePending = false;
            var queries = host.QueryCount;
            var cancelled = await delivery.CancelAsync();
            Check(cancelled.Applied && cancelled.State == TsfCompositionState.Committed && host.QueryCount > queries &&
                host.UpdateCount == 1 && host.CancelCount == 1 && host.Text == "待确认的旧段",
                "旧正文确认超时后可只读确认再取消一次，不重复正文且不堵死后续录音");
        }
        await using (var host = new RecoveryHost())
        {
            var diagnostics = new List<TsfPipeDiagnostic>();
            var delivery = new TsfCompositionSession(new(host.Endpoint, diagnostics.Add), Guid.NewGuid());
            await delivery.BeginAsync(); host.DropNextReply = TsfCommand.Update;
            Check((await delivery.UpdateAsync("可能已经写入")).Applied && host.UpdateCount == 1 && host.QueryCount == 1,
                "快速丢失Update回复后立即只读确认，不空等首字或重发正文");
            Check((await delivery.UpdateAsync("可能已经写入")).Applied && host.UpdateCount == 1 && host.QueryCount == 1,
                "确切原序号确认同一快照后不再发送第二次Update");
            await delivery.UpdateAsync("可能已经写入并继续说");
            Check(host.UpdateCount == 2 && host.Text == "可能已经写入并继续说", "确认成功后仅在同一组合更新最新变化");
            host.DropNextReply = TsfCommand.Commit;
            Check((await delivery.CommitAsync(host.Text)).State == TsfCompositionState.Committed && host.CommitCount == 1,
                "Commit回复快速丢失立即只查询原序号，不再次提交");
            Check(!(await delivery.CommitAsync(host.Text)).Applied && host.CommitCount == 1,
                "原Commit序号和最终状态证明完成后不二次提交");
            Check(diagnostics.All(x => x.TextLength >= 0 && x.TotalMilliseconds >= 0) && diagnostics.Any(x => x.Command == TsfCommand.Query),
                "诊断包含分阶段时长、长度和Query而不携带正文");
        }
        await using (var host = new RecoveryHost { PendingQueries = 2 })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await delivery.BeginAsync(); host.PendingNextUpdate = true;
            await MustThrow<TsfConfirmationPendingException>(() => delivery.UpdateAsync("异步编辑"), "原生E_PENDING视为在途而不是已交付");
            await MustThrow<TsfConfirmationPendingException>(() => delivery.UpdateAsync("异步编辑的新快照"), "Query仍E_PENDING时不能覆盖在途正文");
            await MustThrow<TsfConfirmationPendingException>(() => delivery.UpdateAsync("异步编辑的新快照"), "重复旧确认仍只查原命令");
            try { await delivery.UpdateAsync("异步编辑的新快照"); throw new InvalidOperationException("应分开确认和新写入"); }
            catch (TsfConfirmationPendingException confirmation) { Check(confirmation.Recovered, "确认旧序号后下一轮单独更新，避免叠加两次管道预算"); }
            Check(host.UpdateCount == 1 && host.QueryCount == 3, "确认期间只有原始一条Update，三个Query不修改正文");
            Check((await delivery.UpdateAsync("异步编辑的新快照")).Applied && host.UpdateCount == 2,
                "异步确认完成后同一范围继续实时更新");
        }
        await using (var host = new RecoveryHost())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await delivery.BeginAsync(); await delivery.UpdateAsync("甲甲");
            host.DropNextReply = TsfCommand.Update; host.DropWithoutApply = true;
            await MustThrow<TsfConfirmationPendingException>(() => delivery.UpdateAsync("乙乙"), "相同长度的另一快照失去回复仍进入未知状态");
            await MustThrow<TsfConfirmationPendingException>(() => delivery.UpdateAsync("乙乙"), "只有旧序号即便长度相同也不能解除未知状态");
            await Task.Delay(2550);
            await MustThrow<TsfConfirmationPendingException>(() => delivery.UpdateAsync("不得覆盖用户新字"), "确认预算到期最多补一次只读查询，不能盲目重发");
            await MustThrow<IOException>(() => delivery.UpdateAsync("仍不能覆盖用户新字"), "迟到只读确认仍无证明时停止正文重试，不能无限查询");
            Check(host.UpdateCount == 2 && host.QueryCount == 3 && host.Text == "甲甲", "相同长度不同字负例没有重放或覆盖旧正文");
        }
        await using (var host = new RecoveryHost())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            host.DropNextReply = TsfCommand.Begin;
            Check((await delivery.BeginAsync()).Applied && host.BeginCount == 1, "Begin回复丢失立即核验原会话，不重捕获目标");
            Check((await delivery.BeginAsync()).Applied && host.BeginCount == 1 && host.QueryCount == 1,
                "仅Query确证原Begin成功，未创建第二个组合");
            await delivery.UpdateAsync("开始后原范围文字");
            host.FocusUnavailable = true;
            Check(!(await delivery.UpdateAsync("失焦期间新增")).Applied, "明确失焦未交付且不会提交或改写");
            host.FocusUnavailable = false;
            Check((await delivery.UpdateAsync("失焦期间新增")).Applied && host.Text == "失焦期间新增" && host.BeginCount == 1,
                "返回原范围无需新Begin即可续写累计快照");
        }
        await using (var host = new RecoveryHost())
        {
            var diagnostics = new List<TsfPipeDiagnostic>();
            var delivery = new TsfCompositionSession(new(host.Endpoint, diagnostics.Add), Guid.NewGuid());
            await delivery.BeginAsync(); host.DelayNextUpdateReplyMilliseconds = 850;
            await MustThrow<TsfConfirmationPendingException>(() => delivery.UpdateAsync("慢宿主原快照"), "宿主已写但回复超过700ms时进入查询而非永久丢弃后续转写");
            var watch = Stopwatch.StartNew();
            TsfReply? confirmed = null;
            while (watch.ElapsedMilliseconds < 1800)
            {
                try { confirmed = await delivery.UpdateAsync("慢宿主原快照"); if (confirmed.Applied) break; }
                catch (TsfConfirmationPendingException) { }
                await Task.Delay(100);
            }
            Check(confirmed?.Applied == true && host.UpdateCount == 1 && host.QueryCount >= 1,
                "慢回复经只读原序号确认恢复，未重发原正文");
            Check(diagnostics.Any(x => x.Stage == "reply" && x.ReplyMilliseconds >= 600) && diagnostics.Any(x => x.Command == TsfCommand.Query),
                "慢宿主诊断可区分回复等待与连接排队");
        }
        await using (var host = new RecoveryHost { Text = "原选区内容" })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            host.DropNextReply = TsfCommand.Begin;
            Check((await delivery.BeginAsync()).Applied && host.BeginCount == 1 && host.QueryCount == 1 && host.Text == "原选区内容",
                "Begin丢回复后按确切序号确认保留的非空选区，不错误要求零长度");
        }
        var absentEndpoint = Endpoint(); var unchanged = true;
        var recovering = new TsfCompositionSession(new(absentEndpoint), Guid.NewGuid(), () => unchanged);
        await MustThrow<TsfTransportException>(() => recovering.BeginAsync(), "首次连接尚未送达允许暂存而不冒充已初始化");
        await using (var host = new RecoveryHost(absentEndpoint))
        {
            Check((await recovering.UpdateAsync("原焦点未变化")).Applied && host.BeginCount == 1 && host.UpdateCount == 1,
                "首次安全连接失败后在原焦点及活动版本未变化时仅重试空Begin");
        }
        foreach (var changed in new[] { "同HWND内键鼠版本已变", "焦点已移至其他输入框" })
        {
            await using var host = new RecoveryHost { FocusUnavailable = true };
            var safe = true;
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => safe);
            Check(!(await delivery.BeginAsync()).Applied, changed + "：首次失焦明确未初始化");
            safe = false; host.FocusUnavailable = false;
            Check(!(await delivery.UpdateAsync("不得写到迟到目标")).Applied && host.BeginCount == 1 && host.UpdateCount == 0,
                changed + "：禁止第二次Begin和累计正文发送");
            Check((await delivery.CancelAsync()).State == TsfCompositionState.Idle && host.CancelCount == 0,
                changed + "：未创建范围的放弃初始化可安全清理");
        }
        await using (var host = new RecoveryHost { FocusUnavailable = true })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await delivery.BeginAsync(); host.FocusUnavailable = false;
            Check((await delivery.UpdateAsync("同一目标恢复")).Applied && host.BeginCount == 2 && host.UpdateCount == 1,
                "首次FocusUnavailable未封死本段，无活动同一目标恢复后可初始化并更新");
        }
        await using (var host = new RecoveryHost())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await delivery.BeginAsync(); await delivery.UpdateAsync("旧段已显示正文"); host.BusyCancelsRemaining = 2;
            Check(!(await delivery.CancelAsync()).Applied && !(await delivery.CancelAsync()).Applied && host.CancelCount == 2,
                "取消遇Busy不能提前标记完成，仍保留清理能力");
            Check((await delivery.CancelAsync()).State == TsfCompositionState.Committed && host.CancelCount == 3 && host.Text == "旧段已显示正文",
                "Busy解除后明确取消旧范围且保留正文");
            await delivery.CancelAsync(); Check(host.CancelCount == 3, "已确认取消后不再次发送取消命令");
        }
        await using (var host = new RecoveryHost())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await delivery.BeginAsync(); await delivery.UpdateAsync("取消保留正文"); host.DropNextReply = TsfCommand.Cancel;
            Check((await delivery.CancelAsync()).State == TsfCompositionState.Committed && host.CancelCount == 1 && host.QueryCount == 1 && host.Text == "取消保留正文",
                "取消丢回复只查原序号，非空保留正文不妨碍终态确认");
            await delivery.CancelAsync(); Check(host.CancelCount == 1, "只读确认取消成功后不二次发送");
        }
        await using (var host = new RecoveryHost())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await delivery.BeginAsync(); await delivery.UpdateAsync("取消超时仍保留原文"); host.DelayNextCancelReplyMilliseconds = 350;
            using (var deadline = new CancellationTokenSource(60))
                await MustThrow<OperationCanceledException>(() => delivery.CancelAsync(deadline.Token), "调用方取消清理等待不能冒充宿主已结束");
            await Task.Delay(400);
            Check((await delivery.CancelAsync()).State == TsfCompositionState.Committed && host.CancelCount == 1 && host.QueryCount == 1 && host.Text == "取消超时仍保留原文",
                "取消已生效但调用方先超时，下一次清理只读确认而非重新取消");
        }
        await using (var host = new RecoveryHost { PendingQueries = 0 })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid());
            await delivery.BeginAsync(); await delivery.UpdateAsync("旧取消查询预算保留文字"); host.PendingNextCancel = true;
            await MustThrow<TsfConfirmationPendingException>(() => delivery.CancelAsync(), "异步取消保持待确认而非提前标记结束");
            await Task.Delay(2550);
            Check((await delivery.CancelAsync()).State == TsfCompositionState.Committed && host.CancelCount == 1 && host.QueryCount == 1,
                "下一次明确清理可有界只读确认超出原预算的旧Cancel，不重发空取消或正文");
        }
        await using (var host = new RecoveryHost())
        {
            var safe = true;
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => safe);
            await delivery.UpdateAsync("首段", default); await delivery.UpdateAsync(new string('文',136));
            host.TerminateNextUpdate = true;
            Check((await delivery.UpdateAsync(new string('文',203))).Applied && host.OwnedRecoveryCount == 1 && host.BeginCount == 1 && host.Text.Length == 203,
                "长段被宿主结束后只走原范围恢复协议不再次Begin");
            host.TerminateNextUpdate = true; host.DropNextReply = TsfCommand.RecoverOwnedRange;
            Check((await delivery.UpdateAsync(new string('文',250))).Applied && host.OwnedRecoveryCount == 2 && host.Text.Length == 250,
                "恢复回复丢失只读确认不重复全文");
            host.TerminateNextUpdate = true; safe = false;
            Check(!(await delivery.UpdateAsync("手改后不得覆盖")).Applied && host.OwnedRecoveryCount == 2 && host.Text.Length == 250,
                "物理输入版本变化拒绝长段恢复");
        }
        await using (var host = new RecoveryHost { RejectOwnedRecovery = true })
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => true);
            await delivery.UpdateAsync("首段"); await delivery.UpdateAsync(new string('文',70));
            host.TerminateNextUpdate = true;
            Check(!(await delivery.UpdateAsync(new string('文',136))).Applied && host.OwnedRecoveryCount == 1 && host.BeginCount == 1 && host.Text.Length == 70,
                "旧宿主不支持新命令时不新建范围不普通键入");
        }
    }

    private sealed class RecoveryHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        private ulong accepted;
        private Request? deferred;
        private TsfCompositionState state = TsfCompositionState.Idle;
        public TsfEndpoint Endpoint { get; }
        public TsfCommand? DropNextReply;
        public bool DropWithoutApply, PendingNextUpdate, PendingNextCancel, PendingNextBegin, PendingNextCommit, FocusUnavailable, TerminateNextUpdate, RejectOwnedRecovery, TerminatedWhilePending, ResetOnNextQuery;
        public int OwnedRecoveryCount, InitialRecoveryCount;
        public int TerminateAfterFirstBatchQueries;
        public int PendingQueries, UpdateCount, QueryCount, BeginCount, CommitCount, CancelCount, BusyCancelsRemaining;
        public int DelayNextUpdateReplyMilliseconds, DelayNextCancelReplyMilliseconds;
        public string Text = "";
        public TsfReply? QueryOverride;
        public RecoveryHost(TsfEndpoint? endpoint = null) { Endpoint = endpoint ?? Program.Endpoint(); serving = ServeAsync(); }
        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                    var header = new byte[40]; await pipe.ReadExactlyAsync(header, stop.Token);
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                    var bytes = new byte[checked((int)length * 2)]; if (bytes.Length != 0) await pipe.ReadExactlyAsync(bytes, stop.Token);
                    var request = new Request((TsfCommand)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), new Guid(header.AsSpan(16, 16)), BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)), Encoding.Unicode.GetString(bytes));
                    var dropping = DropNextReply == request.Command;
                    var hr = 0;
                    if (request.Command == TsfCommand.Query)
                    {
                        QueryCount++;
                        if (ResetOnNextQuery) { ResetOnNextQuery = false; deferred = null; accepted = 0; state = TsfCompositionState.Idle; Text = ""; }
                        if (UpdateCount == 1 && state == TsfCompositionState.Composing && TerminateAfterFirstBatchQueries > 0 &&
                            --TerminateAfterFirstBatchQueries == 0) state = TsfCompositionState.HostTerminated;
                        if (deferred is not null)
                        {
                            if (PendingQueries-- > 0) hr = unchecked((int)0x8000000A);
                            else { Apply(deferred); deferred = null; }
                        }
                    }
                    else
                    {
                        if (request.Command == TsfCommand.Update) UpdateCount++;
                        if (request.Command == TsfCommand.RecoverOwnedRange) OwnedRecoveryCount++;
                        if (request.Command == TsfCommand.RecoverInitial) InitialRecoveryCount++;
                        if (request.Command == TsfCommand.Begin) BeginCount++;
                        if (request.Command == TsfCommand.Commit) CommitCount++;
                        if (request.Command == TsfCommand.Cancel) CancelCount++;
                        if (request.Command == TsfCommand.Cancel && BusyCancelsRemaining-- > 0) hr = unchecked((int)0x800700AA);
                        else if (request.Command == TsfCommand.Update && PendingNextUpdate || request.Command == TsfCommand.Begin && PendingNextBegin || request.Command == TsfCommand.Commit && PendingNextCommit)
                        { PendingNextUpdate = PendingNextBegin = PendingNextCommit = false; deferred = request; hr = unchecked((int)0x8000000A); }
                        else if (request.Command == TsfCommand.Cancel && PendingNextCancel)
                        { PendingNextCancel = false; deferred = request; hr = unchecked((int)0x8000000A); }
                        else if (FocusUnavailable && request.Command is TsfCommand.Begin or TsfCommand.Update) hr = 1;
                        else if (request.Command == TsfCommand.Update && TerminateNextUpdate)
                        { TerminateNextUpdate = false; state = TsfCompositionState.HostTerminated; hr = unchecked((int)0x80040504); }
                        else if (request.Command == TsfCommand.RecoverOwnedRange && RejectOwnedRecovery) hr = unchecked((int)0x80070057);
                        else if (!dropping || !DropWithoutApply) Apply(request);
                    }
                    if (dropping) { DropNextReply = null; DropWithoutApply = false; continue; }
                    var reply = Reply(request, hr == unchecked((int)0x8000000A) && TerminatedWhilePending ?
                        TsfCompositionState.HostTerminated : FocusUnavailable ? TsfCompositionState.FocusUnavailable : state, hr);
                    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(16), (uint)Text.Length);
                    BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(24), accepted);
                    if (request.Command == TsfCommand.Query && QueryOverride is { } query)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(8), query.HResult);
                        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), (uint)query.State);
                        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(16), (uint)query.TextLength);
                        BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(24), query.AcceptedSequence);
                    }
                    if (request.Command == TsfCommand.Update && DelayNextUpdateReplyMilliseconds > 0)
                    {
                        var delay = DelayNextUpdateReplyMilliseconds; DelayNextUpdateReplyMilliseconds = 0;
                        await Task.Delay(delay, stop.Token);
                    }
                    if (request.Command == TsfCommand.Cancel && DelayNextCancelReplyMilliseconds > 0)
                    {
                        var delay = DelayNextCancelReplyMilliseconds; DelayNextCancelReplyMilliseconds = 0;
                        await Task.Delay(delay, stop.Token);
                    }
                    await pipe.WriteAsync(reply, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (IOException) { }
            }
        }
        private void Apply(Request request)
        {
            accepted = request.Sequence;
            if (request.Command is TsfCommand.Update or TsfCommand.Commit or TsfCommand.RecoverOwnedRange or TsfCommand.RecoverInitial) Text = request.Text;
            state = request.Command is TsfCommand.Commit or TsfCommand.Cancel ? TsfCompositionState.Committed : TsfCompositionState.Composing;
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); await serving; stop.Dispose(); }
    }
    private static async Task RunNativeHost(string executable, string dll)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TSF桥接数据", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(directory);
        var readyPath = Path.Combine(directory, "就绪.json"); var resultPath = Path.Combine(directory, "结果.json");
        var stopName = "Local\\iSayTsfBridgeTest_" + Guid.NewGuid().ToString("N");
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "--pipe-host", Path.GetFullPath(dll), readyPath, resultPath, stopName }) start.ArgumentList.Add(argument);
        using var host = Process.Start(start) ?? throw new InvalidOperationException("隐藏TSF宿主未启动。");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(readyPath))
            {
                if (host.HasExited) throw new InvalidOperationException("隐藏TSF宿主提前退出。");
                await Task.Delay(25, deadline.Token);
            }
            using var ready = JsonDocument.Parse(File.ReadAllText(readyPath));
            var endpoint = new TsfEndpoint(ready.RootElement.GetProperty("ProcessId").GetUInt32(), ready.RootElement.GetProperty("ThreadId").GetUInt32());
            Check(endpoint.ProcessId == host.Id && ready.RootElement.GetProperty("Hidden").GetBoolean(), "跨进程TSF宿主身份正确且始终隐藏");
            var delivery = new TsfCompositionSession(new(endpoint), Guid.NewGuid());
            Check((await delivery.ApplyAsync("今天", false)).Applied, "C#管道首字进入真实TSF组合范围");
            Check((await delivery.ApplyAsync("今天准备出门🙂", false)).Applied, "C#累计快照更新同一原生组合范围");
            Check((await delivery.ApplyAsync("今天准备出门🙂", false)).Applied, "相同快照在真实原生服务保持幂等");
            await Task.Delay(100, deadline.Token);
            Check((await delivery.ApplyAsync("今天准备出门🙂", true)).Applied, "F3对应Commit经管道在真实TSF范围提交");
            await CheckNativeAbsentSessionCleanup(endpoint);
            stop.Set();
            await host.WaitForExitAsync(deadline.Token);
            using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
            var root = result.RootElement;
            Check(root.GetProperty("Passed").GetBoolean(), "隐藏宿主完整退出且没有后台错误");
            Check(root.GetProperty("Text").GetString() == "已有内容：今天准备出门🙂", "最终正文只有一份并完整保留已有前文、中文和emoji");
            Check(root.GetProperty("SetTextCount").GetInt32() == 2, "两次正文变化只发生两次SetText且结束不再重打全文");
            Check(root.GetProperty("CompositionStarts").GetInt32() == 1 && root.GetProperty("CompositionEnds").GetInt32() == 1 && root.GetProperty("ActiveCompositions").GetInt32() == 0,
                "整段输入只开始一次组合并在Commit结束一次");
            Check(root.GetProperty("SawDisplayAttribute").GetBoolean(), "跨进程交付中读取到真实下划线属性");
            Check(root.GetProperty("Hidden").GetBoolean() && root.GetProperty("CleanUnload").GetBoolean(), "真实宿主不显示前台且服务全部释放可卸载");
        }
        finally
        {
            stop.Set();
            if (!host.HasExited)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await host.WaitForExitAsync(cleanup.Token); }
                catch (OperationCanceledException) { host.Kill(); await host.WaitForExitAsync(); }
            }
        }
    }
    private sealed record Request(TsfCommand Command, Guid Session, ulong Sequence, string Text);
    private static async Task Serve(TsfEndpoint endpoint, int count, Func<Request, byte[]> respond, bool truncateLast = false)
    {
        // 每个连接只收一帧；限制缓冲区，不接受声明之外的无限正文。
        for (var index = 0; index < count; index++)
        {
            using var pipe = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.WaitForConnectionAsync(timeout.Token);
            var header = new byte[40]; await pipe.ReadExactlyAsync(header, timeout.Token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != TsfCompositionClient.Magic) throw new InvalidDataException();
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            if (length < 0 || length > TsfCompositionClient.MaximumTextLength) throw new InvalidDataException();
            var text = new byte[length * 2]; await pipe.ReadExactlyAsync(text, timeout.Token);
            var request = new Request((TsfCommand)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), new Guid(header.AsSpan(16, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)), new UnicodeEncoding(false, false, true).GetString(text));
            var response = respond(request);
            await pipe.WriteAsync(truncateLast && index == count - 1 ? response.AsMemory(0, 12) : response, timeout.Token);
        }
    }
    private static byte[] Reply(Request request, TsfCompositionState state, int hresult = 0)
    {
        var response = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(response, TsfCompositionClient.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(8), hresult);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), (uint)state);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(16), (uint)request.Text.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(24), request.Sequence);
        return response;
    }
    private static async Task InvalidReply(string name, Action<byte[]> corrupt)
    {
        var endpoint = Endpoint();
        var server = Serve(endpoint, 1, request => { var response = Reply(request, TsfCompositionState.Composing); corrupt(response); return response; });
        await MustThrow<InvalidDataException>(() => new TsfCompositionClient(endpoint).SendAsync(TsfCommand.Update, Guid.NewGuid(), 1, "测试"), name);
        await server;
    }
    private static async Task MustThrow<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); } catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("未抛出预期异常：" + name);
    }
    private static void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); Checks.Add(new { 名称 = name, 通过 = true }); Console.WriteLine("通过：" + name); }
}
