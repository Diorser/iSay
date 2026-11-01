using System.Diagnostics;

namespace iSay.Desktop.Services;

/// <summary>按录音会话记录单调时钟阶段，诊断不保存音频或正文。</summary>
public sealed class SessionPerformance(Guid sessionId, long startedAt = 0)
{
    private readonly long started = startedAt == 0 ? Stopwatch.GetTimestamp() : startedAt;
    private long feedback, firstAudio, firstVoicedAudio, engineStarted, firstRecognition, firstPreview, firstDelivery;
    private long captureRequested, microphoneResolved, captureStarted, enginePrepared, engineStartRequested, firstAudioSent;
    private long stopRequested, audioStopped, recognitionFinal, editCommitted, ready;
    private long maximumPreviewDelayTicks;
    private long lastRecognitionAt, lastPreviewAt, maximumRecognitionGapTicks, maximumPreviewGapTicks;
    private long maximumDeliveryQueueTicks, maximumRecognitionToDeliveryTicks;
    private long lastLiveDeliveryAt, maximumLiveDeliveryGapTicks;
    private long transportCount, queryCount, transportFailures, maximumPipeQueueTicks, maximumPipeConnectTicks, maximumPipeReplyTicks;
    public Guid SessionId { get; } = sessionId;
    public string OutputKind { get; init; } = "target";
    private static void Mark(ref long value) => Interlocked.CompareExchange(ref value, Stopwatch.GetTimestamp(), 0);
    public void MarkFeedback() => Mark(ref feedback);
    public void MarkFirstAudio() => Mark(ref firstAudio);
    public void MarkVoicedAudio() => Mark(ref firstVoicedAudio);
    public void MarkEngineStarted() => Mark(ref engineStarted);
    public void MarkCaptureRequested() => Mark(ref captureRequested);
    public void MarkMicrophoneResolved() => Mark(ref microphoneResolved);
    public void MarkCaptureStarted() => Mark(ref captureStarted);
    public void MarkEnginePrepared() => Mark(ref enginePrepared);
    public void MarkEngineStartRequested() => Mark(ref engineStartRequested);
    public void MarkFirstAudioSent() => Mark(ref firstAudioSent);
    public void MarkRecognition(long receivedAt = 0)
    {
        var observedAt = receivedAt == 0 ? Stopwatch.GetTimestamp() : receivedAt;
        Interlocked.CompareExchange(ref firstRecognition, observedAt, 0);
        var previous = Interlocked.Exchange(ref lastRecognitionAt, observedAt);
        if (previous != 0) Maximum(ref maximumRecognitionGapTicks, observedAt - previous);
    }
    public void MarkDelivery(long receivedAt = 0, long attemptedAt = 0)
    {
        Mark(ref firstDelivery);
        if (receivedAt != 0) Maximum(ref maximumRecognitionToDeliveryTicks, Stopwatch.GetTimestamp() - receivedAt);
        if (receivedAt != 0 && attemptedAt != 0) Maximum(ref maximumDeliveryQueueTicks, attemptedAt - receivedAt);
        if (attemptedAt != 0)
        {
            var now = Stopwatch.GetTimestamp(); var previous = Interlocked.Exchange(ref lastLiveDeliveryAt, now);
            if (previous != 0) Maximum(ref maximumLiveDeliveryGapTicks, now - previous);
        }
    }
    public void MarkStopRequested() => Mark(ref stopRequested);
    public void MarkAudioStopped() => Mark(ref audioStopped);
    public void MarkRecognitionFinal() => Mark(ref recognitionFinal);
    public void MarkEditCommitted() => Mark(ref editCommitted);
    public void MarkReady() => Mark(ref ready);
    public void MarkPreview(long receivedAt)
    {
        Mark(ref firstPreview);
        var previous = Interlocked.Exchange(ref lastPreviewAt, Stopwatch.GetTimestamp());
        if (previous != 0) Maximum(ref maximumPreviewGapTicks, Stopwatch.GetTimestamp() - previous);
        var delay = Stopwatch.GetTimestamp() - receivedAt;
        long observed;
        do { observed = Interlocked.Read(ref maximumPreviewDelayTicks); if (observed >= delay) return; }
        while (Interlocked.CompareExchange(ref maximumPreviewDelayTicks, delay, observed) != observed);
    }
    internal void RecordTransport(TsfPipeDiagnostic value)
    {
        Interlocked.Increment(ref transportCount);
        if (value.Command == TsfCommand.Query) Interlocked.Increment(ref queryCount);
        if (value.Stage != "completed" || value.HResult != 0) Interlocked.Increment(ref transportFailures);
        Maximum(ref maximumPipeQueueTicks, (long)(value.QueueMilliseconds * Stopwatch.Frequency / 1000d));
        Maximum(ref maximumPipeConnectTicks, (long)(value.ConnectMilliseconds * Stopwatch.Frequency / 1000d));
        Maximum(ref maximumPipeReplyTicks, (long)(value.ReplyMilliseconds * Stopwatch.Frequency / 1000d));
    }
    private static void Maximum(ref long destination, long value)
    {
        long previous;
        do { previous = Interlocked.Read(ref destination); if (value <= previous) return; }
        while (Interlocked.CompareExchange(ref destination, value, previous) != previous);
    }
    private static double Duration(long ticks) => Math.Round(ticks * 1000d / Stopwatch.Frequency, 3);
    private double? Milliseconds(long value, long origin = 0) => value == 0 ? null : Math.Round(Stopwatch.GetElapsedTime(origin == 0 ? started : origin, value).TotalMilliseconds, 3);
    public object Snapshot() => new
    {
        SessionId, OutputKind, FeedbackMs = Milliseconds(Interlocked.Read(ref feedback)), FirstAudioMs = Milliseconds(Interlocked.Read(ref firstAudio)),
        CaptureRequestedMs = Milliseconds(Interlocked.Read(ref captureRequested)),
        MicrophoneResolvedMs = Milliseconds(Interlocked.Read(ref microphoneResolved)),
        CaptureStartedMs = Milliseconds(Interlocked.Read(ref captureStarted)),
        EnginePreparedMs = Milliseconds(Interlocked.Read(ref enginePrepared)),
        EngineStartRequestedMs = Milliseconds(Interlocked.Read(ref engineStartRequested)),
        FirstAudioSentMs = Milliseconds(Interlocked.Read(ref firstAudioSent)),
        AudioToEngineMs = Interlocked.Read(ref firstAudio) == 0 ? null : Milliseconds(Interlocked.Read(ref firstAudioSent), Interlocked.Read(ref firstAudio)),
        // 原生 Start 返回包含词表确认，不代表云端连接就绪；声音判定也只是采样峰值标记。
        EngineStartCallMs = Interlocked.Read(ref engineStartRequested) == 0 ? null : Milliseconds(Interlocked.Read(ref engineStarted), Interlocked.Read(ref engineStartRequested)),
        FirstVoicedAudioMs = Milliseconds(Interlocked.Read(ref firstVoicedAudio)),
        VoiceToRecognitionMs = Interlocked.Read(ref firstVoicedAudio) == 0 ? null : Milliseconds(Interlocked.Read(ref firstRecognition), Interlocked.Read(ref firstVoicedAudio)),
        VoiceToPreviewMs = Interlocked.Read(ref firstVoicedAudio) == 0 ? null : Milliseconds(Interlocked.Read(ref firstPreview), Interlocked.Read(ref firstVoicedAudio)),
        VoiceToDeliveryMs = Interlocked.Read(ref firstVoicedAudio) == 0 ? null : Milliseconds(Interlocked.Read(ref firstDelivery), Interlocked.Read(ref firstVoicedAudio)),
        EngineStartedMs = Milliseconds(Interlocked.Read(ref engineStarted)), FirstRecognitionMs = Milliseconds(Interlocked.Read(ref firstRecognition)),
        FirstPreviewMs = Milliseconds(Interlocked.Read(ref firstPreview)), FirstDeliveryMs = Milliseconds(Interlocked.Read(ref firstDelivery)),
        MaximumPreviewQueueMs = Math.Round(Interlocked.Read(ref maximumPreviewDelayTicks) * 1000d / Stopwatch.Frequency, 3),
        MaximumRecognitionGapMs = Duration(Interlocked.Read(ref maximumRecognitionGapTicks)),
        MaximumPreviewGapMs = Duration(Interlocked.Read(ref maximumPreviewGapTicks)),
        MaximumDeliveryQueueMs = Duration(Interlocked.Read(ref maximumDeliveryQueueTicks)),
        MaximumRecognitionToDeliveryMs = Duration(Interlocked.Read(ref maximumRecognitionToDeliveryTicks)),
        MaximumLiveDeliveryGapMs = Duration(Interlocked.Read(ref maximumLiveDeliveryGapTicks)),
        CompositionCommands = Interlocked.Read(ref transportCount), CompositionQueries = Interlocked.Read(ref queryCount), CompositionWaitsOrFailures = Interlocked.Read(ref transportFailures),
        MaximumPipeQueueMs = Duration(Interlocked.Read(ref maximumPipeQueueTicks)), MaximumPipeConnectMs = Duration(Interlocked.Read(ref maximumPipeConnectTicks)), MaximumPipeReplyMs = Duration(Interlocked.Read(ref maximumPipeReplyTicks)),
        StopToAudioStoppedMs = Milliseconds(Interlocked.Read(ref audioStopped), Interlocked.Read(ref stopRequested)),
        StopToRecognitionFinalMs = Milliseconds(Interlocked.Read(ref recognitionFinal), Interlocked.Read(ref stopRequested)),
        StopToEditCommittedMs = Interlocked.Read(ref stopRequested) == 0 ? null : Milliseconds(Interlocked.Read(ref editCommitted), Interlocked.Read(ref stopRequested)),
        RecognitionFinalToEditCommittedMs = Interlocked.Read(ref recognitionFinal) == 0 ? null : Milliseconds(Interlocked.Read(ref editCommitted), Interlocked.Read(ref recognitionFinal)),
        StopToReadyMs = Milliseconds(Interlocked.Read(ref ready), Interlocked.Read(ref stopRequested))
    };
}
