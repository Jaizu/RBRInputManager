using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RBRInputManager.Core;

/// <summary>Done = an action that changed files finished (saved, applied, restored).</summary>
public enum LogLevel { Info, Done, Warning, Error }

/// <summary>The page an entry belongs to; the Logs page can show one page's entries. General is the app itself.</summary>
public enum LogArea { General, GameControls, RbrControls, ForceFeedback, Profiles, Settings }

/// <param name="Details">Extra lines shown under the message (changed bindings, files written, backup folder…).</param>
/// <param name="Trace">Exception stack trace: written to the file and copied, not shown in the app.</param>
public sealed record LogEntry(DateTime Time, LogLevel Level, LogArea Area, string Message, IReadOnlyList<string> Details, string? Trace)
{
    /// <summary>Order within its session: entries logged in the same second still sort the way they happened.</summary>
    public long Seq { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");
    public string AreaName => Log.Name(Area);
    public string LevelName => Level switch
    {
        LogLevel.Done => "Done",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        _ => "Info",
    };
}

/// <summary>A log file: one per run of the app.</summary>
public sealed record LogSession(string Path, DateTime Started, bool IsCurrent)
{
    public string Label => IsCurrent
        ? $"This session · {Started:HH:mm}"
        : Started.ToString("ddd d MMM yyyy · HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// The session log. Entries are kept in memory for the Logs page and appended to
/// %AppData%\RBRInputManager\Logs\RBRInputManager_&lt;start time&gt;.log, one file per run; the newest
/// <see cref="KeptSessions"/> files are kept. Logging never throws: an entry that can't be written is only lost.
/// </summary>
public static class Log
{
    public const int KeptSessions = 20;
    public static readonly string Dir = Path.Combine(AppSettings.DataDir, "Logs");

    private const string FilePrefix = "RBRInputManager_";
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";
    // Columns in the file: "HH:mm:ss  LEVEL  Area            message"; details start at the message column.
    private const int LevelColumn = 10, AreaColumn = 17, Indent = 33;
    private const int MaxEntries = 5000;

    private static readonly object Gate = new();
    private static readonly List<LogEntry> All = new();
    private static readonly HashSet<string> Reported = new();
    private static StreamWriter? _writer;
    private static long _seq;
    private static string _header = "";

    /// <summary>This session's file, or null if it could not be created.</summary>
    public static string? FilePath { get; private set; }

    public static DateTime Started { get; } = DateTime.Now;

    /// <summary>
    /// Page of the action in progress (set while a button's action runs). Low-level entries such as each file
    /// written are logged on it, so they show next to the action that caused them.
    /// </summary>
    public static LogArea Context { get; set; } = LogArea.General;

    /// <summary>Raised on the thread that logged.</summary>
    public static event Action<LogEntry>? Added;

    public static IReadOnlyList<LogEntry> Entries
    {
        get { lock (Gate) return All.ToList(); }
    }

    public static string Name(LogArea area) => area switch
    {
        LogArea.GameControls => "Game controls",
        LogArea.RbrControls => "RBR controls",
        LogArea.ForceFeedback => "Force feedback",
        LogArea.Profiles => "Profiles",
        LogArea.Settings => "Settings",
        _ => "App",
    };

    /// <summary>Opens this session's file, writes the header and removes the oldest sessions beyond <see cref="KeptSessions"/>.</summary>
    public static void Start()
    {
        _header = Header();
        try
        {
            Directory.CreateDirectory(Dir);
            string stamp = Started.ToString(StampFormat);
            string path = Path.Combine(Dir, $"{FilePrefix}{stamp}.log");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(Dir, $"{FilePrefix}{stamp}_{i}.log");

            // Shared so the file can be opened in an editor while the app runs.
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            lock (Gate)
            {
                _writer = new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
                _writer.Write(_header);
            }
            FilePath = path;

            // File names start with the start time, so the name order is the age order.
            foreach (var old in Files().Skip(KeptSessions))
            {
                try { File.Delete(old); }
                catch { /* still open in another copy of the app: removed next time */ }
            }
        }
        catch { /* no file this session; entries still show in the app */ }
    }

    public static void Stop()
    {
        Info(LogArea.General, "Session ended");
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static void Info(LogArea area, string message, IEnumerable<string>? details = null) =>
        Write(LogLevel.Info, area, message, details);

    public static void Done(LogArea area, string message, IEnumerable<string>? details = null) =>
        Write(LogLevel.Done, area, message, details);

    public static void Warn(LogArea area, string message, IEnumerable<string>? details = null) =>
        Write(LogLevel.Warning, area, message, details);

    /// <summary>A warning logged only the first time <paramref name="key"/> is seen this session (for problems met on every reload).</summary>
    public static void WarnOnce(string key, LogArea area, string message, Exception? ex = null, IEnumerable<string>? details = null)
    {
        lock (Gate)
            if (!Reported.Add(key)) return;
        Write(LogLevel.Warning, area, message, WithException(details, ex), ex?.ToString());
    }

    /// <summary>An error; the exception's type and message become a detail line, its stack trace goes to the file.</summary>
    public static void Error(LogArea area, string message, Exception? ex = null, IEnumerable<string>? details = null) =>
        Write(LogLevel.Error, area, message, WithException(details, ex), ex?.ToString());

    private static List<string> WithException(IEnumerable<string>? details, Exception? ex)
    {
        var lines = details?.ToList() ?? new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            lines.Add($"{e.GetType().Name}: {e.Message}");
        return lines;
    }

    public static void Write(LogLevel level, LogArea area, string message, IEnumerable<string>? details = null, string? trace = null)
    {
        // A detail may hold several lines (a binding list); each becomes its own line.
        var lines = (details ?? Enumerable.Empty<string>())
            .SelectMany(d => d.Split('\n'))
            .Select(d => d.TrimEnd('\r'))
            .Where(d => d.Trim().Length > 0)
            .ToList();
        LogEntry entry;
        lock (Gate)
        {
            entry = new LogEntry(DateTime.Now, level, area, message.Trim(), lines, trace) { Seq = ++_seq };
            All.Add(entry);
            if (All.Count > MaxEntries) All.RemoveRange(0, All.Count - MaxEntries);
            try { _writer?.Write(Format(entry)); }
            catch { /* disk full or the file was removed: keep running without it */ }
        }
        try { Added?.Invoke(entry); }
        catch { /* a listener's problem must not break the caller */ }
    }

    // ------------------------------------------------------------ earlier sessions

    private static IEnumerable<string> Files() =>
        Directory.Exists(Dir)
            ? Directory.GetFiles(Dir, $"{FilePrefix}*.log").OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    /// <summary>The kept log files, newest first; this session's first even if its file could not be created.</summary>
    public static List<LogSession> Sessions()
    {
        var list = new List<LogSession> { new(FilePath ?? "", Started, true) };
        foreach (var file in Files())
        {
            if (string.Equals(file, FilePath, StringComparison.OrdinalIgnoreCase)) continue;
            string stamp = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (stamp.Length > StampFormat.Length) stamp = stamp[..StampFormat.Length]; // "_2" when two started in the same second
            if (DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var started))
                list.Add(new(file, started, false));
        }
        return list;
    }

    /// <summary>Reads back a log file written by <see cref="Write"/>. Returns its header (for copying) and its entries.</summary>
    public static (string Header, List<LogEntry> Entries) Read(LogSession session)
    {
        string text;
        using (var s = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(s, Encoding.UTF8))
            text = reader.ReadToEnd();

        var lines = text.Replace("\r", "").Split('\n');
        int body = Array.FindIndex(lines, l => l.StartsWith('─')) + 1;
        string header = string.Join(Environment.NewLine, lines.Take(body)) + Environment.NewLine + Environment.NewLine;

        var entries = new List<LogEntry>();
        LogEntry? current = null;
        List<string>? details = null;
        StringBuilder? trace = null;
        void Flush()
        {
            if (current is not null) entries.Add(current with { Details = details!, Trace = trace?.ToString(), Seq = entries.Count + 1 });
            current = null;
        }

        string detail = new string(' ', Indent) + "· ", traced = new string(' ', Indent + 2);
        foreach (var line in lines.Skip(body))
        {
            if (line.Length > Indent && line[2] == ':' && char.IsDigit(line[0]) &&
                TimeSpan.TryParseExact(line[..8], @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var time))
            {
                Flush();
                string level = line[LevelColumn..(LevelColumn + 5)].Trim(), area = line[AreaColumn..(AreaColumn + 14)].Trim();
                current = new LogEntry(
                    session.Started.Date + time,
                    level switch { "DONE" => LogLevel.Done, "WARN" => LogLevel.Warning, "ERROR" => LogLevel.Error, _ => LogLevel.Info },
                    Enum.GetValues<LogArea>().FirstOrDefault(a => Name(a) == area),
                    line[Indent..], Array.Empty<string>(), null);
                details = new();
                trace = null;
            }
            else if (current is not null && line.StartsWith(detail)) details!.Add(line[detail.Length..]);
            // The first trace line is the message, which the file leaves out: keep an empty one in its place.
            else if (current is not null && line.StartsWith(traced)) (trace ??= new StringBuilder().AppendLine()).AppendLine(line.Trim());
        }
        Flush();
        return (header, entries);
    }

    // ------------------------------------------------------------ text format

    /// <summary>Entries as they appear in the log file, after a session header, for pasting into a report.</summary>
    public static string Export(IEnumerable<LogEntry> entries, string? header = null)
    {
        var sb = new StringBuilder(header ?? _header);
        foreach (var e in entries) sb.Append(Format(e));
        return sb.ToString();
    }

    private static string Header()
    {
        string version = UpdateService.CurrentVersion.ToString(3);
        return $"RBR Input Manager {version} · session started {Started:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
               $"{RuntimeInformation.OSDescription} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSArchitecture}{Environment.NewLine}" +
               $"Program:  {Environment.ProcessPath}{Environment.NewLine}" +
               $"App data: {AppSettings.DataDir}{Environment.NewLine}" +
               new string('─', 100) + Environment.NewLine + Environment.NewLine;
    }

    private static string Format(LogEntry e)
    {
        string level = e.Level switch
        {
            LogLevel.Done => "DONE",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO",
        };
        var sb = new StringBuilder();
        sb.Append($"{e.TimeText}  {level,-5}  {e.AreaName,-14}  {e.Message}").AppendLine();
        foreach (var d in e.Details) sb.Append(' ', Indent).Append("· ").Append(d).AppendLine();
        if (e.Trace is not null)
            foreach (var t in e.Trace.Split('\n').Skip(1).Select(t => t.Trim()).Where(t => t.Length > 0)) // the first line repeats the message
                sb.Append(' ', Indent + 2).Append(t).AppendLine();
        return sb.ToString();
    }
}
