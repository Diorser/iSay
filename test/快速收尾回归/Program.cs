using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using iSay.Desktop;
using iSay.Desktop.Services;
using iSay.Voice;
using iSay.Voice.Engines;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> checks = [];
    private static readonly List<object> metrics = [];
    private static App app = null!;
    private static VoiceViewModel vm = null!;
    private static string output = "";
    private static uint nextThread = 0x75000000;

    [STAThread]
    private static int Main(string[] args)
    {
        var foreground = GetForegroundWindow();
        try
        {
            // 仅运行真实App方法和本进程假管道，不启动正式入口、窗口、系统热键或麦克风。
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            app = new iSay.Tests.OffscreenApp(); vm = (VoiceViewModel)Get("vm")!;
            using var input = new WindowsInputService(false);
            Set("input", input); Set("floating", new FloatingBarWindow()); Set("preparing", 1);
            output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../..", "证据", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
            Directory.CreateDirectory(output);
            var realRuntime = OptionPath(args, "--real-native-runtime");
            var realData = OptionPath(args, "--real-native-data");
            var realOnly = args.Contains("--real-native-only", StringComparer.Ordinal);
            var realRequested = realRuntime is not null || realData is not null || realOnly;
            if (realRequested && (realRuntime is null || realData is null))
                throw new ArgumentException("真实原生整理验收必须同时指定运行库和独立测试数据目录。");
            if (!realOnly)
            {
            RunAppCase("无AI最终纠正后提交", false);
            RunAppCase("AI慢返回先退出编辑", true);
            RunAppCase("真实本地慢HTTP先退出编辑", true, localAi: true);
            RunAppCase("豆包内置无需API配置且先退出编辑", true, nativeAi: true);
            RunAppCase("豆包内置失败仍保留已提交原文", true, nativeAi: true, failAi: true);
            RunAppCase("豆包内置取消不影响已提交原文", true, nativeAi: true, cancelAi: true);
            RunAppCase("AI失败仍保留已提交基础文字", true, failAi: true);
            RunAppCase("AI超时仍保留已提交基础文字", true, timeoutAi: true);
            RunAppCase("主动取消AI仍保留已提交基础文字", true, cancelAi: true);
            RunAppCase("意外异常仍保留已提交基础文字", true, throwAi: true);
            RunAppCase("外部编辑拒绝旧AI覆盖", true, mutate: "external");
            RunAppCase("本地手改拒绝旧AI覆盖", true, mutate: "revision");
            RunAppCase("新会话拒绝旧AI覆盖", true, mutate: "session");
            RunAppCase("结束后输入同样先提交", true, mode: "on-stop");
            RunAppCase("空录音不提交空段", false, empty: true);
            RunAppCase("设备中断不自动整理", true, reason: "设备中断");
            RunAppCase("取消录音不自动整理", true, reason: "用户取消");
            RunLocalHotkeyWithOldDelivery();
            Pump(BridgeCases());
            }
            var nativeIndex = Array.IndexOf(args, "--native-host");
            var dllIndex = Array.IndexOf(args, "--native-dll");
            var nativeValidated = nativeIndex >= 0 && dllIndex >= 0;
            if (realRequested && !nativeValidated) throw new ArgumentException("真实原生整理验收必须指定隐藏TSF宿主及组件。");
            if (nativeValidated && !realOnly)
            {
                RunNativeAppCase(args[nativeIndex + 1], args[dllIndex + 1], false);
                RunNativeAppCase(args[nativeIndex + 1], args[dllIndex + 1], true);
            }
            if (realRequested) RunRealNativeAppCase(args[nativeIndex + 1], args[dllIndex + 1], realRuntime!, realData!);
            Check(GetForegroundWindow() == foreground && !((FloatingBarWindow)Get("floating")!).IsVisible,
                "全部测试保持原前台窗口且浮条从未显示");
            ((FloatingBarWindow)Get("floating")!).Close();
            File.WriteAllText(Path.Combine(output, "结果.json"), JsonSerializer.Serialize(new
            {
                成功 = true, 检查数 = checks.Count, 测试 = checks, 指标 = metrics, 原生闭环 = nativeValidated, 真实原生整理闭环 = realRequested,
                范围 = realRequested ? "真实App离屏方法、可控最终识别、隐藏真实TSF宿主及组件、冻结豆包原生整理helper与云端；未打开麦克风、正式App或用户窗口。" :
                    "真实App离屏方法、可控延迟假引擎、慢HTTP处理器、真实C#协议会话；指定原生参数时额外验证隐藏真实TSF文本存储。所有测试不代表真实麦克风或云端延迟。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"通过 {checks.Count} 项；证据：{output}");
            return 0;
        }
        catch (Exception error)
        {
            if (output.Length > 0) File.WriteAllText(Path.Combine(output, "失败.json"), JsonSerializer.Serialize(new { 测试 = checks, 指标 = metrics, 错误 = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error); return 1;
        }
    }

    private static void RunLocalHotkeyWithOldDelivery()
    {
        var directory = Path.Combine(output, "本页热键不等待旧宿主");
        using var handler = new DelayedHandler(new MemoryComposition(), false, false);
        var settings = new VoiceSettings { OutputMode = "live", Hotkey = "RightAlt", SmartFormattingEnabled = false,
            SoundFeedback = false, ShowFloatingBar = false, SaveHistory = false, SaveAudio = false };
        var id = Guid.NewGuid();
        var engine = new ControlledEngine();
        var target = new MemoryComposition();
        var compatibility = new MemoryCompatibility();
        var window = new VoiceDeskWindow { DataContext = vm, ShowInTaskbar = false };
        Set("window", window); Set("store", new SettingsStore(directory)); Set("formatter", new TextFormatting(handler));
        Set("delivery", new CompositionDeliveryRouter(compatibility, _ => target)); Set("sessionId", id);
        Set("performance", new SessionPerformance(id)); Set("activeSettings", settings); Set("savedSettings", settings);
        Set("engine", engine); Set("localTranscript", true); Set("stopping", false); Set("previewOnly", false);
        Set("capture", null); Set("captureStopped", null); Set("audioPump", null); Set("deliveryCapture", null);
        Set("deliveryPending", 1); Set("testAudioPath", "离屏假音频"); Set("testOutputPath", null);
        vm.ResetTranscript(); vm.IsRecording = true; vm.IsBusy = false;
        try
        {
            Emit(id, "本页正在转写");
            var stop = (Task)typeof(App).GetMethod("HandleRecordingHotkeyAsync", Private)!.Invoke(app, [RecordingHotkeyAction.Toggle])!;
            var uiProbe = app.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Input);
            Pump(uiProbe.Task);
            Check(uiProbe.Task.Result && !stop.IsCompleted, "等待句尾时本页窗口仍可处理界面事件");
            engine.Complete("本页正在转写，句尾已保留。");
            Pump(stop, 1500);
            Check(stop.IsCompletedSuccessfully && !vm.IsBusy && !vm.IsRecording && vm.TranscriptText == "本页正在转写，句尾已保留。",
                "旧宿主仍待响应时右 Alt 可结束本页录音并显示完整句尾");
            Check(target.Commits == 0 && compatibility.Calls == 0, "本页转写不会向外部输入框发送正文");
        }
        finally { Set("deliveryPending", 0); window.Close(); }
    }

    private static string? OptionPath(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("测试路径参数缺少值：" + option);
        return Path.GetFullPath(args[index + 1]);
    }

    private static void RunRealNativeAppCase(string hostPath, string dllPath, string runtimePath, string dataRoot)
    {
        const string name = "真实App到豆包原生整理及TSF原范围闭环";
        const string partialText = "嗯那个我们今天先完成测试";
        const string finalText = "嗯那个我们今天先完成测试然后再检查用户体验";
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var frozenRuntime = Path.GetFullPath(Path.Combine(projectRoot, "external", "smart-runtime"));
        var testRoot = Path.GetFullPath(Path.Combine(projectRoot, "test")) + Path.DirectorySeparatorChar;
        Check(runtimePath.Equals(frozenRuntime, StringComparison.OrdinalIgnoreCase), name + "：只使用冻结external/smart-runtime运行库");
        Check(dataRoot.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase), name + "：数据目录限于本项目test内，与正式资料隔离");
        var directory = Path.Combine(output, name); Directory.CreateDirectory(directory);
        var nativeData = Path.Combine(dataRoot, "App闭环-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nativeData);
        using var native = new NativeHarness(hostPath, dllPath, directory);
        var beforeWorkers = RuntimeWorkers(runtimePath);
        var participants = new List<HelperIdentity>();
        var commands = new List<TsfPipeDiagnostic>();
        var commandTimes = new List<object>();
        long commitAt = 0, refineAt = 0;
        var id = Guid.NewGuid();
        var compatibility = new MemoryCompatibility();
        var router = new CompositionDeliveryRouter(compatibility, session => new TsfCompositionSession(new(native.Endpoint, diagnostic =>
        {
            var now = Stopwatch.GetTimestamp();
            lock (commands)
            {
                commands.Add(diagnostic);
                commandTimes.Add(new { 命令 = diagnostic.Command.ToString(), 阶段 = diagnostic.Stage, 时间戳 = now });
            }
            if (diagnostic.Stage == "completed" && diagnostic.HResult == 0 && diagnostic.State == TsfCompositionState.Committed)
            {
                if (diagnostic.Command == TsfCommand.Commit) Interlocked.CompareExchange(ref commitAt, now, 0);
                if (diagnostic.Command == TsfCommand.RefineCommitted) Interlocked.CompareExchange(ref refineAt, now, 0);
            }
        }), session, () => true, () => () => true));
        var observed = new ObservedNativeOrganizer(new NativeSmartTextOrganizer(runtimePath, nativeData), native.Snapshot);
        var formatter = new TextFormatting(nativeOrganizer: observed);
        var settings = new VoiceSettings
        {
            OutputMode = "live", InputBackend = "tsf", SmartFormattingEnabled = true, FormattingBackend = "auto",
            ApiBaseUrl = "", ApiModel = "", ApiKey = "", SaveHistory = false, SaveAudio = false,
            ShowFloatingBar = false, SoundFeedback = false
        };
        var engine = new ControlledEngine();
        var performance = new SessionPerformance(id);
        NativeSnapshot? partial = null, afterAi = null;
        HelperIdentity[] afterDispose = [];
        try
        {
            // 校验和预热在后台完成，再进入App真实收尾；没有以假整理器替代任何业务结果。
            Pump(Task.Run(() => formatter.PrepareAsync(settings)), 35000);
            Check(observed.IsAvailable && !TextFormatting.UsesCustomApi(settings), name + "：无API地址、模型或Key仍可使用真实内置组件");
            participants.AddRange(RuntimeWorkers(runtimePath).Except(beforeWorkers));
            Check(participants.Count > 0, name + "：真实预热helper已经启动，非仅模拟IsAvailable");
            Set("store", new SettingsStore(directory)); Set("formatter", formatter);
            Set("delivery", router); Set("sessionId", id); Set("performance", performance);
            Set("activeSettings", settings); Set("savedSettings", settings); Set("engine", engine);
            Set("localTranscript", false); Set("previewOnly", false); Set("exiting", false); Set("stopping", false);
            Set("preparing", 1); Set("diagnosticPath", Path.Combine(directory, "状态.json")); Set("testAudioPath", "离屏假音频");
            Set("testOutputPath", null); Set("audioPump", null); Set("captureStopped", null); Set("capture", null);
            Set("automaticFormattingRequests", 0); Set("finalDelivered", false); Set("deliveryPending", 0);
            Set("lastDeliverySucceeded", false); Set("lastDeliveredSnapshot", ""); Set("lastDeliveryMessage", "");
            Set("deliveryRequested", false); Set("pendingSpeech", null); Set("speechDrainScheduled", 0);
            Set("formattingCancellation", null); Set("startAfterStop", false);
            vm.ResetTranscript(); vm.IsRecording = true; vm.IsBusy = false; vm.IsFormatting = false;
            var capture = router.BeginAsync(id, true); Pump(capture); Set("deliveryCapture", capture);
            Check(capture.Result.Success, name + "：真实TSF组件在隐藏文档建立组合");
            Emit(id, partialText);
            PumpUntil(() => native.Snapshot().Text == "已有内容：" + partialText, 10000);
            partial = native.Snapshot();
            Check(partial.Active == 1 && partial.CompositionCount == 1 && partial.Attribute,
                name + "：实时部分文本具有活动组合及下划线属性");
            var stop = (Task)typeof(App).GetMethod("StopAsync", Private)!.Invoke(app, ["用户结束"])!;
            PumpFor(70);
            Check(!stop.IsCompleted && native.Snapshot().Active == 1 && !observed.Started.IsCompleted,
                name + "：最终识别尚未返回时不提前提交，也不启动完整整理");
            engine.Complete(finalText);
            Pump(stop, 10000);
            var stopReturnedAt = Stopwatch.GetTimestamp();
            Check(!vm.IsRecording && !vm.IsBusy && (bool)Get("finalDelivered")!, name + "：F3收尾已完成基础交付且不等待云端整理");
            // 新调度把整理入口放到Task.Run；等待真实启动信号，不假设Stop返回时任务已获线程。
            Pump(observed.Started, 10000);
            var beforeAi = observed.HostAtRequest!;
            Check(beforeAi.Text == "已有内容：" + finalText && beforeAi.Active == 0 && beforeAi.CompositionCount == 0 && !beforeAi.Attribute,
                name + "：原生整理实际开始前最终句尾已完整提交，组合与下划线均结束");
            Check(Volatile.Read(ref commitAt) > 0 && Volatile.Read(ref commitAt) <= observed.RequestedAt,
                name + "：真实Commit确认时间早于原生整理请求入口");
            Pump(observed.Completed, 45000);
            PumpUntil(() => ((HashSet<Task>)Get("formattingTasks")!).Count == 0, 10000);
            participants.AddRange(RuntimeWorkers(runtimePath).Except(beforeWorkers));
            var result = observed.Result!;
            Check(observed.Requests == 1 && result.Warning is null && result.Method == "内置智能整理" && result.Text != finalText,
                name + "：唯一一次真实原生整理返回改写正文，没有用基础降级冒充成功");
            Check(observed.CompletedAt > observed.RequestedAt && observed.CompletedAt > Volatile.Read(ref commitAt) && observed.CompletedAt > stopReturnedAt,
                name + "：真实云端结果返回晚于Commit和Stop返回");
            afterAi = native.Snapshot();
            Check(afterAi.Text == "已有内容：" + vm.FormattedText && vm.FormattedText == result.Text &&
                afterAi.Writes == beforeAi.Writes + 1 && vm.RawText == finalText,
                name + "：完整原文保留，原有前缀保留，整理仅改原段一次且没有重复全文");
            lock (commands)
                Check(commands.Count(item => item.Command == TsfCommand.Begin) == 1 && commands.Count(item => item.Command == TsfCommand.Commit) == 1 &&
                    commands.Count(item => item.Command == TsfCommand.RefineCommitted) == 1 && Volatile.Read(ref refineAt) > observed.CompletedAt,
                    name + "：全链只有一次Begin、一次Commit，真实结果后才有一次Refine");
            Check(afterAi.Active == 0 && afterAi.CompositionCount == 0 && !afterAi.Attribute && afterAi.Hidden && afterAi.ForegroundUnchanged && compatibility.Calls == 0,
                name + "：整理后没有活动编辑态或粘贴回退，真实宿主全程隐藏且前台未变");
            Check((bool)Get("finalDelivered")! && !vm.IsFormatting && !vm.IsBusy, name + "：最终交付状态成功且后台整理已收尾");
        }
        finally
        {
            participants.AddRange(RuntimeWorkers(runtimePath).Except(beforeWorkers));
            Pump(formatter.DisposeAsync().AsTask(), 15000);
            afterDispose = RuntimeWorkers(runtimePath).Except(beforeWorkers).ToArray();
        }
        Check(afterDispose.Length == 0 && participants.Count > 0, name + "：请求helper及下一段预热helper均退出，没有遗留新进程");
        native.Finish();
        Check(native.FinalResult.GetProperty("Passed").GetBoolean() && native.FinalResult.GetProperty("CleanUnload").GetBoolean() &&
            native.FinalResult.GetProperty("CreatedStores").GetInt32() == native.FinalResult.GetProperty("DestroyedStores").GetInt32(),
            name + "：真实文本存储析构，TSF组件卸载干净");
        metrics.Add(new
        {
            场景 = name, 真实识别 = false, 真实TSF = true, 真实豆包原生整理 = true, 独立数据目录 = nativeData,
            组件SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dllPath))),
            整理程序SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(runtimePath, "ImeService.exe")))),
            原文 = finalText, 原生整理结果 = observed.Result, 原生请求数 = observed.Requests,
            Commit确认时间戳 = commitAt, 原生整理入口时间戳 = observed.RequestedAt, 原生整理返回时间戳 = observed.CompletedAt,
            Refine确认时间戳 = refineAt, 原生整理耗时毫秒 = Stopwatch.GetElapsedTime(observed.RequestedAt, observed.CompletedAt).TotalMilliseconds,
            部分编辑态 = partial, 整理请求前 = observed.HostAtRequest, 整理后 = afterAi,
            参与helper = participants.Distinct().ToArray(), 清理后新增helper = afterDispose,
            命令时序 = commandTimes, 收尾时延 = performance.Snapshot(), 宿主清理 = native.FinalResult
        });
    }

    private sealed record HelperIdentity(int Pid, long StartUtcTicks);
    private static HelperIdentity[] RuntimeWorkers(string runtimePath)
    {
        var executable = Path.Combine(runtimePath, "ImeService.exe");
        var result = new List<HelperIdentity>();
        foreach (var process in Process.GetProcessesByName("ImeService"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        result.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return result.ToArray();
    }

    private sealed class ObservedNativeOrganizer(NativeSmartTextOrganizer inner, Func<NativeSnapshot> snapshot) : INativeTextOrganizer
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task;
        public Task Completed => completed.Task;
        public int Requests { get; private set; }
        public long RequestedAt { get; private set; }
        public long CompletedAt { get; private set; }
        public NativeSnapshot? HostAtRequest { get; private set; }
        public FormattingResult? Result { get; private set; }
        public bool IsAvailable => inner.IsAvailable;
        public Task PrepareAsync(CancellationToken token = default) => inner.PrepareAsync(token);
        public async Task<FormattingResult> OrganizeAsync(string text, CancellationToken token = default)
        {
            try
            {
                // 仅旁观实际宿主状态与时序，所有正文仍直接交给真实原生实现。
                HostAtRequest = snapshot(); RequestedAt = Stopwatch.GetTimestamp(); Requests++;
                started.TrySetResult();
                Result = await inner.OrganizeAsync(text, token).ConfigureAwait(false);
                CompletedAt = Stopwatch.GetTimestamp(); completed.TrySetResult();
                return Result;
            }
            catch (Exception error) { started.TrySetException(error); completed.TrySetException(error); throw; }
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static void RunNativeAppCase(string hostPath, string dllPath, bool externalEdit)
    {
        var name = externalEdit ? "真实TSF闭环用户改字拒绝AI覆盖" : "真实TSF闭环先提交后整理";
        var directory = Path.Combine(output, name); Directory.CreateDirectory(directory);
        using var native = new NativeHarness(hostPath, dllPath, directory);
        var id = Guid.NewGuid();
        var compatibility = new MemoryCompatibility();
        var commands = new List<TsfPipeDiagnostic>();
        // 注入会话工厂时显式订阅客户端诊断，管道仍连接独立原生进程。
        var router = new CompositionDeliveryRouter(compatibility, session => new TsfCompositionSession(new(native.Endpoint, diagnostic => { lock (commands) commands.Add(diagnostic); }), session, () => true, () => () => true));
        using var ai = new LocalSlowAi(() => native.Snapshot().Active > 0);
        var settings = new VoiceSettings
        {
            OutputMode = "live", InputBackend = "tsf", SmartFormattingEnabled = true,
            ApiBaseUrl = ai.BaseUrl, ApiModel = "test", ProxyMode = "direct",
            SaveHistory = false, SaveAudio = false, ShowFloatingBar = false, SoundFeedback = false
        };
        var engine = new ControlledEngine();
        var performance = new SessionPerformance(id);
        Set("store", new SettingsStore(directory)); Set("formatter", new TextFormatting(null, TimeSpan.FromSeconds(10)));
        Set("delivery", router); Set("sessionId", id); Set("performance", performance);
        Set("activeSettings", settings); Set("savedSettings", settings); Set("engine", engine);
        Set("localTranscript", false); Set("previewOnly", false); Set("exiting", false); Set("stopping", false);
        Set("preparing", 1); Set("diagnosticPath", Path.Combine(directory, "状态.json")); Set("testAudioPath", "离屏假音频");
        Set("testOutputPath", null); Set("audioPump", null); Set("captureStopped", null); Set("capture", null);
        Set("automaticFormattingRequests", 0); Set("finalDelivered", false); Set("deliveryPending", 0);
        Set("lastDeliverySucceeded", false); Set("lastDeliveredSnapshot", ""); Set("lastDeliveryMessage", "");
        Set("deliveryRequested", false); Set("pendingSpeech", null); Set("speechDrainScheduled", 0);
        Set("formattingCancellation", null); Set("startAfterStop", false);
        vm.ResetTranscript(); vm.IsRecording = true; vm.IsBusy = false; vm.IsFormatting = false;
        var capture = router.BeginAsync(id, true); Pump(capture); Set("deliveryCapture", capture);
        Check(capture.Result.Success, name + "：真实组件通过本机管道在隐藏文档开始组合");
        Emit(id, "准备出门");
        PumpUntil(() => native.Snapshot().Text == "已有内容：准备出门", 10000);
        var partial = native.Snapshot();
        Check(partial.Active == 1 && partial.CompositionCount == 1 && partial.Attribute,
            name + "：说话快照具有真实活动组合和下划线属性");
        var stop = (Task)typeof(App).GetMethod("StopAsync", Private)!.Invoke(app, ["用户结束"])!;
        PumpFor(70);
        Check(!stop.IsCompleted && native.Snapshot().Active == 1, name + "：等待迟到句尾期间没有提前结束编辑或丢失尾字");
        engine.Complete("准备出门买菜。"); Pump(stop, 10000);
        PumpUntil(() => ai.Requests > 0, 10000);
        var beforeAi = native.Snapshot();
        Check(beforeAi.Text == "已有内容：准备出门买菜。" && beforeAi.Active == 0 && beforeAi.CompositionCount == 0 && !beforeAi.Attribute,
            name + "：AI等待时宿主实际已经提交最终句尾，活动组合为零且下划线清除");
        Check(ai.EditingAtRequest == false && !vm.IsRecording && !vm.IsBusy && (bool)Get("finalDelivered")!,
            name + "：真正HTTP请求前已完成基础交付，录音流程不等待AI");
        // 保持慢AI悬置，反复读取真实宿主，排除只观察到过渡状态的假阳性。
        PumpFor(120);
        var stillWaiting = native.Snapshot();
        Check(stillWaiting.Text == beforeAi.Text && stillWaiting.Writes == beforeAi.Writes && stillWaiting.Active == 0 && !stillWaiting.Attribute,
            name + "：慢AI期间没有重开编辑、闪回下划线或重新写入全文");
        NativeSnapshot? edited = null;
        if (externalEdit)
        {
            edited = native.Edit();
            Check(edited.Text.StartsWith("已有内容：用户修改", StringComparison.Ordinal) && edited.Writes == beforeAi.Writes,
                name + "：仅隐藏宿主外部改字，未伪造iSay提交成功");
        }
        ai.Release();
        PumpUntil(() => ((HashSet<Task>)Get("formattingTasks")!).Count == 0, 10000);
        var afterAi = native.Snapshot();
        if (externalEdit)
            Check(afterAi.Text == edited!.Text && afterAi.Writes == edited.Writes && vm.FormattedText == "准备出门，顺便买菜。",
                name + "：真实原生范围校验拒绝覆盖用户编辑，AI结果保留在iSay");
        else
            Check(afterAi.Text == "已有内容：准备出门，顺便买菜。" && afterAi.Writes == beforeAi.Writes + 1,
                name + "：最终只在原范围改写一次，保留原有前缀且未重复全文");
        Check(afterAi.Active == 0 && afterAi.CompositionCount == 0 && !afterAi.Attribute &&
            afterAi.Starts - beforeAi.Starts == afterAi.Ends - beforeAi.Ends && afterAi.Starts - beforeAi.Starts <= 1,
            name + "：整理后没有活动组合或下划线，系统瞬时默认组合在同次修正中结束");
        Check(afterAi.Hidden && afterAi.ForegroundUnchanged && compatibility.Calls == 0,
            name + "：全程隐藏真实TSF测试宿主，无回退粘贴或控制用户窗口");
        lock (commands)
        {
            Check(commands.Count(item => item.Command == TsfCommand.Begin) == 1 && commands.Count(item => item.Command == TsfCommand.Commit) == 1 &&
                commands.Count(item => item.Command == TsfCommand.RefineCommitted) == 1,
                name + "：协议仅一次Begin、一次Commit、一次Refine，AI不重新Begin或Commit");
        }
        Check((bool)Get("finalDelivered")! && !vm.IsFormatting && !vm.IsBusy,
            name + "：整理完成或安全拒绝后保留基础成功状态并结束忙碌态");
        native.Finish();
        Check(native.FinalResult.GetProperty("Passed").GetBoolean() && native.FinalResult.GetProperty("CleanUnload").GetBoolean() &&
            native.FinalResult.GetProperty("CreatedStores").GetInt32() == native.FinalResult.GetProperty("DestroyedStores").GetInt32(),
            name + "：真实TSF宿主清理完毕，文本存储析构且组件可卸载");
        metrics.Add(new { 场景 = name, 原生组件SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dllPath))),
            原生宿主SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hostPath))),
            中间态 = partial, AI前 = beforeAi, AI等待 = stillWaiting, 用户编辑 = edited, AI后 = afterAi, 清理 = native.FinalResult,
            时延 = performance.Snapshot(), 管道诊断 = commands });
    }

    private static void RunAppCase(string name, bool ai, bool failAi = false, bool timeoutAi = false,
        string mutate = "", string mode = "live", bool empty = false, string reason = "用户结束", bool cancelAi = false, bool throwAi = false, bool localAi = false, bool nativeAi = false)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(output, name); Directory.CreateDirectory(directory);
        var target = new MemoryComposition(); var compatibility = new MemoryCompatibility();
        var router = new CompositionDeliveryRouter(compatibility, _ => target);
        using ILateAi handler = nativeAi ? new DelayedNative(target, failAi) : localAi ? new LocalSlowAi(target) : new DelayedHandler(target, failAi, throwAi);
        var settings = new VoiceSettings
        {
            OutputMode = mode, InputBackend = "tsf", SmartFormattingEnabled = ai,
            ApiBaseUrl = nativeAi ? "" : handler.BaseUrl, ApiModel = nativeAi ? "" : "test", ProxyMode = "direct",
            SaveHistory = false, SaveAudio = false, ShowFloatingBar = false, SoundFeedback = false
        };
        var engine = new ControlledEngine();
        var performance = new SessionPerformance(id);
        Set("store", new SettingsStore(directory)); Set("formatter", new TextFormatting(handler as HttpMessageHandler, timeoutAi ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromSeconds(3), handler as INativeTextOrganizer));
        Set("delivery", router); Set("sessionId", id); Set("performance", performance);
        Set("activeSettings", settings); Set("savedSettings", settings); Set("engine", engine);
        Set("localTranscript", false); Set("previewOnly", false); Set("exiting", false); Set("stopping", false);
        Set("preparing", 1); Set("diagnosticPath", Path.Combine(directory, "状态.json")); Set("testAudioPath", "离屏假音频");
        Set("testOutputPath", null); Set("audioPump", null); Set("captureStopped", null); Set("capture", null);
        Set("automaticFormattingRequests", 0); Set("finalDelivered", false); Set("deliveryPending", 0);
        Set("lastDeliverySucceeded", false); Set("lastDeliveredSnapshot", ""); Set("lastDeliveryMessage", "");
        Set("deliveryRequested", false); Set("pendingSpeech", null); Set("speechDrainScheduled", 0);
        Set("formattingCancellation", null); Set("startAfterStop", false);
        vm.ResetTranscript(); vm.IsRecording = true; vm.IsBusy = false; vm.IsFormatting = false;
        if (mode == "live")
        {
            var begin = router.BeginAsync(id, true); Pump(begin); Set("deliveryCapture", begin);
        }
        else Set("deliveryCapture", null);
        if (!empty) Emit(id, "准备出门");
        var stop = (Task)typeof(App).GetMethod("StopAsync", Private)!.Invoke(app, [reason])!;
        PumpFor(70);
        Check(!stop.IsCompleted && target.Commits == 0, name + "：等待真实最终纠正，不拿中间快照冒充最终文字");
        if (!empty) Check(target.Editing, name + "：最终纠正尚未返回时原编辑组合仍有效");
        engine.Complete(empty ? "" : "准备出门买菜。");
        Pump(stop); stop.GetAwaiter().GetResult();
        var expectedCommit = reason == "用户结束" && !empty;
        Check(target.Commits == (expectedCommit ? 1 : 0) && !target.Editing,
            name + "：最终纠正后唯一提交或安全取消，原组合已结束");
        if (expectedCommit)
            Check(target.Text == "准备出门买菜。" && target.CommitText == target.Text,
                name + "：句尾晚到的买菜二字完整保留，没有再次追加全文");
        Check(compatibility.Calls == 0, name + "：没有回退普通键入或剪贴板");
        if (ai && expectedCommit)
        {
            PumpUntil(() => handler.Requests > 0);
            Check(handler.EditingAtRequest == false && target.Commits == 1 && !target.Editing,
                name + "：AI请求发出前已确认退出编辑态");
            Check(!vm.IsRecording && !vm.IsBusy && target.Refines == 0,
                name + "：AI仍在等待时停录已返回且不占用录音忙碌态");
            if (mutate == "external") { target.Text = "用户手动修改的正文"; target.AllowRefine = false; }
            if (mutate == "revision") { vm.FormattedText = "用户本地修改"; Set("transcriptRevision", (long)Get("transcriptRevision")! + 1); }
            if (mutate == "session") { Set("sessionId", Guid.NewGuid()); vm.RawText = "新会话文字"; vm.FormattedText = "新会话结果"; vm.IsRecording = true; }
            if (cancelAi) ((CancellationTokenSource)Get("formattingCancellation")!).Cancel();
            else if (!timeoutAi) handler.Release();
            PumpUntil(() => ((HashSet<Task>)Get("formattingTasks")!).Count == 0);
            if (mutate == "external")
                Check(target.Text == "用户手动修改的正文" && vm.FormattedText == "准备出门，顺便买菜。",
                    name + "：范围已变时仅保留AI结果，不覆盖用户文字");
            else if (mutate == "revision")
                Check(target.Refines == 0 && vm.FormattedText == "用户本地修改", name + "：本地版本变化阻止迟到结果");
            else if (mutate == "session")
                Check(target.Refines == 0 && vm.RawText == "新会话文字" && vm.FormattedText == "新会话结果", name + "：旧AI不写入新会话");
            else if (failAi || timeoutAi || cancelAi || throwAi)
                Check(target.Text == "准备出门买菜。" && target.Commits == 1 && !target.Editing,
                    name + "：AI失败不会撤回基础文字或重开组合");
            else
                Check(target.Refines == 1 && target.Text == "准备出门，顺便买菜。" && target.Commits == 1 && target.Begins == 1,
                    name + "：AI只更新已提交原段一次，不重新Begin或Commit");
            if (mutate != "session")
                Check((bool)Get("finalDelivered")! && !vm.IsFormatting && !vm.IsBusy && !vm.StatusText.Contains("AI正在后台整理", StringComparison.Ordinal),
                    name + "：后台任务结束后基础交付仍成功，状态退出整理中且可继续录音");
            if (mutate == "revision")
                Check(vm.StatusText.Contains("当前草稿保持你的修改", StringComparison.Ordinal),
                    name + "：本页已手改时明确结束整理提示，不留下后台进行中的状态");
            if (cancelAi || throwAi)
                Check(target.Refines == 0 && vm.StatusText.Contains(cancelAi ? "智能整理已取消" : "智能整理未完成", StringComparison.Ordinal),
                    name + "：取消或异常明确收尾，不误报基础输入失败");
        }
        else Check(handler.Requests == 0 && target.Refines == 0, name + "：没有不该发生的AI或提交后改写");

        using var timing = JsonDocument.Parse(JsonSerializer.Serialize(performance.Snapshot()));
        var sealedMs = timing.RootElement.GetProperty("StopToEditCommittedMs");
        Check(expectedCommit ? sealedMs.ValueKind == JsonValueKind.Number : sealedMs.ValueKind == JsonValueKind.Null,
            name + "：编辑结束计时只记录真实目标提交确认");
        if (expectedCommit)
        {
            var afterFinal = timing.RootElement.GetProperty("RecognitionFinalToEditCommittedMs").GetDouble();
            Check(afterFinal >= 0 && afterFinal < 300, name + "：最终纠正到编辑提交不等待慢AI");
        }
        metrics.Add(new { 场景 = name, Commit = target.Commits, Refine = target.Refines, Begin = target.Begins,
            最终文本 = target.Text, 编辑结束计时 = timing.RootElement.Clone(), AI请求数 = handler.Requests });
        vm.IsRecording = false;
        var cancel = router.CancelAsync(id); Pump(cancel);
    }

    private static async Task BridgeCases()
    {
        // 真实会话客户端连接本进程假管道，验证键鼠版本守卫和“可能已写入”时的只读确认。
        using (var server = new MemoryPipe())
        {
            var guardCaptures = 0; var revision = 3;
            var session = new TsfCompositionSession(new(server.Endpoint), Guid.NewGuid(), null,
                () => { guardCaptures++; var captured = revision; return () => revision == captured; });
            Check(!(await session.RefineAsync("不能提前整理")).Applied && server.Commands.Count == 0, "协议：提交前拒绝Refine且不自行开始新组合");
            Check((await session.UpdateAsync("基础正文")).Applied, "协议：真实客户端先向同一会话更新基础正文");
            revision = 4;
            Check((await session.CommitAsync("最终基础正文")).Applied && guardCaptures == 1, "协议：在真正Commit之前固定新的物理版本守卫");
            Check((await session.RefineAsync("整理后的正文")).Applied, "协议：原版本未变时只发命令8进行安全整理");
            _ = await session.RefineAsync("整理后的正文");
            _ = await session.RefineAsync("另一份正文");
            Check(server.Commands.Count(item => item.Command == TsfCommand.RefineCommitted) == 1 && server.Text == "整理后的正文",
                "协议：成功整理仅发送一次，重复调用不重发正文");
        }
        using (var server = new MemoryPipe())
        {
            var revision = 10;
            var session = new TsfCompositionSession(new(server.Endpoint), Guid.NewGuid(), null,
                () => { var captured = revision; return () => captured == revision; });
            await session.CommitAsync("原段"); revision++;
            Check(!(await session.RefineAsync("迟到AI")).Applied && server.Commands.All(item => item.Command != TsfCommand.RefineCommitted),
                "协议：提交后键鼠版本变化则不发送整理正文");
        }
        using (var server = new MemoryPipe())
        {
            var session = new TsfCompositionSession(new(server.Endpoint), Guid.NewGuid());
            await session.CommitAsync("原段");
            Check(!(await session.RefineAsync("无凭据AI")).Applied && server.Commands.All(item => item.Command != TsfCommand.RefineCommitted),
                "协议：没有范围守卫凭据时安全拒绝整理");
        }
        using (var server = new MemoryPipe { DropRefineResponse = true })
        {
            var session = new TsfCompositionSession(new(server.Endpoint), Guid.NewGuid(), null, () => () => true);
            await session.CommitAsync("原段");
            Check((await session.RefineAsync("已写入但断连的AI")).Applied, "协议：整理回复断连后只读Query确切原序号确认成功");
            Check(server.Commands.Count(item => item.Command == TsfCommand.RefineCommitted) == 1 &&
                server.Commands.Count(item => item.Command == TsfCommand.Query) == 1 &&
                server.Commands.Last().Sequence == server.Commands.First(item => item.Command == TsfCommand.RefineCommitted).Sequence,
                "协议：确认只查原序号，没有第二份Refine正文");
        }
        using (var server = new MemoryPipe { RefineUnsupported = true })
        {
            var session = new TsfCompositionSession(new(server.Endpoint), Guid.NewGuid(), null, () => () => true);
            await session.CommitAsync("原段");
            Check(!(await session.RefineAsync("旧组件不能处理")).Applied && server.Text == "原段",
                "协议：旧组件不支持命令8时保留原段，不转为其他输入通道");
        }
        using (var server = new MemoryPipe { DropRefineResponse = true, StaleQuerySequence = true })
        {
            var session = new TsfCompositionSession(new(server.Endpoint), Guid.NewGuid(), null, () => () => true);
            await session.CommitAsync("原段");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var waiting = false;
                try { await session.RefineAsync("等待精确确认的AI"); }
                catch (TsfConfirmationPendingException) { waiting = true; }
                Check(waiting, "协议：旧序号即使正文长度相同也不能冒充本次整理确认");
            }
            Check(server.Commands.Count(item => item.Command == TsfCommand.RefineCommitted) == 1 &&
                server.Commands.Where(item => item.Command == TsfCommand.Query).All(item => item.Sequence == server.Commands.First(item => item.Command == TsfCommand.RefineCommitted).Sequence),
                "协议：未获得精确确认时继续只查原序号，不再发送整理正文");
        }
        using (var server = new MemoryPipe { WrongRefineLength = true })
        {
            var session = new TsfCompositionSession(new(server.Endpoint), Guid.NewGuid(), null, () => () => true);
            await session.CommitAsync("原段");
            var rejected = false;
            try { await session.RefineAsync("长度不明的AI"); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && !(await session.RefineAsync("长度不明的AI")).Applied &&
                server.Commands.Count(item => item.Command == TsfCommand.RefineCommitted) == 1,
                "协议：成功状态但长度不符仍拒绝，不重复改写未知范围");
        }
    }

    private static void Emit(Guid id, string text)
    {
        typeof(App).GetMethod("OnSpeechResult", Private)!.Invoke(app, [null, new SpeechResult(id, SpeechResultKind.Partial, text)]);
        PumpFor(20);
    }
    private static void Pump(Task task, int milliseconds = 5000)
    {
        PumpUntil(() => task.IsCompleted, milliseconds); task.GetAwaiter().GetResult();
    }
    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { PumpOnce(); Thread.Sleep(1); }
    }
    private static void PumpUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < milliseconds) { PumpOnce(); Thread.Sleep(1); }
        if (!condition()) throw new TimeoutException("离屏快速收尾回归超时。");
    }
    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static object? Get(string name) => typeof(App).GetField(name, Private)!.GetValue(app);
    private static void Set(string name, object? value) => typeof(App).GetField(name, Private)!.SetValue(app, value);
    private static void Check(bool value, string name)
    {
        checks.Add(new { 名称 = name, 通过 = value });
        if (!value) throw new InvalidOperationException(name);
        Console.WriteLine("通过：" + name);
    }

    private sealed class ControlledEngine : IStreamingSpeechEngine
    {
        private readonly TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<SpeechResult>? ResultReceived { add { } remove { } }
        public void Complete(string text) => completion.TrySetResult(text);
        public Task PrepareAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StartAsync(Guid id, CancellationToken token = default) => Task.CompletedTask;
        public ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm, CancellationToken token = default) => ValueTask.CompletedTask;
        public Task<string> StopAsync(CancellationToken token = default) => completion.Task.WaitAsync(token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private interface ILateAi : IDisposable
    {
        int Requests { get; }
        bool? EditingAtRequest { get; }
        string BaseUrl { get; }
        void Release();
    }
    private sealed class DelayedHandler(MemoryComposition target, bool fail, bool unexpectedFailure) : HttpMessageHandler, ILateAi
    {
        private readonly TaskCompletionSource<HttpResponseMessage> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }
        public bool? EditingAtRequest { get; private set; }
        public string BaseUrl => "http://127.0.0.1/v1";
        public void Release()
        {
            // 常规网络失败由整理器降级；额外异常用来验证App外层兜底仍会结束忙碌状态。
            if (unexpectedFailure) { completion.TrySetException(new ApplicationException("离屏测试注入的整理异常")); return; }
            completion.TrySetResult(new(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"准备出门，顺便买菜。\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json") });
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; EditingAtRequest = target.Editing; return completion.Task.WaitAsync(token); }
    }
    private sealed class DelayedNative(MemoryComposition target, bool fail) : INativeTextOrganizer, ILateAi
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAvailable => true;
        public int Requests { get; private set; }
        public bool? EditingAtRequest { get; private set; }
        public string BaseUrl => "";
        public Task PrepareAsync(CancellationToken token = default) => Task.CompletedTask;
        public async Task<FormattingResult> OrganizeAsync(string text, CancellationToken token = default)
        {
            Requests++; EditingAtRequest = target.Editing;
            await completion.Task.WaitAsync(token);
            if (fail) throw new IOException("离屏测试注入的原生整理失败");
            return new("准备出门，顺便买菜。", "内置智能整理");
        }
        public void Release() => completion.TrySetResult();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class LocalSlowAi : ILateAi
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Func<bool> isEditing;
        private readonly Task server;
        private int requests;
        public int Requests => Volatile.Read(ref requests);
        public bool? EditingAtRequest { get; private set; }
        public string BaseUrl { get; }
        public LocalSlowAi(MemoryComposition target) : this(() => target.Editing) { }
        public LocalSlowAi(Func<bool> isEditing)
        {
            this.isEditing = isEditing;
            listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            // 真正经过HTTP序列化和环回套接字；所有请求都仅在本机，延迟由测试明确放行。
            server = Task.Run(ServeAsync);
        }
        public void Release() => release.TrySetResult();
        private async Task ServeAsync()
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false);
                using var stream = client.GetStream();
                using var header = new MemoryStream();
                var one = new byte[1];
                while (header.Length < 32768)
                {
                    await stream.ReadExactlyAsync(one, cancellation.Token).ConfigureAwait(false);
                    header.WriteByte(one[0]);
                    if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                }
                var headerText = Encoding.ASCII.GetString(header.ToArray());
                var contentLengthLine = headerText.Split("\r\n").First(row => row.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = int.Parse(contentLengthLine.AsSpan("Content-Length:".Length));
                if (length is < 0 or > 32768) throw new InvalidDataException("测试请求长度异常。");
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, cancellation.Token).ConfigureAwait(false);
                EditingAtRequest = isEditing();
                Interlocked.Increment(ref requests);
                await release.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
                var response = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"准备出门，顺便买菜。\"},\"finish_reason\":\"stop\"}]}");
                var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, cancellation.Token).ConfigureAwait(false);
                await stream.WriteAsync(response, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        public void Dispose()
        {
            cancellation.Cancel(); listener.Stop(); server.GetAwaiter().GetResult(); cancellation.Dispose();
        }
    }
    private sealed record NativeSnapshot(string Text, int Active, int CompositionCount, bool Attribute, int Writes, int Starts, int Ends, bool Hidden, bool ForegroundUnchanged);
    private sealed class NativeHarness : IDisposable
    {
        private readonly object sync = new();
        private readonly string statusPath, resultPath;
        private readonly EventWaitHandle stop, snapshot, mutate;
        private readonly Process process;
        private bool finished;
        public TsfEndpoint Endpoint { get; }
        public JsonElement FinalResult { get; private set; }
        public NativeHarness(string hostPath, string dllPath, string directory)
        {
            statusPath = Path.Combine(directory, "宿主快照.json"); resultPath = Path.Combine(directory, "宿主退出.json");
            var readyPath = Path.Combine(directory, "宿主就绪.json");
            var prefix = "Local\\iSay.F3.ClosedLoop." + Guid.NewGuid().ToString("N");
            stop = new(false, EventResetMode.ManualReset, prefix + ".Stop");
            snapshot = new(false, EventResetMode.AutoReset, prefix + ".Snapshot");
            mutate = new(false, EventResetMode.AutoReset, prefix + ".Mutate");
            var start = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in new[] { dllPath, readyPath, statusPath, resultPath, prefix + ".Stop", prefix + ".Snapshot", prefix + ".Mutate" }) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new InvalidOperationException("无法启动独立后台原生宿主。");
            PumpUntil(() => File.Exists(readyPath) || process.HasExited, 10000);
            if (process.HasExited) throw new InvalidOperationException("独立原生宿主在就绪前退出：" + (File.Exists(resultPath) ? File.ReadAllText(resultPath) : process.ExitCode));
            using var ready = JsonDocument.Parse(File.ReadAllText(readyPath));
            Endpoint = new(ready.RootElement.GetProperty("ProcessId").GetUInt32(), ready.RootElement.GetProperty("ThreadId").GetUInt32());
            if (!ready.RootElement.GetProperty("Hidden").GetBoolean()) throw new InvalidOperationException("原生测试宿主意外显示了窗口。");
        }
        public NativeSnapshot Snapshot() => ReadSnapshot(snapshot);
        public NativeSnapshot Edit() => ReadSnapshot(mutate);
        private NativeSnapshot ReadSnapshot(EventWaitHandle signal)
        {
            lock (sync)
            {
                if (File.Exists(statusPath)) File.Delete(statusPath);
                signal.Set();
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(statusPath) && !process.HasExited && elapsed.ElapsedMilliseconds < 5000) Thread.Sleep(2);
                if (!File.Exists(statusPath)) throw new TimeoutException("读取真实原生宿主快照超时。");
                return JsonSerializer.Deserialize<NativeSnapshot>(File.ReadAllText(statusPath)) ?? throw new InvalidDataException("原生宿主快照为空。");
            }
        }
        public void Finish()
        {
            if (finished) return;
            stop.Set();
            if (!process.WaitForExit(5000)) throw new TimeoutException("真实原生宿主清理超时。");
            using var result = JsonDocument.Parse(File.ReadAllText(resultPath)); FinalResult = result.RootElement.Clone();
            finished = true;
            if (process.ExitCode != 0) throw new InvalidOperationException("真实原生宿主未干净结束：" + FinalResult);
        }
        public void Dispose()
        {
            try { Finish(); }
            finally { stop.Dispose(); snapshot.Dispose(); mutate.Dispose(); process.Dispose(); }
        }
    }
    private sealed class MemoryCompatibility : ICompatibilityInputDelivery
    {
        public int Calls;
        public Task<DeliveryResult> BeginAsync(CancellationToken token) { Calls++; return Task.FromResult(DeliveryResult.Delivered()); }
        public Task<DeliveryResult> UpdateAsync(string text, CancellationToken token) { Calls++; return Task.FromResult(DeliveryResult.Delivered()); }
        public Task<DeliveryResult> CommitAsync(string text, CancellationToken token) { Calls++; return Task.FromResult(DeliveryResult.Delivered()); }
        public void Cancel() => Calls++;
    }
    private sealed class MemoryComposition : ICompositionSession
    {
        public bool Editing, AllowRefine = true;
        public string Text = "", CommitText = "";
        public int Begins, Commits, Refines;
        public Task<TsfReply> BeginAsync(CancellationToken token = default) { Begins++; Editing = true; return Reply(TsfCompositionState.Composing); }
        public Task<TsfReply> UpdateAsync(string text, CancellationToken token = default) { Text = text; return Reply(TsfCompositionState.Composing); }
        public Task<TsfReply> CommitAsync(string text, CancellationToken token = default) { Commits++; Text = CommitText = text; Editing = false; return Reply(TsfCompositionState.Committed); }
        public Task<TsfReply> RefineAsync(string text, CancellationToken token = default)
        {
            Refines++;
            if (!AllowRefine) return Task.FromResult(new TsfReply(unchecked((int)0x8007051A), TsfCompositionState.Committed, Text.Length, 1));
            Text = text; return Reply(TsfCompositionState.Committed);
        }
        public Task<TsfReply> CancelAsync(CancellationToken token = default) { Editing = false; return Reply(TsfCompositionState.Committed); }
        public Task<TsfReply> QueryAsync(CancellationToken token = default) => Reply(Editing ? TsfCompositionState.Composing : TsfCompositionState.Committed);
        private Task<TsfReply> Reply(TsfCompositionState state) => Task.FromResult(new TsfReply(0, state, Text.Length, 1));
    }

    private sealed record Request(TsfCommand Command, Guid Session, ulong Sequence, string Text);
    private sealed class MemoryPipe : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task task;
        public readonly List<Request> Commands = [];
        public TsfEndpoint Endpoint { get; } = new((uint)Environment.ProcessId, nextThread++);
        public string Text = "";
        public bool DropRefineResponse, RefineUnsupported, StaleQuerySequence, WrongRefineLength;
        private ulong accepted;
        private TsfCompositionState state = TsfCompositionState.Idle;
        public MemoryPipe() => task = ServeAsync();
        private async Task ServeAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                    var header = new byte[40]; await pipe.ReadExactlyAsync(header, cancellation.Token).ConfigureAwait(false);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
                    if (length < 0 || length > TsfCompositionClient.MaximumTextLength) throw new InvalidDataException();
                    var body = new byte[length * 2]; await pipe.ReadExactlyAsync(body, cancellation.Token).ConfigureAwait(false);
                    var request = new Request((TsfCommand)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)),
                        new Guid(header.AsSpan(16, 16)), BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)), Encoding.Unicode.GetString(body));
                    Commands.Add(request);
                    var error = 0;
                    if (request.Command == TsfCommand.RefineCommitted && RefineUnsupported) error = unchecked((int)0x80070057);
                    else if (request.Command != TsfCommand.Query)
                    {
                        accepted = request.Sequence;
                        state = request.Command is TsfCommand.Commit or TsfCommand.RefineCommitted or TsfCommand.Cancel
                            ? TsfCompositionState.Committed : TsfCompositionState.Composing;
                        if (request.Command is TsfCommand.Update or TsfCommand.Commit or TsfCommand.RefineCommitted) Text = request.Text;
                    }
                    if (request.Command == TsfCommand.RefineCommitted && DropRefineResponse) continue;
                    var response = new byte[32];
                    BinaryPrimitives.WriteUInt32LittleEndian(response, TsfCompositionClient.Magic);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 1);
                    BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(8), error);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), (uint)state);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(16), (uint)(Text.Length + (request.Command == TsfCommand.RefineCommitted && WrongRefineLength ? 1 : 0)));
                    BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(24), request.Command == TsfCommand.Query && StaleQuerySequence ? accepted - 1 : accepted);
                    await pipe.WriteAsync(response, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        public void Dispose()
        {
            cancellation.Cancel(); task.GetAwaiter().GetResult(); cancellation.Dispose();
        }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
