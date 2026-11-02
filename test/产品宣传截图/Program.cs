using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using iSay.Desktop;
using iSay.Voice;

internal static class Program
{
    private const int Width = 1100, Height = 760;
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static readonly string Output = Path.Combine(Root, "website/dist/assets/screenshots");
    private static readonly List<object> Images = [];
    private static Border surface = null!;

    [STAThread]
    private static int Main()
    {
        try
        {
            Directory.CreateDirectory(Output);
            var app = new SafePreviewApp();
            var settings = new VoiceSettings
            {
                UiLanguage = "zh-CN", Hotkey = "RightAlt", HotkeyMode = "toggle", MicrophoneId = "default",
                SmartFormattingEnabled = true, FormattingBackend = "auto", FormattingStyle = "standard",
                MuteSystemAudioWhileRecording = true, StartAtLogin = true, ShowFloatingBar = true,
                SaveHistory = true, HistoryRetentionDays = 30, SaveAudio = false, AsrVocabularyEnabled = true
            };
            var vm = new VoiceViewModel { Settings = settings };
            vm.ApplySavedSettings(settings);
            vm.Microphones.Add(new MicrophoneInfo("default", "系统默认麦克风"));
            vm.RawText = "给团队发个消息啊我们下周三下午两点半在三楼会议室开项目评审会主要看两个事情一个是新版首页的设计方案另一个是下载和新手引导的流程大家提前把反馈整理一下如果时间冲突呢就在群里说一声谢谢";
            vm.FormattedText = "各位同事：\n\n下周三 14:30，我们在三楼会议室召开项目评审会。\n\n本次重点讨论：\n1. 新版首页的设计方案。\n2. 软件下载与新手引导流程。\n\n请大家提前整理反馈；如有时间冲突，请在群里告知。谢谢！";
            vm.StatusText = "整理完成，可以复制或继续输入。";
            vm.EngineStatusText = "中文语音输入";
            var now = new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.FromHours(8));
            var history = new[]
            {
                ("请大家提前整理一下今天评审的反馈，下周三下午两点半我们在三楼会议室碰一下。", "请大家提前整理今天评审的反馈。下周三 14:30，我们在三楼会议室讨论。"),
                ("今天先把首页的体验做好，然后检查下载按钮，最后把新手引导再走一遍。", "今天的工作安排：完善首页体验、检查下载入口，并完整验证新手引导流程。"),
                ("帮我记一下这篇文章的想法，语音输入最好的体验就是不用打断思路，想到什么就说出来。", "文章灵感：好的语音输入体验，是让表达跟上思路。想到什么，就自然地说出来。"),
                ("这周末我们去郊外走走吧，带上相机，早上九点出发，中午找一家小店吃饭。", "周末计划：早上 9:00 出发去郊外，带上相机，中午找一家小店吃饭。")
            };
            for (var i = 0; i < history.Length; i++)
                vm.History.Add(new HistoryEntry("demo-" + i, now.AddHours(-i * 2), history[i].Item1, history[i].Item2, "已完成"));
            var words = new[] { "iSay", "Windows", "星河工作室", "项目评审", "用户体验", "新手引导", "语音输入", "产品周报" };
            for (var i = 0; i < words.Length; i++) vm.RecognitionWords.Add(new RecognitionVocabularyEntry(words[i], now.AddDays(-i), i < 3));
            vm.NotifyRecognitionWordsChanged();
            vm.VocabularyStatusText = "";
            var window = new VoiceDeskWindow { DataContext = vm };
            // 只创建真实界面与演示视图模型，不连接正式应用动作或任何设备服务。
            var content = (FrameworkElement)window.Content;
            content.Resources = window.Resources;
            content.DataContext = vm;
            content.SetValue(Control.FontFamilyProperty, window.FontFamily);
            content.SetValue(Control.FontSizeProperty, window.FontSize);
            content.SetValue(Control.ForegroundProperty, window.Foreground);
            window.Content = null;
            surface = new Border { Background = Brushes.White, Child = content, DataContext = vm };
            var nav = (TabControl)window.FindName("NavigationTabs");
            var transcript = (TabControl)window.FindName("TranscriptTabs");
            var vocabulary = (TabControl)window.FindName("VocabularyContent");
            var configuration = (TabControl)window.FindName("SettingsContent");
            nav.SelectedIndex = 0; transcript.SelectedIndex = 0; Layout(); Capture("voice-input.png");
            transcript.SelectedIndex = 1; Layout(); Capture("raw-input.png");
            nav.SelectedIndex = 1; Layout(); Capture("history.png");
            nav.SelectedIndex = 2; vocabulary.SelectedIndex = 0; Layout(); Capture("dictionary.png");
            nav.SelectedIndex = 4; configuration.SelectedIndex = 0; Layout(); Capture("settings.png");
            SocialPreview();
            var manifest = new
            {
                Source = "iSay 0.10.5 发布程序集真实 WPF 控件，离屏渲染",
                SyntheticData = true, RealMicrophone = false, NetworkUsed = false, UserProfileDataRead = false,
                RawText = vm.RawText, FormattedText = vm.FormattedText,
                HistoryCount = vm.History.Count, Vocabulary = words, Hotkey = settings.Hotkey, Images
            };
            File.WriteAllText(Path.Combine(Root, "test/产品宣传截图/截图说明.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
            Console.WriteLine(JsonSerializer.Serialize(new { Success = true, Images, HistoryCount = vm.History.Count, VocabularyCount = words.Length }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Layout()
    {
        surface.Measure(new Size(Width, Height)); surface.Arrange(new Rect(0, 0, Width, Height)); surface.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        surface.UpdateLayout();
    }

    private static void Capture(string name)
    {
        Layout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            draw.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, Width, Height));
        }
        bitmap.Render(visual);
        Save(bitmap, name, Width, Height);
    }

    private static void SocialPreview()
    {
        var background = (Color)ColorConverter.ConvertFromString("#0B3937");
        var mint = (Color)ColorConverter.ConvertFromString("#BCECD5");
        var paper = (Color)ColorConverter.ConvertFromString("#F5F4EC");
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, 1200, 630));
            var logo = new BitmapImage(new Uri(Path.Combine(Root, "src/iSay.Desktop/Assets/iSay.png")));
            draw.DrawImage(logo, new Rect(88, 62, 62, 62));
            Text(draw, "iSay", 177, 50, 68, paper, "Segoe UI", FontWeights.SemiBold);
            Text(draw, "开口，就成文。", 82, 208, 91, paper, "Microsoft YaHei UI", FontWeights.Medium);
            Text(draw, "Windows 中文语音输入法", 88, 369, 29, mint, "Microsoft YaHei UI", FontWeights.Normal);
            draw.DrawRoundedRectangle(new SolidColorBrush(mint), null, new Rect(88, 469, 160, 51), 5, 5);
            Text(draw, "永久免费", 113, 475, 25, background, "Microsoft YaHei UI", FontWeights.SemiBold);
            Text(draw, "isay-voice.pages.dev", 720, 490, 23, mint, "Segoe UI", FontWeights.Normal);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(55, mint.R, mint.G, mint.B)), 1);
            draw.DrawLine(pen, new Point(88, 554), new Point(1110, 554));
        }
        var bitmap = new RenderTargetBitmap(1200, 630, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        Save(bitmap, "social-preview.png", 1200, 630);
    }

    private static void Text(DrawingContext drawing, string text, double x, double y, double size, Color color, string family, FontWeight weight)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily(family), FontStyles.Normal, weight, FontStretches.Normal), size, new SolidColorBrush(color), 1);
        drawing.DrawText(formatted, new Point(x, y));
    }

    private static void Save(BitmapSource bitmap, string name, int width, int height)
    {
        var path = Path.Combine(Output, name);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        Images.Add(new { Name = name, Width = width, Height = height, Bytes = new FileInfo(path).Length });
    }
}

internal sealed class SafePreviewApp : App
{
    // 拦截应用启动，避免正式单实例、热键、录音、TSF 与后台服务初始化。
    protected override void OnStartup(StartupEventArgs args) => ShutdownMode = ShutdownMode.OnExplicitShutdown;
}
