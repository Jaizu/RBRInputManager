using System.IO;

namespace RBRInputManager.Core;

/// <summary>
/// Minimal read-only INI parser. Keeps key order, lookups are case-insensitive.
/// Only full-line comments (';' or '#') are skipped because RBRControls values
/// legitimately start with '#' after the '=' sign.
/// </summary>
public sealed class IniFile
{
    private readonly Dictionary<string, List<KeyValuePair<string, string>>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; }

    private IniFile(string path) => Path = path;

    public static IniFile Load(string path)
    {
        var ini = new IniFile(path);
        var current = ini.GetOrAddSection("");
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

            if (line[0] == '[' && line.EndsWith(']'))
            {
                current = ini.GetOrAddSection(line[1..^1].Trim());
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            current.Add(new(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return ini;
    }

    public static IniFile? TryLoad(string path) => File.Exists(path) ? Load(path) : null;

    private List<KeyValuePair<string, string>> GetOrAddSection(string name)
    {
        if (!_sections.TryGetValue(name, out var list))
            _sections[name] = list = new();
        return list;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Section(string name) =>
        _sections.TryGetValue(name, out var list) ? list : Array.Empty<KeyValuePair<string, string>>();

    public IEnumerable<string> SectionNames => _sections.Keys.Where(k => k.Length > 0);

    public string? Get(string section, string key)
    {
        foreach (var kv in Section(section))
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }
}
