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

    /// <summary>Why <paramref name="name"/> can't be used as an RBR profile name, or null if it can.</summary>
    public string? ProfileNameProblem(string name)
    {
        if (name.Length == 0) return "Type a name.";
        if (name.Length > RbrProfileFile.MaxNameLength) return $"RBR keeps at most {RbrProfileFile.MaxNameLength} characters.";
        // What RBR's own name entry accepts (tried in game: '_' can't be typed, '.' and '-' can).
        if (!name.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
            return "RBR only allows letters, digits, '.' and '-'.";
        // RBR keeps the case as typed, but Windows file names ignore it: a profile differing only in case would be the
        // same files (the game's own check compares case-sensitively, so it would overwrite the other profile).
        bool sameProfile = string.Equals(name, Profile, StringComparison.OrdinalIgnoreCase);
        if (!sameProfile && (File.Exists(Path($@"SavedGames\pf{name}.rbr")) || File.Exists(Path($@"SavedGames\pf{name}.acm"))))
            return $"There already is a profile called \"{name}\".";
        return null;
    }

    /// <summary>
    /// Renames <see cref="Profile"/> the way RBR would have named it: the name stored inside pf&lt;NAME&gt;.rbr
    /// (RBR saves to the files that name points at) and the .rbr / .acm / season save files. Everything is put
    /// back if a step fails. Returns the files renamed.
    /// </summary>
    public IReadOnlyList<string> RenameProfile(string newName)
    {
        if (ProfileNameProblem(newName) is { } problem) throw new ArgumentException(problem);
        string dir = Path(SavedGamesDir);
        string oldRbr = ProfileRbr;
        if (!File.Exists(oldRbr)) throw new FileNotFoundException($"{ProfileFile} does not exist.");
        if (newName == Profile && RbrProfileFile.Load(oldRbr).Name == newName) return Array.Empty<string>();

        // The same name only rewrites the stored one (files renamed by hand). Every file name ends in <NAME>.<ext>.
        var moves = new List<(string From, string To)>();
        if (newName != Profile)
            foreach (var f in ProfileFiles())
            {
                string file = System.IO.Path.GetFileName(f), ext = System.IO.Path.GetExtension(f);
                moves.Add((f, System.IO.Path.Combine(dir, file[..^(Profile.Length + ext.Length)] + newName + ext)));
            }

        byte[] originalRbr = File.ReadAllBytes(oldRbr);
        var done = new List<(string From, string To)>();
        try
        {
            RbrProfileFile.SetName(oldRbr, newName);
            foreach (var m in moves)
            {
                // A case-only change (MyName → MYNAME) is the same file to Windows; File.Move handles it.
                File.Move(m.From, m.To);
                done.Add(m);
                Log.Info(Log.Context, $"Renamed {m.From} to {System.IO.Path.GetFileName(m.To)}");
            }
        }
        catch (Exception ex)
        {
            Undo(done, $"Renaming the profile {Profile} to {newName} failed ({ex.Message}); put everything back");
            GameFiles.WriteInPlace(oldRbr, originalRbr, new[] { $"Stored profile name put back to {Profile}" });
            throw;
        }
        if (moves.Count == 0) return new[] { $"Stored name in {ProfileFile} set to {newName}" };
        OriginalBackup.RenameFile(Root, AcmFile, $@"SavedGames\pf{newName}.acm");
        return moves.Select(m => $"{System.IO.Path.GetFileName(m.From)} → {System.IO.Path.GetFileName(m.To)}").ToList();
    }

    private const string SavedGamesDir = "SavedGames";

    /// <summary>Moves files back after a failed rename or delete, logging each one (and any that can't be).</summary>
    private static void Undo(List<(string From, string To)> done, string why)
    {
        var lines = new List<string>();
        foreach (var m in Enumerable.Reverse(done))
        {
            try { File.Move(m.To, m.From); lines.Add($"{m.To} → {m.From}"); }
            catch (Exception ex) { lines.Add($"Could not move {m.To} back to {m.From}: {ex.Message}"); }
        }
        Log.Warn(Log.Context, why, lines.Count > 0 ? lines : new List<string> { "No file had been moved yet" });
    }

    /// <summary>The files of <see cref="Profile"/> that exist: pf&lt;NAME&gt;.rbr, .acm and season saves (&lt;n&gt;slot&lt;m&gt;_&lt;NAME&gt;.lsp).</summary>
    private List<string> ProfileFiles()
    {
        string dir = Path(SavedGamesDir);
        var files = new[] { ProfileRbr, Acm }.Where(File.Exists).ToList();
        if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, $"*slot*_{Profile}.lsp"));
        return files;
    }

    /// <summary>
    /// Removes <see cref="Profile"/> from the game: its files are moved (not deleted) to
    /// %AppData%\RBRInputManager\DeletedProfiles\&lt;NAME&gt; &lt;time&gt;, so it can be put back by moving them into SavedGames.
    /// Refuses to remove the only profile. Returns where the files went.
    /// </summary>
    public string DeleteProfile()
    {
        if (Profiles.Count(p => !string.Equals(p, Profile, StringComparison.OrdinalIgnoreCase)) == 0)
            throw new InvalidOperationException($"{Profile} is the only RBR profile; the game needs at least one.");
        var files = ProfileFiles();
        if (files.Count == 0) throw new FileNotFoundException($"There are no files for the profile {Profile} in SavedGames.");

        string target = System.IO.Path.Combine(AppSettings.DataDir, "DeletedProfiles", $"{Profile} {DateTime.Now:yyyy-MM-dd HHmmss}");
        Directory.CreateDirectory(target);
        var done = new List<(string From, string To)>();
        try
        {
            foreach (var f in files)
            {
                string to = System.IO.Path.Combine(target, System.IO.Path.GetFileName(f));
                File.Move(f, to);
                done.Add((f, to));
                Log.Info(Log.Context, $"Moved {f} out of the game", new[] { $"Kept in {to}" });
            }
        }
        catch (Exception ex)
        {
            Undo(done, $"Deleting the profile {Profile} failed ({ex.Message}); put its files back");
            throw;
        }
        return target;
    }

    /// <summary>The game's executable (the SSE build RSF installs first); null when none is there.</summary>
    public string? GameExe =>
        new[] { "RichardBurnsRally_SSE.exe", "RichardBurnsRally.exe", "RichardBurnsRally_NoSSE.exe" }
            .Select(Path).FirstOrDefault(File.Exists);

    /// <summary>Names of RBR / RSF launcher processes currently running (writing files under them is unsafe).</summary>
    public static IReadOnlyList<string> RunningGameProcesses()
    {
        var names = new[] { "RichardBurnsRally_SSE", "RichardBurnsRally", "RichardBurnsRally_NoSSE", "RSF_Launcher" };
        return names.Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
    }
}
