using System.IO;

namespace RBRInputManager.Core;

public sealed record MapAdaptResult(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed, IReadOnlyList<string> Dropped, string BackupPath);

/// <summary>
/// Rewrites a control map's device list so it matches the controllers connected right now, making
/// RBR pick that map again instead of creating a fresh default one. Connected controllers missing
/// from the map are appended (existing device indices stay put); controllers in the map that are not
/// connected are removed and the remaining indices shifted. Bindings on removed devices are dropped
/// and reported. The system keyboard and mouse are always kept.
/// </summary>
public static class MapAdapter
{
    /// <summary>Connected controllers missing from the map and map controllers that are not connected.</summary>
    public static (List<Guid> Add, List<Guid> Remove) Diff(ActionMap map, IEnumerable<Guid> attachedControllers)
    {
        var attached = attachedControllers.ToList();
        var inMap = map.Controllers.ToHashSet();
        return (attached.Where(g => !inMap.Contains(g)).Distinct().ToList(),
                map.Controllers.Where(g => !attached.Contains(g)).Distinct().ToList());
    }

    /// <summary>The map closest to the connected controllers: fewest device changes, then most bindings, then newest.</summary>
    public static ActionMap? Closest(ActionMapFile file, IEnumerable<Guid> attachedControllers)
    {
        var attached = attachedControllers.ToList();
        return file.Maps
            .OrderBy(m => { var (a, r) = Diff(m, attached); return a.Count + r.Count; })
            .ThenByDescending(m => m.Bindings.Count)
            .ThenByDescending(m => m.Index)
            .FirstOrDefault();
    }

    public static MapAdaptResult Adapt(RbrInstallation rbr, ActionMapFile file, ActionMap map,
        IEnumerable<Guid> attachedControllers, Func<Guid, string> nameOf)
    {
        var (add, remove) = Diff(map, attachedControllers);
        if (add.Count == 0 && remove.Count == 0) throw new ArgumentException("The map already matches the connected controllers.");

        var removeSet = remove.ToHashSet();
        var devices = new List<Guid>();
        var indexMap = new Dictionary<int, int>();
        for (int i = 0; i < map.Devices.Count; i++)
        {
            if (removeSet.Contains(map.Devices[i])) continue;
            indexMap[i] = devices.Count;
            devices.Add(map.Devices[i]);
        }
        devices.AddRange(add);

        var bindings = new List<ActionBinding>();
        var dropped = new List<string>();
        bool secondaryGears = RbrActions.HasSecondaryGears(map, rbr.SecondaryGearControls);
        foreach (var b in map.Bindings)
        {
            if (indexMap.TryGetValue(b.DeviceIndex, out int t))
                bindings.Add(b with { DeviceIndex = t });
            else
            {
                string dev = b.DeviceIndex >= 0 && b.DeviceIndex < map.Devices.Count ? nameOf(map.Devices[b.DeviceIndex]) : "unknown device";
                dropped.Add($"{RbrActions.Get(b.ActionId, secondaryGears).Name} ({dev})");
            }
        }

        var adapted = map with { Devices = devices, Bindings = bindings };
        var maps = file.Maps.Select(m => m.Index == map.Index ? adapted : m).ToList();

        string backup = GameFiles.Backup(rbr, rbr.AcmFile);
        GameFiles.WriteInPlace(file.Path, ActionMapFile.Serialize(file.Version, maps));

        // Make sure what we wrote reads back as intended before reporting success.
        var check = ActionMapFile.Load(file.Path);
        if (check.Maps.Count != maps.Count || !check.Maps[map.Index].Matches(attachedControllers) ||
            check.Maps[map.Index].Bindings.Count != bindings.Count)
        {
            GameFiles.WriteInPlace(file.Path, File.ReadAllBytes(backup));
            throw new InvalidDataException("The rewritten controls file did not verify; the previous file was put back.");
        }

        return new MapAdaptResult(add, remove, dropped, backup);
    }
}
