using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shape = System.Windows.Shapes.Shape;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using Microsoft.Win32;
using RBRInputManager.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace RBRInputManager;

/// <summary>Drives the dot next to device names: green connected, orange seen this month, red missing.</summary>
public enum LinkState { None, Ok, Recent, Missing }

public sealed record GameBindingRow(int ActionId, int? BindingIndex, string Action, string Device, string Input, string Inverted,
    string Status, LinkState State, bool IsUnbound, bool IsChanged);
public sealed record ProfileDeviceRow(string Group, string Index, string Device, string? VidPid, string Guid, string Status, LinkState State);
public sealed record PluginBindingRow(string Key, string Page, string Function, string Device, string Input, string Trigger, string GameMode,
    string Status, LinkState State, bool IsUnbound, bool IsChanged);
public sealed record MapItem(ActionMap Map, string Label);

public partial class MainWindow : FluentWindow
{
    private readonly AppSettings _settings = AppSettings.Load();
    private RbrInstallation? _rbr;
    private DeviceCatalog? _devices;
    private ActionMapFile? _acm;
    private List<GameBindingRow> _gameRows = new();
    private List<PluginBindingRow> _pluginRows = new();
    private List<PluginBinding> _pluginDefs = new();
    private BindingEditor? _editor;
    private InputCapture? _capture;
    private InputCapture? _monitor;
    private bool _closeConfirmed;
    private UpdateInfo? _update;
    private bool _initializing = true;
    private bool _reloading;
    private bool _fillingProfiles;
    private bool _syncingProfileBox;
    private int _pageBeforeSettings = 1; // Game controls (tab 0 is Info)
    private readonly System.Windows.Threading.DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private HashSet<int> _invertedActions = new();

    static MainWindow()
    {
        // Pages scroll as a whole; grids and lists inside them show all rows and must pass the wheel up.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.DataGrid), PreviewMouseWheelEvent, new MouseWheelEventHandler(ForwardWheel));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ListView), PreviewMouseWheelEvent, new MouseWheelEventHandler(ForwardWheel));
    }

    private static void ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement el || VisualTreeHelper.GetParent(el) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    public MainWindow()
    {
        InitializeComponent();
        Tabs.SelectedItem = GameTab; // the Info tab comes first but never opens by default

        ThemeBox.SelectedIndex = (int)_settings.Theme;
        LocateSwitch.IsChecked = _settings.LocateInputs;
        GameBoundOnlyBox.IsChecked = _settings.GameBoundOnly;
        OnlyBoundBox.IsChecked = _settings.PluginBoundOnly;
        ApplicationThemeManager.Changed += (_, _) => ApplyAccent();
        _liveTimer.Tick += (_, _) => UpdateLiveInputs();
        _liveTimer.Start();
        Activated += (_, _) => UpdateMonitorPause();
        Deactivated += (_, _) => UpdateMonitorPause();
        VersionText.Text = $"Version {UpdateService.CurrentVersion.ToString(3)} · github.com/{UpdateService.Repo}";
        SetUpdateState(UpdateState.Checking);

        SourceInitialized += (_, _) =>
        {
            if (_settings.Window is not { } bounds ||
                !WindowPlacement.Restore(new System.Windows.Interop.WindowInteropHelper(this).Handle, bounds)) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (bounds.Maximized) WindowState = WindowState.Maximized;
        };
        Loaded += async (_, _) =>
        {
            ApplyTheme(_settings.Theme);
            _initializing = false;

            string? root = RbrInstallation.IsValid(_settings.RbrPath) ? _settings.RbrPath : RbrInstallation.Detect();
            if (root is null)
                SetStatus("RBR install not found. Pick the folder in Settings → RBR install folder.");
            else
                Open(root);

            await CheckForUpdatesAsync(userInitiated: false);
        };
        Closed += (_, _) =>
        {
            _liveTimer.Stop();
            _capture?.Dispose();
            _monitor?.Dispose();
            _devices?.Dispose();
        };
        Closing += async (_, e) =>
        {
            if (WindowPlacement.Read(new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } bounds)
            {
                _settings.Window = bounds;
                _settings.Save();
            }
            if (_closeConfirmed || _editor?.IsDirty != true) return;
            e.Cancel = true;
            if (await Confirm("Discard unsaved changes?", $"You have {Plural(_editor.ChangeCount, "unsaved change")} to the bindings.", "Discard and close"))
            {
                _closeConfirmed = true;
                Close();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            // While capturing, keys are input to bind; Esc cancels.
            if (CaptureOverlay.Visibility != Visibility.Visible) return;
            if (e.Key == Key.Escape) StopCapture();
            e.Handled = true;
        };
    }

    // ---------------------------------------------------------------- install & loading

    private void Open(string root)
    {
        _rbr = new RbrInstallation(root, _settings.RbrProfile);
        _settings.RbrPath = root;
        _settings.Save();

        InstallText.Text = $"Richard Burns Rally · {root}";
        string how = _rbr.Profiles.Count switch
        {
            0 => "not created yet",
            1 => "the only one in SavedGames",
            _ when _rbr.ProfileChosen => "picked in Settings",
            _ => "the most recently played; change it in Settings",
        };
        InstallSubText.Text = $"RBR profile {_rbr.Profile} ({how})";
        SettingsPathText.Text = root;
        SettingsProfileText.Text = $"Bindings are read from SavedGames\\pf{_rbr.Profile}.acm. " +
            (_rbr.Profiles.Count > 1 ? "By default the most recently played profile is used." : "This is the only profile in this install.");
        _fillingProfiles = true;
        RbrProfileBox.ItemsSource = _rbr.Profiles.Count > 0 ? _rbr.Profiles : new[] { _rbr.Profile };
        RbrProfileBox.SelectedItem = _rbr.Profile;
        RbrProfileBox.IsEnabled = _rbr.Profiles.Count > 1;
        _fillingProfiles = false;

        // First time we see this install: keep a pristine copy before the app ever writes to it.
        string? backupNote = null;
        try
        {
            if (OriginalBackup.EnsureCreated(_rbr, UpdateService.CurrentVersion.ToString(3)) is { } created)
                backupNote = $"Original game files backed up ({created.Files.Count(f => f.Existed)} files with their permissions).";
        }
        catch (Exception ex)
        {
            backupNote = $"Could not back up the original game files: {ex.Message}";
        }
        RefreshBackupInfo();

        Reload();
        if (backupNote is not null) SetStatus(backupNote);
    }

    private void Reload()
    {
        if (_rbr is null) return;
        _devices?.Dispose();
        _devices = new DeviceCatalog();

        var errors = new List<string>();
        void Try(string what, Action load)
        {
            try { load(); }
            catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
        }

        _editor = null;
        Try("Controls", LoadGameBindings);
        Try("RBRControls", LoadPlugin);
        if (_acm is not null)
            _editor = new BindingEditor(_acm, _pluginDefs.ToDictionary(d => d.Key, d => d.RawBinding, StringComparer.OrdinalIgnoreCase));
        ShowMap((MapBox.SelectedItem as MapItem)?.Map);
        BuildPluginRows();
        UpdatePendingBar();
        Try("Force feedback", LoadFfb);
        Try("Default profile", () => ProfileStore.EnsureDefault(_rbr, _devices));
        LoadProfiles();
        RestartMonitor();
        LoadInvertedAxes();

        if (_devices.DirectInputError is not null)
            errors.Add($"DirectInput: {_devices.DirectInputError}");

        SetStatus(errors.Count == 0
            ? $"Loaded {DateTime.Now:HH:mm:ss} · {_devices.Attached.Count} controllers connected"
            : string.Join("   |   ", errors));
        WarmUpPages();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    // ---------------------------------------------------------------- game bindings

    private void LoadGameBindings()
    {
        _acm = null;
        MapBox.ItemsSource = null;
        ShowMap(null);
        if (!File.Exists(_rbr!.Acm))
            throw new FileNotFoundException($"No RBR profile found (SavedGames\\pf*.acm). Start RBR once through the RSF launcher so it creates its profile.");

        _acm = ActionMapFile.Load(_rbr.Acm);
        var active = _acm.ActiveMap(_devices!.AttachedGuids);
        var items = _acm.Maps
            .Select(m => new MapItem(m, $"Map {m.Index + 1}{(m.Index == active?.Index ? " · active" : "")}"))
            .ToList();
        MapBox.ItemsSource = items;
        MapBox.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        MapBox.SelectedItem = items.FirstOrDefault(i => i.Map.Index == active?.Index) ?? items.LastOrDefault();
    }

    private void MapBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowMap((MapBox.SelectedItem as MapItem)?.Map);

    private void ShowMap(ActionMap? map)
    {
        GameGrid.ItemsSource = null;
        _gameRows = new();
        ProfileDevicesGrid.ItemsSource = null;
        BindingCountText.Text = MapDeviceCountText.Text = "";
        MapNotice.Visibility = Visibility.Collapsed;
        CopyMapButton.Visibility = AdaptMapButton.Visibility = Visibility.Collapsed;
        if (map is null || _acm is null || _devices is null) return;

        var saved = map;
        map = _editor?.Maps[map.Index] ?? map; // show pending edits
        var input = IniFile.TryLoad(_rbr!.InputIni);
        var devices = map.Devices.Select(_devices.Resolve).ToList();
        var indexed = map.Bindings.Select((b, i) => (b, i)).ToList();

        var rows = new List<GameBindingRow>();
        foreach (var action in RbrActions.All)
        {
            var binds = indexed.Where(x => x.b.ActionId == action.Id).ToList();
            string inverted = action.InvertKey is null ? "" :
                string.Equals(input?.Get("Settings", action.InvertKey), "true", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
            bool changed = _editor?.IsActionChanged(map.Index, action.Id) == true;

            if (binds.Count == 0)
            {
                rows.Add(new(action.Id, null, action.Name, "—", "Unbound", "", "", LinkState.None, true, changed));
                continue;
            }
            foreach (var (b, i) in binds)
                rows.Add(BindingRow(action.Name, b, i, devices, inverted, changed));
        }
        // Bindings with action IDs outside the known table (e.g. added by plugins).
        foreach (var (b, i) in indexed.Where(x => x.b.ActionId >= RbrActions.All.Count))
            rows.Add(BindingRow(RbrActions.Get(b.ActionId).Name, b, i, devices, "", false));

        _gameRows = rows;
        ApplyGameFilter();
        // The map's devices in RBR's order (orange/red dot when missing), then every other connected controller.
        const string InMap = "In this map", Others = "Other connected devices";
        var deviceRows = devices
            .Select((d, i) => new ProfileDeviceRow(InMap, $"{i}", d.Name, d.VidPid, d.InstanceGuid.ToString("B").ToUpperInvariant(),
                d.Status, Link(d)))
            .ToList();
        var inMap = map.Devices.ToHashSet();
        deviceRows.AddRange(_devices.AttachedGuids
            .Where(g => !inMap.Contains(g))
            .Select(_devices.Resolve)
            .OrderBy(d => d.Name)
            .Select(d => new ProfileDeviceRow(Others, "", d.Name, d.VidPid, d.InstanceGuid.ToString("B").ToUpperInvariant(),
                d.Status, LinkState.Ok)));
        var deviceView = new ListCollectionView(deviceRows);
        deviceView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProfileDeviceRow.Group)));
        ProfileDevicesGrid.ItemsSource = deviceView;
        BindingCountText.Text = $"({Plural(map.Bindings.Count, "binding")})";
        MapDeviceCountText.Text = $"({map.Devices.Count} in map · {deviceRows.Count - map.Devices.Count} other)";

        var active = _acm.ActiveMap(_devices.AttachedGuids);
        string? notice = active is null
            ? NoMatchNotice(saved)
            : map.Index != active.Index
                ? $"RBR is not using this map: it only applies when exactly these controllers are connected. With the current controllers RBR uses map {active.Index + 1}."
                : null;
        if (notice is not null)
        {
            MapNoticeText.Text = notice;
            MapNotice.Visibility = Visibility.Visible;
        }
        if (active is not null && map.Index != active.Index && map.Bindings.Count > 0)
        {
            CopyMapButton.Content = $"Copy to map {active.Index + 1}";
            CopyMapButton.Visibility = Visibility.Visible;
        }
        if (active is null && saved.Bindings.Count > 0)
            AdaptMapButton.Visibility = Visibility.Visible;
    }

    private string NoMatchNotice(ActionMap map)
    {
        var (add, remove) = MapAdapter.Diff(map, _devices!.AttachedGuids);
        var parts = new List<string>();
        if (add.Count > 0) parts.Add($"{string.Join(", ", add.Select(g => _devices.Resolve(g).Name))} {(add.Count == 1 ? "is" : "are")} connected but not in this map");
        if (remove.Count > 0) parts.Add($"{string.Join(", ", remove.Select(g => _devices.Resolve(g).Name))} {(remove.Count == 1 ? "is" : "are")} in this map but not connected");
        return "No map matches the controllers connected right now" +
               (parts.Count > 0 ? $" ({string.Join("; ", parts)})" : "") +
               ". RBR creates a new map with default bindings the next time it starts, unless this map is set to use these controllers.";
    }

    private async void AdaptMap_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null || _acm is null || _devices is null || (MapBox.SelectedItem as MapItem)?.Map is not { } map) return;
        if (_acm.ActiveMap(_devices.AttachedGuids) is not null) return;
        if (!await ConfirmDiscardEdits() || !await EnsureGameClosed()) return;

        var (add, remove) = MapAdapter.Diff(map, _devices.AttachedGuids);
        var lines = new List<string>();
        if (add.Count > 0) lines.Add("Add: " + string.Join(", ", add.Select(g => _devices.Resolve(g).Name)));
        if (remove.Count > 0)
        {
            var removeSet = remove.ToHashSet();
            int lost = map.Bindings.Count(b => b.DeviceIndex >= 0 && b.DeviceIndex < map.Devices.Count && removeSet.Contains(map.Devices[b.DeviceIndex]));
            lines.Add("Remove: " + string.Join(", ", remove.Select(g => _devices.Resolve(g).Name)) +
                      (lost > 0 ? $" (its {Plural(lost, "binding")} will be lost)" : ""));
        }
        var content = new System.Windows.Controls.TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"RBR picks a map by the exact set of connected controllers. Map {map.Index + 1}'s device list will be changed " +
                   "to match the controllers connected now, so RBR uses its bindings instead of creating a default map.\n\n" +
                   string.Join("\n", lines) + "\n\nThe current controls file is backed up first.",
        };

        if (!await Confirm($"Use map {map.Index + 1} with these controllers?", content, "Update map"))
            return;

        await Run(async () =>
        {
            var result = MapAdapter.Adapt(_rbr, _acm, map, _devices.AttachedGuids, g => _devices.Resolve(g).Name);
            Reload();
            SetStatus($"Map {map.Index + 1} now matches the connected controllers. Backup: {result.BackupPath}");
            if (result.Dropped.Count > 0)
                await Info("Some bindings were removed",
                    "These used a controller that is not connected, so they were removed from the map:\n\n" +
                    string.Join("\n", result.Dropped));
        });
    }

    private void ApplyGameFilter() =>
        GameGrid.ItemsSource = GameBoundOnlyBox.IsChecked == true ? _gameRows.Where(r => !r.IsUnbound).ToList() : _gameRows;

    private void GameBoundOnly_Click(object sender, RoutedEventArgs e)
    {
        _settings.GameBoundOnly = GameBoundOnlyBox.IsChecked == true;
        _settings.Save();
        ApplyGameFilter();
    }

    private async void CopyMap_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null || _acm is null || _devices is null || (MapBox.SelectedItem as MapItem)?.Map is not { } source) return;
        if (_acm.ActiveMap(_devices.AttachedGuids) is not { } target || target == source) return;
        if (!await ConfirmDiscardEdits() || !await EnsureGameClosed()) return;

        var keep = new CheckBox
        {
            Content = $"Keep map {target.Index + 1}'s bindings for actions map {source.Index + 1} leaves unbound",
            Margin = new Thickness(0, 14, 0, 0),
        };
        var content = new StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Map {target.Index + 1} is the one RBR uses with the controllers connected now. Its bindings will be replaced " +
                   $"with the {Plural(source.Bindings.Count, "binding")} of map {source.Index + 1}, moved to the same devices.\n\n" +
                   "The current controls file is backed up first.",
        });
        content.Children.Add(keep);

        if (!await Confirm($"Copy map {source.Index + 1} into map {target.Index + 1}?", content, "Copy bindings"))
            return;

        await Run(async () =>
        {
            var result = MapCopier.Copy(_rbr, _acm, source, target, keep.IsChecked == true,
                g => _devices.Resolve(g).VidPid, g => _devices.Resolve(g).Name);
            Reload();
            string msg = $"Copied {Plural(result.Copied, "binding")} into map {target.Index + 1}" +
                         (result.Kept > 0 ? $", kept {result.Kept}" : "") + $". Backup: {result.BackupPath}";
            SetStatus(msg);
            if (result.Skipped.Count > 0)
                await Info("Some bindings were not copied",
                    $"These use a device that is not in map {target.Index + 1} (not connected now), so RBR could not use them there:\n\n" +
                    string.Join("\n", result.Skipped));
        });
    }

    private GameBindingRow BindingRow(string action, ActionBinding b, int index, List<DeviceInfo> devices, string inverted, bool changed)
    {
        if (b.DeviceIndex < 0 || b.DeviceIndex >= devices.Count)
            return new(b.ActionId, index, action, $"Invalid device index ({b.DeviceIndex})", $"0x{b.ObjectType:X8}", inverted, "The map has no device at this index",
                LinkState.Missing, false, changed);

        var dev = devices[b.DeviceIndex];
        bool keyboard = dev.InstanceGuid == DirectInputNames.SysKeyboard;
        string generic = DirectInputNames.Describe(b.ObjectType, keyboard);
        string? real = keyboard ? null : _devices!.ObjectName(dev.InstanceGuid, b.ObjectType);
        string inputText = real is null || string.Equals(real.Trim(), generic, StringComparison.OrdinalIgnoreCase)
            ? real?.Trim() ?? generic
            : $"{real}  ({generic})";

        return new(b.ActionId, index, action, dev.Name, inputText, inverted, dev.Status, Link(dev), false, changed);
    }

    private static LinkState Link(DeviceInfo d) =>
        d.Connected ? LinkState.Ok : d.RecentlySeen ? LinkState.Recent : LinkState.Missing;

    // ---------------------------------------------------------------- editing

    private int? ShownMapIndex => (MapBox.SelectedItem as MapItem)?.Map.Index;

    private void RefreshEdits()
    {
        ShowMap((MapBox.SelectedItem as MapItem)?.Map);
        BuildPluginRows();
        UpdatePendingBar();
    }

    private void UpdatePendingBar()
    {
        int n = _editor?.ChangeCount ?? 0;
        PendingBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingText.Text = $"{Plural(n, "unsaved change")} to the bindings";
    }

    private static T? RowOf<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void BindAction_Click(object sender, RoutedEventArgs e) => BindAction(RowOf<GameBindingRow>(sender), add: false);

    private void AddAction_Click(object sender, RoutedEventArgs e) => BindAction(RowOf<GameBindingRow>(sender), add: true);

    private void BindAction(GameBindingRow? row, bool add)
    {
        if (row is null || _editor is null || ShownMapIndex is not int map) return;
        var action = RbrActions.Get(row.ActionId);
        string what = action.Accepts switch
        {
            InputKind.Axis => "Move an axis",
            InputKind.Button => "Press a button or key",
            _ => "Move an axis or press a button / key",
        };
        StartCapture(add ? $"Add a binding for {action.Name}" : $"Bind {action.Name}", what, action.Accepts, async input =>
        {
            var conflicts = _editor.Conflicts(map, input, action.Id);
            var removeFrom = new List<int>();
            if (conflicts.Count > 0)
            {
                string names = string.Join(", ", conflicts.Select(c => RbrActions.Get(c).Name));
                var choice = await Ask("Input already in use", $"{input.DeviceName} · {input.Label} is already bound to {names}.",
                    "Move it here", "Use it for both");
                if (choice == MessageBoxResult.None) return;
                if (choice == MessageBoxResult.Primary) removeFrom = conflicts;
            }
            _editor.Bind(map, action.Id, input, add ? null : row.BindingIndex, removeFrom);
            RefreshEdits();
            SetStatus($"{action.Name} → {input.DeviceName} · {input.Label} (not saved yet)");
        });
    }

    private void ClearAction_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<GameBindingRow>(sender) is not { BindingIndex: int index } row || _editor is null || ShownMapIndex is not int map) return;
        _editor.Unbind(map, index);
        RefreshEdits();
        SetStatus($"{row.Action}: binding removed (not saved yet)");
    }

    private void BindPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<PluginBindingRow>(sender) is not { } row || _editor is null) return;
        StartCapture($"Bind {row.Function}", "Press a button or key", InputKind.Button, async input =>
        {
            string value = BindingEditor.PluginBindValue(input);
            var conflicts = _editor.PluginConflicts(value, row.Key);
            if (conflicts.Count > 0)
            {
                string names = string.Join(", ", conflicts.Select(k => _pluginDefs.FirstOrDefault(d => d.Key == k)?.Function ?? k));
                var choice = await Ask("Input already in use", $"{input.DeviceName} · {input.Label} is already bound to {names} in RBRControls.",
                    "Move it here", "Use it for both");
                if (choice == MessageBoxResult.None) return;
                if (choice == MessageBoxResult.Primary)
                    foreach (var k in conflicts) _editor.SetPlugin(k, "");
            }
            _editor.SetPlugin(row.Key, value);
            RefreshEdits();
            SetStatus($"{row.Function} → {input.DeviceName} · {input.Label} (not saved yet)");
        });
    }

    private void ClearPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<PluginBindingRow>(sender) is not { } row || _editor is null) return;
        _editor.SetPlugin(row.Key, "");
        RefreshEdits();
    }

    private async void SaveEdits_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null || _editor is null || !_editor.IsDirty) return;
        if (!await EnsureGameClosed()) return;
        await Run(() =>
        {
            int n = _editor.ChangeCount;
            string? backup = _editor.Save(_rbr);
            Reload();
            SetStatus($"Saved {Plural(n, "change")} to the game." + (backup is null ? "" : $" Backup: {backup}"));
        });
    }

    private void DiscardEdits_Click(object sender, RoutedEventArgs e)
    {
        Reload();
        SetStatus("Unsaved changes discarded.");
    }

    /// <summary>Asks before an action that reloads or rewrites the game files while edits are pending.</summary>
    private async Task<bool> ConfirmDiscardEdits()
    {
        if (_editor?.IsDirty != true) return true;
        return await Confirm("Discard unsaved changes?",
            $"You have {Plural(_editor.ChangeCount, "unsaved change")} to the bindings. Continue and lose them?", "Discard");
    }

    // ---------------------------------------------------------------- capture overlay

    private Func<CapturedInput, Task>? _onCaptured;

    private void StartCapture(string title, string hint, InputKind accept, Func<CapturedInput, Task> onCaptured)
    {
        StopCapture();
        try
        {
            // A fresh capture each time picks up devices plugged in since the app started.
            _capture = new InputCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }
        catch (Exception ex)
        {
            SetStatus($"Can't read input devices: {ex.Message}");
            return;
        }
        _onCaptured = onCaptured;
        CaptureTitle.Text = title;
        CaptureHint.Text = $"{hint} on any connected device.";
        CaptureOverlay.Visibility = Visibility.Visible;
        UpdateMonitorPause();
        CaptureOverlay.Focus();
        ((Storyboard)CaptureOverlay.Resources["Pulse"]).Begin();
        _capture.Captured += async input =>
        {
            var handler = _onCaptured;
            StopCapture();
            if (handler is not null) await Run(() => handler(input));
        };
        _capture.Start(accept);
    }

    private void StopCapture()
    {
        _capture?.Dispose();
        _capture = null;
        _onCaptured = null;
        CaptureOverlay.Visibility = Visibility.Collapsed;
        UpdateMonitorPause();
    }

    private void CancelCapture_Click(object sender, RoutedEventArgs e) => StopCapture();

    // ---------------------------------------------------------------- live input display

    private void LoadInvertedAxes()
    {
        var input = IniFile.TryLoad(_rbr!.InputIni);
        _invertedActions = RbrActions.All
            .Where(a => a.InvertKey is not null &&
                        string.Equals(input?.Get("Settings", a.InvertKey), "true", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Id)
            .ToHashSet();
    }

    /// <summary>Shows what RBR would read: the active map's bindings (with pending edits) applied to the monitor's last poll.</summary>
    private void UpdateLiveInputs()
    {
        if (WindowState == WindowState.Minimized) return;
        var active = _monitor is null || _acm is null || _devices is null ? null : _acm.ActiveMap(_devices.AttachedGuids);
        if (active is null)
        {
            LiveInputs.Show(default);
            return;
        }
        var map = _editor?.Maps[active.Index] ?? active;
        var monitor = _monitor!;

        // Each bound input as 0..1 (axes after RBR's invert setting, buttons and keys 0 or 1); null when unbound.
        double? Level(int actionId, bool axesOnly = false)
        {
            double? level = null;
            foreach (var b in map.Bindings)
            {
                if (b.ActionId != actionId || b.DeviceIndex < 0 || b.DeviceIndex >= map.Devices.Count) continue;
                var device = map.Devices[b.DeviceIndex];
                double v;
                if ((b.ObjectType & 0x03) != 0)
                {
                    if (monitor.AxisValue(device, b.ObjectType) is not double x) continue; // device not connected
                    // RBR reads a pedal as pressed at the low end of its range unless inverted (pedals that
                    // rest at 0 need "invert"); steering and the combined axis are taken as they come.
                    if (actionId is not (0 or 4)) x = 1 - x;
                    v = _invertedActions.Contains(actionId) ? 1 - x : x;
                }
                else if (axesOnly) continue;
                else v = monitor.IsDown(device, b.ObjectType) ? 1 : 0;
                level = Math.Max(level ?? 0, v);
            }
            return level;
        }

        double? Sum(double? a, double? b) => a is null && b is null ? null : Math.Min((a ?? 0) + (b ?? 0), 1);

        // The combined axis rests in the middle: one half throttle, the other brake.
        double? combined = Level(4);
        double? steerAxis = Level(0, axesOnly: true), left = Level(1), right = Level(2);
        double? steering = steerAxis is null && left is null && right is null
            ? null
            : Math.Clamp((steerAxis is double s ? s * 2 - 1 : 0) - (left ?? 0) + (right ?? 0), -1, 1);

        LiveInputs.Show(new InputLevels(
            steering,
            Level(11),
            Sum(Level(5), combined is double cb ? Math.Max(0, 1 - cb * 2) : null),
            Sum(Level(3), combined is double ct ? Math.Max(0, ct * 2 - 1) : null),
            Level(6)));
    }

    // ---------------------------------------------------------------- locate pressed inputs

    /// <summary>Reopens the devices (after a reload they may have changed) and listens for presses.</summary>
    private void RestartMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;
        try
        {
            _monitor = new InputCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            _monitor.Captured += LocateInput;
            _monitor.StartMonitor();
        }
        catch { _monitor = null; /* no DirectInput: the locator just stays off */ }
        UpdateMonitorPause();
    }

    /// <summary>Only report presses while the window is in front, nothing modal is open and the setting is on.</summary>
    private void UpdateMonitorPause()
    {
        if (_monitor is not null)
            _monitor.Paused = !_settings.LocateInputs || !IsActive || WindowState == WindowState.Minimized ||
                              CaptureOverlay.Visibility == Visibility.Visible;
    }

    private void LocateSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.LocateInputs = LocateSwitch.IsChecked == true;
        _settings.Save();
        UpdateMonitorPause();
    }

    /// <summary>Same object on the same device: compare kind (axis/button) and instance, ignore attribute flags.</summary>
    private static bool SameObject(uint a, uint b) =>
        DirectInputNames.Instance(a) == DirectInputNames.Instance(b) && ((a & 0x03) != 0) == ((b & 0x03) != 0) &&
        ((a & 0x0C) != 0) == ((b & 0x0C) != 0);

    private async void LocateInput(CapturedInput input)
    {
        // Typing a profile name is not a binding lookup.
        if (input.IsKeyboard && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

        string what = $"{input.DeviceName} · {input.Label}";

        // Game bindings in the map on screen (pending edits included).
        var gameHits = new List<GameBindingRow>();
        if ((MapBox.SelectedItem as MapItem)?.Map is { } shown)
        {
            var rows = _gameRows;
            var map = _editor?.Maps[shown.Index] ?? shown;
            var indexes = map.Bindings.Select((b, i) => (b, i))
                .Where(x => x.b.DeviceIndex >= 0 && x.b.DeviceIndex < map.Devices.Count &&
                            map.Devices[x.b.DeviceIndex] == input.Device && SameObject(x.b.ObjectType, input.ObjectType))
                .Select(x => x.i).ToHashSet();
            gameHits = rows.Where(r => r.BindingIndex is int i && indexes.Contains(i)).ToList();
        }

        // RBRControls binds (buttons and keys only).
        var pluginHits = new List<PluginBindingRow>();
        if (input.Kind == InputKind.Button)
        {
            string value = BindingEditor.PluginBindValue(input);
            pluginHits = _pluginRows.Where(r => !r.IsUnbound &&
                string.Equals(_editor?.PluginValue(r.Key) ?? _pluginDefs.FirstOrDefault(d => d.Key == r.Key)?.RawBinding, value,
                    StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (gameHits.Count == 0 && pluginHits.Count == 0)
        {
            SetStatus($"{what}: not bound in this map or in RBRControls");
            return;
        }

        var names = gameHits.Select(r => r.Action).Concat(pluginHits.Select(r => $"{r.Function} (RBRControls)"));
        SetStatus($"{what} → {string.Join(", ", names)}");

        // Stay on the RBRControls tab if the input is bound there; otherwise prefer the game bindings.
        bool usePlugin = pluginHits.Count > 0 && (gameHits.Count == 0 || Tabs.SelectedItem == PluginTab);
        if (usePlugin)
        {
            Tabs.SelectedItem = PluginTab;
            await Highlight(PluginGrid, PluginGrid.Items.Cast<object>().FirstOrDefault(o => o is PluginBindingRow p && p.Key == pluginHits[0].Key));
        }
        else
        {
            Tabs.SelectedItem = GameTab;
            await Highlight(GameGrid, gameHits[0]);
        }
    }

    /// <summary>Expands the section/category holding a row, scrolls it into view and flashes it.</summary>
    private async Task Highlight(System.Windows.Controls.DataGrid grid, object? item)
    {
        if (item is null) return;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded); // let the tab render

        bool expanded = ExpandToggle(FindAncestor<HeaderedContentControl>(grid));
        grid.SelectedItem = item;
        grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
        {
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow r2) return;
            row = r2;
        }
        expanded |= ExpandToggle(FindAncestor<GroupItem>(row));
        if (expanded) await Task.Delay(260); // wait for the expand animation before scrolling
        row.BringIntoView();

        var accent = (TryFindResource("AccentFillColorDefaultBrush") as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var flash = new SolidColorBrush(accent) { Opacity = 0.55 };
        row.Background = flash;
        var fade = new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(1300))
        {
            BeginTime = TimeSpan.FromMilliseconds(350),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => row.ClearValue(BackgroundProperty);
        flash.BeginAnimation(Brush.OpacityProperty, fade);
    }

    /// <summary>Opens a collapsed section or category (their templates have a ToggleButton named "Toggle").</summary>
    private static bool ExpandToggle(Control? container)
    {
        if (container?.Template?.FindName("Toggle", container) is ToggleButton { IsChecked: false } toggle)
        {
            toggle.IsChecked = true;
            return true;
        }
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    // ---------------------------------------------------------------- force feedback

    private void LoadFfb()
    {
        var rows = FfbSettings.Load(_rbr!);
        rows.AddRange(FfbSettings.LoadPerCar(_rbr!));
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SettingRow.Group)));
        FfbGrid.ItemsSource = view;

        float? strength = null;
        try
        {
            if (File.Exists(_rbr!.ProfileRbr)) strength = RbrProfileFile.Load(_rbr.ProfileRbr).ForceFeedbackStrength;
        }
        catch { /* shown as unknown below */ }
        ShowStrength(strength);
    }

    private void ShowStrength(float? strength)
    {
        StrengthFullButton.Visibility = strength is < 0.995f ? Visibility.Visible : Visibility.Collapsed;
        if (strength is null)
        {
            StrengthText.Text = "—";
            StrengthSlider.Value = 0;
            SetNotice(StrengthNotice, StrengthNoticeIcon, SymbolRegular.Info24, "SystemFillColorAttentionBackgroundBrush", "TextFillColorPrimaryBrush");
            StrengthNoticeText.Text = "Could not read the in-game strength from the RBR profile.";
            return;
        }

        int percent = (int)Math.Round(strength.Value * 100);
        StrengthText.Text = $"{percent}%";
        StrengthSlider.Value = percent;

        if (percent >= 100)
        {
            SetNotice(StrengthNotice, StrengthNoticeIcon, SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush");
            StrengthNoticeText.Text = "At 100%, as recommended. Adjust the overall force in your wheelbase software.";
        }
        else
        {
            SetNotice(StrengthNotice, StrengthNoticeIcon, SymbolRegular.Warning24, "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush");
            StrengthNoticeText.Text =
                $"The in-game strength is at {percent}%. In RBR it should stay at 100%: lowering it here clips detail. " +
                "Set it back to 100% (here or in RBR) and adjust the force in your wheelbase software instead.";
        }
    }

    private async void StrengthFull_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null || !await EnsureGameClosed()) return;
        await Run(() =>
        {
            RbrProfileFile.SetForceFeedbackStrength(_rbr.ProfileRbr, 1f);
            LoadFfb();
            SetStatus($"In-game strength set to 100%. The profile as it was is kept in {RbrProfileFile.BackupPath(_rbr.ProfileRbr)}");
        });
    }

    private static void SetNotice(Border notice, SymbolIcon icon, SymbolRegular symbol, string background, string iconBrush)
    {
        notice.SetResourceReference(Border.BackgroundProperty, background);
        icon.Symbol = symbol;
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, iconBrush);
    }

    // ---------------------------------------------------------------- RBRControls

    private void LoadPlugin()
    {
        _pluginDefs = new();
        ShifterGrid.ItemsSource = null;
        var ini = IniFile.TryLoad(_rbr!.RbrControlsIni);
        if (ini is null)
            throw new FileNotFoundException("Not installed (Plugins\\RBRControls\\RBRControls.ini is missing)");

        _pluginDefs = RbrControlsConfig.LoadBindings(ini);
        var options = new ListCollectionView(RbrControlsConfig.LoadShifterSettings(ini));
        options.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SettingRow.Group)));
        ShifterGrid.ItemsSource = options;
    }

    private void BuildPluginRows()
    {
        _pluginRows = new();
        foreach (var d in _pluginDefs)
        {
            string value = _editor?.PluginValue(d.Key) ?? d.RawBinding;
            bool changed = _editor?.IsPluginChanged(d.Key) == true;
            if (value.Length == 0)
            {
                _pluginRows.Add(new(d.Key, d.Page, d.Function, "—", "Unbound", d.Trigger, d.GameMode, "", LinkState.None, true, changed));
                continue;
            }
            var (device, inputText) = RbrControlsConfig.ParseBind(value);
            var info = device == RbrControlsConfig.KeyboardDevice
                ? new DeviceInfo(DirectInputNames.SysKeyboard, device, null, true, "System")
                : _devices?.ResolveName(device) ?? new DeviceInfo(Guid.Empty, device, null, false, "—");
            _pluginRows.Add(new(d.Key, d.Page, d.Function, device.Length > 0 ? device : value, inputText, d.Trigger, d.GameMode,
                info.Status, Link(info), false, changed));
        }
        ApplyPluginFilter();
    }

    private void ApplyPluginFilter()
    {
        var rows = OnlyBoundBox.IsChecked == true ? _pluginRows.Where(r => !r.IsUnbound).ToList() : _pluginRows;
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PluginBindingRow.Page)));
        PluginGrid.ItemsSource = view;
        PluginCountText.Text = $"({Plural(_pluginRows.Count(r => !r.IsUnbound), "bind")})";
    }

    // ---------------------------------------------------------------- profiles

    private void LoadProfiles()
    {
        var profiles = ProfileStore.LoadAll();
        ProfileList.ItemsSource = profiles;
        NoProfilesText.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfileCountText.Text = $"({Plural(profiles.Count, "profile")})";
        UpdateProfileButtons();
        RefreshCurrentProfile(profiles);
    }

    /// <summary>Header selector: the current profile and whether the game still matches it.</summary>
    private void RefreshCurrentProfile(List<InputProfile>? profiles = null)
    {
        profiles ??= ProfileStore.LoadAll();
        var current = profiles.FirstOrDefault(p => string.Equals(p.Name, _settings.CurrentProfile, StringComparison.CurrentCultureIgnoreCase));
        if (current is null && _rbr is not null)
        {
            // Nothing recorded (or it was deleted): adopt a profile the game already matches exactly. Read-only.
            current = profiles.FirstOrDefault(p => { try { return ProfileStore.MatchesGame(_rbr, p); } catch { return false; } });
            if (current is not null) SetCurrentProfile(current.Name);
        }

        _syncingProfileBox = true;
        CurrentProfileBox.ItemsSource = profiles;
        CurrentProfileBox.SelectedItem = current;
        _syncingProfileBox = false;

        // Nothing to pick from: don't open an empty (or single, already selected) list.
        bool canPick = profiles.Count > 1 || (profiles.Count == 1 && current is null);
        CurrentProfileBox.IsHitTestVisible = canPick;
        CurrentProfileBox.Focusable = canPick;
        if (!canPick) CurrentProfileBox.IsDropDownOpen = false;

        NoProfileText.Text = profiles.Count == 0 ? "No profiles yet" : "No profile";
        NoProfileText.Visibility = current is null ? Visibility.Visible : Visibility.Collapsed;

        bool modified = false;
        try { modified = current is not null && _rbr is not null && !ProfileStore.MatchesGame(_rbr, current); }
        catch { /* unreadable files: don't claim anything */ }
        ProfileStateHost.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        ProfileStateIcon.Symbol = modified ? SymbolRegular.Warning24 : SymbolRegular.CheckmarkCircle24;
        ProfileStateIcon.SetResourceReference(ForegroundProperty,
            modified ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
        ProfileStateHost.ToolTip = modified
            ? $"The game files differ from \"{current!.Name}\" (changed in game, in the launcher or here)"
            : $"The game files match \"{current?.Name}\"";
        ApplyCurrentButton.IsEnabled = modified;
        ApplyCurrentHost.ToolTip = modified
            ? $"Apply: write \"{current!.Name}\" to the game files"
            : current is null
                ? "Apply: pick a profile first"
                : $"Apply: nothing to do, the game files already match \"{current.Name}\"";
    }

    private void SetCurrentProfile(string? name)
    {
        _settings.CurrentProfile = name;
        _settings.Save();
    }

    private void CurrentProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProfileBox || _rbr is null || CurrentProfileBox.SelectedItem is not InputProfile p) return;
        // Picking a profile only selects it; the state icon shows whether the game differs and "Apply" writes it.
        SetCurrentProfile(p.Name);
        RefreshCurrentProfile();
    }

    private async void ApplyCurrentProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null || CurrentProfileBox.SelectedItem is not InputProfile p) return;
        if (!await ConfirmDiscardEdits() || !await EnsureGameClosed()) return;
        if (!await Confirm("Apply profile?",
                $"Apply \"{p.Name}\" to the game? Its bindings, axis inversion, RBRControls binds and force feedback values " +
                "replace the current ones (backed up first).", "Apply"))
            return;
        await Run(() =>
        {
            ApplyProfile(p);
            SetStatus($"Profile \"{p.Name}\" applied to the game.");
        });
    }

    /// <summary>Backs up the files a profile overwrites, applies it and makes it the current profile.</summary>
    private void ApplyProfile(InputProfile p)
    {
        foreach (var rel in new[] { _rbr!.AcmFile, RbrInstallation.InputIniFile, RbrInstallation.RbrControlsIniFile,
                                    RbrInstallation.GameIniFile, RbrInstallation.LauncherIniFile })
            if (File.Exists(_rbr!.Path(rel))) GameFiles.Backup(_rbr, rel);
        ProfileStore.Apply(_rbr!, p);
        SetCurrentProfile(p.Name);
        Reload();
    }

    /// <summary>Shows the page for the selected tab; pages are never rebuilt, only shown or hidden.</summary>
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || Pages is null) return;
        var pages = new UIElement[] { InfoPage, GamePage, PluginPagePanel, FfbPage, ProfilesPagePanel, SettingsPagePanel };
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == Tabs.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;

        // On the settings page the settings button becomes a back arrow.
        bool inSettings = Tabs.SelectedItem == SettingsPage;
        SettingsIcon.Symbol = inSettings ? SymbolRegular.ArrowLeft24 : SymbolRegular.Settings24;
        SettingsButton.ToolTip = inSettings ? "Back" : "Settings";
        if (!inSettings) _pageBeforeSettings = Tabs.SelectedIndex;
    }

    private void OpenProfiles_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = ProfilesPage;

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem == SettingsPage) Tabs.SelectedIndex = _pageBeforeSettings;
        else Tabs.SelectedItem = SettingsPage;
    }

    /// <summary>
    /// Lays out the hidden pages while the app is idle so the first visit to a tab doesn't pay for building
    /// its grids (they show all rows, so every row is created on the first layout).
    /// </summary>
    private void WarmUpPages()
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var page in new UIElement[] { PluginPagePanel, FfbPage, ProfilesPagePanel, SettingsPagePanel })
            {
                if (page.Visibility != Visibility.Collapsed) continue;
                page.Visibility = Visibility.Hidden; // measured and arranged, but not drawn
                page.UpdateLayout();
                if (page.Visibility == Visibility.Hidden) page.Visibility = Visibility.Collapsed;
            }
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private InputProfile? SelectedProfile => ProfileList.SelectedItem as InputProfile;

    private void UpdateProfileButtons()
    {
        bool has = SelectedProfile is not null && _rbr is not null;
        ApplyProfileButton.IsEnabled = has;
        // "Default" always holds the original game setup.
        UpdateProfileButton.IsEnabled = DeleteProfileButton.IsEnabled = has && !ProfileStore.IsDefault(SelectedProfile!);
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProfileButtons();

    private void ProfileName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveProfile_Click(sender, e);
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null || _devices is null) return;
        string name = ProfileNameBox.Text.Trim();
        if (name.Length == 0)
        {
            await Info("Profile name", "Type a name for the profile first.");
            return;
        }
        if (string.Equals(name, ProfileStore.DefaultName, StringComparison.CurrentCultureIgnoreCase))
        {
            await Info("Profile name", $"\"{ProfileStore.DefaultName}\" is the original game setup and can't be overwritten. Pick another name.");
            return;
        }
        if (ProfileStore.Exists(name) &&
            !await Confirm("Overwrite profile?", $"A profile called \"{name}\" already exists. Replace it with the current setup?", "Overwrite"))
            return;

        await Run(() =>
        {
            ProfileStore.SaveCurrent(_rbr, name, _devices);
            SetCurrentProfile(name);
            ProfileNameBox.Text = "";
            LoadProfiles();
            SetStatus($"Profile \"{name}\" saved.");
        });
    }

    private async void UpdateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null || _devices is null || SelectedProfile is not { } p) return;
        if (!await Confirm("Overwrite profile?", $"Replace \"{p.Name}\" with the setup currently in the game?", "Overwrite"))
            return;
        await Run(() =>
        {
            ProfileStore.SaveCurrent(_rbr, p.Name, _devices);
            SetCurrentProfile(p.Name);
            LoadProfiles();
            SetStatus($"Profile \"{p.Name}\" updated.");
        });
    }

    private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null || SelectedProfile is not { } p) return;
        if (!await ConfirmDiscardEdits() || !await EnsureGameClosed()) return;
        if (!await Confirm("Apply profile?",
                $"This replaces the game's current bindings, axis inversion, RBRControls binds and force feedback values with \"{p.Name}\".\n\n" +
                "Save the current setup as a profile first if you want to keep it. You can always go back to the original files from Settings.",
                "Apply"))
            return;

        await Run(() =>
        {
            ApplyProfile(p);
            SetStatus($"Profile \"{p.Name}\" applied to the game.");
        });
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } p) return;
        if (!await Confirm("Delete profile?", $"Delete \"{p.Name}\"? The game files are not touched.", "Delete"))
            return;
        await Run(() =>
        {
            ProfileStore.Delete(p);
            if (string.Equals(p.Name, _settings.CurrentProfile, StringComparison.CurrentCultureIgnoreCase)) SetCurrentProfile(null);
            LoadProfiles();
            SetStatus($"Profile \"{p.Name}\" deleted.");
        });
    }

    // ---------------------------------------------------------------- original files

    private void RefreshBackupInfo()
    {
        var manifest = _rbr is null ? null : OriginalBackup.Load(_rbr.Root);
        RestoreButton.IsEnabled = manifest is not null;
        BackupText.Text = manifest is null
            ? "No backup yet. It is created automatically the first time the app opens an install."
            : $"Backed up {manifest.CreatedAtUtc.ToLocalTime():g}: " +
              string.Join(", ", manifest.Files.Where(f => f.Existed).Select(f => Path.GetFileName(f.RelativePath))) +
              ". Restoring puts back their contents, attributes, timestamps and permissions.";
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null) return;
        if (!await ConfirmDiscardEdits() || !await EnsureGameClosed()) return;
        if (!await Confirm("Restore original files?",
                "The game files will go back to how they were when RBR Input Manager first opened this install. " +
                "Any changes made since then (in the game, the RSF launcher or this app) are lost. Your saved profiles are kept.",
                "Restore"))
            return;

        await Run(async () =>
        {
            var result = OriginalBackup.Restore(_rbr);
            Reload();
            string msg = $"{result.Restored} file(s) restored, {result.Unchanged} already original.";
            SetStatus(msg);
            if (result.Warnings.Count > 0)
                await Info("Restore finished with warnings", msg + "\n\n" + string.Join("\n", result.Warnings));
        });
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(AppSettings.DataDir, "Originals");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ---------------------------------------------------------------- updates

    private enum UpdateState { Checking, UpToDate, Available, Failed }

    private void SetUpdateState(UpdateState state)
    {
        string v = $"v{UpdateService.CurrentVersion.ToString(3)}";
        (string text, string brush, string tip) = state switch
        {
            UpdateState.UpToDate => ($"{v} · Up to date", "SystemFillColorSuccessBrush", "You have the latest release. Click to check again."),
            UpdateState.Available => ($"{v} · Update {_update?.Version.ToString(3)} available", "SystemFillColorCautionBrush",
                "A newer release is on GitHub. Click to install it."),
            UpdateState.Failed => ($"{v} · Update check failed", "TextFillColorTertiaryBrush", "Could not reach GitHub. Click to try again."),
            _ => ($"{v} · Checking…", "TextFillColorTertiaryBrush", "Checking GitHub for a newer release."),
        };
        VersionLabel.Text = text;
        UpdateDot.SetResourceReference(Shape.FillProperty, brush);
        VersionButton.ToolTip = tip;
    }

    private async void Version_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not null) UpdateBanner.Visibility = Visibility.Visible;
        else await CheckForUpdatesAsync(userInitiated: true);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        CheckUpdatesButton.IsEnabled = false;
        SetUpdateState(UpdateState.Checking);
        try
        {
            _update = await UpdateService.CheckAsync();
            if (_update is null)
            {
                SetUpdateState(UpdateState.UpToDate);
                if (userInitiated) await Info("Updates", $"You have the latest version ({UpdateService.CurrentVersion.ToString(3)}).");
                return;
            }
            SetUpdateState(UpdateState.Available);
            UpdateText.Text = $"RBR Input Manager {_update.Version.ToString(3)} is available (you have {UpdateService.CurrentVersion.ToString(3)}).";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            SetUpdateState(UpdateState.Failed);
            if (userInitiated) await Info("Updates", $"Could not check for updates: {ex.Message}");
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(userInitiated: true);

    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        InstallUpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateText.Text = $"Downloading {_update.Version.ToString(3)}… {p:P0}");
            await UpdateService.InstallAsync(_update, progress);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            InstallUpdateButton.IsEnabled = true;
            UpdateText.Text = $"Update failed: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- theme & settings

    // App accent instead of the Windows one; the input display's steering bar uses the same red.
    private static readonly Color AccentColor = Color.FromRgb(0xC5, 0x21, 0x28);

    private void ApplyTheme(ThemePreference pref)
    {
        if (pref == ThemePreference.System)
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, false);
            ApplicationThemeManager.ApplySystemTheme(false);
        }
        else
        {
            SystemThemeWatcher.UnWatch(this);
            ApplicationThemeManager.Apply(pref == ThemePreference.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                WindowBackdropType.Mica, false);
        }
        ApplyAccent();
    }

    /// <summary>
    /// The exact colour for every accent shade: the theme-based overload lightens it a lot in dark mode.
    /// Reapplied on theme changes in case a theme switch resets the accent resources.
    /// Text never takes the accent: WPF-UI's accent text brushes (links etc.) are pointed at the primary text colour.
    /// Text on the accent is always white: the dark-theme default is black, unreadable on this red.
    /// </summary>
    private static void ApplyAccent()
    {
        ApplicationAccentColorManager.Apply(AccentColor, AccentColor, AccentColor, AccentColor);
        var resources = Application.Current.Resources;
        foreach (var level in new[] { "Primary", "Secondary", "Tertiary" })
            resources[$"AccentTextFillColor{level}Brush"] = resources["TextFillColorPrimaryBrush"];
        var dimmed = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        resources["TextOnAccentFillColorPrimaryBrush"] = Brushes.White;
        resources["TextOnAccentFillColorSecondaryBrush"] = dimmed;
        resources["AccentButtonForeground"] = Brushes.White;
        resources["AccentButtonForegroundPointerOver"] = Brushes.White;
        resources["AccentButtonForegroundPressed"] = dimmed;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _settings.Theme = Enum.Parse<ThemePreference>(tag);
        _settings.Save();
        ApplyTheme(_settings.Theme);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Richard Burns Rally install folder" };
        if (dlg.ShowDialog(this) != true) return;
        if (!RbrInstallation.IsValid(dlg.FolderName))
        {
            await Info("RBR install folder", "That folder does not contain RichardBurnsRally.ini.");
            return;
        }
        Open(dlg.FolderName);
    }

    private async void RbrProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingProfiles || _rbr is null || RbrProfileBox.SelectedItem is not string profile || profile == _rbr.Profile) return;
        if (!await ConfirmDiscardEdits())
        {
            _fillingProfiles = true;
            RbrProfileBox.SelectedItem = _rbr.Profile;
            _fillingProfiles = false;
            return;
        }
        _settings.RbrProfile = profile;
        Open(_rbr.Root);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_reloading || !await ConfirmDiscardEdits()) return;
        // The button stays enabled (a disabled button greys out its text); repeat clicks are ignored instead.
        _reloading = true;
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(700)) { RepeatBehavior = RepeatBehavior.Forever };
        ReloadRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        try
        {
            // Let the spinner start before the (synchronous) DirectInput enumeration blocks the UI briefly.
            await Task.Delay(350);
            Reload();
            await Task.Delay(250);
        }
        finally
        {
            // Finish the current turn smoothly instead of snapping back.
            double angle = ReloadRotate.Angle % 360;
            var settle = new DoubleAnimation(angle, 360, TimeSpan.FromMilliseconds(250 + (360 - angle) / 360 * 400))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            settle.Completed += (_, _) =>
            {
                ReloadRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                ReloadRotate.Angle = 0;
                _reloading = false;
            };
            ReloadRotate.BeginAnimation(RotateTransform.AngleProperty, settle);
        }
    }

    private void OnlyBound_Click(object sender, RoutedEventArgs e)
    {
        _settings.PluginBoundOnly = OnlyBoundBox.IsChecked == true;
        _settings.Save();
        ApplyPluginFilter();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<bool> EnsureGameClosed()
    {
        var running = RbrInstallation.RunningGameProcesses();
        if (running.Count == 0) return true;
        await Info("Close the game first",
            $"These are running: {string.Join(", ", running)}.\n\nClose RBR and the RSF launcher before changing game files, " +
            "otherwise they may overwrite the changes when they exit.");
        return false;
    }

    private static async Task<bool> Confirm(string title, object content, string action)
    {
        var box = new MessageBox { Title = title, Content = content, PrimaryButtonText = action, CloseButtonText = "Cancel" };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    private static async Task<MessageBoxResult> Ask(string title, string text, string primary, string secondary)
    {
        var box = new MessageBox
        {
            Title = title, Content = text, PrimaryButtonText = primary, SecondaryButtonText = secondary, CloseButtonText = "Cancel",
            IsSecondaryButtonEnabled = true,
        };
        return await box.ShowDialogAsync();
    }

    private static async Task Info(string title, string text)
    {
        var box = new MessageBox { Title = title, Content = text, CloseButtonText = "OK" };
        await box.ShowDialogAsync();
    }

    private async Task Run(Action action) => await Run(() => { action(); return Task.CompletedTask; });

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            await Info("Something went wrong", ex.Message);
        }
    }
}
