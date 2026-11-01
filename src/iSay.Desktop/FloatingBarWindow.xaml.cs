using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace iSay.Desktop;

public partial class FloatingBarWindow : Window
{
    private const int GwlExtendedStyle = -20;
    private const int NoActivateStyle = 0x08000000;
    private const int ToolWindowStyle = 0x00000080;
    private readonly DispatcherTimer timer;
    private DateTimeOffset recordingStartedAt;
    private readonly DispatcherTimer resultTimer = new();
    private string lastStateMessage = "", lastStateLabel = "正在完成";

    public FloatingBarWindow()
    {
        InitializeComponent();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateElapsed();
        SourceInitialized += (_, _) => ApplyNoActivateStyle();
        Closed += (_, _) => { timer.Stop(); resultTimer.Stop(); RecordingWave.IsActive = false; };
        resultTimer.Tick += (_, _) => EndRecording();
        UiText.Current.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => UiText.Current.LanguageChanged -= OnLanguageChanged;
    }

    public void BeginRecording()
    {
        resultTimer.Stop();
        recordingStartedAt = DateTimeOffset.Now;
        SetRecordingPresentation();
        PositionNearBottom();
        timer.Start();
        Show();
    }

    public void SetProcessing(string message = "正在整理识别结果", string shortLabel = "正在完成")
    {
        lastStateMessage = message; lastStateLabel = shortLabel;
        timer.Stop();
        RecordingWave.IsActive = false;
        RecordingWave.Visibility = Visibility.Collapsed;
        StatePanel.Visibility = Visibility.Visible;
        StateIcon.Text = "···";
        StateIcon.Foreground = Brushes.LightSlateGray;
        StateLabel.Text = UiText.Translate(shortLabel);
        // 整理期间保留状态，只在收到成功或失败结果后启动提示隐藏计时。
        resultTimer.Stop();
        Capsule.ToolTip = UiText.Translate(message);
        System.Windows.Automation.AutomationProperties.SetName(Capsule, UiText.Translate(message));
    }

    public void EndRecording()
    {
        timer.Stop(); resultTimer.Stop(); RecordingWave.IsActive = false;
        Hide();
    }
    public void ShowResult(string message, bool success)
        => ShowResult(message, success, false);

    public void ShowResult(string message, bool success, bool transcriptAvailable)
    {
        timer.Stop(); resultTimer.Stop();
        SetResultPresentationCore(message, success, transcriptAvailable);
        PositionNearBottom(); Show();
        resultTimer.Interval = ResultDisplayDuration(success, transcriptAvailable);
        resultTimer.Start();
    }
    internal static TimeSpan ResultDisplayDuration(bool success, bool transcriptAvailable)
        => TimeSpan.FromMilliseconds(success ? 650 : transcriptAvailable ? 1600 : 2200);

    private void UpdateElapsed()
    {
        var elapsed = DateTimeOffset.Now - recordingStartedAt;
        var hotkey = (DataContext as VoiceViewModel)?.AppliedHotkeyDisplay ?? "F3";
        Capsule.ToolTip = UiText.Current.IsEnglish
            ? $"Recording {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00} · {hotkey} to stop\nSee the iSay window for the transcript and details."
            : $"正在录音 {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00} · {hotkey} 结束\n文字和详细状态可在 iSay 主窗口查看。";
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (RecordingWave.IsActive) UpdateElapsed();
        else
        {
            StateLabel.Text = UiText.Translate(lastStateLabel);
            Capsule.ToolTip = UiText.Translate(lastStateMessage);
            System.Windows.Automation.AutomationProperties.SetName(Capsule, UiText.Translate(lastStateMessage));
        }
    }

    internal void SetRecordingPresentation()
    {
        StatePanel.Visibility = Visibility.Collapsed;
        RecordingWave.Visibility = Visibility.Visible;
        RecordingWave.IsActive = true;
        UpdateElapsed();
        System.Windows.Automation.AutomationProperties.SetName(Capsule, UiText.Translate("iSay 正在录音"));
    }

    internal void SetResultPresentation(string message, bool success)
        => SetResultPresentationCore(message, success, false);

    private void SetResultPresentationCore(string message, bool success, bool transcriptAvailable)
    {
        RecordingWave.IsActive = false;
        RecordingWave.Visibility = Visibility.Collapsed;
        StatePanel.Visibility = Visibility.Visible;
        StateIcon.Text = success ? "✓" : "!";
        StateIcon.Foreground = success ? new SolidColorBrush(Color.FromRgb(100, 206, 176)) : new SolidColorBrush(Color.FromRgb(233, 180, 91));
        // 输入框未确认不等于录音丢失；只有本段确实有转写时才提示文字已保留。
        var noSpeech = !transcriptAvailable && message.Contains("没有识别到文字", StringComparison.Ordinal);
        lastStateMessage = message;
        lastStateLabel = success ? "已完成" : transcriptAvailable ? "文字已保留" : noSpeech ? "未识别到语音" : "操作未完成";
        StateLabel.Text = UiText.Translate(lastStateLabel);
        Capsule.ToolTip = UiText.Translate(message);
        System.Windows.Automation.AutomationProperties.SetName(Capsule, UiText.Translate(message));
    }

    private void PositionNearBottom()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Bottom - Height - 28;
    }

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 浮条只作状态展示；不抢走正在输入的目标窗口焦点。
        e.Handled = true;
    }

    private void ApplyNoActivateStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExtendedStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExtendedStyle, new IntPtr(style | NoActivateStyle | ToolWindowStyle));
        HwndSource.FromHwnd(handle)?.AddHook(WindowProcedure);
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021)
        {
            handled = true;
            return new IntPtr(3);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
