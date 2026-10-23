using System.IO;
using System.Text;

namespace RBRInputManager.Core;

/// <summary>Writes into game files without disturbing their permissions or formatting.</summary>
public static class GameFiles
{
    // RBR's INIs are plain ANSI; Latin-1 round-trips every byte unchanged.
    private static readonly Encoding Ansi = Encoding.Latin1;

    /// <summary>
    /// Overwrites the contents in place (truncate + write) so the existing file keeps its ACL,
    /// owner and creation time. A read-only attribute is lifted for the write and put back.
    /// </summary>
    public static void WriteInPlace(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var attrs = File.Exists(path) ? File.GetAttributes(path) : (FileAttributes?)null;
        if (attrs is { } a && a.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        try
        {
            using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            fs.SetLength(0);
            fs.Write(content);
        }
        finally
        {
            if (attrs is { } orig) File.SetAttributes(path, orig);
        }
    }

    /// <summary>
    /// Sets INI values while keeping everything else byte-for-byte: comments, order, spacing
    /// around '=', line endings. Missing keys are appended to their section (created if needed).
    /// </summary>
    public static void SetIniValues(string path, IEnumerable<(string Section, string Key, string Value)> values,
        bool onlyExistingKeys = false)
    {
        string text = File.Exists(path) ? Ansi.GetString(File.ReadAllBytes(path)) : "";
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Split(nl).ToList();
        bool trailingNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (trailingNewline) lines.RemoveAt(lines.Count - 1);

        foreach (var (section, key, value) in values)
        {
            int start = -1, end = lines.Count, lastKey = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    if (start >= 0) { end = i; break; }
                    if (string.Equals(t[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase)) start = i;
                }
                else if (start >= 0 && t.Length > 0 && t[0] != ';') lastKey = i;
            }

            bool done = false;
            if (start >= 0)
            {
                for (int i = start + 1; i < end && !done; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0 || !string.Equals(lines[i][..eq].Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                    string afterEq = lines[i][(eq + 1)..];
                    string pad = afterEq[..(afterEq.Length - afterEq.TrimStart().Length)];
                    lines[i] = lines[i][..(eq + 1)] + pad + value;
                    done = true;
                }
            }
            if (done || onlyExistingKeys) continue;

            if (start < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add($"[{section}]");
                lines.Add($"{key}={value}");
            }
            else
            {
                lines.Insert((lastKey >= 0 ? lastKey : start) + 1, $"{key}={value}");
            }
        }

        string result = string.Join(nl, lines) + (trailingNewline ? nl : "");
        WriteInPlace(path, Ansi.GetBytes(result));
    }
}
