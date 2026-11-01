using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace iSay.Desktop;

internal enum TrayIconState { Ready, Recording, Processing }

/// <summary>按托盘实际像素尺寸读取嵌入的三态图标，集中拥有并释放原生图标句柄。</summary>
internal sealed class TrayIconResources : IDisposable
{
    private readonly Dictionary<TrayIconState, OwnedIcon> icons = [];
    public int Size { get; }

    public TrayIconResources(int size)
    {
        Size = size;
        try
        {
            foreach (var (state, file) in new[]
            {
                (TrayIconState.Ready, "托盘待机.ico"),
                (TrayIconState.Recording, "托盘录音.ico"),
                (TrayIconState.Processing, "托盘处理.ico")
            })
            {
                var resource = Application.GetResourceStream(new Uri($"/iSay;component/Assets/{file}", UriKind.Relative))
                    ?? throw new FileNotFoundException("没有找到内置托盘图标：" + file);
                using var stream = resource.Stream;
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                icons.Add(state, CreateIcon(bytes.ToArray(), size));
            }
        }
        catch { Dispose(); throw; }
    }

    public nint Get(TrayIconState state) => icons[state].DangerousGetHandle();

    private static OwnedIcon CreateIcon(byte[] bytes, int size)
    {
        if (bytes.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) != 1)
            throw new InvalidDataException("内置托盘图标格式无效。");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (count == 0 || 6 + count * 16 > bytes.Length) throw new InvalidDataException("内置托盘图标目录不完整。");
        var bestOffset = 0;
        var bestLength = 0;
        var bestScore = long.MaxValue;
        for (var index = 0; index < count; index++)
        {
            var entry = bytes.AsSpan(6 + index * 16, 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            var depth = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (width != height || length == 0 || offset < 6 + count * 16 || (ulong)offset + length > (ulong)bytes.Length) continue;
            // 优先原尺寸，其次选较大的帧缩小，避免把16像素图标放大成模糊边缘。
            var distance = width >= size ? width - size : 10_000 + size - width;
            var score = distance * 100L - depth;
            if (score >= bestScore) continue;
            bestScore = score; bestOffset = (int)offset; bestLength = (int)length;
        }
        if (bestLength == 0) throw new InvalidDataException("内置托盘图标没有可用的图像帧。");
        var frame = bytes.AsSpan(bestOffset, bestLength).ToArray();
        var handle = CreateIconFromResourceEx(frame, (uint)frame.Length, true, 0x00030000, size, size, 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建内置托盘图标。");
        return new OwnedIcon(handle);
    }

    public void Dispose()
    {
        foreach (var icon in icons.Values) icon.Dispose();
        icons.Clear();
    }

    private sealed class OwnedIcon : SafeHandleZeroOrMinusOneIsInvalid
    {
        public OwnedIcon(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateIconFromResourceEx(byte[] bits, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
