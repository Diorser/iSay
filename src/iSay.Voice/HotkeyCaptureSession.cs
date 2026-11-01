namespace iSay.Voice;

/// <summary>一次按键录入；等所有键松开才确认，避免把组合键的修饰键提前当成完整快捷键。</summary>
public sealed class HotkeyCaptureSession
{
    private readonly HashSet<int> pressed = [];
    private readonly HashSet<int> seen = [];
    private int primary;
    private uint modifiers;
    private bool invalid, cancelled;

    public HotkeyCaptureResult Process(int key, bool down)
    {
        if (down)
        {
            if (!pressed.Add(key)) return new(HotkeyCaptureStatus.Pending, Display());
            seen.Add(key);
            var modifier = Modifier(key);
            if (modifier != 0) modifiers |= modifier;
            else if (key == 0x1b) cancelled = true;
            else if (primary == 0) primary = key;
            else if (primary != key) invalid = true;
            return new(HotkeyCaptureStatus.Pending, cancelled ? "松开按键取消" : Display());
        }
        if (!pressed.Remove(key) || pressed.Count != 0) return new(HotkeyCaptureStatus.Pending, Display());
        if (cancelled) { Reset(); return new(HotkeyCaptureStatus.Cancelled, "已取消，快捷键未更改"); }
        try
        {
            if (invalid) throw new ArgumentException("一次只能按一个组合，请松开后重试。");
            string gesture;
            if (primary == 0)
            {
                if (seen.Count != 1 || modifiers != HotkeyDefinition.AltModifier)
                    throw new ArgumentException("请加一个字母、空格或功能键；单独修饰键仅支持 Alt。");
                gesture = seen.Contains(0xa5) ? "RightAlt" : "Alt";
            }
            else
            {
                var names = new List<string>();
                if ((modifiers & HotkeyDefinition.ControlModifier) != 0) names.Add("Ctrl");
                if ((modifiers & HotkeyDefinition.AltModifier) != 0) names.Add("Alt");
                if ((modifiers & HotkeyDefinition.ShiftModifier) != 0) names.Add("Shift");
                if ((modifiers & HotkeyDefinition.WindowsModifier) != 0) names.Add("Win");
                names.Add(KeyName(primary));
                gesture = string.Join('+', names);
            }
            var definition = HotkeyDefinition.Parse(gesture);
            return new(HotkeyCaptureStatus.Accepted, HotkeyDefinition.DisplayName(definition.Canonical), definition.Canonical);
        }
        catch (ArgumentException error) { return new(HotkeyCaptureStatus.Invalid, error.Message); }
        finally { Reset(); }
    }

    private string Display()
    {
        var names = new List<string>();
        if ((modifiers & HotkeyDefinition.ControlModifier) != 0) names.Add("Ctrl");
        if ((modifiers & HotkeyDefinition.AltModifier) != 0) names.Add(seen.Contains(0xa5) && primary == 0 ? "RightAlt" : "Alt");
        if ((modifiers & HotkeyDefinition.ShiftModifier) != 0) names.Add("Shift");
        if ((modifiers & HotkeyDefinition.WindowsModifier) != 0) names.Add("Win");
        if (primary != 0) names.Add(KeyName(primary));
        return names.Count == 0 ? "请按下快捷键…" : HotkeyDefinition.DisplayName(string.Join('+', names));
    }

    private void Reset() { pressed.Clear(); seen.Clear(); primary = 0; modifiers = 0; invalid = cancelled = false; }
    private static uint Modifier(int key) => key switch
    {
        0x10 or 0xa0 or 0xa1 => HotkeyDefinition.ShiftModifier,
        0x11 or 0xa2 or 0xa3 => HotkeyDefinition.ControlModifier,
        0x12 or 0xa4 or 0xa5 => HotkeyDefinition.AltModifier,
        0x5b or 0x5c => HotkeyDefinition.WindowsModifier,
        _ => 0
    };
    private static string KeyName(int key) => key switch
    {
        >= 0x70 and <= 0x87 => "F" + (key - 0x6f),
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5a => ((char)key).ToString(),
        0x20 => "Space", 0x0d => "Enter", 0x09 => "Tab", 0x08 => "Backspace", 0x2e => "Delete",
        0x2d => "Insert", 0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        _ => "未知按键"
    };
}

public enum HotkeyCaptureStatus { Pending, Accepted, Invalid, Cancelled }
public sealed record HotkeyCaptureResult(HotkeyCaptureStatus Status, string Display, string? Canonical = null);
