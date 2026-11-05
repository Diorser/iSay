using System.Windows;
using iSay.Desktop;

namespace iSay.Tests;

/// <summary>WPF构造会在Dispatcher首次泵送时触发启动，离屏测试必须拦截，不能唤起正式单实例。</summary>
public sealed class OffscreenApp : App
{
    public bool StartupWasIntercepted { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupWasIntercepted = true;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }
}
