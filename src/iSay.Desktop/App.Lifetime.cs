using System.Windows;
using System.Windows.Threading;
using iSay.Desktop.Services;
using Microsoft.Win32;

namespace iSay.Desktop;

public partial class App
{
    private ApplicationLifetimeJournal? lifetimeJournal;
    private DispatcherTimer? lifetimeTimer;
    private bool startupCompleted, exitCleanupCompleted;

    private void InitializeLifetimeJournal(bool background)
    {
        lifetimeJournal = new ApplicationLifetimeJournal(store.DirectoryPath,
            typeof(App).Assembly.GetName().Version?.ToString() ?? "未知", background);
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedException;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        lifetimeTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMinutes(1) };
        lifetimeTimer.Tick += (_, _) =>
        {
            var state = new { Recording = vm.IsRecording, Busy = vm.IsBusy, Formatting = vm.IsFormatting,
                Exiting = exiting, WindowVisible = window?.IsVisible ?? false, TrayRegistered = tray?.IsRegistered ?? false };
            diagnosticWrites.TryEnqueue(() => lifetimeJournal.Heartbeat(state));
        };
        lifetimeTimer.Start();
    }
    private void OnFatalException(object sender, UnhandledExceptionEventArgs args)
        => lifetimeJournal?.Exception("后台线程未处理异常", args.ExceptionObject as Exception ?? new Exception("未知异常对象"), args.IsTerminating);
    private void OnProcessExit(object? sender, EventArgs args) => lifetimeJournal?.ProcessExited(Environment.ExitCode);
    private void OnUnobservedException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        lifetimeJournal?.Exception("未观察的后台任务异常", args.Exception, false);
        args.SetObserved();
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        lifetimeJournal?.Record("电源状态", new { Mode = args.Mode.ToString() });
        if (args.Mode == PowerModes.Resume && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(() => tray?.EnsureRegistered());
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
        => lifetimeJournal?.Record("桌面会话", new { Reason = args.Reason.ToString() });
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        lifetimeJournal?.BeginExit("系统" + e.ReasonSessionEnding);
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        lifetimeTimer?.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        TaskScheduler.UnobservedTaskException -= OnUnobservedException;
        lifetimeJournal?.Complete(e.ApplicationExitCode, exitCleanupCompleted);
        base.OnExit(e);
    }
}
