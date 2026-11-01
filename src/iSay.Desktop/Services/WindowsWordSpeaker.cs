using System.Globalization;
using System.Runtime.InteropServices;

namespace iSay.Desktop.Services;

public sealed record WordSpeechResult(bool Success, string Message, string? VoiceName = null);

// 使用系统已安装的语音，独立线程播放，避免语音合成或设备初始化阻塞界面。
public sealed class WindowsWordSpeaker : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? active;
    private bool disposed;

    public Task<WordSpeechResult> SpeakAsync(string text) => SpeakAsync(text, null);

    internal Task<WordSpeechResult> SpeakAsync(string text, string? wavePath)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
            return Task.FromResult(new WordSpeechResult(false, "请选择 1–64 字的词汇。"));
        CancellationTokenSource cancellation;
        lock (gate)
        {
            if (disposed) return Task.FromResult(new WordSpeechResult(false, "朗读服务已关闭。"));
            active?.Cancel();
            active = cancellation = new CancellationTokenSource();
        }
        var completion = new TaskCompletionSource<WordSpeechResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            object? voiceObject = null, voicesObject = null, selectedObject = null, streamObject = null;
            WordSpeechResult result = new(false, "朗读未完成。");
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                voiceObject = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice", true)!)!;
                dynamic voice = voiceObject;
                voicesObject = voice.GetVoices();
                dynamic voices = voicesObject;
                var chinese = text.Any(character => character is >= '\u3400' and <= '\u9fff');
                var language = chinese ? 4 : 9;
                for (var index = 0; index < (int)voices.Count; index++)
                {
                    object candidate = voices.Item(index);
                    dynamic token = candidate;
                    var languages = ((string)token.GetAttribute("Language")).Split(';');
                    if (languages.Any(value => int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) && (id & 0x3ff) == language))
                    { selectedObject = candidate; break; }
                    Marshal.FinalReleaseComObject(candidate);
                }
                if (selectedObject is null)
                {
                    result = new(false, chinese ? "系统未安装中文语音，请在 Windows 语言设置中添加。" : "系统未安装英语语音，请在 Windows 语言设置中添加。");
                    return;
                }
                voice.Voice = (dynamic)selectedObject;
                var name = (string)((dynamic)selectedObject).GetDescription();
                if (wavePath is not null)
                {
                    streamObject = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream", true)!)!;
                    ((dynamic)streamObject).Open(wavePath, 3, false);
                    voice.AudioOutputStream = (dynamic)streamObject;
                }
                // 异步朗读并明确按纯文本处理；新一次播放或开始录音可立即取消。
                voice.Speak(text, 17);
                while (!(bool)voice.WaitUntilDone(20))
                {
                    if (!cancellation.IsCancellationRequested) continue;
                    voice.Speak("", 19);
                    result = new(false, "朗读已停止。", name);
                    return;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                result = new(true, $"已朗读“{text}”", name);
            }
            catch (OperationCanceledException) { result = new(false, "朗读已停止。"); }
            catch (Exception) { result = new(false, "系统语音暂时无法播放，请检查声音输出和语音设置。"); }
            finally
            {
                if (streamObject is not null) { try { ((dynamic)streamObject).Close(); } catch (Exception) { } }
                foreach (var value in new[] { streamObject, selectedObject, voicesObject, voiceObject })
                    if (value is not null && Marshal.IsComObject(value))
                        try { Marshal.FinalReleaseComObject(value); } catch (InvalidComObjectException) { }
                lock (gate)
                {
                    if (ReferenceEquals(active, cancellation)) active = null;
                    cancellation.Dispose();
                }
                completion.TrySetResult(result);
            }
        }) { IsBackground = true, Name = "词汇朗读" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    public void Stop() { lock (gate) active?.Cancel(); }
    public void Dispose() { lock (gate) { disposed = true; active?.Cancel(); } }
}
