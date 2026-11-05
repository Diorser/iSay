using System.Diagnostics;
using System.IO;
using System.Text.Json;
using iSay.Desktop.Services;

// 调度验证全部使用可注入内存会话，不建立窗口，不读取麦克风，不发送系统输入。
var results = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("验证失败：" + name);
    results.Add(name); Console.WriteLine("通过：" + name);
}

var legacy = new CompatibilityMemory();
var composition = new CompositionMemory();
var captures = 0;
var router = new CompositionDeliveryRouter(legacy, _ => { captures++; return composition; });
var id = Guid.NewGuid();
Check((await router.BeginAsync(id, true)).Success && captures == 1 && composition.Begins == 1 && legacy.Total == 0,
    "开始录音即捕获固定组合目标，不等首字也不调用兼容输入");
await router.UpdateAsync(id, "我"); await router.UpdateAsync(id, "我我"); await router.UpdateAsync(id, "我我继续说");
Check(composition.Updates == 3 && composition.Text == "我我继续说" && composition.Commits == 0 && legacy.Total == 0,
    "实时累计和真实口语重复只更新同一组合且不提交");
for (var repeat = 0; repeat < 20; repeat++) await router.UpdateAsync(id, "我我继续说");
Check(composition.Updates == 3 && composition.Commits == 0, "停顿与相同稳定快照不结束组合也不重写");
await router.UpdateAsync(id, "我我继续说。");
Check(composition.Commits == 0, "录音中的稳定Final仍是组合更新而不是结束指令");
Check((await router.CommitAsync(id, "我我继续说。")).Success && composition.Commits == 1 && composition.TextWrites == 4 && legacy.Total == 0,
    "F3最终相同正文只提交原组合，不额外写第二份正文");
await router.CommitAsync(id, "我我继续说。"); await router.UpdateAsync(id, "我我继续说。");
Check(composition.Commits == 1 && composition.TextWrites == 4, "重复最终通知不再Commit或重新写字");
Check(!(await router.CommitAsync(id, "后来另一份整理")).Success && composition.Text == "我我继续说。", "提交后不同结果只保留不偷偷改写原输入框");
await router.CancelAsync(id);
Check(composition.Cancels == 0 && composition.Text == "我我继续说。", "已完成组合的清理不会再次发取消或删除正文");

composition = new CompositionMemory(); id = Guid.NewGuid(); await router.BeginAsync(id, true);
await router.UpdateAsync(id, "原始转写");
var ai = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
async Task<DeliveryResult> FinishAiAsync(Guid session)
{
    await router.UpdateAsync(session, "基础转写。");
    return await router.CommitAsync(session, await ai.Task);
}
var finishing = FinishAiAsync(id);
Check(composition.Commits == 0 && composition.Text == "基础转写。" && !finishing.IsCompleted,
    "F3等待AI期间仅Update基础文字，保持同一组合而不提前Commit");
ai.SetResult("智能整理完成。");
Check((await finishing).Success && composition.Commits == 1 && composition.Text == "智能整理完成。" && legacy.Total == 0,
    "AI完成后在原组合内唯一Commit且不转普通键入");

composition = new CompositionMemory(); id = Guid.NewGuid(); await router.BeginAsync(id, true);
await router.UpdateAsync(id, "焦点离开前"); composition.Focused = false;
Check(!(await router.UpdateAsync(id, "焦点离开期间继续说")).Success && composition.Commits == 0 && composition.Cancels == 0,
    "失焦只保留新转写而不结束组合或录音");
composition.Focused = true;
Check((await router.UpdateAsync(id, "焦点离开期间继续说")).Success && composition.Text == "焦点离开期间继续说" && composition.TextWrites == 2,
    "返回原目标续写同一组合累计范围而不追加重复全文");
composition.Terminated = true;
Check(!(await router.UpdateAsync(id, "宿主终止后新增")).Success && !(await router.CommitAsync(id, "最终结果")).Success && legacy.Total == 0,
    "宿主强终止后保留文字不复活组合也不回退SendInput");

var missing = new CompositionDeliveryRouter(legacy, _ => null); var missingId = Guid.NewGuid();
Check(!(await missing.BeginAsync(missingId, true)).Success && !(await missing.UpdateAsync(missingId, "不明目标")).Success && legacy.Total == 0,
    "组合服务未激活时明确保留，不悄悄切换兼容后端");
composition = new CompositionMemory { RejectBegin = true }; id = Guid.NewGuid();
Check(!(await router.BeginAsync(id, true)).Success && !(await router.CommitAsync(id, "拒绝目标")).Success && legacy.Total == 0,
    "Begin被宿主拒绝后固定该段失败状态，不从当前光标重发");

composition = new CompositionMemory { FailUpdate = true }; id = Guid.NewGuid(); await router.BeginAsync(id, true);
await router.UpdateAsync(id, "可能已经写入");
Check(!(await router.CommitAsync(id, "可能已经写入其他文字")).Success && composition.Updates == 1 && composition.Commits == 0 && legacy.Total == 0,
    "写入后确认异常时阻止同段重试发送和后端切换");

composition = new CompositionMemory(); id = Guid.NewGuid(); await router.BeginAsync(id, true); await router.UpdateAsync(id, "上一段已显示");
var oldComposition = composition; var oldId = id;
var delayedAi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var stale = Task.Run(async () => { await delayedAi.Task; return await router.CommitAsync(oldId, "上一段迟到整理"); });
composition = new CompositionMemory(); id = Guid.NewGuid(); await router.BeginAsync(id, true);
delayedAi.SetResult();
Check(!(await stale).Success && oldComposition.Cancels == 1 && oldComposition.Text == "上一段已显示" && composition.TextWrites == 0,
    "下一段开始会结束旧组合并保留已显示原文，旧AI迟到不能覆盖新段");
await router.UpdateAsync(id, "新段内容"); await router.CancelAsync(id);
Check(composition.Cancels == 1 && composition.Text == "新段内容" && !(await router.CommitAsync(id, "退出后迟到")).Success,
    "退出取消只结束组合并保留原文，后续任务不能继续写入");

composition = new CompositionMemory(); id = Guid.NewGuid(); await router.BeginAsync(id, true);
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
composition.BeforeUpdate = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
var pending = router.UpdateAsync(id, "正在发送旧段"); await entered.Task;
oldComposition = composition; composition = new CompositionMemory(); var newId = Guid.NewGuid();
var beginNext = router.BeginAsync(newId, true);
Check(!(await pending).Success && (await beginNext).Success && oldComposition.Cancels == 1 && composition.TextWrites == 0,
    "新段取消在途旧Update，串行清理后新段不会接到旧文字");

var fixedFactoryCalls = captures;
id = Guid.NewGuid(); await router.BeginAsync(id, false); await router.UpdateAsync(id, "兼容实时"); await router.CommitAsync(id, "兼容最终");
Check(captures == fixedFactoryCalls && legacy.Begins == 1 && legacy.Updates == 1 && legacy.Commits == 1 && legacy.Text == "兼容最终",
    "只有手动选择兼容后端时才调用原有输入服务且每段后端固定");

composition = new CompositionMemory(); id = Guid.NewGuid(); await router.BeginAsync(id, true);
Check((await router.CommitAsync(id, "结束后输入模式")).Success && composition.Updates == 0 && composition.Commits == 1,
    "结束后输入模式在结束键固定组合后可直接提交，无需先做实时更新");

var recoveredTarget = new CompositionMemory(); var recoveredLegacy = new CompatibilityMemory();
var recoveredRouter = new CompositionDeliveryRouter(recoveredLegacy, _ => recoveredTarget); var recoveredId = Guid.NewGuid();
await recoveredRouter.BeginAsync(recoveredId, true);
recoveredTarget.BeforeUpdate = _ => throw new TsfTransportException("connect", false, true, new TimeoutException("测试连接前失败"));
Check(!(await recoveredRouter.UpdateAsync(recoveredId, "连接期间新增")).Success, "确认尚未发送的连接失败仅暂存，不立即永久封禁原范围");
recoveredTarget.BeforeUpdate = null; await Task.Delay(270);
Check((await recoveredRouter.UpdateAsync(recoveredId, "连接期间新增")).Success && recoveredTarget.Begins == 1 && recoveredTarget.TextWrites == 1 && recoveredLegacy.Total == 0,
    "连接恢复后只向同一已开始组合续写，无新Begin或兼容全文粘贴");
recoveredTarget.BeforeUpdate = _ => throw new TsfConfirmationPendingException("测试只读等待");
await recoveredRouter.UpdateAsync(recoveredId, "等待确认"); var previousAttempts = recoveredTarget.Updates;
for (var update = 0; update < 100; update++) await recoveredRouter.UpdateAsync(recoveredId, "等待确认的新快照" + update);
Check(recoveredTarget.Updates == previousAttempts && recoveredTarget.Text == "连接期间新增",
    "高频新快照在确认冷却期间只保留最新值，不形成100条管道请求");
await recoveredRouter.CancelAsync(recoveredId);

recoveredTarget = new CompositionMemory(); recoveredId = Guid.NewGuid(); await recoveredRouter.BeginAsync(recoveredId, true);
var finalAttempts = 0;
recoveredTarget.BeforeCommit = _ => ++finalAttempts < 3 ? throw new TsfConfirmationPendingException("测试最终只读确认") : Task.CompletedTask;
var finalWatch = Stopwatch.StartNew();
Check((await recoveredRouter.CommitAsync(recoveredId, "最终完整文字")).Success && recoveredTarget.Commits == 1 && finalAttempts == 3 && finalWatch.ElapsedMilliseconds < 1500,
    "主动结束仅对明确AwaitingHost有界继续收尾，确认后只Commit一次");

var initializeTarget = new CompositionMemory { BeforeBegin = _ => throw new TsfTransportException("connect", false, true, new TimeoutException("首次连接前失败")) };
var initializeCaptures = 0;
var initializeRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => { initializeCaptures++; return initializeTarget; });
var initializeId = Guid.NewGuid();
Check((await initializeRouter.BeginAsync(initializeId, true)).AwaitingHost, "首次安全连接失败不会被Begin收尾逻辑永久封禁");
initializeTarget.BeforeBegin = null; await Task.Delay(270);
Check((await initializeRouter.UpdateAsync(initializeId, "初始化恢复文字")).Success && initializeCaptures == 1,
    "初始化恢复仍使用首次捕获的会话对象，不捕获当前新目标");

initializeTarget = new CompositionMemory { BeforeBegin = _ => throw new TsfTransportException("connect", false, true, new TimeoutException("首次安全连接失败")) };
initializeRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => initializeTarget); initializeId = Guid.NewGuid();
await initializeRouter.BeginAsync(initializeId, true); initializeTarget.BeforeBegin = null;
var cooldownCommit = Stopwatch.StartNew();
Check((await initializeRouter.CommitAsync(initializeId, "冷却内结束仍须提交")).Success && initializeTarget.Commits == 1 && cooldownCommit.ElapsedMilliseconds >= 200,
    "首次连接失败的250毫秒冷却内结束会等待恢复并唯一提交，不误报无需等待");

initializeTarget = new CompositionMemory(); initializeRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => initializeTarget); initializeId = Guid.NewGuid();
await initializeRouter.BeginAsync(initializeId, true);
initializeTarget.BeforeUpdate = _ => throw new TsfTransportException("connect", false, true, new TimeoutException("已有组合连接前失败"));
await initializeRouter.UpdateAsync(initializeId, "原组合临时断连"); initializeTarget.BeforeUpdate = null;
Check((await initializeRouter.CommitAsync(initializeId, "原组合冷却内最终文字")).Success && initializeTarget.Commits == 1,
    "已有组合安全连接失败后冷却内结束仍等待同一范围恢复，不丢最终文字");

await 初字恢复回归.RunAsync(Check);
await 范围同步回归.RunAsync(Check);
await 旧会话暂态清理回归.RunAsync(Check);
await 初始化拒绝清理回归.RunAsync(Check);
await 切换取消清理回归.RunAsync(Check);

var cleanupOld = new CompositionMemory(); var cleanupNext = new CompositionMemory(); var selectedCleanup = cleanupOld;
var cleanupRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => selectedCleanup);
var cleanupOldId = Guid.NewGuid(); await cleanupRouter.BeginAsync(cleanupOldId, true); await cleanupRouter.UpdateAsync(cleanupOldId, "旧组合正文");
var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseCleanup = false;
cleanupOld.BeforeCancel = _ => { busy.TrySetResult(); return Task.FromResult(new TsfReply(releaseCleanup ? 0 : unchecked((int)0x800700AA), releaseCleanup ? TsfCompositionState.Committed : TsfCompositionState.Composing, cleanupOld.Text.Length, 1)); };
selectedCleanup = cleanupNext; var cleanupNextId = Guid.NewGuid(); var awaitCleanup = cleanupRouter.BeginAsync(cleanupNextId, true);
await busy.Task;
Check(!awaitCleanup.IsCompleted && cleanupNext.Begins == 0 && cleanupOld.Text == "旧组合正文", "旧Cancel明确Busy时新Begin必须等待，不能误判旧组合已结束");
releaseCleanup = true;
Check((await awaitCleanup).Success && cleanupNext.Begins == 1 && cleanupOld.Cancels >= 2 && cleanupOld.Text == "旧组合正文",
    "旧取消确认后才初始化新段，未删除旧正文或发送旧全文");

cleanupNext.BeforeCancel = _ => Task.FromResult(new TsfReply(unchecked((int)0x800700AA), TsfCompositionState.Composing, 0, 1));
selectedCleanup = new CompositionMemory(); var blockedTarget = selectedCleanup; var blockedId = Guid.NewGuid(); var cleanupWatch = Stopwatch.StartNew();
Check(!(await cleanupRouter.BeginAsync(blockedId, true)).Success && cleanupWatch.ElapsedMilliseconds is >= 2400 and < 3300 && blockedTarget.Begins == 0,
    "旧范围永远Busy时有界保留新转写，绝不向新目标抢建组合");
Check(!(await cleanupRouter.UpdateAsync(blockedId, "本段保留文字")).Success && blockedTarget.TextWrites == 0,
    "旧范围未确认清理不能在后续识别快照中绕过护栏写字");
cleanupNext.BeforeCancel = null; selectedCleanup = new CompositionMemory(); var afterCleanup = selectedCleanup;
Check((await cleanupRouter.BeginAsync(Guid.NewGuid(), true)).Success && afterCleanup.Begins == 1,
    "有界未完成的旧清理仍保留能力，下一段可再次确认结束而非永久僵死");


// 只在明确结束时接住已确认的当前空框，先清理原组合再建立新段。
var emptyTarget = new CompositionMemory(); var emptyCaptures = 0; Guid adoptedId = Guid.Empty;
var finalRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => null, (targetId, _) =>
{ emptyCaptures++; adoptedId = targetId; return Task.FromResult<ICompositionSession?>(emptyTarget); });
var lateId = Guid.NewGuid(); await finalRouter.BeginAsync(lateId, true);
for (var index = 0; index < 6; index++) await finalRouter.UpdateAsync(lateId, "没有输入焦点时说的完整内容");
Check(emptyCaptures == 0 && emptyTarget.TextWrites == 0, "没有焦点时不追着窗口自动写字");
var stopTarget = finalRouter.CaptureEmptyFinalTarget(lateId, CancellationToken.None);
Check(emptyCaptures == 1 && emptyTarget.Begins == 0, "按结束键时开始只读探测，不必等待最终识别且不先写字");
Check((await finalRouter.CommitAsync(lateId, "没有输入焦点时说的完整内容", emptyFinalTarget: stopTarget)).Success && emptyTarget.Text == "没有输入焦点时说的完整内容" && adoptedId != lateId,
    "结束时确认空输入框后，以新的传输会话一次提交完整内容");
await finalRouter.CommitAsync(lateId, "没有输入焦点时说的完整内容");
Check(emptyCaptures == 1 && emptyTarget.Commits == 1, "重复结束事件不再次捕获或追加全文");

var noEmptyCaptures = 0;
var nonemptyRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => null, (_, _) =>
{ noEmptyCaptures++; return Task.FromResult<ICompositionSession?>(null); });
lateId = Guid.NewGuid(); await nonemptyRouter.BeginAsync(lateId, true);
Check(!(await nonemptyRouter.CommitAsync(lateId, "转写正文", emptyFinalTarget: nonemptyRouter.CaptureEmptyFinalTarget(lateId, CancellationToken.None))).Success &&
    noEmptyCaptures == 1, "无法证明当前框为空则保留转写，不覆盖现有文字");
await nonemptyRouter.CommitAsync(lateId, "转写正文");
Check(noEmptyCaptures == 1, "失败后不通过反复结束盲目换框");

var noTextCaptures = 0;
var noTextRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => null, (_, _) =>
{ noTextCaptures++; return Task.FromResult<ICompositionSession?>(new CompositionMemory()); });
lateId = Guid.NewGuid(); await noTextRouter.BeginAsync(lateId, true);
await noTextRouter.CommitAsync(lateId, "");
Check(noTextCaptures == 0, "没有转写正文时不捕获或建立新空组合");
await noTextRouter.RefineAsync(lateId, "迟到整理正文");
Check(noTextCaptures == 0, "后台整理不因当前焦点变化另建组合或补发全文");

var oldUnwritten = new CompositionMemory { RejectBegin = true }; var afterFocus = new CompositionMemory();
var rejectedRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => oldUnwritten, (_, _) => Task.FromResult<ICompositionSession?>(afterFocus));
lateId = Guid.NewGuid(); await rejectedRouter.BeginAsync(lateId, true);
Check((await rejectedRouter.CommitAsync(lateId, "后来点进空框", emptyFinalTarget: rejectedRouter.CaptureEmptyFinalTarget(lateId, CancellationToken.None))).Success &&
    oldUnwritten.Cancels == 1 && afterFocus.Commits == 1,
    "初始失败的空组合先确认清理，再向当前空框提交");

var alreadyWritten = new CompositionMemory(); var forbiddenCaptures = 0;
var onceRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => alreadyWritten, (_, _) =>
{ forbiddenCaptures++; return Task.FromResult<ICompositionSession?>(new CompositionMemory()); });
lateId = Guid.NewGuid(); await onceRouter.BeginAsync(lateId, true); await onceRouter.UpdateAsync(lateId, "已经出现的文字"); alreadyWritten.Terminated = true;
Check((await onceRouter.CommitAsync(lateId, "已经出现的文字以及后续", emptyFinalTarget: onceRouter.CaptureEmptyFinalTarget(lateId, CancellationToken.None))).Success &&
    forbiddenCaptures == 1, "旧位置出现过正文但已结束，仍能在明确结束时接住经确认的当前空框");

var uncertainWrite = new CompositionMemory { FailUpdate = true };
var uncertainRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => uncertainWrite, (_, _) =>
{ forbiddenCaptures++; return Task.FromResult<ICompositionSession?>(new CompositionMemory()); });
lateId = Guid.NewGuid(); await uncertainRouter.BeginAsync(lateId, true); await uncertainRouter.UpdateAsync(lateId, "回复可能丢失");
Check((await uncertainRouter.CommitAsync(lateId, "回复可能丢失", emptyFinalTarget: uncertainRouter.CaptureEmptyFinalTarget(lateId, CancellationToken.None))).Success &&
    forbiddenCaptures == 2 && uncertainWrite.Cancels == 1, "旧写入曾不确定，但结束已经确认且当前框经核验为空，才能一次补填");

var unusedTarget = new CompositionMemory();
var cleanupUncertain = new CompositionMemory { RejectBegin = true, BeforeCancel = _ => Task.FromResult(new TsfReply(1, TsfCompositionState.Composing, 0, 0)) };
var cleanupFinalRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => cleanupUncertain, (_, _) =>
    Task.FromResult<ICompositionSession?>(unusedTarget));
lateId = Guid.NewGuid(); await cleanupFinalRouter.BeginAsync(lateId, true);
Check(!(await cleanupFinalRouter.CommitAsync(lateId, "空组合未确认结束", emptyFinalTarget: cleanupFinalRouter.CaptureEmptyFinalTarget(lateId, CancellationToken.None))).Success &&
    unusedTarget.Begins == 0, "旧空组合清理不明确时不能在新框开始");

var disconnected = new CompositionMemory { BeginReply = new(unchecked((int)0x80040209), TsfCompositionState.Committed, 0, 0),
    BeforeCancel = _ => Task.FromResult(new TsfReply(unchecked((int)0x80040209), TsfCompositionState.HostTerminated, 0, 0)) };
var firstFocus = new CompositionMemory();
var laterFocus = new CompositionMemory();
var focusedTarget = firstFocus;
var disconnectedRouter = new CompositionDeliveryRouter(new CompatibilityMemory(), _ => disconnected, (_, _) =>
    Task.FromResult<ICompositionSession?>(focusedTarget));
lateId = Guid.NewGuid(); await disconnectedRouter.BeginAsync(lateId, true);
var pinnedTarget = disconnectedRouter.CaptureEmptyFinalTarget(lateId, CancellationToken.None);
focusedTarget = laterFocus;
firstFocus.Focused = false;
Check(!(await disconnectedRouter.CommitAsync(lateId, "句尾确认期间切走", emptyFinalTarget: pinnedTarget)).Success &&
    disconnected.Cancels == 1 && firstFocus.TextWrites == 0 && laterFocus.Begins == 0,
    "原空组合已终止可以清理，但句尾确认期间切换焦点不会重新找新框或写入");
Check(WindowsInputService.IsEmptyCaret(new InputTextSnapshot("", 0, 0)) && WindowsInputService.IsEmptyCaret(new InputTextSnapshot("\r\n", 0, 0)) &&
    !WindowsInputService.IsEmptyCaret(new InputTextSnapshot("手打内容", 0, 0)) && !WindowsInputService.IsEmptyCaret(new InputTextSnapshot("\n", 0, 1)),
    "空框检查拒绝非空正文和非折叠选区");


var guardTarget = new EmptyGuardTarget();
using (var guardInput = new WindowsInputService(() => guardTarget, () => false))
{
    var guard = await guardInput.CaptureEmptyTargetGuardAsync(CancellationToken.None);
    Check(guard is not null && await guard(CancellationToken.None), "真实交付执行器捕获可写空框的只读检查器");
    guardTarget.Text = "用户刚刚打的内容";
    Check(!await guard!(CancellationToken.None), "空框检查后用户输入正文，提交前再次检查必须拒绝");
    guardTarget.Text = ""; guardTarget.Focused = false;
    Check(!await guard!(CancellationToken.None), "最后提交前焦点已变，不能填入先前的空框");
    guardTarget.Focused = true;
    typeof(WindowsInputService).GetField("_physicalRevision", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(guardInput, 9L);
    Check(!await guard!(CancellationToken.None), "捕获空框后发生物理输入，即使再次为空也不使用旧许可");
    guardTarget.Text = "已有文字";
    Check(await guardInput.CaptureEmptyTargetGuardAsync(CancellationToken.None) is null, "一开始非空就不能生成空框提交许可");
}

var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../test/组合交付调度回归结果.json"));
File.WriteAllText(output, JsonSerializer.Serialize(new { 通过数 = results.Count, 场景 = results, 测试方式 = "纯后台可注入内存会话；未操作真实窗口、键盘、麦克风或注册表" }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"通过 {results.Count} 项组合交付调度验证。");

sealed class CompatibilityMemory : ICompatibilityInputDelivery
{
    public int Begins, Updates, Commits, Cancels;
    public int Total => Begins + Updates + Commits + Cancels;
    public string Text = "";
    public Task<DeliveryResult> BeginAsync(CancellationToken token) { Begins++; return Task.FromResult(DeliveryResult.Delivered()); }
    public Task<DeliveryResult> UpdateAsync(string text, CancellationToken token) { Updates++; Text = text; return Task.FromResult(DeliveryResult.Delivered()); }
    public Task<DeliveryResult> CommitAsync(string text, CancellationToken token) { Commits++; Text = text; return Task.FromResult(DeliveryResult.Delivered()); }
    public void Cancel() => Cancels++;
}
sealed class CompositionMemory : ICompositionSession
{
    public bool MayHaveWrittenText => TextWrites > 0 || FailUpdate;
    public int Begins, Updates, Commits, Cancels, TextWrites;
    public string Text = "";
    public bool Focused = true, Terminated, RejectBegin, FailUpdate;
    public Func<CancellationToken, Task>? BeforeUpdate;
    public Func<CancellationToken, Task>? BeforeCommit;
    public Func<CancellationToken, Task>? BeforeBegin;
    public TsfReply? BeginReply;
    public Func<CancellationToken, Task<TsfReply>>? BeforeCancel;
    public async Task<TsfReply> BeginAsync(CancellationToken token = default) { Begins++; if (BeforeBegin is not null) await BeforeBegin(token); return BeginReply ?? Reply(RejectBegin ? TsfCompositionState.Rejected : TsfCompositionState.Composing); }
    public async Task<TsfReply> UpdateAsync(string text, CancellationToken token = default)
    {
        Updates++;
        if (BeforeUpdate is not null) await BeforeUpdate(token);
        if (FailUpdate) throw new IOException("模拟发送后失去回复");
        if (Terminated) return Reply(TsfCompositionState.HostTerminated);
        if (!Focused) return Reply(TsfCompositionState.FocusUnavailable);
        SetText(text); return Reply(TsfCompositionState.Composing);
    }
    public async Task<TsfReply> CommitAsync(string text, CancellationToken token = default)
    {
        if (BeforeCommit is not null) await BeforeCommit(token);
        Commits++;
        if (Terminated) return Reply(TsfCompositionState.HostTerminated);
        if (!Focused) return Reply(TsfCompositionState.FocusUnavailable);
        SetText(text); return Reply(TsfCompositionState.Committed);
    }
    public Task<TsfReply> CancelAsync(CancellationToken token = default) { Cancels++; return BeforeCancel is null ? Task.FromResult(Reply(TsfCompositionState.Committed)) : BeforeCancel(token); }
    public Task<TsfReply> QueryAsync(CancellationToken token = default) => Task.FromResult(Reply(TsfCompositionState.Composing));
    private void SetText(string text) { if (Text != text) { Text = text; TextWrites++; } }
    private TsfReply Reply(TsfCompositionState state) => new(state is TsfCompositionState.Composing or TsfCompositionState.Committed ? 0 : 1, state, Text.Length, 1);
}

sealed class EmptyGuardTarget : IInputTextTarget
{
    public string Text = "";
    public bool Focused = true;
    public InputTextSnapshot Initial => Read();
    public bool IsCurrentFocus() => Focused;
    public IInputTextTarget? TryRecaptureOwned(string expectedText, bool allowUniqueSlot = false) => null;
    public InputTextSnapshot Read() => new(Text, Text.Length, Text.Length);
    public void ReplaceOwned(int start, int length, string text, InputTextSnapshot expected, Func<bool>? stillValid = null) => throw new InvalidOperationException("此用例只允许读取，不应写入");
}
