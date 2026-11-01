using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using iSay.Voice;

namespace iSay.Desktop.Services;

public sealed record MicrophoneDevice(int DeviceNumber, string Name, string? EndpointId, string? IdentityError = null);

public sealed record MicrophoneResolution(bool Success, int DeviceNumber, string? PersistentId, string? Error,
    bool LegacyMigration = false);

/// <summary>每次录音重新将不透明的端点身份解析为当前 WinMM 序号，不按设备名称猜测匹配。</summary>
public sealed class MicrophoneCatalog
{
    public const string DefaultId = "default";
    private const string EndpointPrefix = "winmm-endpoint:";
    private const string UnavailablePrefix = "unavailable-wavein:";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<IReadOnlyList<MicrophoneDevice>> enumerate;
    private readonly Func<int> countDevices;

    public MicrophoneCatalog(Func<IReadOnlyList<MicrophoneDevice>>? enumerate = null, Func<int>? countDevices = null)
    {
        this.enumerate = enumerate ?? ReadSystemDevices;
        this.countDevices = countDevices ?? (enumerate is null ? () => checked((int)Native.waveInGetNumDevs()) : () => ReadSnapshot().Length);
    }

    public IReadOnlyList<MicrophoneInfo> GetChoices()
    {
        var devices = ReadSnapshot();
        var result = new List<MicrophoneInfo> { new(DefaultId, "系统默认麦克风") };
        foreach (var device in devices)
        {
            var usable = ValidEndpoint(device.EndpointId) && device.IdentityError is null;
            result.Add(new(usable ? PersistentId(device.EndpointId!) : UnavailablePrefix + device.DeviceNumber.ToString(CultureInfo.InvariantCulture),
                usable ? device.Name : device.Name + "（设备身份暂不可用）"));
        }
        return result;
    }

    public MicrophoneResolution Resolve(string? configuredId)
    {
        try
        {
            if (configuredId == DefaultId)
            {
                // WaveMapper 自行跟随系统默认，只查询数量，不逐项查询端点，也不缓存易失的设备序号。
                var count = countDevices();
                if (count is < 0 or > 4096) return Failed("麦克风设备数量异常，请刷新设备后重试。");
                return count == 0 ? Failed("未检测到可用麦克风，请连接设备后重试。") : new(true, -1, DefaultId, null);
            }
            if (TryLegacyIndex(configuredId, out _))
                return Failed("该麦克风使用旧版设备编号，请在设置中重新确认设备并保存，避免选择到其他麦克风。");
            if (configuredId?.StartsWith(UnavailablePrefix, StringComparison.Ordinal) == true)
                return Failed("所选麦克风无法取得可靠设备身份，请刷新后重新选择；不会自动切换到其他麦克风。");
            if (!TryEndpoint(configuredId, out var endpoint)) return Failed("麦克风配置无效，请在设置中重新选择。");
            return ResolveEndpoint(ReadSnapshot(), endpoint!, configuredId!);
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return Failed("无法读取麦克风列表，请刷新设备后重试；不会自动切换到其他麦克风。");
        }
    }

    /// <summary>仅用于用户明确确认旧选择时迁移；录音开始不能把旧数字编号静默当成当前设备。</summary>
    public MicrophoneResolution MigrateLegacy(string? legacyId)
    {
        if (!TryLegacyIndex(legacyId, out var index)) return Failed("不是可迁移的旧版麦克风编号，请重新选择设备。");
        try
        {
            var devices = ReadSnapshot();
            var device = devices.SingleOrDefault(value => value.DeviceNumber == index);
            if (device is null) return Failed("旧版麦克风编号已不存在，请重新选择设备；不会改用默认麦克风。");
            if (!ValidEndpoint(device.EndpointId) || device.IdentityError is not null)
                return Failed("旧版麦克风暂时无法取得可靠身份，不能自动迁移，请刷新后重新选择。");
            var resolved = ResolveEndpoint(devices, device.EndpointId!, PersistentId(device.EndpointId!));
            return resolved with { LegacyMigration = resolved.Success };
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return Failed("无法确认旧版麦克风，请刷新设备后重新选择。");
        }
    }

    public static string PersistentId(string endpointId)
    {
        if (!ValidEndpoint(endpointId)) throw new ArgumentException("麦克风端点身份无效。", nameof(endpointId));
        return EndpointPrefix + Convert.ToBase64String(StrictUtf8.GetBytes(endpointId));
    }

    private static MicrophoneResolution ResolveEndpoint(MicrophoneDevice[] devices, string endpoint, string persistentId)
    {
        // 端点身份可能在驱动重装后改变；宁可要求重新选择，也不能按名称或相似字符串换设备。
        var matches = devices.Where(value => string.Equals(value.EndpointId, endpoint, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return Failed("所选麦克风已断开、被禁用或设备身份已变化，请连接后重试或重新选择；不会自动切换到其他麦克风。");
        if (matches.Length != 1) return Failed("麦克风设备身份不唯一，无法安全选择，请刷新设备后重试。");
        var selected = matches[0];
        if (selected.IdentityError is not null) return Failed("所选麦克风身份查询失败，请刷新设备后重试；不会自动切换到其他麦克风。");
        return new(true, selected.DeviceNumber, persistentId, null);
    }

    private MicrophoneDevice[] ReadSnapshot()
    {
        var devices = enumerate()?.ToArray() ?? throw new InvalidOperationException("麦克风列表不可用。");
        if (devices.Any(value => value is null || value.DeviceNumber < 0) || devices.Select(value => value.DeviceNumber).Distinct().Count() != devices.Length)
            throw new InvalidOperationException("麦克风列表包含无效设备编号。");
        return devices;
    }

    private static bool TryLegacyIndex(string? value, out int index) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0;
    private static bool ValidEndpoint(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 16384 && !value.Contains('\0');
    private static bool TryEndpoint(string? configuredId, out string? endpoint)
    {
        endpoint = null;
        if (configuredId is null || !configuredId.StartsWith(EndpointPrefix, StringComparison.Ordinal) || configuredId.Length > 90000) return false;
        try
        {
            var decoded = StrictUtf8.GetString(Convert.FromBase64String(configuredId[EndpointPrefix.Length..]));
            if (!ValidEndpoint(decoded) || PersistentId(decoded) != configuredId) return false;
            endpoint = decoded; return true;
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException or EncoderFallbackException) { return false; }
    }
    private static MicrophoneResolution Failed(string error) => new(false, -1, null, error);

    private static IReadOnlyList<MicrophoneDevice> ReadSystemDevices()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("麦克风枚举仅支持 Windows。");
        var count = Native.waveInGetNumDevs();
        if (count > 4096) throw new InvalidOperationException("麦克风设备数量异常。");
        var devices = new List<MicrophoneDevice>((int)count);
        for (var index = 0; index < count; index++)
        {
            var before = ReadEndpointId(index);
            var result = Native.waveInGetDevCapsW((nuint)index, out var caps, (uint)Marshal.SizeOf<Native.WaveInCaps>());
            var after = ReadEndpointId(index);
            var name = result == 0 && !string.IsNullOrWhiteSpace(caps.Name) ? caps.Name : $"麦克风 {index + 1}";
            // 同一序号查询期间热插拔也可能改变列表，身份前后不一致的项不能成为可固定选择的设备。
            var error = before.Error ?? after.Error ?? (result != 0 ? "设备信息查询失败。" :
                !string.Equals(before.Id, after.Id, StringComparison.OrdinalIgnoreCase) ? "设备列表在查询时发生变化。" : null);
            devices.Add(new(index, name, error is null ? after.Id : null, error));
        }
        return devices;
    }

    private static (string? Id, string? Error) ReadEndpointId(int index)
    {
        nuint bytes = 0;
        var result = Native.waveInMessageSize((nint)index, Native.QueryFunctionInstanceIdSize, ref bytes, 0);
        if (result != 0 || bytes < 4 || bytes > 32768 || (bytes & 1) != 0) return (null, "设备身份长度查询失败。");
        var size = checked((int)bytes);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            result = Native.waveInMessage((nint)index, Native.QueryFunctionInstanceId, buffer, bytes);
            if (result != 0) return (null, "设备身份查询失败。");
            var data = Marshal.PtrToStringUni(buffer, size / 2)!;
            var end = data.IndexOf('\0');
            if (end <= 0) return (null, "设备身份未正确终止。");
            var endpoint = data[..end];
            return ValidEndpoint(endpoint) ? (endpoint, null) : (null, "设备身份为空或无效。");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static class Native
    {
        internal const uint QueryFunctionInstanceId = 0x0800 + 17;
        internal const uint QueryFunctionInstanceIdSize = 0x0800 + 18;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WaveInCaps
        {
            internal ushort Manufacturer, Product;
            internal uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
            internal uint Formats;
            internal ushort Channels, Reserved;
        }
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern uint waveInGetDevCapsW(nuint device, out WaveInCaps capabilities, uint size);
        [DllImport("winmm.dll", EntryPoint = "waveInMessage", ExactSpelling = true)] internal static extern uint waveInMessageSize(nint device, uint message, ref nuint bytes, nuint unused);
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveInMessage(nint device, uint message, nint buffer, nuint bytes);
    }
}
