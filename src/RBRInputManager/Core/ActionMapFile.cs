using System.IO;

namespace RBRInputManager.Core;

/// <summary>One binding: device index into <see cref="ActionMapFile.Devices"/>, DirectInput object dwType, RBR action.</summary>
public sealed record ActionBinding(int DeviceIndex, uint ObjectType, int ActionId);

/// <summary>
/// RBR control bindings, SavedGames\pf&lt;PROFILE&gt;.acm. Little-endian layout:
///   char[4] "PAMA", u32 version (0x300), u32 unknown (1), u32 deviceCount, u32 bindingCount,
///   GUID[deviceCount]  (DirectInput instance GUIDs),
///   { u32 deviceIndex, u32 dwType (DIDFT_*), u32 actionId }[bindingCount]
/// Devices are referenced by instance GUID, so when Windows re-enumerates a device
/// (new USB port, driver reinstall...) the GUID changes and RBR drops the binding.
/// </summary>
public sealed class ActionMapFile
{
    public const uint Magic = 0x414D4150; // "PAMA" read as little-endian u32

    public required string Path { get; init; }
    public uint Version { get; init; }
    public uint Unknown { get; init; }
    public required IReadOnlyList<Guid> Devices { get; init; }
    public required IReadOnlyList<ActionBinding> Bindings { get; init; }

    public static ActionMapFile Load(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (r.BaseStream.Length < 20 || r.ReadUInt32() != Magic)
            throw new InvalidDataException($"{path} is not an RBR controls file (missing PAMA header).");

        uint version = r.ReadUInt32();
        uint unknown = r.ReadUInt32();
        int deviceCount = r.ReadInt32();
        int bindingCount = r.ReadInt32();

        long expected = 20 + deviceCount * 16L + bindingCount * 12L;
        if (deviceCount < 0 || bindingCount < 0 || expected != r.BaseStream.Length)
            throw new InvalidDataException(
                $"Unexpected size for {path}: {r.BaseStream.Length} bytes, expected {expected}.");

        var devices = new List<Guid>(deviceCount);
        for (int i = 0; i < deviceCount; i++)
            devices.Add(new Guid(r.ReadBytes(16)));

        var bindings = new List<ActionBinding>(bindingCount);
        for (int i = 0; i < bindingCount; i++)
            bindings.Add(new ActionBinding(r.ReadInt32(), r.ReadUInt32(), r.ReadInt32()));

        return new ActionMapFile { Path = path, Version = version, Unknown = unknown, Devices = devices, Bindings = bindings };
    }
}
