using System.IO;

namespace RBRInputManager.Core;

/// <summary>One thing the game files hold differently from a profile. Values are already readable text.</summary>
public sealed record ProfileDifference(string Area, string Item, string Profile, string Game);

public static partial class ProfileStore
{
    private const string NotSet = "(not set)";

    private static readonly Dictionary<string, string> FfbNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Settings.ForceFeedback"] = "Force feedback enabled",
        ["Settings.ConstantForceMultiplier"] = "Constant force multiplier",
        ["NGP.ForceFeedbackSensitivityGravel"] = "Strength · Gravel",
        ["NGP.ForceFeedbackSensitivityTarmac"] = "Strength · Tarmac",
        ["NGP.ForceFeedbackSensitivitySnow"] = "Strength · Snow",
        ["NGP.ffbDeadZone"] = "FFB dead zone",
        ["NGP.ffbDeadZoneThreshold"] = "Dead zone threshold",
        ["NGP.ffbRate"] = "FFB rate (Hz)",
    };

    /// <summary>
    /// Everything <see cref="MatchesGame"/> would find different, one entry per binding, setting or value.
    /// <paramref name="inputName"/> describes a bound input, e.g. "Logitech G29: X Axis".
    /// </summary>
    public static List<ProfileDifference> Differences(RbrInstallation rbr, InputProfile profile,
        Func<Guid, string> deviceName, Func<Guid, uint, string> inputName, bool secondaryGears)
    {
        var result = new List<ProfileDifference>();
        foreach (var (stored, game) in Files(rbr))
        {
            string src = Path.Combine(profile.Dir, stored), dst = rbr.Path(game);
            if (!File.Exists(src)) continue;
            string area = stored switch
            {
                "controls.acm" => "Game controls",
                "input.ini" => "Axis inversion (input.ini)",
                _ => "RBRControls",
            };
            if (!File.Exists(dst))
            {
                result.Add(new(area, Path.GetFileName(game), "Saved", "Missing from the game"));
                continue;
            }
            if (File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(dst))) continue;

            int before = result.Count;
            try
            {
                switch (stored)
                {
                    case "controls.acm": DiffAcm(src, dst, area, deviceName, inputName, secondaryGears, result); break;
                    case "input.ini": DiffIni(src, dst, area, InputIniLabel, (_, _, v) => v ?? NotSet, result); break;
                    default: DiffRbrControls(src, dst, area, result); break;
                }
            }
            catch (Exception ex)
            {
                result.RemoveRange(before, result.Count - before);
                result.Add(new(area, Path.GetFileName(game), "Saved", $"Differs (could not read it: {ex.Message})"));
                continue;
            }
            // Bytes differ but every value is the same: only order, comments or spacing changed.
            if (result.Count == before)
                result.Add(new(area, Path.GetFileName(game), "", stored == "controls.acm"
                    ? "Same bindings, stored in a different order"
                    : "Same values; comments, order or spacing differ"));
        }

        var gameIni = IniFile.TryLoad(rbr.GameIni);
        foreach (var (key, value) in profile.Ffb)
        {
            int dot = key.IndexOf('.');
            string? current = gameIni?.Get(key[..dot], key[(dot + 1)..]);
            if (!string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
                result.Add(new("Force feedback", FfbNames.GetValueOrDefault(key, key), value, current ?? NotSet));
        }
        return result;
    }

    private static void DiffAcm(string profilePath, string gamePath, string area,
        Func<Guid, string> deviceName, Func<Guid, uint, string> inputName, bool secondaryGears, List<ProfileDifference> result)
    {
        var saved = ActionMapFile.Load(profilePath);
        var game = ActionMapFile.Load(gamePath);
        int count = Math.Max(saved.Maps.Count, game.Maps.Count);

        for (int i = 0; i < count; i++)
        {
            var p = i < saved.Maps.Count ? saved.Maps[i] : null;
            var g = i < game.Maps.Count ? game.Maps[i] : null;
            string prefix = count > 1 ? $"Map {i + 1} · " : "";

            // RBR adds a map whenever it sees a new set of controllers.
            if (p is null || g is null)
            {
                string Describe(ActionMap m) => $"{string.Join(", ", m.Controllers.Select(deviceName).DefaultIfEmpty("keyboard only"))} " +
                                                $"({m.Bindings.Count} bindings)";
                result.Add(new(area, $"Map {i + 1}", p is null ? "Not in the profile" : Describe(p),
                    g is null ? "Not in the game" : Describe(g)));
                continue;
            }

            var pDevices = p.Controllers.ToHashSet();
            var gDevices = g.Controllers.ToHashSet();
            if (!pDevices.SetEquals(gDevices))
                result.Add(new(area, prefix + "Devices",
                    Join(pDevices.Except(gDevices).Select(deviceName), "No other devices"),
                    Join(gDevices.Except(pDevices).Select(deviceName), "No other devices")));

            bool gears = RbrActions.HasSecondaryGears(g, secondaryGears);
            var actions = p.Bindings.Concat(g.Bindings).Select(b => b.ActionId).Distinct().OrderBy(a => a);
            foreach (int id in actions)
            {
                var pInputs = Inputs(p, id);
                var gInputs = Inputs(g, id);
                if (pInputs.SetEquals(gInputs)) continue;
                result.Add(new(area, prefix + RbrActions.Get(id, gears).Name,
                    Join(pInputs.Select(x => Input(x.Device, x.Type)), "Unbound"),
                    Join(gInputs.Select(x => Input(x.Device, x.Type)), "Unbound")));
            }
        }

        string Input(Guid? device, uint type) =>
            device is { } d ? $"{deviceName(d)}: {inputName(d, type)}" : $"Invalid device · 0x{type:X8}";
    }

    private static HashSet<(Guid? Device, uint Type)> Inputs(ActionMap m, int actionId) =>
        m.Bindings.Where(b => b.ActionId == actionId)
            .Select(b => (b.DeviceIndex >= 0 && b.DeviceIndex < m.Devices.Count ? m.Devices[b.DeviceIndex] : (Guid?)null, b.ObjectType))
            .ToHashSet();

    private static string Join(IEnumerable<string> items, string empty)
    {
        var list = items.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
        return list.Count == 0 ? empty : string.Join("\n", list);
    }

    private static string InputIniLabel(string section, string key)
    {
        var action = RbrActions.All.FirstOrDefault(a => string.Equals(a.InvertKey, key, StringComparison.OrdinalIgnoreCase));
        return action is not null && string.Equals(section, "Settings", StringComparison.OrdinalIgnoreCase)
            ? $"Invert · {action.Name}"
            : SectionKey(section, key);
    }

    private static string SectionKey(string section, string key) => section.Length == 0 ? key : $"[{section}] {key}";

    private static void DiffRbrControls(string profilePath, string gamePath, string area, List<ProfileDifference> result)
    {
        // Name binds after their function ("JoyKey · Menu · Up arrow") and show them as "Device: Button 3".
        var names = RbrControlsConfig.LoadBindings(IniFile.Load(profilePath))
            .Concat(RbrControlsConfig.LoadBindings(IniFile.Load(gamePath)))
            .GroupBy(b => b.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grp => grp.Key, grp => $"{grp.First().Page} · {grp.First().Function}", StringComparer.OrdinalIgnoreCase);

        string Label(string section, string key)
        {
            if (!string.Equals(section, "KeyBind", StringComparison.OrdinalIgnoreCase)) return SectionKey(section, key);
            if (names.TryGetValue(key, out var name)) return name;
            foreach (var (suffix, what) in new[] { ("_TRIGGERTYPE", "trigger"), ("_GAMEMODE", "active in"), ("_KEYCODE", "key code") })
                if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && names.TryGetValue(key[..^suffix.Length], out var baseName))
                    return $"{baseName} ({what})";
            return SectionKey(section, key);
        }

        string Value(string section, string key, string? value)
        {
            if (value is null) return NotSet;
            if (!names.ContainsKey(key) || !string.Equals(section, "KeyBind", StringComparison.OrdinalIgnoreCase)) return value;
            if (value.Length == 0) return "Unbound";
            var (device, input) = RbrControlsConfig.ParseBind(value);
            return device.Length > 0 ? $"{device}: {input}" : value;
        }

        DiffIni(profilePath, gamePath, area, Label, Value, result);
    }

    /// <summary>Key-by-key comparison of two INI files, in the profile's order and then keys only the game has.</summary>
    private static void DiffIni(string profilePath, string gamePath, string area, Func<string, string, string> label,
        Func<string, string, string?, string> value, List<ProfileDifference> result)
    {
        var saved = IniFile.Load(profilePath);
        var game = IniFile.Load(gamePath);
        var sections = new[] { "" }.Concat(saved.SectionNames).Concat(game.SectionNames).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            var keys = saved.Section(section).Concat(game.Section(section)).Select(kv => kv.Key).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                string? p = saved.Get(section, key), g = game.Get(section, key);
                if (string.Equals(p, g, StringComparison.Ordinal)) continue;
                result.Add(new(area, label(section, key), value(section, key, p), value(section, key, g)));
            }
        }
    }
}
