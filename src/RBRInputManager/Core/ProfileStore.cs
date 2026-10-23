using System.IO;
using System.Text.Json;

namespace RBRInputManager.Core;

public sealed class InputProfile
{
    public required string Name { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    /// <summary>FFB values keyed "Section.Key" (e.g. "NGP.ForceFeedbackSensitivityGravel").</summary>
    public Dictionary<string, string> Ffb { get; set; } = new();
    /// <summary>Device names referenced by the saved bindings, for display.</summary>
    public List<string> Devices { get; set; } = new();
    public int BindingCount { get; set; }

    internal string Dir { get; set; } = "";

    public string Summary => $"{BindingCount} bindings · {(Devices.Count == 0 ? "no devices" : string.Join(", ", Devices))}";
    public string Updated => UpdatedAtUtc.ToLocalTime().ToString("g");
}

/// <summary>
/// Named snapshots of the input setup, in %AppData%\RBRInputManager\Profiles\&lt;name&gt;\:
/// the game bindings (.acm), input.ini (axis inversion), RBRControls.ini and the FFB values.
/// FFB is stored as individual values because RichardBurnsRally.ini also holds graphics options.
/// </summary>
public static partial class ProfileStore
{
    private static readonly string Root = Path.Combine(AppSettings.DataDir, "Profiles");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Profile file name -> path in the install.
    private static (string Stored, string Game)[] Files(RbrInstallation rbr) =>
    [
        ("controls.acm", rbr.AcmFile),
        ("input.ini", RbrInstallation.InputIniFile),
        ("RBRControls.ini", RbrInstallation.RbrControlsIniFile),
    ];

    public static readonly (string Section, string Key)[] FfbKeys =
    {
        ("Settings", "ForceFeedback"),
        ("Settings", "ConstantForceMultiplier"),
        ("NGP", "ForceFeedbackSensitivityGravel"),
        ("NGP", "ForceFeedbackSensitivityTarmac"),
        ("NGP", "ForceFeedbackSensitivitySnow"),
        ("NGP", "ffbDeadZone"),
        ("NGP", "ffbDeadZoneThreshold"),
        ("NGP", "ffbRate"),
    };

    public static List<InputProfile> LoadAll()
    {
        if (!Directory.Exists(Root)) return new();
        var list = new List<InputProfile>();
        foreach (var dir in Directory.GetDirectories(Root))
        {
            try
            {
                var p = JsonSerializer.Deserialize<InputProfile>(File.ReadAllText(Path.Combine(dir, "profile.json")));
                if (p is null) continue;
                p.Dir = dir;
                list.Add(p);
            }
            catch { /* skip broken profile folders */ }
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool Exists(string name) => LoadAll().Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Saves the current game setup as a profile (creating it or overwriting one with the same name).</summary>
    public static InputProfile SaveCurrent(RbrInstallation rbr, string name, DeviceCatalog devices) =>
        Save(rbr, name, devices, rbr.Path);

    /// <summary>
    /// Creates the built-in "Default" profile from the original game files (the first-run backup)
    /// if it doesn't exist yet. Returns false when there is no backup to build it from.
    /// </summary>
    public static bool EnsureDefault(RbrInstallation rbr, DeviceCatalog devices)
    {
        if (Exists(DefaultName)) return true;
        if (OriginalBackup.Load(rbr.Root) is null) return false;
        Save(rbr, DefaultName, devices, rel => OriginalBackup.OriginalFile(rbr.Root, rel));
        return true;
    }

    public const string DefaultName = "Default";

    public static bool IsDefault(InputProfile p) => string.Equals(p.Name, DefaultName, StringComparison.CurrentCultureIgnoreCase);

    /// <param name="source">Maps a game file (relative to the install) to the file to read it from, or null if it doesn't exist.</param>
    private static InputProfile Save(RbrInstallation rbr, string name, DeviceCatalog devices, Func<string, string?> source)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("The profile needs a name.");

        var existing = LoadAll().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        string dir = existing?.Dir ?? UniqueDir(name);
        Directory.CreateDirectory(dir);

        foreach (var (stored, game) in Files(rbr))
        {
            string? src = source(game);
            string dst = Path.Combine(dir, stored);
            if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal);
            if (src is not null && File.Exists(src))
            {
                File.Copy(src, dst, true);
                // The game's .acm is often read-only; the profile copy must stay writable/deletable.
                File.SetAttributes(dst, FileAttributes.Normal);
            }
            else if (File.Exists(dst)) File.Delete(dst);
        }

        var profile = new InputProfile
        {
            Name = name,
            CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Dir = dir,
        };

        var gameIni = source(RbrInstallation.GameIniFile) is { } ini ? IniFile.TryLoad(ini) : null;
        foreach (var (section, key) in FfbKeys)
            if (gameIni?.Get(section, key) is { } v)
                profile.Ffb[$"{section}.{key}"] = v;

        if (source(rbr.AcmFile) is { } acmPath && File.Exists(acmPath))
        {
            var acm = ActionMapFile.Load(acmPath);
            // Summarize the map RBR would use with the devices connected now.
            if ((acm.ActiveMap(devices.AttachedGuids) ?? acm.Maps.LastOrDefault()) is { } map)
            {
                var used = map.Bindings.Select(b => b.DeviceIndex).Distinct()
                    .Where(i => i >= 0 && i < map.Devices.Count).Select(i => map.Devices[i]);
                profile.Devices = used.Select(g => devices.Resolve(g).Name).Distinct().ToList();
                profile.BindingCount = map.Bindings.Count;
            }
        }

        File.WriteAllText(Path.Combine(dir, "profile.json"), JsonSerializer.Serialize(profile, Json));
        return profile;
    }

    /// <summary>Writes a profile into the game. Files keep their existing permissions.</summary>
    public static void Apply(RbrInstallation rbr, InputProfile profile)
    {
        foreach (var (stored, game) in Files(rbr))
        {
            string src = Path.Combine(profile.Dir, stored);
            if (File.Exists(src)) GameFiles.WriteInPlace(rbr.Path(game), File.ReadAllBytes(src));
        }

        if (profile.Ffb.Count == 0) return;
        var values = profile.Ffb.Select(kv =>
        {
            int dot = kv.Key.IndexOf('.');
            return (kv.Key[..dot], kv.Key[(dot + 1)..], kv.Value);
        }).ToList();

        GameFiles.SetIniValues(rbr.GameIni, values);

        // The RSF launcher rewrites RichardBurnsRally.ini from its own copy on launch,
        // so update the keys it already tracks there too.
        if (File.Exists(rbr.LauncherIni))
            GameFiles.SetIniValues(rbr.LauncherIni,
                values.Select(v => ("RichardBurnsRally", $"{v.Item1}.{v.Item2}", v.Value)), onlyExistingKeys: true);
    }

    /// <summary>True when the game files hold exactly what the profile would write.</summary>
    public static bool MatchesGame(RbrInstallation rbr, InputProfile profile)
    {
        foreach (var (stored, game) in Files(rbr))
        {
            string src = Path.Combine(profile.Dir, stored), dst = rbr.Path(game);
            if (!File.Exists(src)) continue;
            if (!File.Exists(dst) || !File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(dst))) return false;
        }

        var gameIni = IniFile.TryLoad(rbr.GameIni);
        foreach (var (key, value) in profile.Ffb)
        {
            int dot = key.IndexOf('.');
            if (!string.Equals(gameIni?.Get(key[..dot], key[(dot + 1)..]), value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static void Delete(InputProfile profile)
    {
        if (!profile.Dir.StartsWith(Root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(profile.Dir)) return;
        foreach (var f in Directory.GetFiles(profile.Dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(profile.Dir, true);
    }

    private static string UniqueDir(string name)
    {
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        if (safe.Length == 0) safe = "profile";
        string dir = Path.Combine(Root, safe);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(Root, $"{safe} ({i})");
        return dir;
    }
}
