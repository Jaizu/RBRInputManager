using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Net.Http.Json;

namespace RBRInputManager.Core;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string PageUrl, string Notes);

/// <summary>
/// Checks GitHub releases of Jaizu/RBRInputManager and swaps the running exe for a newer one.
/// A running exe can be renamed but not overwritten, so the update renames the current file to
/// *.old, puts the download in its place, restarts, and the next start deletes the *.old file.
/// </summary>
public static class UpdateService
{
    public const string Repo = "Jaizu/RBRInputManager";
    private const string AssetName = "RBRInputManager.exe";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return ParseVersion(info) ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RBRInputManager", "1"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Returns the latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();

        var release = await resp.Content.ReadFromJsonAsync<GhRelease>(cancellationToken: ct);
        if (release is null || release.Draft || release.Prerelease) return null;
        var version = ParseVersion(release.TagName);
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (version is null || asset is null || version <= CurrentVersion) return null;

        return new UpdateInfo(version, release.TagName, asset.BrowserDownloadUrl, release.HtmlUrl, release.Body ?? "");
    }

    /// <summary>Downloads the update, swaps the exe and starts the new version. The caller should then exit.</summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Updates can only be installed from the released RBRInputManager.exe.");

        string newFile = exe + ".new", oldFile = exe + ".old";
        using (var resp = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(newFile);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total.Value);
            }
        }

        if (File.Exists(oldFile)) File.Delete(oldFile);
        File.Move(exe, oldFile);
        try
        {
            File.Move(newFile, exe);
        }
        catch
        {
            File.Move(oldFile, exe); // put the working version back
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>Removes the previous exe left behind by an update. Safe to call on every start.</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var old = exe + ".old";
                for (int i = 0; i < 10 && File.Exists(old); i++)
                {
                    try { File.Delete(old); }
                    catch (IOException) { Thread.Sleep(300); } // old process may still be exiting
                }
            }
        }
        catch { /* not critical */ }
    }

    private static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '+', '-' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GhAsset> Assets { get; set; } = new();
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }
}
