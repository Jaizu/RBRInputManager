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
        var file = Load(path);
        if (!file.ChecksumValid)
            throw new InvalidDataException("The RBR profile's checksum doesn't verify, so it isn't safe to change it here.");
        int gear = file.FindTag("GEAR", 1);
        if (gear < 0 || file.ForceFeedbackStrength is null)
            throw new InvalidDataException("Could not find the force feedback strength in the RBR profile.");

        byte[] original = File.ReadAllBytes(path);
        string backup = BackupPath(path);
        if (!File.Exists(backup))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.WriteAllBytes(backup, original);
            Log.Info(Log.Context, $"Kept the RBR profile as it was in {backup}");
        }

        float strength = Math.Clamp(value, 0f, 1f);
        BitConverter.GetBytes(strength).CopyTo(file._plain, gear + 8);
        BitConverter.GetBytes(Checksum(file._plain)).CopyTo(file._plain, 0);
        GameFiles.WriteInPlace(path, file.Encode());

        var check = Load(path);
        if (!check.ChecksumValid || check.ForceFeedbackStrength != strength)
        {
            GameFiles.WriteInPlace(path, original);
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
}
