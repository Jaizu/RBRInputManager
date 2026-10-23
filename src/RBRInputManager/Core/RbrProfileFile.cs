using System.IO;
using System.Text;

namespace RBRInputManager.Core;

/// <summary>
/// Access to SavedGames\pf&lt;PROFILE&gt;.rbr, where RBR keeps its in-game options.
///
/// The file is XOR-ed with a 500-byte repeating key. Almost all of the file is zero padding,
/// so the key is recovered as the most common byte in each of the 500 columns. The plain text is
/// a sequence of sections tagged with reversed four-letter names ("RAEG" = GEAR, ...).
/// Some float options are additionally stored XOR 0x40000000.
///
/// The in-game "Force Feedback → Strength" slider is the float right after the second GEAR tag's
/// flag field (found by changing the slider in game: 1.0 = 100 %, 0.6 = 60 %).
/// The first four bytes change on every save and match no common checksum (CRC32, Adler, sums, XOR);
/// the only write here patches the strength float in place, re-encrypted with the same key, and leaves
/// those four bytes alone. The first write keeps a copy of the untouched file (see <see cref="BackupPath"/>).
/// </summary>
public sealed class RbrProfileFile
{
    private const int KeyLength = 500;
    private const uint FloatMask = 0x40000000;

    private readonly byte[] _data;
    private readonly byte[] _key;
    private readonly byte[] _plain;

    private RbrProfileFile(byte[] data, byte[] key, byte[] plain) => (_data, _key, _plain) = (data, key, plain);

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
            key[col] = (byte)Array.IndexOf(counts, counts.Max());
        }

        var plain = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) plain[i] = (byte)(data[i] ^ key[i % KeyLength]);
        return new RbrProfileFile(data, key, plain);
    }

    /// <summary>Where the first write keeps the profile as it was (%AppData%\RBRInputManager\ProfileBackups).</summary>
    public static string BackupPath(string path) =>
        Path.Combine(AppSettings.DataDir, "ProfileBackups", Path.GetFileName(path));

    /// <summary>Sets the in-game force feedback strength (0.0 – 1.0) and writes the file back.</summary>
    public static void SetForceFeedbackStrength(string path, float value)
    {
        var file = Load(path);
        int gear = file.FindTag("GEAR", 1);
        if (gear < 0 || file.ForceFeedbackStrength is null)
            throw new InvalidDataException("Could not find the force feedback strength in the RBR profile.");

        string backup = BackupPath(path);
        if (!File.Exists(backup))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.WriteAllBytes(backup, file._data);
        }

        int offset = gear + 8;
        uint raw = (uint)BitConverter.SingleToInt32Bits(Math.Clamp(value, 0f, 1f)) ^ FloatMask;
        var data = (byte[])file._data.Clone();
        BitConverter.GetBytes(raw).CopyTo(data, offset);
        for (int i = offset; i < offset + 4; i++) data[i] ^= file._key[i % KeyLength];
        GameFiles.WriteInPlace(path, data);
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

    private float? MaskedFloat(int offset)
    {
        if (offset < 0 || offset + 4 > _plain.Length) return null;
        uint raw = BitConverter.ToUInt32(_plain, offset) ^ FloatMask;
        float v = BitConverter.Int32BitsToSingle((int)raw);
        return float.IsFinite(v) ? v : null;
    }

    /// <summary>In-game force feedback strength, 0.0 – 1.0, or null if it can't be located.</summary>
    public float? ForceFeedbackStrength
    {
        get
        {
            int gear = FindTag("GEAR", 1);
            float? v = gear < 0 ? null : MaskedFloat(gear + 8);
            return v is >= 0f and <= 1.0001f ? v : null;
        }
    }
}
