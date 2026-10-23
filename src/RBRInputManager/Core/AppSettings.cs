using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RBRInputManager.Core;

public enum ThemePreference { System, Light, Dark }

/// <summary>App settings, stored in %AppData%\RBRInputManager\settings.json.</summary>
public sealed class AppSettings
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RBRInputManager");

    private static readonly string FilePath = Path.Combine(DataDir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

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

    // What is on disk, to log only the values a save changes.
    private string? _saved;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new();
                settings._saved = json;
                return settings;
            }

            // Settings from the first version of the app only had the install path.
            string legacy = Path.Combine(DataDir, "rbrpath.txt");
            if (File.Exists(legacy))
                return new AppSettings { RbrPath = File.ReadAllText(legacy).Trim() };
        }
        catch (Exception ex)
        {
            Log.Error(LogArea.Settings, $"Could not read {FilePath}: starting with the default settings", ex);
        }
        return new();
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(this, Json);
        if (json == _saved) return;
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, json);
            Log.Info(LogArea.Settings, $"Saved the app settings to {FilePath}", Changes(_saved, json));
            _saved = json;
        }
        catch (Exception ex)
        {
            // Settings are a convenience only: the app keeps working with them in memory.
            Log.Error(LogArea.Settings, $"Could not save the app settings to {FilePath}", ex);
        }
    }

    /// <summary>"Theme: System → Dark" for each top-level value that differs.</summary>
    private static IEnumerable<string> Changes(string? before, string after)
    {
        JsonObject? old = null;
        try { old = before is null ? null : JsonNode.Parse(before) as JsonObject; }
        catch { /* unreadable before: list everything as new */ }
        var now = JsonNode.Parse(after) as JsonObject ?? new JsonObject();
        foreach (var (key, value) in now)
        {
            string? a = old?[key]?.ToJsonString(), b = value?.ToJsonString();
            if (a == b) continue;
            yield return value is JsonObject
                ? $"{key} changed"
                : $"{key}: {Show(old?[key])} → {Show(value)}";
        }

        static string Show(JsonNode? v) => v is null ? "(not set)" : v is JsonValue j && j.TryGetValue<string>(out var s) ? s : v.ToJsonString();
    }
}
