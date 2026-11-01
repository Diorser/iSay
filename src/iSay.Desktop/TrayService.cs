using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace iSay.Desktop;

public sealed class TrayService : IDisposable
{
    private readonly HwndSource source;
    private readonly Func<uint, nint, bool>? notifyOverride;
    private readonly uint taskbarCreatedMessage;
    private readonly DispatcherTimer recoveryTimer;
    private TrayIconResources icons = null!;
    private NotifyIconData data;
    private bool recording, busy, registered, disposed;
    private string currentHotkey = "F3";
    public event Action? OpenRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;
    public event Action? AboutRequested;
    public event Action? CheckUpdatesRequested;
    public event Action<string>? Diagnostic;
    public bool IsRegistered => registered && !disposed;
    public TrayService(string hotkey = "F3") : this(hotkey, null) { }

    internal TrayService(string hotkey, Func<uint, nint, bool>? notifyOverride)
    {
        this.notifyOverride = notifyOverride;
        currentHotkey = hotkey;
        recoveryTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(30) };
        recoveryTimer.Tick += (_, _) => EnsureRegistered();
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        source = new HwndSource(new HwndSourceParameters("iSay 托盘") { Width = 0, Height = 0, WindowStyle = 0 });
        try
        {
            icons = new TrayIconResources(CurrentIconSize());
            data = new NotifyIconData { Size = Marshal.SizeOf<NotifyIconData>(), Window = source.Handle, Id = 1, Flags = 1 | 2 | 4, CallbackMessage = 0x8001, Icon = icons.Get(TrayIconState.Ready), Tip = BuildTip(hotkey), Info = "", InfoTitle = "" };
            // 资源就绪后再接收外壳和DPI消息，避免构造期间访问尚未初始化的图标。
            source.AddHook(Procedure);
            registered = Notify(0);
            UiText.Current.LanguageChanged += OnLanguageChanged;
            recoveryTimer.Start();
        }
        catch { UiText.Current.LanguageChanged -= OnLanguageChanged; icons?.Dispose(); source.RemoveHook(Procedure); source.Dispose(); throw; }
    }
    public void UpdateState(bool isRecording, bool isBusy, string hotkey)
    {
        if (disposed) return;
        recording = isRecording; busy = isBusy;
        currentHotkey = hotkey;
        var icon = icons.Get(CurrentState);
        var tip = BuildTip(hotkey);
        if (data.Icon == icon && data.Tip == tip && registered) return;
        data.Icon = icon; data.Tip = tip;
        if (!registered || !Notify(1)) registered = Notify(0);
    }

    private string BuildTip(string hotkey)
    {
        var key = iSay.Voice.HotkeyDefinition.DisplayName(hotkey);
        if (UiText.Current.IsEnglish) return busy ? "iSay · Processing transcript" : recording ? $"iSay · Recording · {key} to stop" : $"iSay · {key} to start / stop recording";
        return busy ? "iSay · 正在处理转写" : recording ? $"iSay · 正在录音 · {key} 结束" : $"iSay · {key} 开始 / 结束录音";
    }

    private void OnLanguageChanged(object? sender, EventArgs args) => UpdateState(recording, busy, currentHotkey);

    public void ShowNotification(string title, string text)
    {
        if (disposed) return;
        var previousFlags = data.Flags;
        try
        {
            data.Flags |= 0x10;
            data.InfoTitle = title.Length > 63 ? title[..63] : title;
            data.Info = text.Length > 255 ? text[..255] : text;
            data.InfoFlags = 1;
            Notify(1);
        }
        finally { data.Flags = previousFlags; data.Info = ""; data.InfoTitle = ""; }
    }

    private TrayIconState CurrentState => busy ? TrayIconState.Processing : recording ? TrayIconState.Recording : TrayIconState.Ready;
    public void EnsureRegistered()
    {
        if (disposed) return;
        try
        {
            var wasRegistered = registered;
            RestoreIcon(false);
            if (wasRegistered != registered || !registered)
                Diagnostic?.Invoke(registered ? "托盘已恢复" : "托盘登记失败，将继续后台重试");
        }
        catch (Exception error)
        {
            registered = false;
            Diagnostic?.Invoke("托盘恢复异常：" + error.GetType().Name);
        }
    }
    private bool Notify(uint command) => notifyOverride?.Invoke(command, data.Icon) ?? Shell_NotifyIcon(command, ref data);
    private int CurrentIconSize()
    {
        var dpi = GetDpiForWindow(source.Handle);
        return Math.Clamp(GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi), 16, 128);
    }
    private void RestoreIcon(bool afterExplorerRestart)
    {
        if (disposed) return;
        var size = CurrentIconSize();
        var previous = icons;
        if (size != icons.Size)
        {
            icons = new TrayIconResources(size);
            data.Icon = icons.Get(CurrentState);
        }
        try
        {
            if (afterExplorerRestart) registered = false;
            if (!registered || !Notify(1)) registered = Notify(0);
        }
        finally { if (!ReferenceEquals(previous, icons)) previous.Dispose(); }
    }
    private IntPtr Procedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (disposed) return IntPtr.Zero;
        if (taskbarCreatedMessage != 0 && (uint)message == taskbarCreatedMessage)
        {
            registered = false; Diagnostic?.Invoke("资源管理器重建托盘"); EnsureRegistered(); return IntPtr.Zero;
        }
        if (message is 0x02E0 or 0x001A or 0x007E) { EnsureRegistered(); return IntPtr.Zero; }
        if (message != 0x8001) return IntPtr.Zero;
        if (lParam.ToInt32() == 0x0405) AboutRequested?.Invoke();
        if (lParam.ToInt32() == 0x0203) OpenRequested?.Invoke();
        if (lParam.ToInt32() == 0x0205)
        {
            var menu = CreatePopupMenu();
            AppendMenu(menu, 0, 1, UiText.Translate("打开 iSay"));
            AppendMenu(menu, busy ? 1u : 0u, 2, UiText.Translate(busy ? "正在处理转写" : recording ? "结束录音" : "开始录音"));
            AppendMenu(menu, 0x0800, 0, "");
            AppendMenu(menu, 0, 4, UiText.Translate("检查更新"));
            AppendMenu(menu, 0, 5, UiText.Translate("关于 iSay"));
            AppendMenu(menu, 0x0800, 0, "");
            AppendMenu(menu, 0, 3, UiText.Translate("退出"));
            GetCursorPos(out var point);
            SetForegroundWindow(hwnd);
            var choice = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, hwnd, IntPtr.Zero);
            DestroyMenu(menu);
            if (choice == 1) OpenRequested?.Invoke();
            if (choice == 2) ToggleRequested?.Invoke();
            if (choice == 3) ExitRequested?.Invoke();
            if (choice == 4) CheckUpdatesRequested?.Invoke();
            if (choice == 5) AboutRequested?.Invoke();
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        UiText.Current.LanguageChanged -= OnLanguageChanged;
        recoveryTimer.Stop();
        try { if (registered) Notify(2); }
        finally
        {
            registered = false;
            source.RemoveHook(Procedure); source.Dispose(); icons.Dispose();
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public int Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr Balloon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint command, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
