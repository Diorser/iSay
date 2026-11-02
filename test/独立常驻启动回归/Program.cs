using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using iSay.Desktop.Services;

internal static class Program
{
    private static readonly List<string> checks=[];
    private static readonly string[] sampleArguments=["", "中文 路径", "引号\"内容", "C:\\末尾目录\\", "两条\\\\\"引号", "一行\n另一行"];
    private static async Task<int> Main(string[] args)
    {
        if(args.Length>=3&&args[0]=="--child")return await Child(args);
        if(args.Length==6&&args[0]=="--parent")return await Parent(args);
        var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../证据",DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(output);
        try
        {
            var current=IndependentProcessLauncher.CaptureEnvironment();
            Check(current.QueryError==0,"读取当前作业组限制成功");
            foreach(var detached in new[]{false,true})await Scenario(output,detached);
            try{IndependentProcessLauncher.StartDetached("relative.exe",[]);throw new Exception("相对路径未被拒绝");}catch(ArgumentException){Check(true,"拒绝非完整程序路径");}
            try{IndependentProcessLauncher.StartDetached(Path.Combine(output,"不存在.exe"),[]);throw new Exception("不存在程序未报错");}catch(System.ComponentModel.Win32Exception){Check(true,"独立创建失败明确返回错误而非报告成功");}
            File.WriteAllText(Path.Combine(output,"结果.json"),JsonSerializer.Serialize(new{通过=true,当前启动环境=current,检查=checks},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"通过 {checks.Count} 项独立常驻启动回归；{output}");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static async Task Scenario(string output,bool detached)
    {
        var ready=Path.Combine(output,detached?"独立子进程.json":"普通子进程.json");
        var suffix=Guid.NewGuid().ToString("N");var stopName="Local\\iSayTestStop_"+suffix;var startName="Local\\iSayTestStart_"+suffix;
        using var stop=new EventWaitHandle(false,EventResetMode.ManualReset,stopName);
        using var start=new EventWaitHandle(false,EventResetMode.ManualReset,startName);
        var job=CreateJobObjectW(0,null);if(job==0)throw new System.ComponentModel.Win32Exception();
        var size=IntPtr.Size==8?144:112;var buffer=Marshal.AllocHGlobal(size);
        Process? child=null;Process? parent=null;
        try
        {
            Marshal.Copy(new byte[size],0,buffer,size);Marshal.WriteInt32(buffer,16,0x2800);
            if(!SetInformationJobObject(job,9,buffer,(uint)size))throw new System.ComponentModel.Win32Exception();
            parent=Start(["--parent",detached?"detached":"normal",ready,stopName,startName,"保留参数"]);
            if(!AssignProcessToJobObject(job,parent.Handle))throw new System.ComponentModel.Win32Exception();
            start.Set();
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while(!File.Exists(ready)){if(parent.HasExited)throw new Exception("隔离启动器提前退出");await Task.Delay(20,deadline.Token);}
            using var data=JsonDocument.Parse(File.ReadAllText(ready));
            child=Process.GetProcessById(data.RootElement.GetProperty("ProcessId").GetInt32());_ = child.Handle;
            var launch=data.RootElement.GetProperty("Environment");
            Check(launch.GetProperty("KillOnJobClose").GetBoolean()!=detached,detached?"独立启动的子进程不继承整组退出限制":"普通启动重现整组退出限制继承");
            Check(data.RootElement.GetProperty("Arguments").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(sampleArguments),"真实进程参数往返保留中文、空参数、引号、换行和末尾反斜杠");
            CloseHandle(job);job=0;
            await parent.WaitForExitAsync(deadline.Token);
            if(detached)
            {
                await Task.Delay(1100,deadline.Token);child.Refresh();
                Check(!child.HasExited,"启动器被整组结束后，独立子进程仍存活");
                Check(File.Exists(ready+".心跳")&&File.GetLastWriteTimeUtc(ready+".心跳")>DateTime.UtcNow.AddSeconds(-2),"启动器消失后子进程仍持续更新心跳");
                stop.Set();await child.WaitForExitAsync(deadline.Token);
                Check(child.ExitCode==0,"独立子进程可自行正常退出，不依赖启动器");
            }
            else {await child.WaitForExitAsync(deadline.Token);Check(child.HasExited,"旧启动方式的子进程确实随作业组关闭而结束");}
        }
        finally{if(job!=0)CloseHandle(job);stop.Set();child?.Dispose();parent?.Dispose();Marshal.FreeHGlobal(buffer);}
    }
    private static async Task<int> Parent(string[] args)
    {
        using var start=EventWaitHandle.OpenExisting(args[4]);if(!start.WaitOne(10000))return 2;
        var childArgs=new[]{"--child",args[2],args[3]}.Concat(sampleArguments).ToArray();
        if(args[1]=="detached")IndependentProcessLauncher.StartDetached(Environment.ProcessPath!,childArgs);
        else using(var child=Start(childArgs)){}
        await Task.Delay(TimeSpan.FromSeconds(20));return 3;
    }
    private static async Task<int> Child(string[] args)
    {
        using var stop=EventWaitHandle.OpenExisting(args[2]);
        var ready=args[1];
        File.WriteAllText(ready+".tmp",JsonSerializer.Serialize(new{ProcessId=Environment.ProcessId,Environment=IndependentProcessLauncher.CaptureEnvironment(),Arguments=args.Skip(3)}));
        File.Move(ready+".tmp",ready);
        var watch=Stopwatch.StartNew();
        while(!stop.WaitOne(0)&&watch.Elapsed.TotalSeconds<20){File.WriteAllText(ready+".心跳",DateTimeOffset.Now.ToString("o"));await Task.Delay(100);}
        return 0;
    }
    private static Process Start(string[] args)
    {
        var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        foreach(var arg in args)info.ArgumentList.Add(arg);return Process.Start(info)!;
    }
    private static void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);Console.WriteLine("通过："+name);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern nint CreateJobObjectW(nint security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(nint job,int kind,nint data,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(nint job,nint process);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(nint handle);
}
