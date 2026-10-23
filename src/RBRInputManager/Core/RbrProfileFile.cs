using System.IO;
using System.Text;

namespace RBRInputManager.Core;

/// <summary>
/// Access to SavedGames\pf&lt;PROFILE&gt;.rbr, where RBR keeps its in-game options.
///
/// Encoding, from RichardBurnsRally_SSE.exe (CPC_SaveFileManager): every byte is
/// <c>stored = ~((plain - 0x60) ^ key[i % 500])</c>, decoded as <c>plain = (~stored ^ key[i % 500]) + 0x60</c>.
/// The 500-byte key is fixed in the exe (0x7D12C8). Almost all of the plain text is zero padding, so the key
/// is recovered here from the file itself (the most common byte of each column is an encoded zero) and the
/// checksum confirms it. The plain text is a sequence of sections tagged with reversed four-letter names
/// ("RAEG" = GEAR, ...).
///
/// The first four plain bytes are a checksum of the rest (ReadProfile, 0x49EA80). RBR rejects a profile whose
/// checksum doesn't match and then retries loading it forever, so every write recomputes it.
/// It is a CRC-32 (reflected, init and final XOR 0xFFFFFFFF) whose table RBR builds wrongly (0x490C40): each
/// entry ends up as the bit-reversed index instead of the CRC of the index, i.e. table[i] = reverse8(i) &lt;&lt; 24.
///
/// The in-game "Force Feedback → Strength" slider is the float right after the second GEAR tag's
/// flag field (found by changing the slider in game: 1.0 = 100 %, 0.6 = 60 %).
///
/// The first PROF section holds the profile name (16 bytes, zero padded). RBR's profile list shows the file names
/// (pf&lt;NAME&gt;.rbr without "pf" and ".rbr"), but once a profile is loaded the game uses this stored name to find
/// pf&lt;NAME&gt;.acm and to save pf&lt;NAME&gt;.rbr, so it must always match the file name.
/// </summary>
public sealed class RbrProfileFile
{
    private const int KeyLength = 500;
    private const byte Bias = 0x60;

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256)
        .Select(i => (uint)ReverseBits((byte)i) << 24).ToArray();

    private readonly byte[] _key;
    private readonly byte[] _plain;

    private RbrProfileFile(byte[] key, byte[] plain) => (_key, _plain) = (key, plain);

    /// <summary>True when the stored checksum matches the contents, as RBR requires.</summary>
    public bool ChecksumValid => BitConverter.ToUInt32(_plain, 0) == Checksum(_plain);

    public static RbrProfileFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < KeyLength * 8)
            throw new InvalidDataException($"{path} is too small to be an RBR profile.");

        var key = new byte[KeyLength];
        var counts = new int[256];
        for (int col = 0; col < KeyLength; col++)
        {
            Array.Clear(counts);
            for (int i = col; i < data.Length; i += KeyLength) counts[data[i]]++;
            byte encodedZero = (byte)Array.IndexOf(counts, counts.Max());
            key[col] = (byte)(~encodedZero ^ (0 - Bias)); // solves (~encodedZero ^ key) + 0x60 == 0
        }

        var plain = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) plain[i] = (byte)((~data[i] ^ key[i % KeyLength]) + Bias);
        return new RbrProfileFile(key, plain);
    }

    private byte[] Encode()
    {
        var data = new byte[_plain.Length];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)~((_plain[i] - Bias) ^ _key[i % KeyLength]);
        return data;
    }

    private static uint Checksum(byte[] plain)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 4; i < plain.Length; i++) crc = (crc >> 8) ^ CrcTable[(byte)(plain[i] ^ crc)];
        return ~crc;
    }

    private static byte ReverseBits(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++) r |= ((b >> i) & 1) << (7 - i);
        return (byte)r;
    }

    /// <summary>Where the first write keeps the profile as it was (%AppData%\RBRInputManager\ProfileBackups).</summary>
    public static string BackupPath(string path) =>
        Path.Combine(AppSettings.DataDir, "ProfileBackups", Path.GetFileName(path));

    /// <summary>
    /// Sets the in-game force feedback strength (0.0 – 1.0) and writes the file back with a new checksum.
    /// Refuses a profile whose checksum doesn't verify (unknown encoding), and puts the file back if the
    /// written one doesn't read back correctly.
    /// </summary>
    public static void SetForceFeedbackStrength(string path, float value)
    {
        float strength = Math.Clamp(value, 0f, 1f);
        Modify(path, $"In-game force feedback strength set to {strength:P0}", file =>
        {
            int gear = file.FindTag("GEAR", 1);
            if (gear < 0 || file.ForceFeedbackStrength is null)
                throw new InvalidDataException("Could not find the force feedback strength in the RBR profile.");
            BitConverter.GetBytes(strength).CopyTo(file._plain, gear + 8);
        }, check => check.ForceFeedbackStrength == strength);
    }

    /// <summary>
    /// Stores a new profile name inside the file. RBR builds the file names it saves to (pf&lt;NAME&gt;.rbr / .acm)
    /// from this name, so the files must be renamed to match (see <see cref="RbrInstallation.RenameProfile"/>).
    /// </summary>
    public static void SetName(string path, string name)
    {
        if (name.Length is 0 or > MaxNameLength || name.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException($"A profile name is 1 to {MaxNameLength} plain characters.", nameof(name));
        Modify(path, $"Stored profile name set to {name}", file =>
        {
            int at = file.NameOffset;
            if (at < 0 || file.Name is null)
                throw new InvalidDataException("Could not find the profile name in the RBR profile.");
            Array.Clear(file._plain, at, NameField);
            Encoding.ASCII.GetBytes(name).CopyTo(file._plain, at);
        }, check => check.Name == name);
    }

    /// <summary>
    /// Applies a change to the decoded profile and writes it back with a new checksum. Refuses a profile whose
    /// checksum doesn't verify (unknown encoding), and puts the file back if the written one doesn't read back.
    /// </summary>
    /// <param name="what">What changes, for the log.</param>
    private static void Modify(string path, string what, Action<RbrProfileFile> change, Func<RbrProfileFile, bool> verify)
    {
        var file = Load(path);
        if (!file.ChecksumValid)
            throw new InvalidDataException("The RBR profile's checksum doesn't verify, so it isn't safe to change it here.");
        string? oldName = file.Name;
        change(file);

        byte[] original = File.ReadAllBytes(path);
        string backup = BackupPath(path);
        if (!File.Exists(backup))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.WriteAllBytes(backup, original);
            Log.Info(Log.Context, $"Kept the RBR profile as it was in {backup}");
        }

        BitConverter.GetBytes(Checksum(file._plain)).CopyTo(file._plain, 0);
        GameFiles.WriteInPlace(path, file.Encode(), new[]
        {
            what, $"Profile in the file: {oldName ?? "unknown"}", $"Checksum recomputed: {BitConverter.ToUInt32(file._plain, 0):X8}",
        });

        var check = Load(path);
        if (!check.ChecksumValid || !verify(check))
        {
            GameFiles.WriteInPlace(path, original, new[] { "Put back as it was: the rewritten file did not read back correctly" });
            throw new InvalidDataException("The rewritten RBR profile did not verify; the previous file was put back.");
        }
    }

    /// <summary>Offset of the n-th (0-based) occurrence of a section tag, or -1.</summary>
    private int FindTag(string tag, int occurrence)
    {
        var needle = Encoding.ASCII.GetBytes(new string(tag.Reverse().ToArray()));
        for (int i = 0, seen = 0; i + 4 <= Math.Min(_plain.Length, 0x4000); i += 4)
            if (_plain.AsSpan(i, 4).SequenceEqual(needle) && seen++ == occurrence)
                return i;
        return -1;
    }

    private float? Float(int offset)
    {
        if (offset < 0 || offset + 4 > _plain.Length) return null;
        float v = BitConverter.ToSingle(_plain, offset);
        return float.IsFinite(v) ? v : null;
    }

    /// <summary>In-game force feedback strength, 0.0 – 1.0, or null if it can't be located.</summary>
    public float? ForceFeedbackStrength
    {
        get
        {
            int gear = FindTag("GEAR", 1);
            float? v = gear < 0 ? null : Float(gear + 8);
            return v is >= 0f and <= 1.0001f ? v : null;
        }
    }

    /// <summary>
    /// Longest name RBR keeps: the profile holds 32 bytes, but loading copies only 16 of them, terminator included,
    /// before building the .acm / .rbr file names. Case is stored as typed.
    /// </summary>
    public const int MaxNameLength = 15;
    private const int NameField = 16;

    /// <summary>The name sits in the first PROF section, after its two flag fields.</summary>
    private int NameOffset => FindTag("PROF", 0) is >= 0 and var prof ? prof + 12 : -1;

    /// <summary>The profile name stored in the file (RBR's own name for it), or null if it can't be located.</summary>
    public string? Name
    {
        get
        {
            int at = NameOffset;
            if (at < 0 || at + NameField > _plain.Length) return null;
            var field = _plain.AsSpan(at, NameField);
            int end = field.IndexOf((byte)0) is >= 0 and var z ? z : NameField;
            if (end == 0) return null;
            var name = field[..end];
            return name.ToArray().All(b => b is >= 0x20 and < 0x7F) ? Encoding.ASCII.GetString(name) : null;
        }
    }
}
