using System.Diagnostics;

namespace iSay.Desktop.Services;

/// <summary>按单调时钟记录真正变化的识别文字；重复快照、旧会话与键鼠活动不能延长录音。</summary>
public sealed class RecognitionIdleTimeout
{
    private readonly object sync = new();
    private Guid session;
    private string latestText = "";
    private long changedAt;
    private bool expired;

    public void Start(Guid id, long now)
    {
        lock (sync) { session = id; latestText = ""; changedAt = now; expired = false; }
    }

    public void Observe(Guid id, string text, long now)
    {
        lock (sync)
        {
            if (id != session || expired || string.IsNullOrWhiteSpace(text) || text == latestText) return;
            latestText = text;
            changedAt = Math.Max(changedAt, now);
        }
    }

    public bool TryExpire(Guid id, long now, int seconds)
    {
        lock (sync)
        {
            if (seconds <= 0 || id != session || expired || Stopwatch.GetElapsedTime(changedAt, now).TotalSeconds < seconds) return false;
            expired = true;
            return true;
        }
    }
}
