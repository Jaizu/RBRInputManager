namespace RBRInputManager.Core;

public sealed record SettingRow(string Group, string Name, string? GameValue, string? LauncherValue, string Source, string Note)
{
    public bool Differs => LauncherValue is not null && GameValue is not null &&
                           !string.Equals(GameValue, LauncherValue, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Force feedback related settings. The RSF launcher keeps its own copy in
/// rsf_launcher\Configs\default.ini (keys prefixed with "NGP." / "Settings.") and writes it
/// into RichardBurnsRally.ini on launch, so both values are shown side by side.
/// </summary>
public static class FfbSettings
{
    public static List<SettingRow> Load(RbrInstallation rbr)
    {
        var game = IniFile.TryLoad(rbr.GameIni);
        var launcher = IniFile.TryLoad(rbr.LauncherIni);
        var rows = new List<SettingRow>();

        void Add(string group, string section, string key, string name, string note)
        {
            string? g = game?.Get(section, key);
            string? l = launcher?.Get("RichardBurnsRally", $"{section}.{key}");
            rows.Add(new(group, name, g, l, $"[{section}] {key}", note));
        }

        Add("General", "Settings", "ForceFeedback", "Force feedback enabled", "");
        Add("General", "Settings", "ConstantForceMultiplier", "Constant force multiplier",
            "Negative values invert the force direction");

        Add("Strength per surface", "NGP", "ForceFeedbackSensitivityGravel", "Gravel", "NGP global sensitivity");
        Add("Strength per surface", "NGP", "ForceFeedbackSensitivityTarmac", "Tarmac", "NGP global sensitivity");
        Add("Strength per surface", "NGP", "ForceFeedbackSensitivitySnow", "Snow", "NGP global sensitivity");

        Add("Advanced", "NGP", "ffbDeadZone", "FFB dead zone", "");
        Add("Advanced", "NGP", "ffbDeadZoneThreshold", "Dead zone threshold", "");
        Add("Advanced", "NGP", "ffbRate", "FFB rate (Hz)", "");
        Add("Advanced", "NGP", "inputRate", "Input rate (Hz)", "");

        // Per-slot overrides (0 = use the global value).
        for (int slot = 0; slot < 8; slot++)
            foreach (var surface in new[] { "Gravel", "Tarmac", "Snow" })
            {
                string key = $"ForceFeedbackSensitivity{surface}_Car{slot}";
                string? v = game?.Get("NGP", key);
                if (v is not null && v != "0")
                    rows.Add(new("Per car slot", $"Slot {slot} · {surface}", v, null, $"[NGP] {key}", "Overrides the global value"));
            }

        return rows;
    }

    /// <summary>Per-car FFB overrides set from the RSF launcher (rallysimfans_personal.ini).</summary>
    public static List<SettingRow> LoadPerCar(RbrInstallation rbr)
    {
        var rows = new List<SettingRow>();
        var personal = IniFile.TryLoad(rbr.PersonalIni);
        if (personal is null) return rows;

        foreach (var section in personal.SectionNames.Where(s => s.StartsWith("car", StringComparison.OrdinalIgnoreCase)))
        {
            string car = personal.Get(section, "name") ?? section;
            foreach (var (key, label) in new[]
                     {
                         ("forcefeedbacksensitivitygravel", "Gravel"),
                         ("forcefeedbacksensitivitytarmac", "Tarmac"),
                         ("forcefeedbacksensitivitysnow", "Snow"),
                         ("steeringrotation", "Steering rotation"),
                     })
            {
                string? v = personal.Get(section, key);
                if (!string.IsNullOrEmpty(v))
                    rows.Add(new("Per car (RSF)", $"{car} · {label}", v, null, $"[{section}] {key}", ""));
            }
        }
        return rows;
    }
}
