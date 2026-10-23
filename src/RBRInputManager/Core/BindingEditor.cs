using System.IO;

namespace RBRInputManager.Core;

/// <summary>
/// Pending edits to the game bindings (pf&lt;PROFILE&gt;.acm), the axis invert flags (input.ini) and the
/// RBRControls binds. Nothing touches the game until <see cref="Save"/>, which backs up the files and writes them together.
/// </summary>
public sealed class BindingEditor
{
    private readonly ActionMapFile _file;
    private readonly List<ActionMap> _maps;
    private readonly Dictionary<string, string> _pluginOriginal;
    private readonly Dictionary<string, string> _pluginPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _invertOriginal;
    private readonly Dictionary<string, bool> _invertPending = new(StringComparer.OrdinalIgnoreCase);

    public BindingEditor(ActionMapFile file, IReadOnlyDictionary<string, string> pluginBinds, IReadOnlyDictionary<string, bool> inverted)
    {
        _file = file;
        _maps = file.Maps.ToList();
        _pluginOriginal = new(pluginBinds, StringComparer.OrdinalIgnoreCase);
        _invertOriginal = new(inverted, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ActionMap> Maps => _maps;

    /// <summary>Changed actions (per map), RBRControls binds and invert flags: one per entry of <see cref="Changes"/>.</summary>
    public int ChangeCount =>
        _maps.Sum(m => ChangedActions(m.Index).Count()) +
        ChangedPluginKeys.Count() +
        _invertPending.Keys.Count(IsInvertChanged);

    public bool IsDirty => ChangeCount > 0;

    private IEnumerable<int> ChangedActions(int map) =>
        _maps[map].Bindings.Concat(_file.Maps[map].Bindings).Select(b => b.ActionId).Distinct().OrderBy(a => a)
            .Where(a => IsActionChanged(map, a));

    private IEnumerable<string> ChangedPluginKeys => _pluginPending.Keys.Where(IsPluginChanged);

    /// <summary>
    /// Every pending edit as readable text (Profile = saved in the game, Game = after saving).
    /// <paramref name="actionName"/> gets the map and action id, <paramref name="input"/> a device and DirectInput dwType.
    /// </summary>
    public List<ProfileDifference> Changes(Func<int, int, string> actionName, Func<Guid, string> deviceName,
        Func<Guid, uint, string> input, Func<string, string> pluginName)
    {
        var result = new List<ProfileDifference>();
        bool manyMaps = _maps.Count > 1;
        foreach (var m in _maps)
            foreach (int a in ChangedActions(m.Index))
                result.Add(new("Game controls", (manyMaps ? $"Map {m.Index + 1} · " : "") + actionName(m.Index, a),
                    Describe(_file.Maps[m.Index], a), Describe(m, a)));

        foreach (var key in RbrActions.All.Where(a => a.InvertKey is not null).Select(a => a.InvertKey!).Distinct().Where(IsInvertChanged))
        {
            string name = RbrActions.All.First(a => a.InvertKey == key).Name;
            result.Add(new("Axis inversion", name, _invertOriginal.GetValueOrDefault(key) ? "Inverted" : "Normal",
                IsInverted(key) ? "Inverted" : "Normal"));
        }

        foreach (var key in ChangedPluginKeys)
            result.Add(new("RBRControls", pluginName(key), PluginText(_pluginOriginal.GetValueOrDefault(key, "")), PluginText(PluginValue(key))));
        return result;

        string Describe(ActionMap m, int actionId)
        {
            var list = m.Bindings.Where(b => b.ActionId == actionId)
                .Select(b => b.DeviceIndex >= 0 && b.DeviceIndex < m.Devices.Count
                    ? $"{deviceName(m.Devices[b.DeviceIndex])}: {input(m.Devices[b.DeviceIndex], b.ObjectType)}"
                    : $"Invalid device · 0x{b.ObjectType:X8}")
                .ToList();
            return list.Count == 0 ? "Unbound" : string.Join("\n", list);
        }

        static string PluginText(string raw)
        {
            if (raw.Length == 0) return "Unbound";
            var (device, inputText) = RbrControlsConfig.ParseBind(raw);
            return device.Length > 0 ? $"{device}: {inputText}" : raw;
        }
    }

    // ------------------------------------------------------------ game bindings

    /// <summary>True when an action's bindings in a map differ from what is saved in the game.</summary>
    public bool IsActionChanged(int map, int actionId) =>
        !Inputs(_maps[map], actionId).SetEquals(Inputs(_file.Maps[map], actionId));

    private static HashSet<(Guid, uint)> Inputs(ActionMap m, int actionId) =>
        m.Bindings.Where(b => b.ActionId == actionId && b.DeviceIndex >= 0 && b.DeviceIndex < m.Devices.Count)
            .Select(b => (m.Devices[b.DeviceIndex], b.ObjectType)).ToHashSet();

    private static bool SameBindings(ActionMap a, ActionMap b) =>
        a.Bindings.Count == b.Bindings.Count && a.Bindings.SequenceEqual(b.Bindings);

    /// <summary>Actions (other than <paramref name="exceptAction"/>) that already use this input in the map.</summary>
    public List<int> Conflicts(int map, CapturedInput input, int exceptAction)
    {
        var m = _maps[map];
        int dev = IndexOf(m.Devices, input.Device);
        return m.Bindings.Where(b => b.DeviceIndex == dev && b.ObjectType == input.ObjectType && b.ActionId != exceptAction)
            .Select(b => b.ActionId).Distinct().ToList();
    }

    /// <summary>
    /// Binds an input to an action. <paramref name="replace"/> = the binding (index in the map) to replace,
    /// or null to add alongside existing ones. Inputs used by <paramref name="removeFrom"/> actions are unbound there.
    /// </summary>
    public void Bind(int map, int actionId, CapturedInput input, int? replace, IEnumerable<int> removeFrom)
    {
        var m = _maps[map];
        int dev = IndexOf(m.Devices, input.Device);
        if (dev < 0)
            throw new InvalidOperationException(
                $"{input.DeviceName} is not part of map {map + 1}. RBR picks a map by its exact set of devices, " +
                "so adding one would make RBR ignore this map. Bind it in the map that is active with this device connected.");

        var remove = removeFrom.ToHashSet();
        var bindings = new List<ActionBinding>();
        for (int i = 0; i < m.Bindings.Count; i++)
        {
            var b = m.Bindings[i];
            if (i == replace) continue;
            if (b.DeviceIndex == dev && b.ObjectType == input.ObjectType && (remove.Contains(b.ActionId) || b.ActionId == actionId))
                continue; // moved to this action, or would be a duplicate
            bindings.Add(b);
        }
        bindings.Add(new ActionBinding(dev, input.ObjectType, actionId));
        _maps[map] = m with { Bindings = bindings.OrderBy(b => b.ActionId).ToList() };
    }

    public void Unbind(int map, int bindingIndex)
    {
        var m = _maps[map];
        _maps[map] = m with { Bindings = m.Bindings.Where((_, i) => i != bindingIndex).ToList() };
    }

    private static int IndexOf(IReadOnlyList<Guid> list, Guid g)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == g) return i;
        return -1;
    }

    // ------------------------------------------------------------ axis inversion (input.ini, shared by every map)

    /// <summary>input.ini [Settings] flags such as InvertThrottleAxis, read as RBR does (only "true" inverts).</summary>
    public static Dictionary<string, bool> LoadInverted(RbrInstallation rbr)
    {
        var input = IniFile.TryLoad(rbr.InputIni);
        return RbrActions.All.Where(a => a.InvertKey is not null).Select(a => a.InvertKey!).Distinct()
            .ToDictionary(k => k, k => string.Equals(input?.Get("Settings", k), "true", StringComparison.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    public bool IsInverted(string key) =>
        _invertPending.TryGetValue(key, out var v) ? v : _invertOriginal.GetValueOrDefault(key);

    public bool IsInvertChanged(string key) => IsInverted(key) != _invertOriginal.GetValueOrDefault(key);

    public void SetInverted(string key, bool inverted) => _invertPending[key] = inverted;

    // ------------------------------------------------------------ RBRControls

    public string PluginValue(string key) =>
        _pluginPending.TryGetValue(key, out var v) ? v : _pluginOriginal.GetValueOrDefault(key, "");

    public bool IsPluginChanged(string key) =>
        !string.Equals(PluginValue(key), _pluginOriginal.GetValueOrDefault(key, ""), StringComparison.Ordinal);

    /// <summary>RBRControls format: "#&lt;button&gt; &lt;device name&gt;" or "K&lt;dik&gt; Keyboard".</summary>
    public static string PluginBindValue(CapturedInput input) =>
        input.IsKeyboard ? $"K{input.Instance} Keyboard" : $"#{input.Instance} {input.DeviceName}";

    /// <summary>Other RBRControls functions already bound to the same value.</summary>
    public List<string> PluginConflicts(string value, string exceptKey) =>
        _pluginOriginal.Keys.Concat(_pluginPending.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !string.Equals(k, exceptKey, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(PluginValue(k), value, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public void SetPlugin(string key, string value) => _pluginPending[key] = value;

    // ------------------------------------------------------------ save

    /// <summary>Backs up and writes whatever changed. Returns the backup folder, or null if nothing was written.</summary>
    public string? Save(RbrInstallation rbr)
    {
        bool acmChanged = _maps.Any(m => !SameBindings(m, _file.Maps[m.Index]));
        var pluginChanges = _pluginPending
            .Where(kv => !string.Equals(kv.Value, _pluginOriginal.GetValueOrDefault(kv.Key, ""), StringComparison.Ordinal))
            .ToList();
        var invertChanges = _invertPending.Keys.Where(IsInvertChanged).ToList();
        if (!acmChanged && pluginChanges.Count == 0 && invertChanges.Count == 0) return null;

        string? backup = null;
        if (acmChanged)
        {
            backup = GameFiles.Backup(rbr, rbr.AcmFile);
            GameFiles.WriteInPlace(_file.Path, ActionMapFile.Serialize(_file.Version, _maps));
            var check = ActionMapFile.Load(_file.Path);
            if (check.Maps.Count != _maps.Count || check.Maps.Where((m, i) => !SameBindings(m, _maps[i])).Any())
            {
                GameFiles.WriteInPlace(_file.Path, File.ReadAllBytes(backup));
                throw new InvalidDataException("The rewritten controls file did not verify; the previous file was put back.");
            }
        }
        if (pluginChanges.Count > 0)
        {
            backup = GameFiles.Backup(rbr, RbrInstallation.RbrControlsIniFile);
            GameFiles.SetIniValues(rbr.RbrControlsIni, pluginChanges.Select(kv => ("KeyBind", kv.Key, kv.Value)));
        }
        if (invertChanges.Count > 0)
        {
            if (File.Exists(rbr.InputIni)) backup = GameFiles.Backup(rbr, RbrInstallation.InputIniFile);
            GameFiles.SetIniValues(rbr.InputIni, invertChanges.Select(k => ("Settings", k, IsInverted(k) ? "true" : "false")));
        }
        return backup is null ? null : GameFiles.BackupFolder(backup);
    }
}
