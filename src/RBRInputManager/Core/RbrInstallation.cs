using System.Diagnostics;
using System.IO;

namespace RBRInputManager.Core;

public sealed class RbrInstallation
{
    /// <summary>RallySimFans always plays with this RBR profile, whatever the RSF account name is.</summary>
    public const string RsfProfile = "MULLIGATAWNY";

    private static readonly string[] Candidates =
    {
        @"C:\RBR", @"C:\Richard Burns Rally", @"D:\RBR", @"D:\Richard Burns Rally",
        @"C:\Games\Richard Burns Rally", @"E:\RBR", @"E:\Richard Burns Rally",
    };

    public string Root { get; }

    public RbrInstallation(string root) => Root = root;

    public static bool IsValid(string? root) =>
        !string.IsNullOrWhiteSpace(root) && File.Exists(System.IO.Path.Combine(root, "RichardBurnsRally.ini"));

    public static string? Detect() => Candidates.FirstOrDefault(IsValid);

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    // Files the app reads and may write, relative to the install root.
    public const string AcmFile = @"SavedGames\pf" + RsfProfile + ".acm";
    public const string ProfileFile = @"SavedGames\pf" + RsfProfile + ".rbr";
    public const string GameIniFile = "RichardBurnsRally.ini";
    public const string InputIniFile = "input.ini";
    public const string LauncherIniFile = @"rsf_launcher\Configs\default.ini";
    public const string RbrControlsIniFile = @"Plugins\RBRControls\RBRControls.ini";
    public const string PersonalIniFile = "rallysimfans_personal.ini";

    /// <summary>Files backed up on first run and restorable with "Restore originals".</summary>
    public static readonly IReadOnlyList<string> ManagedFiles =
        new[] { AcmFile, InputIniFile, GameIniFile, LauncherIniFile, RbrControlsIniFile };

    public string GameIni => Path(GameIniFile);
    public string InputIni => Path(InputIniFile);
    public string PersonalIni => Path(PersonalIniFile);
    public string LauncherIni => Path(LauncherIniFile);
    public string RbrControlsIni => Path(RbrControlsIniFile);
    public string Acm => Path(AcmFile);
    public string ProfileRbr => Path(ProfileFile);

    /// <summary>Names of RBR / RSF launcher processes currently running (writing files under them is unsafe).</summary>
    public static IReadOnlyList<string> RunningGameProcesses()
    {
        var names = new[] { "RichardBurnsRally_SSE", "RichardBurnsRally", "RichardBurnsRally_NoSSE", "RSF_Launcher" };
        return names.Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
    }
}
