using System.Runtime.InteropServices;
using System.Text;

namespace iSay.Desktop.Services;

/// <summary>一次性最终整理的剪贴板事务；完整复制原格式，且只恢复仍属于本次写入的剪贴板。</summary>
internal interface IParagraphPasteLease : IDisposable
{
    bool MayHaveSent { get; }
    bool Paste(Func<bool> guard, CancellationToken token);
}

internal sealed class RefinementClipboardLease : IParagraphPasteLease
{
    private readonly List<Entry> previous = [];
    private readonly List<Entry> next = [];
    private nint owner;
    private uint writtenSequence;
    private bool changed;
    private bool pasteAttempted;
    public bool MayHaveSent { get; private set; }
    internal Action<string, int, int>? Diagnostic { get; set; }
    private sealed class Entry(uint format, nint data)
    {
        internal uint Format = format;
        internal nint Data = data;
    }

    internal static RefinementClipboardLease? TryCreate(string text, Func<bool> guard, CancellationToken token)
    {
        var lease = new RefinementClipboardLease();
        try
        {
            lease.next.Add(new(13, Allocate(Encoding.Unicode.GetBytes(text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n") + "\0"))));
            lease.next.Add(new(RegisterClipboardFormatW("HTML Format"), Allocate(RefinementClipboardFormat.Html(text))));
            // 临时整理结果不进入系统历史或跨设备同步。
            foreach (var name in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                lease.next.Add(new(RegisterClipboardFormatW(name), Allocate(new byte[4])));
            lease.next.Add(new(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), Allocate(BitConverter.GetBytes(1))));
            if (lease.next.Any(e => e.Format == 0 || e.Data == 0)) return null;
            lease.owner = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
            if (lease.owner == 0 || !OpenClipboard(lease.owner)) return null;
            try
            {
                // 任何格式不能完整复制时，保留原剪贴板并放弃自动写回。
                uint format = 0;
                nuint snapshotBytes = 0;
                while (true)
                {
                    SetLastError(0);
                    format = EnumClipboardFormats(format);
                    if (format == 0) { if (Marshal.GetLastWin32Error() != 0) return null; break; }
                    token.ThrowIfCancellationRequested();
                    if (lease.previous.Count >= 128 || format is 0x80 or 0x81 or 0x82 or 0x83 or 0x8e || format is >= 0x200 and <= 0x3ff) return null;
                    var value = GetClipboardData(format);
                    if (value != 0 && format is not (2 or 3 or 9 or 14))
                    {
                        var size = GlobalSize(value);
                        if (size == 0 || size > 64 * 1024 * 1024 || snapshotBytes + size > 128 * 1024 * 1024) return null;
                        snapshotBytes += size;
                    }
                    var copy = value == 0 ? 0 : OleDuplicateData(value, format, 0);
                    if (copy == 0) return null;
                    lease.previous.Add(new(format, copy));
                }
                token.ThrowIfCancellationRequested();
                if (!guard() || !EmptyClipboard()) return null;
                lease.changed = true;
                try
                {
                    foreach (var entry in lease.next)
                    {
                        if (SetClipboardData(entry.Format, entry.Data) == 0) return null;
                        entry.Data = 0;
                    }
                }
                finally { lease.writtenSequence = GetClipboardSequenceNumber(); }
            }
            finally
            {
                CloseClipboard();
                // Windows 在关闭时补充文本派生格式，序号须在关闭后读取；仅接纳仍由本事务持有的剪贴板。
                var closedSequence = GetClipboardSequenceNumber();
                if (lease.changed && GetClipboardOwner() == lease.owner && GetClipboardSequenceNumber() == closedSequence)
                    lease.writtenSequence = closedSequence;
            }
            var result = lease;
            lease = null!;
            return result;
        }
        finally { lease?.Dispose(); }
    }

    public bool Paste(Func<bool> guard, CancellationToken token)
    {
        if (pasteAttempted) return false;
        pasteAttempted = true;
        token.ThrowIfCancellationRequested();
        var currentSequence = GetClipboardSequenceNumber();
        if (!changed || currentSequence != writtenSequence || GetClipboardOwner() != owner)
        { Diagnostic?.Invoke("粘贴剪贴板已变化", (int)writtenSequence, (int)currentSequence); return false; }
        if (!guard()) { Diagnostic?.Invoke("粘贴焦点已变化", 0, 0); return false; }
        var pressed = new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 1, 2, 4 }.FirstOrDefault(k => (GetAsyncKeyState(k) & 0x8000) != 0);
        if (pressed != 0) { Diagnostic?.Invoke("粘贴等待物理松键", pressed, 0); return false; }
        // 同批注入按下与释放，失败或未知结果均不重发。
        var input = new[] { Key(0x11, 0), Key(0x56, 0), Key(0x56, 2), Key(0x11, 2) };
        MayHaveSent = true;
        var sent = SendInput((uint)input.Length, input, Marshal.SizeOf<Input>());
        Diagnostic?.Invoke("粘贴输入数量", (int)sent, Marshal.GetLastWin32Error());
        MayHaveSent = sent != 0;
        if (sent > 0 && sent < input.Length)
        {
            // 部分注入只释放本批按键，不能重发粘贴或遗留按下的 Ctrl。
            var release = new[] { Key(0x56, 2), Key(0x11, 2) };
            SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
        }
        return sent == input.Length;
    }

    public void Dispose()
    {
        // 其他应用短暂占用时有限重试；序号一变立即停止恢复。
        var opened = false;
        for (var attempt = 0; changed && GetClipboardSequenceNumber() == writtenSequence && GetClipboardOwner() == owner && attempt < 8; attempt++)
        {
            if (OpenClipboard(owner)) { opened = true; break; }
            Thread.Sleep(10);
        }
        if (opened)
        {
            try
            {
                // 打开期间再次核对，不能覆盖用户或其他程序后来复制的内容。
                if (GetClipboardSequenceNumber() == writtenSequence && GetClipboardOwner() == owner)
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        if (!EmptyClipboard()) break;
                        var restored = true;
                        foreach (var entry in previous)
                        {
                            // 保留原快照到整批恢复完成，某种格式短暂失败时仍可重建完整批次。
                            var copy = OleDuplicateData(entry.Data, entry.Format, 0);
                            if (copy == 0) { restored = false; break; }
                            if (SetClipboardData(entry.Format, copy) == 0)
                            { Release(new(entry.Format, copy)); restored = false; break; }
                        }
                        if (restored) break;
                    }
            }
            finally { CloseClipboard(); }
        }
        changed = false;
        foreach (var entry in previous.Concat(next)) Release(entry);
        previous.Clear(); next.Clear();
        if (owner != 0) { DestroyWindow(owner); owner = 0; }
    }

    private static nint Allocate(byte[] bytes)
    {
        var memory = GlobalAlloc(0x42, (nuint)bytes.Length);
        if (memory == 0) return 0;
        var address = GlobalLock(memory);
        if (address == 0) { GlobalFree(memory); return 0; }
        try { Marshal.Copy(bytes, 0, address, bytes.Length); }
        finally { GlobalUnlock(memory); }
        return memory;
    }

    private static void Release(Entry entry)
    {
        if (entry.Data == 0) return;
        if (entry.Format is 2 or 9 or 0x82) DeleteObject(entry.Data);
        else if (entry.Format is 14 or 0x8e) DeleteEnhMetaFile(entry.Data);
        else if (entry.Format is 3 or 0x83)
        {
            var address = GlobalLock(entry.Data);
            if (address != 0)
            {
                var picture = Marshal.PtrToStructure<MetaPicture>(address);
                DeleteMetaFile(picture.Handle); GlobalUnlock(entry.Data);
            }
            GlobalFree(entry.Data);
        }
        else GlobalFree(entry.Data);
        entry.Data = 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MetaPicture { internal int Mode, X, Y; internal nint Handle; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] internal Keyboard Keyboard;
        [FieldOffset(0)] internal Mouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    private static Input Key(ushort key, uint flags) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = flags } } };
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("ole32.dll")] private static extern nint OleDuplicateData(nint data, uint format, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint data);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(nint data);
    [DllImport("gdi32.dll")] private static extern bool DeleteMetaFile(nint data);
}
