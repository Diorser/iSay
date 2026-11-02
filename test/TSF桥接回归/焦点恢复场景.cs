using iSay.Desktop.Services;

internal static partial class Program
{
    private static async Task RunFocusRecoveryScenarios()
    {
        await using var host = new RecoveryHost();
        var sameElement = true;
        var unchangedInput = true;
        var session = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(), () => unchangedInput,
            canWriteOriginal: _ => Task.FromResult(sameElement));
        Check((await session.UpdateAsync("今天准备", default)).Applied, "焦点保护首段正常交付");
        var commandsBefore = host.UpdateCount;
        sameElement = false; unchangedInput = false;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var paused = await session.UpdateAsync("今天准备出门。失焦期间继续说话。");
            Check(!paused.Applied && paused.State == TsfCompositionState.FocusUnavailable, "失焦核验只暂停交付且保留可恢复状态");
        }
        Check(host.UpdateCount == commandsBefore && host.Text == "今天准备", "失焦期间十二次重试不向宿主发送正文");
        sameElement = true; host.TerminateNextUpdate = true;
        Check((await session.UpdateAsync("今天准备出门。失焦期间继续说话。")).Applied,
            "有键鼠活动但回到原元素时通过原范围恢复");
        Check(host.OwnedRecoveryCount == 1 && host.BeginCount == 1 && host.Text == "今天准备出门。失焦期间继续说话。",
            "回焦仅恢复原范围不新建Begin或追加累计全文");
        Check((await session.CommitAsync(host.Text)).Applied && host.CommitCount == 1, "恢复后正常提交且只提交一次");

        await using var refusedHost = new RecoveryHost { RejectOwnedRecovery = true };
        var refused = new TsfCompositionSession(new(refusedHost.Endpoint), Guid.NewGuid(), () => false,
            canWriteOriginal: _ => Task.FromResult(true));
        Check((await refused.UpdateAsync("保留原文")).Applied, "拒绝恢复场景首段成功");
        refusedHost.TerminateNextUpdate = true;
        Check(!(await refused.UpdateAsync("保留原文以及新话")).Applied && refusedHost.Text == "保留原文", "原范围拒绝恢复时保留原文");
        Check(!(await refused.UpdateAsync("不能强行补发")).Applied && refusedHost.BeginCount == 1,
            "恢复不支持时不会从当前光标重新开始");
    }
}
