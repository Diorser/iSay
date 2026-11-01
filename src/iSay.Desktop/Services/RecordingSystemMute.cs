using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace iSay.Desktop.Services;

public interface IPlaybackMuteDevice : IDisposable
{
    string Id { get; }
    bool Muted { get; set; }
}

/// <summary>只改变播放设备的静音状态，不改变音量或麦克风。恢复记录先于系统修改落盘。</summary>
public sealed class RecordingSystemMute(string path, Func<IReadOnlyList<IPlaybackMuteDevice>>? devices = null)
{
    private readonly Func<IReadOnlyList<IPlaybackMuteDevice>> enumerate = devices ?? EnumeratePlayback;
    private readonly Guid owner = Guid.NewGuid();
    private static readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private Process? watchdog;
    private int restorePending;
    private bool capturing;
    public bool NeedsRestore => Volatile.Read(ref restorePending) != 0;

    public void Begin(bool enabled, bool startWatchdog)
    {
        if (!enabled) return;
        Locked(path, () =>
        {
            if (capturing) return;
            RecoverCore(path, null, enumerate);
            Recovery? pending = null;
            if (File.Exists(path))
            {
                pending = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(path)) ?? throw new InvalidDataException("静音恢复记录无效。");
                if (pending.Owner != owner && OwnerAlive(pending)) throw new IOException("其他录音仍在使用系统静音。");
                if (pending.Owner == owner) RecoverCore(path, owner, enumerate);
                pending = File.Exists(path) ? JsonSerializer.Deserialize<Recovery>(File.ReadAllText(path)) : null;
            }
            var outputs = enumerate();
            try
            {
                var state = new Recovery(owner, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                    (pending?.Devices ?? []).Concat(outputs.Where(x => !x.Muted).Select(x => x.Id)).Distinct().ToArray());
                if (state.Devices.Length == 0) return;
                Save(path, state);
                Volatile.Write(ref restorePending, 1);
                capturing = true;
                try
                {
                    if (startWatchdog) EnsureWatchdog();
                    foreach (var output in outputs.Where(x => state.Devices.Contains(x.Id))) output.Muted = true;
                }
                catch { capturing = false; RecoverCore(path, owner, enumerate); throw; }
            }
            finally { foreach (var output in outputs) output.Dispose(); }
        });
    }

    public void Restore()
    {
        if (!NeedsRestore) return;
        Locked(path, () =>
        {
            capturing = false;
            RecoverCore(path, owner, enumerate);
            if (!File.Exists(path)) Volatile.Write(ref restorePending, 0);
        });
    }

    public static void RecoverAbandoned(string path, Func<IReadOnlyList<IPlaybackMuteDevice>>? devices = null)
        => Locked(path, () => RecoverCore(path, null, devices ?? EnumeratePlayback));

    public static void WatchOwner(int processId, long startedTicks, string path)
    {
        try
        {
            using var parent = Process.GetProcessById(processId);
            if (parent.StartTime.ToUniversalTime().Ticks == startedTicks) parent.WaitForExit();
        }
        catch (ArgumentException) { }
        RecoverAbandoned(path);
    }

    private void EnsureWatchdog()
    {
        if (watchdog is { HasExited: false }) return;
        var executable = Path.Combine(AppContext.BaseDirectory, "iSay.AudioRestore.exe");
        if (!File.Exists(executable)) throw new IOException("声音恢复守护程序尚未安装。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "--restore-audio-on-exit", Environment.ProcessId.ToString(), Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(), Path.GetFullPath(path) })
            start.ArgumentList.Add(argument);
        watchdog = Process.Start(start) ?? throw new IOException("声音恢复守护程序未能启动。");
    }

    private static void RecoverCore(string path, Guid? expectedOwner, Func<IReadOnlyList<IPlaybackMuteDevice>> enumerate)
    {
        if (!File.Exists(path)) return;
        var state = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(path)) ?? throw new InvalidDataException("静音恢复记录无效。");
        if (state.Devices is null || state.Devices.Length > 256) throw new InvalidDataException("静音恢复设备记录无效。");
        if (expectedOwner is { } expected && state.Owner != expected) return;
        if (expectedOwner is null && OwnerAlive(state)) return;
        var outputs = enumerate();
        var remaining = new List<string>();
        try
        {
            foreach (var id in state.Devices)
            {
                var output = outputs.FirstOrDefault(x => x.Id == id);
                if (output is null) { remaining.Add(id); continue; }
                try { if (output.Muted) output.Muted = false; }
                catch (Exception) { remaining.Add(id); }
            }
        }
        finally { foreach (var output in outputs) output.Dispose(); }
        if (remaining.Count == 0) File.Delete(path);
        else Save(path, state with { Devices = remaining.ToArray() });
    }

    private static bool OwnerAlive(Recovery state)
    {
        try { using var process = Process.GetProcessById(state.ProcessId); return process.StartTime.ToUniversalTime().Ticks == state.StartedTicks; }
        catch (ArgumentException) { return false; }
    }

    private static void Locked(string path, Action action)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\iSayAudioRecovery_" + key);
        var entered = false;
        try
        {
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new IOException("声音恢复状态正被其他进程处理。");
            action();
        }
        finally { if (entered) mutex.ReleaseMutex(); }
    }

    private static void Save(string path, Recovery state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, json));
        File.Move(path + ".tmp", path, true);
    }

    public static IReadOnlyList<IPlaybackMuteDevice> EnumeratePlayback()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<IPlaybackMuteDevice>();
        try
        {
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in collection) result.Add(new PlaybackDevice(device));
            return result;
        }
        catch { foreach (var device in result) device.Dispose(); throw; }
    }

    private sealed class PlaybackDevice(MMDevice device) : IPlaybackMuteDevice
    {
        public string Id => device.ID;
        public bool Muted { get => device.AudioEndpointVolume.Mute; set => device.AudioEndpointVolume.Mute = value; }
        public void Dispose() => device.Dispose();
    }

    private sealed record Recovery(Guid Owner, int ProcessId, long StartedTicks, string[] Devices);
}
