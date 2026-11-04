using iSay.Desktop.Services;

internal static partial class Program
{
    private static async Task RunStopEmptyTargetGuards()
    {
        var endpoint = Endpoint();
        var capturedTargetStillEmpty = false;
        var guarded = new TsfCompositionSession(new(endpoint), Guid.NewGuid(),
            canBegin: _ => Task.FromResult(capturedTargetStillEmpty));
        Check(!(await guarded.BeginAsync()).Applied && !guarded.MayHaveWrittenText,
            "探测后焦点或正文变化，结束空框不建立组合");

        endpoint = Endpoint();
        var requests = new List<TsfCommand>();
        var server = Serve(endpoint, 2, request =>
        {
            requests.Add(request.Command);
            return Reply(request, request.Command == TsfCommand.Cancel ? TsfCompositionState.Committed : TsfCompositionState.Composing);
        });
        capturedTargetStillEmpty = true;
        guarded = new(new(endpoint), Guid.NewGuid(), canBegin: _ => Task.FromResult(capturedTargetStillEmpty),
            canWriteOriginal: _ => Task.FromResult(capturedTargetStillEmpty));
        Check((await guarded.BeginAsync()).Applied, "结束时经重新确认可建立空组合");
        capturedTargetStillEmpty = false;
        Check(!(await guarded.CommitAsync("用户已改变目标，保留转写")).Applied && !guarded.MayHaveWrittenText,
            "空组合建立后若目标变化，也不能发送正文");
        Check((await guarded.CancelAsync()).Applied, "放弃结束空框时仍可清理空组合");
        await server;
        Check(requests.SequenceEqual(new[] { TsfCommand.Begin, TsfCommand.Cancel }), "不安全目标全程无任何正文命令");
    }
}
