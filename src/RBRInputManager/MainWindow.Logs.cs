using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using RBRInputManager.Core;
using Wpf.Ui.Controls;

namespace RBRInputManager;

/// <summary>The Logs page: this session's log or an earlier one, for one page of the app or all of them.</summary>
public partial class MainWindow
{
    private sealed record LogAreaItem(LogArea? Area, string Label);

    // Page order, as the tabs; the app's own entries last.
    private static readonly LogArea[] LogAreas =
        { LogArea.GameControls, LogArea.RbrControls, LogArea.ForceFeedback, LogArea.Profiles, LogArea.Settings, LogArea.General };

    private LogArea? _logArea;
    private bool _logAreaChosen;
    private bool _fillingLog;
    // An earlier session read from its file: its header (for Copy) and entries.
    private (LogSession Session, string Header, List<LogEntry> Entries)? _pastLog;
    private List<LogEntry> _logShown = new();
    // Column sort picked in the grid, kept when the entries are refreshed; null shows them as logged.
    private (string Member, ListSortDirection Direction)? _logSort;

    /// <summary>Opens the Logs page on one page's entries (null: every page) of this session.</summary>
    private void ShowLogsPage(LogArea? area)
    {
        _logArea = area;
        _logAreaChosen = true;
        if (Tabs.SelectedItem == LogsTab) OnLogsTabShown();
        else Tabs.SelectedItem = LogsTab;
    }

    /// <summary>The Logs tab opens on every page's entries, unless a page was asked for (a click on the status bar).</summary>
    private void OnLogsTabShown()
    {
        if (!_logAreaChosen) _logArea = null;
        bool showCurrent = _logAreaChosen || LogSessionBox.SelectedItem is null;
        _logAreaChosen = false;

        // Files can come and go (another copy of the app pruning them), so list them each time.
        var sessions = Log.Sessions();
        var selected = showCurrent ? null : LogSessionBox.SelectedItem as LogSession;
        _fillingLog = true;
        LogSessionBox.ItemsSource = sessions;
        LogSessionBox.SelectedItem = sessions.FirstOrDefault(s => s.Path == selected?.Path) ?? sessions[0];
        _fillingLog = false;
        RefreshLog(scrollToEnd: true);
    }

    private void OnLogAdded()
    {
        if (Tabs.SelectedItem != LogsTab || LogSessionBox.SelectedItem is not LogSession { IsCurrent: true }) return;
        // Follow new entries only when already at the end, so reading further up isn't interrupted.
        var scroller = FindDescendant<ScrollViewer>(LogGrid);
        RefreshLog(scrollToEnd: scroller is null || scroller.VerticalOffset >= scroller.ScrollableHeight - 1);
    }

    private void RefreshLog(bool scrollToEnd)
    {
        if (LogSessionBox.SelectedItem is not LogSession session) return;
        List<LogEntry> entries;
        string? error = null;
        if (session.IsCurrent) entries = Log.Entries.ToList();
        else
        {
            if (_pastLog?.Session != session)
            {
                try
                {
                    var (header, read) = Log.Read(session);
                    _pastLog = (session, header, read);
                }
                catch (Exception ex)
                {
                    _pastLog = null;
                    error = $"Could not read {session.Path}: {ex.Message}";
                }
            }
            entries = _pastLog?.Entries ?? new();
        }

        var items = new List<LogAreaItem> { new(null, $"All pages ({entries.Count})") };
        items.AddRange(LogAreas.Select(a => new LogAreaItem(a, $"{Log.Name(a)} ({entries.Count(e => e.Area == a)})")));
        _fillingLog = true;
        LogAreaBox.ItemsSource = items;
        LogAreaBox.SelectedItem = items.First(i => i.Area == _logArea);
        _fillingLog = false;

        _logShown = entries.Where(e => _logArea is null || e.Area == _logArea).ToList();
        LogGrid.ItemsSource = _logShown;
        ApplyLogSort();
        foreach (var c in LogGrid.Columns)
            c.SortDirection = _logSort is { } sort && c.SortMemberPath == sort.Member ? sort.Direction : null;
        LogCountText.Text = _logShown.Count == 1 ? "(1 entry)" : $"({_logShown.Count} entries)";
        LogEmpty.Visibility = _logShown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LogEmptyText.Text = error is not null ? "This log can't be read"
            : _logArea is { } area ? $"Nothing logged for {Log.Name(area)} in this session" : "Nothing logged in this session";
        LogFileText.Text = error
            ?? (session.IsCurrent
                ? Log.FilePath is { } path
                    ? $"This session is saved to {path}. The last {Log.KeptSessions} sessions are kept in that folder."
                    : $"This session could not be saved to a file. Earlier sessions are in {Log.Dir}."
                : session.Path);

        // The newest entry: at the end as logged, at the top when sorted newest first.
        if (scrollToEnd && _logShown.Count > 0)
            Dispatcher.BeginInvoke(() =>
            {
                var view = CollectionViewSource.GetDefaultView(LogGrid.ItemsSource);
                var last = view?.Cast<object>().LastOrDefault();
                if (last is not null) LogGrid.ScrollIntoView(last);
            }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Sorts by the clicked column, then by when each entry happened (same direction), so equal values
    /// (the same second, page or level) keep their real order instead of an arbitrary one.
    /// </summary>
    private void LogGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in LogGrid.Columns) c.SortDirection = null;
        e.Column.SortDirection = direction;
        _logSort = (e.Column.SortMemberPath, direction);
        ApplyLogSort();
    }

    private void ApplyLogSort()
    {
        var view = CollectionViewSource.GetDefaultView(LogGrid.ItemsSource);
        if (view is null) return;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            if (_logSort is not { } sort) return;
            var (member, direction) = sort;
            if (member != nameof(LogEntry.Seq)) view.SortDescriptions.Add(new SortDescription(member, direction));
            view.SortDescriptions.Add(new SortDescription(nameof(LogEntry.Seq), direction));
        }
    }

    private void LogSession_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingLog) RefreshLog(scrollToEnd: true);
    }

    private void LogArea_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingLog || LogAreaBox.SelectedItem is not LogAreaItem item) return;
        _logArea = item.Area;
        RefreshLog(scrollToEnd: true);
    }

    private async void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        bool copied;
        try
        {
            bool past = LogSessionBox.SelectedItem is LogSession { IsCurrent: false };
            Clipboard.SetText(Log.Export(_logShown, past ? _pastLog?.Header : null));
            copied = true;
        }
        catch { copied = false; /* another app is holding the clipboard */ }

        CopyLogButton.Content = copied ? "Copied" : "Clipboard busy, try again";
        CopyLogButton.Icon = new SymbolIcon(copied ? SymbolRegular.Checkmark24 : SymbolRegular.Warning24);
        await Task.Delay(1800);
        CopyLogButton.Content = "Copy";
        CopyLogButton.Icon = new SymbolIcon(SymbolRegular.Copy24);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Dir);
        string? file = (LogSessionBox.SelectedItem as LogSession)?.Path;
        string args = !string.IsNullOrEmpty(file) && File.Exists(file) ? $"/select,\"{file}\"" : $"\"{Log.Dir}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
