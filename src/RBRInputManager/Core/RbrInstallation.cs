using System.Diagnostics;
using System.IO;

namespace RBRInputManager.Core;

public sealed class RbrInstallation
{
    /// <summary>RBR's default profile name, used by RallySimFans unless the player created another one.</summary>
    public const string DefaultProfile = "MULLIGATAWNY";

    private static readonly string[] Candidates =
    {
        @"C:\RBR", @"C:\Richard Burns Rally", @"D:\RBR", @"D:\Richard Burns Rally",
        @"C:\Games\Richard Burns Rally", @"E:\RBR", @"E:\Richard Burns Rally",
    };

    public string Root { get; }

    /// <summary>RBR profiles found in SavedGames (pf&lt;NAME&gt;.acm), most recently played first.</summary>
    public IReadOnlyList<string> Profiles { get; }

    /// <summary>The profile whose files the app reads and writes.</summary>
    public string Profile { get; }

    /// <summary>True when <see cref="Profile"/> is the one asked for rather than picked automatically.</summary>
    public bool ProfileChosen { get; }

    /// <param name="preferredProfile">Profile to use if it exists; otherwise the most recently played one.</param>
    public RbrInstallation(string root, string? preferredProfile = null)
    {
        Root = root;
        Profiles = FindProfiles(root);
        var chosen = Profiles.FirstOrDefault(p => string.Equals(p, preferredProfile, StringComparison.OrdinalIgnoreCase));
        ProfileChosen = chosen is not null;
        Profile = chosen ?? Profiles.FirstOrDefault() ?? DefaultProfile;
    }

    private static List<string> FindProfiles(string root)
    {
        string dir = System.IO.Path.Combine(root, "SavedGames");
        if (!Directory.Exists(dir)) return new();

        // The game rewrites pf<NAME>.rbr (options) and .acm (controls) of the profile it plays with,
        // so the newest of the two tells which profile was used last.
        DateTime LastUsed(string acm)
        {
            var rbr = System.IO.Path.ChangeExtension(acm, ".rbr");
            var t = File.GetLastWriteTimeUtc(acm);
            return File.Exists(rbr) && File.GetLastWriteTimeUtc(rbr) > t ? File.GetLastWriteTimeUtc(rbr) : t;
        }

        return Directory.GetFiles(dir, "pf*.acm")
            .Where(f => System.IO.Path.GetFileNameWithoutExtension(f).Length > 2)
            .OrderByDescending(LastUsed)
            .Select(f => System.IO.Path.GetFileNameWithoutExtension(f)[2..])
            .ToList();
    }

    public static bool IsValid(string? root) =>
        !string.IsNullOrWhiteSpace(root) && File.Exists(System.IO.Path.Combine(root, "RichardBurnsRally.ini"));

    public static string? Detect() => Candidates.FirstOrDefault(IsValid);

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    // Files the app reads and may write, relative to the install root.
    public string AcmFile => $@"SavedGames\pf{Profile}.acm";
    public string ProfileFile => $@"SavedGames\pf{Profile}.rbr";
    public const string GameIniFile = "RichardBurnsRally.ini";
    public const string InputIniFile = "input.ini";
    public const string LauncherIniFile = @"rsf_launcher\Configs\default.ini";
    public const string RbrControlsIniFile = @"Plugins\RBRControls\RBRControls.ini";
    public const string PersonalIniFile = "rallysimfans_personal.ini";
    public const string NgpCarMenuIniFile = @"Plugins\NGPCarMenu.ini";

    /// <summary>Files backed up on first run and restorable with "Restore originals".</summary>
    public IReadOnlyList<string> ManagedFiles =>
        new[] { AcmFile, InputIniFile, GameIniFile, LauncherIniFile, RbrControlsIniFile };

    public string GameIni => Path(GameIniFile);
    public string InputIni => Path(InputIniFile);
    public string PersonalIni => Path(PersonalIniFile);
    public string LauncherIni => Path(LauncherIniFile);
    public string RbrControlsIni => Path(RbrControlsIniFile);
    public string Acm => Path(AcmFile);
    public string ProfileRbr => Path(ProfileFile);

    /// <summary>NGPCarMenu's SecondaryGearUpDownControls (default on); false without the plugin's INI.</summary>
    public bool SecondaryGearControls =>
        IniFile.TryLoad(Path(NgpCarMenuIniFile)) is { } ini && ini.Get("Default", "SecondaryGearUpDownControls") != "0";

    /// <summary>Names of RBR / RSF launcher processes currently running (writing files under them is unsafe).</summary>
    public static IReadOnlyList<string> RunningGameProcesses()
    {
        var names = new[] { "RichardBurnsRally_SSE", "RichardBurnsRally", "RichardBurnsRally_NoSSE", "RSF_Launcher" };
        return names.Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
    }
}
