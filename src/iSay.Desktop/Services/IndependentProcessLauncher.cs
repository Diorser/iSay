using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace iSay.Desktop.Services;

internal sealed record ProcessLaunchEnvironment(bool InJob, uint LimitFlags, int QueryError)
{
    public bool KillOnJobClose => (LimitFlags & 0x2000) != 0;
    public bool BreakawayAllowed => (LimitFlags & 0x1800) != 0;
}

// 常驻客户端不能继承工具启动器的整组退出策略；不修改或终止启动器本身。
internal static class IndependentProcessLauncher
{
    internal const string IndependentMarker = "--independent-launch";

    internal static ProcessLaunchEnvironment CaptureEnvironment()
    {
        if (!IsProcessInJob(GetCurrentProcess(), 0, out var member))
            return new(false, 0, Marshal.GetLastWin32Error());
        if (!member) return new(false, 0, 0);
        // 扩展限制结构的长度随进程位数变化，限制标志在两种布局中均位于第十六字节。
        var size = IntPtr.Size == 8 ? 144 : 112;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return QueryInformationJobObject(0, 9, buffer, (uint)size, out _) ?
                new(true, unchecked((uint)Marshal.ReadInt32(buffer, 16)), 0) :
                new(true, 0, Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void StartDetached(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("独立启动需要完整程序路径。");
        if (!File.Exists(executable)) throw new Win32Exception(2, "独立启动的程序文件不存在。");
        var parameters = string.Join(' ', arguments.Select(Quote));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            object? shell = null, windows = null, desktop = null, document = null, application = null;
            try
            {
                // 取得现有桌面窗口的自动化对象，不能在工具进程里直接 ShellExecute 后又继承原作业组。
                shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("13709620-C279-11CE-A49E-444553540000"), true)!)!;
                windows = ((dynamic)shell).Windows();
                object location = 0, root = 0; int handle = 0;
                desktop = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out handle, 1);
                if (desktop is null) throw new InvalidOperationException("Windows 桌面 Shell 暂不可用。");
                document = ((dynamic)desktop).Document;
                application = ((dynamic)document).Application;
                ((dynamic)application).ShellExecute(executable, parameters, workingDirectory ?? Path.GetDirectoryName(executable)!, "open", 0);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                foreach (var value in new[] { application, document, desktop, windows, shell })
                    if (value is not null && Marshal.IsComObject(value))
                        try { Marshal.FinalReleaseComObject(value); } catch (InvalidComObjectException) { }
            }
        }) { IsBackground = true, Name = "iSay 独立启动交接" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Windows 桌面 Shell 启动交接超时。");
        if (failure is not null) throw new InvalidOperationException("Windows 桌面 Shell 独立启动失败。", failure);
    }

    internal static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0')) throw new ArgumentException("启动参数不能包含空字符。");
        var value = new StringBuilder("\""); var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { value.Append('\\', slashes * 2 + 1).Append(character); slashes = 0; continue; }
            value.Append('\\', slashes).Append(character); slashes = 0;
        }
        return value.Append('\\', slashes * 2).Append('"').ToString();
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(nint process, nint job, out bool member);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int kind, nint data, uint size, out uint written);
}
