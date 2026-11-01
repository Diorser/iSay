using iSay.Voice;

namespace iSay.Desktop.Services;

/// <summary>不依赖窗口的热键判定，注入事件由调用方过滤。</summary>
public sealed class GlobalHotkeyState
{
    private static readonly int[] ModifierKeys = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];
    private readonly HashSet<int> _pressed = [];
    private readonly HashSet<int> _ignoredUntilRelease = [];
    private readonly Func<int, bool>? _isPhysicalKeyDown;
    private HotkeyDefinition _definition = HotkeyDefinition.Parse("Alt");
    private string _mode = "toggle";
    private bool _reservedPrimary;
    private bool _gestureActive;
    private bool _gestureCancelled;
    private bool _maskAltRelease;
    private int _soloAlt;

    public GlobalHotkeyState(Func<int, bool>? isPhysicalKeyDown = null) => _isPhysicalKeyDown = isPhysicalKeyDown;

    public string Hotkey => _definition.Canonical;
    public string Mode => _mode;
    public bool IsHoldActive => _mode == "hold" && _gestureActive;
    // 单独 F3 没有菜单语义，首次按下即切换；Alt 与组合键仍等完整手势，避免误触。
    private bool ToggleOnPress => _mode == "toggle" && _definition.VirtualKey == 0x72 && _definition.Modifiers == 0;

    public void Reset()
    {
        _pressed.Clear();
        _ignoredUntilRelease.Clear();
        _reservedPrimary = _gestureActive = _gestureCancelled = _maskAltRelease = false;
        _soloAlt = 0;
    }

    public void Configure(string hotkey, string mode = "toggle")
    {
        var definition = HotkeyDefinition.Parse(hotkey);
        var normalizedMode = HotkeyDefinition.NormalizeMode(mode);
        // 保存已经吞下的主键，切换配置后仍吞掉其重复事件与松开，避免泄漏到编辑器。
        if (_reservedPrimary) _ignoredUntilRelease.Add(_definition.IsModifierOnly ? _soloAlt : _definition.VirtualKey);
        _definition = definition;
        _mode = normalizedMode;
        _pressed.Clear();
        _reservedPrimary = _gestureActive = _gestureCancelled = _maskAltRelease = false;
        _soloAlt = 0;
    }

    public HotkeyDecision Process(int virtualKey, bool keyDown)
    {
        if (_ignoredUntilRelease.Contains(virtualKey))
        {
            if (!keyDown) _ignoredUntilRelease.Remove(virtualKey);
            return new(false, true, false);
        }
        RemoveReleasedKeys(virtualKey);
        if (_soloAlt != 0 && !_reservedPrimary && !_pressed.Contains(_soloAlt))
        {
            _soloAlt = 0;
            _gestureActive = false;
        }
        return _definition.IsModifierOnly ? ProcessSoloAlt(virtualKey, keyDown) : ProcessCombination(virtualKey, keyDown);
    }

    private HotkeyDecision ProcessCombination(int key, bool down)
    {
        var primary = _definition.VirtualKey;
        if (down)
        {
            var first = _pressed.Add(key);
            if (_reservedPrimary)
            {
                if (_mode == "toggle" && (!IsAllowedComponent(key) || !ModifiersMatch(key))) _gestureCancelled = true;
                // 主键的系统自动重复不能反复启停，也不能提前放行到输入框。
                if (key == primary) return new(false, true, false);
            }
            if (first && key == primary && !_reservedPrimary && ModifiersMatch(key) && _pressed.All(IsAllowedComponent))
            {
                _reservedPrimary = _gestureActive = true;
                _gestureCancelled = false;
                _maskAltRelease |= (_definition.Modifiers & HotkeyDefinition.AltModifier) != 0;
                return new(ToggleOnPress, true, false) { Start = _mode == "hold" };
            }
            // 必要修饰键漏掉松开事件时，在下一个真实事件上结束按住录音。
            if (_mode == "hold" && _gestureActive && !RequiredModifiersHeld(key))
            {
                _gestureActive = false;
                return new(false, false, false) { Stop = true };
            }
            return default;
        }

        var wasDown = _pressed.Remove(key);
        var suppress = key == primary && _reservedPrimary;
        var completes = _gestureActive && (key == primary && wasDown ||
            (ModifierFlag(key) & _definition.Modifiers) != 0 && !RequiredModifiersHeld(key));
        var action = completes && !_gestureCancelled && !HasUnexpectedModifiers(key);
        var stop = completes && _mode == "hold";
        if (completes) _gestureActive = false;
        if (key == primary) _reservedPrimary = false;
        var mask = key is 0xA4 or 0xA5 && _maskAltRelease && !_pressed.Any(IsAlt);
        if (mask) _maskAltRelease = false;
        return new(action && _mode == "toggle" && !ToggleOnPress, suppress, mask) { Stop = stop };
    }

    private HotkeyDecision ProcessSoloAlt(int key, bool down)
    {
        var isAlt = IsAlt(key);
        if (down)
        {
            var first = _pressed.Add(key);
            if (_soloAlt != 0)
            {
                if (key != _soloAlt && _mode == "toggle") _gestureCancelled = true;
                return new(false, _reservedPrimary && key == _soloAlt, false);
            }
            if (first && isAlt && (!_definition.RightAltOnly || key == 0xA5) &&
                _pressed.Count == 1 && !HasSoloAltConflict(key))
            {
                _soloAlt = key;
                _gestureActive = true;
                _gestureCancelled = false;
                _reservedPrimary = _mode == "hold";
                return new(false, _reservedPrimary, false) { Start = _mode == "hold" };
            }
            return default;
        }
        var wasDown = _pressed.Remove(key);
        if (key != _soloAlt) return default;
        var toggle = wasDown && _gestureActive && !_gestureCancelled && !HasSoloAltConflict(key) && _mode == "toggle";
        var stop = _gestureActive && _mode == "hold";
        var suppress = _reservedPrimary;
        _soloAlt = 0;
        _reservedPrimary = _gestureActive = false;
        return new(toggle, suppress, toggle) { Stop = stop };
    }

    private bool IsAllowedComponent(int key) => key == _definition.VirtualKey ||
        (ModifierFlag(key) != 0 && (ModifierFlag(key) & _definition.Modifiers) != 0);

    private bool ModifiersMatch(int eventKey) => CurrentModifiers(eventKey) == _definition.Modifiers;
    private bool RequiredModifiersHeld(int eventKey) => (CurrentModifiers(eventKey) & _definition.Modifiers) == _definition.Modifiers;
    private bool HasUnexpectedModifiers(int eventKey) => (CurrentModifiers(eventKey) & ~_definition.Modifiers) != 0;

    private bool HasSoloAltConflict(int eventKey) => ModifierKeys.Any(key => key != eventKey &&
        (_pressed.Contains(key) || _isPhysicalKeyDown?.Invoke(key) == true));

    private uint CurrentModifiers(int eventKey)
    {
        uint result = 0;
        foreach (var key in ModifierKeys)
        {
            // 当前钩子事件发生时异步状态尚未更新，必须以已经处理过的事件为准。
            if (_pressed.Contains(key) || key != eventKey && _isPhysicalKeyDown?.Invoke(key) == true)
                result |= ModifierFlag(key);
        }
        return result;
    }

    private void RemoveReleasedKeys(int eventKey)
    {
        if (_isPhysicalKeyDown is null) return;
        // 被吞下的主键不会更新系统异步状态；该键必须等实际松开事件，不能据此误清理。
        _pressed.RemoveWhere(key => key != eventKey && !(_reservedPrimary &&
            key == (_definition.IsModifierOnly ? _soloAlt : _definition.VirtualKey)) && !_isPhysicalKeyDown(key));
    }

    private static bool IsAlt(int key) => key is 0xA4 or 0xA5;
    private static uint ModifierFlag(int key) => key switch
    {
        0xA0 or 0xA1 => HotkeyDefinition.ShiftModifier,
        0xA2 or 0xA3 => HotkeyDefinition.ControlModifier,
        0xA4 or 0xA5 => HotkeyDefinition.AltModifier,
        0x5B or 0x5C => HotkeyDefinition.WindowsModifier,
        _ => 0
    };
}

public readonly record struct HotkeyDecision(bool Toggle, bool Suppress, bool MaskAltMenu)
{
    public bool Start { get; init; }
    public bool Stop { get; init; }
}

public enum RecordingHotkeyAction { Toggle, Start, Stop }

public sealed class RecordingHotkeyEventArgs(RecordingHotkeyAction action) : EventArgs
{
    public RecordingHotkeyAction Action { get; } = action;
}
