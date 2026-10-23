using System.Globalization;
using Microsoft.Win32;
using Vortice.DirectInput;

namespace RBRInputManager.Core;

public sealed record DeviceInfo(Guid InstanceGuid, string Name, string? VidPid, bool Connected, string Source)
{
    /// <summary>Last time Windows saw this VID/PID attached or removed (only looked up when not connected).</summary>
    public DateTime? LastSeen => Connected || VidPid is null ? null : UsbHistory.LastSeen(VidPid);

    /// <summary>Not connected, but Windows saw it within the last month.</summary>
    public bool RecentlySeen => LastSeen is { } t && DateTime.Now - t < TimeSpan.FromDays(30);

    public string Status => Connected ? "Connected"
        : LastSeen is { } t ? $"Not connected · last seen {t.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} ({Ago(t)})"
        : "Not connected · never seen on this PC";

    private static string Ago(DateTime t)
    {
        var span = DateTime.Now - t;
        if (span.TotalHours < 1) return "just now";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        int days = (int)span.TotalDays;
        return days == 1 ? "yesterday" : days < 60 ? $"{days} days ago" : $"{days / 30} months ago";
    }
}

/// <summary>
/// Resolves DirectInput instance GUIDs to device names. Connected devices come from a live
/// DirectInput enumeration; disconnected ones are looked up in the per-user DirectInput
/// calibration keys, where Windows remembers every instance GUID it ever assigned.
/// </summary>
public sealed class DeviceCatalog : IDisposable
{
    private const string DiKey = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput";
    private const string OemKey = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";

    private readonly IDirectInput8? _di;
    private readonly Dictionary<Guid, DeviceInstance> _attached = new();
    private readonly Dictionary<Guid, (string VidPid, int Index)> _registry = new();
    private readonly Dictionary<Guid, Dictionary<uint, string>> _objectNames = new();

    public string? DirectInputError { get; }

    public IReadOnlyCollection<DeviceInstance> Attached => _attached.Values;
    public IReadOnlyCollection<Guid> AttachedGuids => _attached.Keys;

    public DeviceCatalog()
    {
        try
        {
            _di = DInput.DirectInput8Create();
            foreach (var d in _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
                _attached[d.InstanceGuid] = d;
        }
        catch (Exception ex)
        {
            DirectInputError = ex.Message;
        }
        LoadRegistry();
        UsbHistory.Reset();
    }

    private void LoadRegistry()
    {
        using var root = Registry.CurrentUser.OpenSubKey(DiKey);
        if (root is null) return;
        foreach (var vidPid in root.GetSubKeyNames())
        {
            using var calib = root.OpenSubKey($@"{vidPid}\Calibration");
            if (calib is null) continue;
            foreach (var idx in calib.GetSubKeyNames())
            {
                using var k = calib.OpenSubKey(idx);
                if (k?.GetValue("GUID") is byte[] { Length: 16 } bytes && int.TryParse(idx, out int i))
                    _registry[new Guid(bytes)] = (vidPid, i);
            }
        }
    }

    // Looked up for every row on every refresh; the catalog is rebuilt on reload, so caching here is safe.
    private readonly Dictionary<string, string?> _oemNames = new(StringComparer.OrdinalIgnoreCase);

    private string? OemName(string vidPid)
    {
        if (_oemNames.TryGetValue(vidPid, out var cached)) return cached;
        string? name = null;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = hive.OpenSubKey($@"{OemKey}\{vidPid}");
            if (k?.GetValue("OEMName") is string s && s.Length > 0) { name = s; break; }
        }
        return _oemNames[vidPid] = name;
    }

    public DeviceInfo Resolve(Guid guid)
    {
        if (guid == DirectInputNames.SysKeyboard) return new(guid, "Keyboard", null, true, "System");
        if (guid == DirectInputNames.SysMouse) return new(guid, "Mouse", null, true, "System");

        _registry.TryGetValue(guid, out var reg);
        string? vidPid = reg.VidPid;

        if (_attached.TryGetValue(guid, out var inst))
            return new(guid, inst.InstanceName.Trim(), vidPid, true, "DirectInput");

        if (vidPid is not null)
        {
            string name = OemName(vidPid) ?? vidPid;
            if (reg.Index > 0) name += $" (instance {reg.Index + 1})";
            return new(guid, name, vidPid, false, "Registry");
        }
        return new(guid, "Unknown device", null, false, "—");
    }

    /// <summary>
    /// Resolves a device known only by name (RBRControls binds). Disconnected devices are matched
    /// against the OEM names of every VID/PID DirectInput remembers.
    /// </summary>
    public DeviceInfo ResolveName(string name)
    {
        if (_attached.Values.FirstOrDefault(d => string.Equals(d.InstanceName.Trim(), name, StringComparison.OrdinalIgnoreCase)) is { } inst)
            return new(inst.InstanceGuid, name, _registry.GetValueOrDefault(inst.InstanceGuid).VidPid, true, "DirectInput");

        string? vidPid = _registry.Values.Select(r => r.VidPid).Distinct()
            .FirstOrDefault(v => string.Equals(OemName(v), name, StringComparison.OrdinalIgnoreCase));
        return new(Guid.Empty, name, vidPid, false, vidPid is null ? "—" : "Registry");
    }

    /// <summary>Real object name ("X Axis", "Button 3"...) when the device is connected.</summary>
    public string? ObjectName(Guid device, uint dwType)
    {
        if (_di is null) return null;
        if (!_objectNames.TryGetValue(device, out var map))
        {
            map = new();
            bool known = _attached.ContainsKey(device) || device == DirectInputNames.SysKeyboard;
            if (known)
            {
                try
                {
                    using var dev = _di.CreateDevice(device);
                    foreach (var o in dev.GetObjects(DeviceObjectTypeFlags.All))
                        map[Key((uint)o.ObjectId.Flags, o.ObjectId.InstanceNumber)] = o.Name;
                }
                catch { /* device vanished or is exclusive: fall back to generic names */ }
            }
            _objectNames[device] = map;
        }
        return map.TryGetValue(Key(dwType, DirectInputNames.Instance(dwType)), out var name) ? name : null;
    }

    // Match on object kind (axis/button/pov) + instance; ignore attribute flags such as FFACTUATOR.
    private static uint Key(uint flags, int instance)
    {
        uint kind = (flags & 0x03) != 0 ? 1u : (flags & 0x0C) != 0 ? 2u : (flags & 0x10) != 0 ? 3u : 0u;
        return (kind << 16) | (uint)(instance & 0xFFFF);
    }

    public void Dispose() => _di?.Dispose();
}
