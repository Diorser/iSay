namespace iSay.Voice;

/// <summary>共享的快捷键解析与规范化，不依赖窗口或系统钩子。</summary>
public sealed record HotkeyDefinition(string Canonical, int VirtualKey, uint Modifiers,
    bool IsModifierOnly = false, bool RightAltOnly = false)
{
    public const uint AltModifier = 1, ControlModifier = 2, ShiftModifier = 4, WindowsModifier = 8;

    public static string DisplayName(string canonical) => string.Join(" + ", canonical.Split('+', StringSplitOptions.TrimEntries).Select(key => key switch
    {
        "RightAlt" => "右 Alt 键", "Alt" => "Alt 键", "Space" => "空格键", "Enter" => "回车键",
        "Escape" => "Esc", "Backspace" => "退格键", "Delete" => "删除键", "Insert" => "插入键",
        "Home" => "行首键", "End" => "行尾键", "PageUp" => "向上翻页", "PageDown" => "向下翻页",
        "Up" => "↑", "Down" => "↓", "Left" => "←", "Right" => "→", _ => key
    }));

    private static readonly Dictionary<string, (int Key, string Name)> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = (0x20, "Space"), ["Spacebar"] = (0x20, "Space"),
        ["Enter"] = (0x0D, "Enter"), ["Return"] = (0x0D, "Enter"),
        ["Tab"] = (0x09, "Tab"), ["Escape"] = (0x1B, "Escape"), ["Esc"] = (0x1B, "Escape"),
        ["Backspace"] = (0x08, "Backspace"), ["Delete"] = (0x2E, "Delete"), ["Del"] = (0x2E, "Delete"),
        ["Insert"] = (0x2D, "Insert"), ["Ins"] = (0x2D, "Insert"),
        ["Home"] = (0x24, "Home"), ["End"] = (0x23, "End"),
        ["PageUp"] = (0x21, "PageUp"), ["PageDown"] = (0x22, "PageDown"),
        ["Up"] = (0x26, "Up"), ["Down"] = (0x28, "Down"),
        ["Left"] = (0x25, "Left"), ["Right"] = (0x27, "Right")
    };

    public static HotkeyDefinition Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("请填写录音快捷键。", nameof(value));
        var text = value.Trim();
        if (text.Equals("Alt", StringComparison.OrdinalIgnoreCase)) return new("Alt", 0xA4, 0, true);
        if (text.Equals("RightAlt", StringComparison.OrdinalIgnoreCase)) return new("RightAlt", 0xA5, 0, true, true);
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        int key = 0;
        string? name = null;
        foreach (var part in parts)
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ControlModifier,
                "ALT" => AltModifier,
                "SHIFT" => ShiftModifier,
                "WIN" or "WINDOWS" => WindowsModifier,
                _ => 0u
            };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) throw new ArgumentException("快捷键的修饰键不能重复。", nameof(value));
                modifiers |= modifier;
                continue;
            }
            if (key != 0) throw new ArgumentException("快捷键只能有一个主键，例如 Ctrl+Shift+Space。", nameof(value));
            if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
            {
                name = part.ToUpperInvariant(); key = name[0];
            }
            else if (part.StartsWith('F') || part.StartsWith('f'))
            {
                if (!int.TryParse(part.AsSpan(1), out var number) || number is < 1 or > 24)
                    throw new ArgumentException("功能键应为 F1 至 F24。", nameof(value));
                if (number == 12) throw new ArgumentException("F12 由 Windows 调试器保留，请选择其他快捷键。", nameof(value));
                key = 0x6F + number; name = "F" + number;
            }
            else if (NamedKeys.TryGetValue(part, out var named)) { key = named.Key; name = named.Name; }
            else throw new ArgumentException("不支持的按键。请使用字母、数字、功能键或 Space 等标准键；Fn 通常不能由软件接收。", nameof(value));
        }
        if (key == 0) throw new ArgumentException("组合快捷键需要一个主键，例如 Ctrl+Shift+Space。单独修饰键仅支持 Alt 或 RightAlt。", nameof(value));
        if (modifiers == 0 && (key < 0x70 || key > 0x87))
            throw new ArgumentException("单键仅支持功能键；字母、数字等请搭配 Ctrl、Alt 或 Shift，避免影响正常输入。", nameof(value));
        if (modifiers == AltModifier && key is 0x09 or 0x1B or 0x73 ||
            (modifiers & (ControlModifier | AltModifier)) == (ControlModifier | AltModifier) && key == 0x2E ||
            modifiers == ControlModifier && key == 0x1B ||
            (modifiers & WindowsModifier) != 0 && key == 0x4C)
            throw new ArgumentException("这个组合键由 Windows 保留，请选择其他快捷键。", nameof(value));
        var names = new List<string>();
        if ((modifiers & ControlModifier) != 0) names.Add("Ctrl");
        if ((modifiers & AltModifier) != 0) names.Add("Alt");
        if ((modifiers & ShiftModifier) != 0) names.Add("Shift");
        if ((modifiers & WindowsModifier) != 0) names.Add("Win");
        names.Add(name!);
        return new(string.Join('+', names), key, modifiers);
    }

    public static string NormalizeMode(string mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "toggle" => "toggle",
        "hold" => "hold",
        _ => throw new ArgumentException("录音热键方式应为单击启停或按住说话。", nameof(mode))
    };
}
