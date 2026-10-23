using System.Text.RegularExpressions;

namespace RBRInputManager.Core;

public sealed record PluginBinding(
    string Key, string Page, string Function, string RawBinding,
    string Device, string Input, string Trigger, string GameMode)
{
    public bool IsBound => RawBinding.Length > 0;
}

/// <summary>
/// Plugins\RBRControls\RBRControls.ini. Binds are stored by device *name*, not GUID:
///   "#&lt;button&gt; &lt;Device name&gt;"  (0-based button index)
///   "K&lt;dik&gt; Keyboard"             (DirectInput scan code)
/// </summary>
public static partial class RbrControlsConfig
{
    public const string KeyboardDevice = "Keyboard";

    private static readonly (string Prefix, string Page, string Name)[] Functions =
    {
        ("JOYKEY_UP", "JoyKey · Menu", "Up arrow"),
        ("JOYKEY_DOWN", "JoyKey · Menu", "Down arrow"),
        ("JOYKEY_LEFT", "JoyKey · Menu", "Left arrow"),
        ("JOYKEY_RIGHT", "JoyKey · Menu", "Right arrow"),
        ("JOYKEY_ESC", "JoyKey · Menu", "Esc / pause"),
        ("JOYKEY_BACK", "JoyKey · Menu", "Backspace"),
        ("JOYKEY_ENTER", "JoyKey · Menu", "Enter"),
        ("JOYKEY_SPACE", "JoyKey · Menu", "Space (favorites)"),
        ("JOYKEY_LSHIFT", "JoyKey · Menu", "Left Shift"),
        ("JOYKEY_LCTRL_S", "JoyKey · Menu", "Ctrl+S (save setup)"),
        ("JOYKEY_LCTRL", "JoyKey · Menu", "Left Ctrl"),
        ("JOYKEY_PAGEUP", "JoyKey · Menu", "Page Up"),
        ("JOYKEY_PAGEDOWN", "JoyKey · Menu", "Page Down"),
        ("JOYKEY_HOME", "JoyKey · Menu", "Home"),
        ("JOYKEY_END", "JoyKey · Menu", "End"),
        ("JOYKEY_DEL", "JoyKey · Menu", "Delete"),
        ("JOYKEY_IGNITION", "JoyKey · Commands", "Ignition"),
        ("JOYKEY_RESTART_STAGE", "JoyKey · Commands", "Restart stage"),
        ("JOYKEY_CALLFORHELP", "JoyKey · Commands", "Call for help"),
        ("JOYKEY_RETIRE_STAGE", "JoyKey · Commands", "Retire stage"),
        ("JOYKEY_EXIT_RBR", "JoyKey · Commands", "Exit RBR"),
        ("JOYKEY_F1", "JoyKey · Commands", "F1 (tyre data)"),
        ("JOYKEY_F9", "JoyKey · Commands", "F9 (free camera)"),
        ("JOYKEY_F11", "JoyKey · Commands", "F11 (screenshot)"),
        ("JOYKEY_NUM4", "JoyKey · Numpad", "Num 4"),
        ("JOYKEY_NUM6", "JoyKey · Numpad", "Num 6"),
        ("JOYKEY_NUM8", "JoyKey · Numpad", "Num 8"),
        ("JOYKEY_NUM2", "JoyKey · Numpad", "Num 2"),
        ("JOYKEY_NUM0", "JoyKey · Numpad", "Num 0"),
        ("JOYKEY_NUMADD", "JoyKey · Numpad", "Num +"),
        ("JOYKEY_NUMSUB", "JoyKey · Numpad", "Num -"),
        ("JOYKEY_MULTIPLY", "JoyKey · Numpad", "Num *"),
        ("JOYKEY_CUSTOM1", "JoyKey · Custom", "Custom 1"),
        ("JOYKEY_CUSTOM2", "JoyKey · Custom", "Custom 2"),
        ("JOYKEY_CUSTOM3", "JoyKey · Custom", "Custom 3"),
        ("JOYKEY_CALLOUT_INC", "JoyKey · Pacenotes", "Callout distance +0.5"),
        ("JOYKEY_CALLOUT_DEC", "JoyKey · Pacenotes", "Callout distance -0.5"),
        ("HSHIFTER_GEAR_1", "HShifter", "1st"),
        ("HSHIFTER_GEAR_2", "HShifter", "2nd"),
        ("HSHIFTER_GEAR_3", "HShifter", "3rd"),
        ("HSHIFTER_GEAR_4", "HShifter", "4th"),
        ("HSHIFTER_GEAR_5", "HShifter", "5th"),
        ("HSHIFTER_GEAR_6", "HShifter", "6th"),
        ("HSHIFTER_GEAR_R", "HShifter", "Reverse"),
        ("HSHIFTER_SEQUENTIAL_NEUTRAL", "HShifter", "Sequential: neutral"),
        ("HSHIFTER_SEQUENTIAL_UP", "HShifter", "Sequential: up"),
        ("HSHIFTER_SEQUENTIAL_DOWN", "HShifter", "Sequential: down"),
        ("HSHIFTER_TOGGLE_MODE", "HShifter", "Toggle H/sequential mode"),
        ("JOYKEY_VR_RESETVIEW", "VR", "Reset view"),
        ("JOYKEY_VR_TOGGLEDEBUG", "VR", "Toggle VR debug"),
        ("JOYKEY_VR_MOVE_SEAT_FORWARD", "VR", "Seat forward"),
        ("JOYKEY_VR_MOVE_SEAT_BACKWARD", "VR", "Seat backward"),
        ("JOYKEY_VR_MOVE_SEAT_LEFT", "VR", "Seat left"),
        ("JOYKEY_VR_MOVE_SEAT_RIGHT", "VR", "Seat right"),
        ("JOYKEY_VR_MOVE_SEAT_UP", "VR", "Seat up"),
        ("JOYKEY_VR_MOVE_SEAT_DOWN", "VR", "Seat down"),
    };

    private static readonly string[] Suffixes = { "_TRIGGERTYPE", "_GAMEMODE", "_KEYCODE" };

    [GeneratedRegex(@"^#(\d+)\s+(.+)$")] private static partial Regex ButtonBind();
    [GeneratedRegex(@"^K(\d+)\s+(.+)$")] private static partial Regex KeyBind();

    public static List<PluginBinding> LoadBindings(IniFile ini)
    {
        var section = ini.Section("KeyBind");
        var result = new List<PluginBinding>();

        foreach (var kv in section)
        {
            if (Suffixes.Any(s => kv.Key.EndsWith(s, StringComparison.OrdinalIgnoreCase))) continue;

            var fn = Functions.FirstOrDefault(f => string.Equals(f.Prefix, kv.Key, StringComparison.OrdinalIgnoreCase));
            string page = fn.Page ?? "Other";
            string name = fn.Name ?? kv.Key;

            var (device, input) = ParseBind(kv.Value);
            if (ini.Get("KeyBind", kv.Key + "_KEYCODE") is { Length: > 0 } keycode)
                name += $" (key {keycode})";

            bool isShifter = kv.Key.StartsWith("HSHIFTER", StringComparison.OrdinalIgnoreCase);
            string trigger = isShifter ? "" : TriggerName(ini.Get("KeyBind", kv.Key + "_TRIGGERTYPE"));
            string mode = isShifter ? "" : GameModeName(ini.Get("KeyBind", kv.Key + "_GAMEMODE"));

            result.Add(new PluginBinding(kv.Key, page, name, kv.Value, device, input, trigger, mode));
        }
        return result;
    }

    public static (string Device, string Input) ParseBind(string raw)
    {
        if (raw.Length == 0) return ("", "");
        var m = ButtonBind().Match(raw);
        if (m.Success) return (m.Groups[2].Value, $"Button {int.Parse(m.Groups[1].Value) + 1}");
        m = KeyBind().Match(raw);
        if (m.Success) return (KeyboardDevice, $"Key {DirectInputNames.KeyName(int.Parse(m.Groups[1].Value))}");
        return ("", raw);
    }

    private static string TriggerName(string? v) => v switch
    {
        "0" => "Instant", "1" => "Short press", "2" => "Long press", _ => v ?? "",
    };

    private static string GameModeName(string? v) => v switch
    {
        "0" => "Always", "1" => "Driving only", "2" => "Menus only", _ => v ?? "",
    };

    public static List<SettingRow> LoadShifterSettings(IniFile ini)
    {
        string? Get(string k) => ini.Get("HShifter", k);
        string Map(string? v, params string[] names) =>
            int.TryParse(v, out int i) && i >= 0 && i < names.Length ? $"{names[i]} ({v})" : v ?? "—";

        return new()
        {
            new("HShifter", "Enabled", Get("Enabled"), null, "[HShifter] Enabled", ""),
            new("HShifter", "Type", Map(Get("Type"), "Sequential", "H-pattern", "Auto per car"), null, "[HShifter] Type", ""),
            new("HShifter", "H-pattern behavior", Map(Get("Mode"), "Normal (NGP)", "Advanced"), null, "[HShifter] Mode", ""),
            new("HShifter", "Gear engagement", Map(Get("Engagement"), "Auto per car", "Synchro", "Dogbox"), null, "[HShifter] Engagement", ""),
            new("HShifter", "Dogleg", Map(Get("Dogleg"), "Off", "Auto per car"), null, "[HShifter] Dogleg", ""),
            new("HShifter", "Auto neutral", Get("AutoNeutral"), null, "[HShifter] AutoNeutral", ""),
            new("HShifter", "Clutch required", Get("ClutchRequired"), null, "[HShifter] ClutchRequired", ""),
            new("HShifter", "Clutch threshold", Get("ClutchThreshold"), null, "[HShifter] ClutchThreshold", "0.0 – 1.0"),
            new("HShifter", "Shift sound volume", Get("SoundVolume"), null, "[HShifter] SoundVolume", ""),
            new("HShifter", "Shift type notice volume", Get("SoundVolumeShiftType"), null, "[HShifter] SoundVolumeShiftType", ""),
            new("JoyKey", "Enabled", ini.Get("JoyKey", "Enabled"), null, "[JoyKey] Enabled", ""),
        };
    }
}
