using System.IO;

namespace RBRInputManager.Core;

public sealed record MapCopyResult(int Copied, int Kept, IReadOnlyList<string> Skipped, string BackupPath);

/// <summary>
/// Copies the bindings of one control map into another inside pf&lt;PROFILE&gt;.acm, typically from the
/// map RBR used before a device change into the fresh default map it created afterwards.
///
/// The target map's device list is never changed: RBR picks a map by its exact set of devices, so
/// adding or removing one would make RBR ignore the map and create yet another. Each source binding
/// is moved to the target device with the same instance GUID, or failing that the single target
/// device with the same VID/PID (the same hardware after Windows gave it a new GUID). Bindings whose
/// device is not in the target map are skipped and reported.
/// </summary>
public static class MapCopier
{
    public static MapCopyResult Copy(RbrInstallation rbr, ActionMapFile file, ActionMap source, ActionMap target,
        bool keepTargetOnlyActions, Func<Guid, string?> vidPidOf, Func<Guid, string> nameOf)
    {
        if (source.Index == target.Index) throw new ArgumentException("Source and target are the same map.");

        var deviceMap = new Dictionary<int, int>();
        for (int s = 0; s < source.Devices.Count; s++)
        {
            Guid g = source.Devices[s];
            int t = IndexOf(target.Devices, g);
            if (t < 0 && vidPidOf(g) is { } vp)
            {
                var sameHardware = target.Devices.Select((d, i) => (d, i)).Where(x => vidPidOf(x.d) == vp).ToList();
                if (sameHardware.Count == 1) t = sameHardware[0].i;
            }
            if (t >= 0) deviceMap[s] = t;
        }

        var copied = new List<ActionBinding>();
        var skipped = new List<string>();
        foreach (var b in source.Bindings)
        {
            if (deviceMap.TryGetValue(b.DeviceIndex, out int t))
                copied.Add(b with { DeviceIndex = t });
            else
            {
                string dev = b.DeviceIndex >= 0 && b.DeviceIndex < source.Devices.Count ? nameOf(source.Devices[b.DeviceIndex]) : "unknown device";
                skipped.Add($"{RbrActions.Get(b.ActionId).Name} ({dev})");
            }
        }

        // Optionally keep target bindings for actions the source map leaves unbound.
        var sourceActions = source.Bindings.Select(b => b.ActionId).ToHashSet();
        var kept = keepTargetOnlyActions
            ? target.Bindings.Where(b => !sourceActions.Contains(b.ActionId)).ToList()
            : new List<ActionBinding>();

        var newTarget = target with { Bindings = copied.Concat(kept).OrderBy(b => b.ActionId).ToList() };
        var maps = file.Maps.Select(m => m.Index == target.Index ? newTarget : m).ToList();

        string backup = GameFiles.Backup(rbr, RbrInstallation.AcmFile);
        GameFiles.WriteInPlace(file.Path, ActionMapFile.Serialize(file.Version, maps));

        // Make sure what we wrote reads back as intended before reporting success.
        var check = ActionMapFile.Load(file.Path);
        if (check.Maps.Count != maps.Count || check.Maps[target.Index].Bindings.Count != newTarget.Bindings.Count)
        {
            GameFiles.WriteInPlace(file.Path, File.ReadAllBytes(backup));
            throw new InvalidDataException("The rewritten controls file did not verify; the previous file was put back.");
        }

        return new MapCopyResult(copied.Count, kept.Count, skipped, backup);
    }

    private static int IndexOf(IReadOnlyList<Guid> list, Guid g)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == g) return i;
        return -1;
    }
}
