using System.IO;
using System.Text.Json;

namespace RBRInputManager.Core;

public enum ThemePreference { System, Light, Dark }

/// <summary>App settings, stored in %AppData%\RBRInputManager\settings.json.</summary>
public sealed class AppSettings
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RBRInputManager");

    private static readonly string FilePath = Path.Combine(DataDir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string? RbrPath { get; set; }
    /// <summary>RBR profile picked in Settings; null uses the most recently played one.</summary>
    public string? RbrProfile { get; set; }
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    /// <summary>Name of the profile last saved or applied to the game (shown as "Current").</summary>
    public string? CurrentProfile { get; set; }
    /// <summary>Jump to where an input is bound when it is pressed while the app is in the foreground.</summary>
    public bool LocateInputs { get; set; } = true;
    /// <summary>"Bound only" filters on the Game controls and RBRControls pages.</summary>
    public bool GameBoundOnly { get; set; }
    public bool PluginBoundOnly { get; set; } = true;
    /// <summary>Main window position and size when last closed; ignored if it no longer fits on a screen.</summary>
    public WindowBounds? Window { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();

            // Settings from the first version of the app only had the install path.
            string legacy = Path.Combine(DataDir, "rbrpath.txt");
            if (File.Exists(legacy))
                return new AppSettings { RbrPath = File.ReadAllText(legacy).Trim() };
        }
        catch { /* corrupt settings: start from defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are a convenience only */ }
    }
}
