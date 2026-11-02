using iSay.Desktop.Services;

internal static partial class Program
{
    private static async Task RunSelectedRefinementTests()
    {
        foreach (var sample in new[] { ("原文", "整理后。\n第二段。"), ("原文", "整理后。\r\n第二段。"), ("原文\n第二段", "单段整理。") })
        {
            await using var host = new RecoveryHost();
            var prepared = false;
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(),
                captureRefinementGuard: () => () => true,
                prepareCommittedRange: (_, _) => { prepared = true; return Task.FromResult<CompositionRangePreparation?>(new(true)); });
            await delivery.UpdateAsync(sample.Item1); await delivery.CommitAsync(sample.Item1);
            Check(!(await delivery.RefineAsync(sample.Item2)).Applied && !prepared && host.Text == sample.Item1 && host.BeginCount == 1,
                "跨段整理在选择正文或新建组合前保留原文，不发布已实测会错位或重复的路径");
        }
        foreach (var dropCommitReply in new[] { false, true })
        {
            await using var host = new RecoveryHost();
            var prepared = 0;
            bool? finished = null;
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(),
                captureRefinementGuard: () => () => true,
                prepareCommittedRange: (expected, _) =>
                {
                    prepared++;
                    Check(expected == "原文", "结束整理只请求已确认的原语音范围");
                    return Task.FromResult<CompositionRangePreparation?>(new(true, applied =>
                    { finished = applied; return Task.CompletedTask; }));
                });
            await delivery.UpdateAsync("原文");
            await delivery.CommitAsync("原文");
            Check(prepared == 0, "实时出字和基础提交不选择整段正文");
            if (dropCommitReply) host.DropNextReply = TsfCommand.Commit;
            Check((await delivery.RefineAsync("整理后。")).Applied && host.Text == "整理后。" &&
                host.BeginCount == 2 && host.CommitCount == 2 && host.UpdateCount == 2 && finished == true,
                $"结束整理在独立编辑事务中只修订一次，提交丢回复={dropCommitReply}");
            Check((await delivery.RefineAsync("整理后。")).Applied && prepared == 1 && host.CommitCount == 2,
                "结束整理重复回调不重新选择或发送正文");
            Check(delivery.RefinementRecreatedComposition && delivery.TryCreateLearningObserver() is RefinementLearningObserver,
                "整理替换后学习观察绑定新原生会话，不沿用失效范围");
        }
        await using (var host = new RecoveryHost())
        {
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(),
                captureRefinementGuard: () => () => true,
                prepareCommittedRange: (_, _) => Task.FromResult<CompositionRangePreparation?>(new(false)));
            await delivery.UpdateAsync("原文"); await delivery.CommitAsync("原文");
            Check(!(await delivery.RefineAsync("不能写入")).Applied && host.Text == "原文" && host.BeginCount == 1,
                "正文或原选区无法核验时不建立替换组合、不回退原生不安全整理");
        }
        await using (var host = new RecoveryHost())
        {
            var guard = true;
            bool? finished = null;
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(),
                captureRefinementGuard: () => () => guard,
                prepareCommittedRange: (_, _) =>
                {
                    guard = false;
                    return Task.FromResult<CompositionRangePreparation?>(new(true, applied =>
                    { finished = applied; return Task.CompletedTask; }));
                });
            await delivery.UpdateAsync("原文"); await delivery.CommitAsync("原文");
            Check(!(await delivery.RefineAsync("不能覆盖手打")).Applied && host.Text == "原文" &&
                host.BeginCount == 1 && finished == false, "选区准备期间发生键鼠变化时清理选区且不写入");
        }
        await using (var host = new RecoveryHost())
        {
            bool? finished = null;
            var delivery = new TsfCompositionSession(new(host.Endpoint), Guid.NewGuid(),
                captureRefinementGuard: () => () => true,
                prepareCommittedRange: (_, _) =>
                {
                    host.Text = "变化后的内容";
                    return Task.FromResult<CompositionRangePreparation?>(new(true, applied =>
                    { finished = applied; return Task.CompletedTask; }));
                });
            await delivery.UpdateAsync("原文"); await delivery.CommitAsync("原文");
            Check(!(await delivery.RefineAsync("不能覆盖变化")).Applied && host.Text == "变化后的内容" &&
                host.CommitCount == 1 && host.CancelCount == 1 && finished == false,
                "原生返回的选区长度不符时只结束新组合、不提交整理正文");
        }
        var logical = Guid.NewGuid(); var actualSession = Guid.NewGuid();
        var observer = new LearningMappingProbe();
        var mapped = new RefinementLearningObserver(observer, logical, actualSession);
        await mapped.ArmAsync(logical, "整理结果"); await mapped.QueryAsync(logical); await mapped.CancelAsync(logical);
        Check(observer.Sessions.Count == 3 && observer.Sessions.All(value => value == actualSession) && observer.Baseline == "整理结果",
            "学习的建立、查询和取消均映射到同一新会话且保留整理基线");
        Check((await mapped.QueryAsync(Guid.NewGuid())).HResult != 0 && observer.Sessions.Count == 3,
            "其他录音段不能借用整理后的学习观察器");
    }

    private sealed class LearningMappingProbe : ITsfLearningObserver
    {
        public string ApplicationName => "测试宿主";
        public List<Guid> Sessions { get; } = [];
        public string? Baseline;
        private Task<TsfLearningReply> Seen(Guid session)
        { Sessions.Add(session); return Task.FromResult(new TsfLearningReply(0, TsfLearningState.Watching, 1, null, null)); }
        public Task<TsfLearningReply> ArmAsync(Guid session, string expectedCommittedText, CancellationToken cancellationToken = default)
        { Baseline = expectedCommittedText; return Seen(session); }
        public Task<TsfLearningReply> QueryAsync(Guid session, CancellationToken cancellationToken = default) => Seen(session);
        public Task<TsfLearningReply> CancelAsync(Guid session, CancellationToken cancellationToken = default) => Seen(session);
    }
}
