using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RBRInputManager.Core;

/// <summary>
/// When Windows last saw each USB/HID device, keyed by "VID_xxxx&amp;PID_yyyy". Plug and Play keeps a
/// record of every device ever attached, including the last arrival and removal times.
/// </summary>
public static partial class UsbHistory
{
    private static Dictionary<string, DateTime>? _lastSeen;

    /// <summary>Drops the cached history so the next lookup reads it again.</summary>
    public static void Reset() => _lastSeen = null;

    /// <summary>Most recent time a device with this VID/PID was attached or removed, or null if never seen.</summary>
    public static DateTime? LastSeen(string vidPid)
    {
        _lastSeen ??= Load();
        return _lastSeen.TryGetValue(vidPid.ToUpperInvariant(), out var t) ? t : null;
    }

    [GeneratedRegex(@"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidPattern();

    private static Dictionary<string, DateTime> Load()
    {
        var result = new Dictionary<string, DateTime>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var id = new StringBuilder(512);
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Capacity, out _)) continue;
                var m = VidPidPattern().Match(id.ToString());
                if (!m.Success) continue;

                DateTime? seen = Max(Date(set, ref info, 102), Date(set, ref info, 103)); // LastArrival, LastRemoval
                if (seen is not { } t) continue;
                string key = m.Value.ToUpperInvariant();
                if (!result.TryGetValue(key, out var prev) || t > prev) result[key] = t;
            }
        }
        catch (Exception ex)
        {
            // History is best effort: missing devices just show without a "last seen" date.
            Log.WarnOnce("usbhistory", LogArea.GameControls, "Could not read when Windows last saw each USB device", ex);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static DateTime? Date(IntPtr set, ref SP_DEVINFO_DATA info, uint pid)
    {
        // DEVPKEY_Device_LastArrivalDate / LastRemovalDate: {83da6326-97a6-4088-9453-a1923f573b29}, 102 / 103
        var key = new DEVPROPKEY { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = pid };
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, out long fileTime, 8, out _, 0)
            || type != DEVPROP_TYPE_FILETIME || fileTime <= 0)
            return null;
        return DateTime.FromFileTime(fileTime);
    }

    private const uint DIGCF_ALLCLASSES = 0x4;
    private const uint DEVPROP_TYPE_FILETIME = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA info, StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key,
        out uint type, out long buffer, uint bufferSize, out uint required, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
