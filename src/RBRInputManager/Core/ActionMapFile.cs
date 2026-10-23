using System.IO;

namespace RBRInputManager.Core;

/// <summary>One binding: device index into <see cref="ActionMap.Devices"/>, DirectInput object dwType, RBR action.</summary>
public sealed record ActionBinding(int DeviceIndex, uint ObjectType, int ActionId);

/// <summary>
/// One control map. RBR keeps a separate map for every set of connected controllers: when the
/// set changes (a device is added, removed or gets a new instance GUID) it creates a fresh map
/// with default bindings, which is why controls "reset" after plugging in other hardware.
/// </summary>
public sealed record ActionMap(int Index, IReadOnlyList<Guid> Devices, IReadOnlyList<ActionBinding> Bindings)
{
    /// <summary>Controllers in the map, ignoring the system keyboard and mouse that RBR always adds.</summary>
    public IEnumerable<Guid> Controllers =>
        Devices.Where(g => g != DirectInputNames.SysKeyboard && g != DirectInputNames.SysMouse);

    public bool Matches(IEnumerable<Guid> attachedControllers) => Controllers.ToHashSet().SetEquals(attachedControllers);
}

/// <summary>
/// RBR control bindings, SavedGames\pf&lt;PROFILE&gt;.acm. Little-endian layout:
///   char[4] "PAMA", u32 version (0x300), u32 mapCount,
///   mapCount × {
///     u32 deviceCount, u32 bindingCount,
///     GUID[deviceCount]  (DirectInput instance GUIDs),
///     { u32 deviceIndex, u32 dwType (DIDFT_*), u32 actionId }[bindingCount]
///   }
/// </summary>
public sealed class ActionMapFile
{
    public const uint Magic = 0x414D4150; // "PAMA" read as little-endian u32

    public required string Path { get; init; }
    public uint Version { get; init; }
    public required IReadOnlyList<ActionMap> Maps { get; init; }

    /// <summary>The map RBR uses with the controllers connected right now, if any.</summary>
    public ActionMap? ActiveMap(IEnumerable<Guid> attachedControllers)
    {
        var attached = attachedControllers.ToList();
        return Maps.LastOrDefault(m => m.Matches(attached));
    }

    public static ActionMapFile Load(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        long length = r.BaseStream.Length;
        if (length < 12 || r.ReadUInt32() != Magic)
            throw new InvalidDataException($"{path} is not an RBR controls file (missing PAMA header).");

        uint version = r.ReadUInt32();
        int mapCount = r.ReadInt32();
        if (mapCount < 0 || mapCount > 1000)
            throw new InvalidDataException($"{path}: invalid map count {mapCount}.");

        var maps = new List<ActionMap>(mapCount);
        for (int m = 0; m < mapCount; m++)
        {
            if (r.BaseStream.Position + 8 > length) throw Truncated(path);
            int deviceCount = r.ReadInt32();
            int bindingCount = r.ReadInt32();
            if (deviceCount < 0 || bindingCount < 0 ||
                r.BaseStream.Position + deviceCount * 16L + bindingCount * 12L > length)
                throw Truncated(path);

            var devices = new List<Guid>(deviceCount);
            for (int i = 0; i < deviceCount; i++)
                devices.Add(new Guid(r.ReadBytes(16)));

            var bindings = new List<ActionBinding>(bindingCount);
            for (int i = 0; i < bindingCount; i++)
                bindings.Add(new ActionBinding(r.ReadInt32(), r.ReadUInt32(), r.ReadInt32()));

            maps.Add(new ActionMap(m, devices, bindings));
        }

        if (r.BaseStream.Position != length)
            throw new InvalidDataException($"{path}: {length - r.BaseStream.Position} unexpected bytes after the last map.");

        return new ActionMapFile { Path = path, Version = version, Maps = maps };
    }

    private static InvalidDataException Truncated(string path) => new($"{path} is truncated or corrupt.");

    /// <summary>Serializes the maps in the same layout <see cref="Load"/> reads.</summary>
    public static byte[] Serialize(uint version, IReadOnlyList<ActionMap> maps)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(version);
        w.Write(maps.Count);
        foreach (var map in maps)
        {
            w.Write(map.Devices.Count);
            w.Write(map.Bindings.Count);
            foreach (var g in map.Devices) w.Write(g.ToByteArray());
            foreach (var b in map.Bindings)
            {
                w.Write(b.DeviceIndex);
                w.Write(b.ObjectType);
                w.Write(b.ActionId);
            }
        }
        w.Flush();
        return ms.ToArray();
    }
}
