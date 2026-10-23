using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;

namespace RBRInputManager.Core;

public sealed record BackedUpFile(
    string RelativePath, bool Existed, string? Sddl, FileAttributes Attributes,
    DateTime CreationTimeUtc, DateTime LastWriteTimeUtc, string? Sha256);

public sealed record BackupManifest(DateTime CreatedAtUtc, string RbrRoot, string AppVersion, List<BackedUpFile> Files);

public sealed record RestoreResult(int Restored, int Unchanged, List<string> Warnings);

/// <summary>
/// One-time snapshot of the game files the app can modify, taken the first time the app sees
/// an install: contents plus permissions (SDDL), attributes and timestamps.
/// Stored in %AppData%\RBRInputManager\Originals\&lt;install-id&gt;\ and never overwritten.
/// </summary>
public static class OriginalBackup
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string DirFor(string rbrRoot)
    {
        // One snapshot per install folder.
        var id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Path.GetFullPath(rbrRoot).TrimEnd('\\').ToUpperInvariant())))[..12];
        return Path.Combine(AppSettings.DataDir, "Originals", id);
    }

    private static string ManifestPath(string rbrRoot) => Path.Combine(DirFor(rbrRoot), "manifest.json");

    public static BackupManifest? Load(string rbrRoot)
    {
        var path = ManifestPath(rbrRoot);
        return File.Exists(path) ? JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path)) : null;
    }

    /// <summary>Creates the snapshot if this install has none yet. Returns the new manifest, or null if one already existed.</summary>
    public static BackupManifest? EnsureCreated(RbrInstallation rbr, string appVersion)
    {
        if (Load(rbr.Root) is not null) return null;

        string dir = DirFor(rbr.Root);
        string staging = dir + ".tmp";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        var files = new List<BackedUpFile>();
        foreach (var rel in RbrInstallation.ManagedFiles)
        {
            var src = new FileInfo(rbr.Path(rel));
            if (!src.Exists)
            {
                files.Add(new(rel, false, null, 0, default, default, null));
                continue;
            }
            string dst = Path.Combine(staging, "files", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src.FullName, dst);

            string sddl = src.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
            files.Add(new(rel, true, sddl, src.Attributes, src.CreationTimeUtc, src.LastWriteTimeUtc, Hash(src.FullName)));
        }

        var manifest = new BackupManifest(DateTime.UtcNow, rbr.Root, appVersion, files);
        File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        Directory.Move(staging, dir); // the snapshot only becomes visible once complete
        return manifest;
    }

    /// <summary>Puts every backed-up file back with its original contents, attributes, timestamps and permissions.</summary>
    public static RestoreResult Restore(RbrInstallation rbr)
    {
        var manifest = Load(rbr.Root) ?? throw new InvalidOperationException("There is no backup of the original files for this install.");
        string dir = DirFor(rbr.Root);
        int restored = 0, unchanged = 0;
        var warnings = new List<string>();

        foreach (var f in manifest.Files)
        {
            string target = rbr.Path(f.RelativePath);
            if (!f.Existed)
            {
                if (File.Exists(target))
                    warnings.Add($"{f.RelativePath} did not exist originally; left as is.");
                continue;
            }

            string saved = Path.Combine(dir, "files", f.RelativePath);
            if (File.Exists(target) && Hash(target) == f.Sha256 && File.GetAttributes(target) == f.Attributes)
            {
                unchanged++;
                ApplySecurity(target, f, warnings);
                continue;
            }

            GameFiles.WriteInPlace(target, File.ReadAllBytes(saved));
            File.SetAttributes(target, FileAttributes.Normal); // timestamps can't be set on read-only files
            File.SetCreationTimeUtc(target, f.CreationTimeUtc);
            File.SetLastWriteTimeUtc(target, f.LastWriteTimeUtc);
            ApplySecurity(target, f, warnings);
            File.SetAttributes(target, f.Attributes);
            restored++;
        }
        return new(restored, unchanged, warnings);
    }

    private static void ApplySecurity(string target, BackedUpFile f, List<string> warnings)
    {
        if (f.Sddl is null) return;
        var info = new FileInfo(target);
        var current = info.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var wanted = new FileSecurity();
        wanted.SetSecurityDescriptorSddlForm(f.Sddl, AccessControlSections.Access);
        if (wanted.GetSecurityDescriptorSddlForm(AccessControlSections.Access) != current)
        {
            try { info.SetAccessControl(wanted); }
            catch (Exception ex) { warnings.Add($"{f.RelativePath}: could not restore permissions ({ex.Message})."); }
        }

        // Owner/group usually need admin rights to change; only try when they actually differ.
        var full = new FileSecurity();
        full.SetSecurityDescriptorSddlForm(f.Sddl, AccessControlSections.Owner | AccessControlSections.Group);
        var now = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group);
        if (full.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group) !=
            now.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group))
        {
            try { info.SetAccessControl(full); }
            catch (Exception ex) { warnings.Add($"{f.RelativePath}: could not restore the owner ({ex.Message}). Run as administrator to restore it."); }
        }
    }

    private static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}
