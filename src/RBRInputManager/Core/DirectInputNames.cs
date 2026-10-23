namespace RBRInputManager.Core;

/// <summary>Fallback names for DirectInput objects when the device is not connected.</summary>
public static class DirectInputNames
{
    public static readonly Guid SysKeyboard = new("6F1D2B61-D5A0-11CF-BFC7-444553540000");
    public static readonly Guid SysMouse = new("6F1D2B60-D5A0-11CF-BFC7-444553540000");

    // DIDFT_* flags
    private const uint AxisMask = 0x03, ButtonMask = 0x0C, Pov = 0x10, FfActuator = 0x01000000;

    public static int Instance(uint dwType) => (int)((dwType >> 8) & 0xFFFF);

    public static string Describe(uint dwType, bool isKeyboard)
    {
        int n = Instance(dwType);
        if (isKeyboard) return $"Key {KeyName(n)}";

        string ff = (dwType & FfActuator) != 0 ? " · FFB" : "";
        if ((dwType & AxisMask) != 0) return $"Axis {n}{ff}";
        if ((dwType & ButtonMask) != 0) return $"Button {n + 1}";
        if ((dwType & Pov) != 0) return $"POV {n}";
        return $"Object 0x{dwType:X8}";
    }

    /// <summary>DIK_* scan codes (same numbering RBRControls uses for its "K&lt;n&gt; Keyboard" binds).</summary>
    public static string KeyName(int dik) => Keys.TryGetValue(dik, out var s) ? s : $"0x{dik:X2}";

    private static readonly Dictionary<int, string> Keys = new()
    {
        [0x01] = "Esc", [0x02] = "1", [0x03] = "2", [0x04] = "3", [0x05] = "4", [0x06] = "5", [0x07] = "6",
        [0x08] = "7", [0x09] = "8", [0x0A] = "9", [0x0B] = "0", [0x0C] = "-", [0x0D] = "=", [0x0E] = "Backspace",
        [0x0F] = "Tab", [0x10] = "Q", [0x11] = "W", [0x12] = "E", [0x13] = "R", [0x14] = "T", [0x15] = "Y",
        [0x16] = "U", [0x17] = "I", [0x18] = "O", [0x19] = "P", [0x1A] = "[", [0x1B] = "]", [0x1C] = "Enter",
        [0x1D] = "Left Ctrl", [0x1E] = "A", [0x1F] = "S", [0x20] = "D", [0x21] = "F", [0x22] = "G", [0x23] = "H",
        [0x24] = "J", [0x25] = "K", [0x26] = "L", [0x27] = ";", [0x28] = "'", [0x29] = "`", [0x2A] = "Left Shift",
        [0x2B] = "\\", [0x2C] = "Z", [0x2D] = "X", [0x2E] = "C", [0x2F] = "V", [0x30] = "B", [0x31] = "N",
        [0x32] = "M", [0x33] = ",", [0x34] = ".", [0x35] = "/", [0x36] = "Right Shift", [0x37] = "Num *",
        [0x38] = "Left Alt", [0x39] = "Space", [0x3A] = "Caps Lock", [0x3B] = "F1", [0x3C] = "F2", [0x3D] = "F3",
        [0x3E] = "F4", [0x3F] = "F5", [0x40] = "F6", [0x41] = "F7", [0x42] = "F8", [0x43] = "F9", [0x44] = "F10",
        [0x45] = "Num Lock", [0x46] = "Scroll Lock", [0x47] = "Num 7", [0x48] = "Num 8", [0x49] = "Num 9",
        [0x4A] = "Num -", [0x4B] = "Num 4", [0x4C] = "Num 5", [0x4D] = "Num 6", [0x4E] = "Num +", [0x4F] = "Num 1",
        [0x50] = "Num 2", [0x51] = "Num 3", [0x52] = "Num 0", [0x53] = "Num .", [0x57] = "F11", [0x58] = "F12",
        [0x9C] = "Num Enter", [0x9D] = "Right Ctrl", [0xB5] = "Num /", [0xB7] = "Print Screen", [0xB8] = "Right Alt",
        [0xC5] = "Pause", [0xC7] = "Home", [0xC8] = "Up Arrow", [0xC9] = "Page Up", [0xCB] = "Left Arrow",
        [0xCD] = "Right Arrow", [0xCF] = "End", [0xD0] = "Down Arrow", [0xD1] = "Page Down", [0xD2] = "Insert",
        [0xD3] = "Delete",
    };
}
