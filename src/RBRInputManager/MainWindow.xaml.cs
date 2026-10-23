using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using RBRInputManager.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace RBRInputManager;

/// <summary>Drives the green/red dot in the Status columns.</summary>
public enum LinkState { None, Ok, Missing }

public sealed record GameBindingRow(string Action, string Device, string Input, string Inverted, string Status, LinkState State, bool IsUnbound);
public sealed record ProfileDeviceRow(int Index, string Name, string? VidPid, string Guid, string Status, LinkState State);
public sealed record AttachedDeviceRow(string Name, string Product, string Guid, string InProfile, string Status, LinkState State);
public sealed record PluginBindingRow(string Page, string Function, string Device, string Input, string Trigger, string GameMode,
    string Status, LinkState State, bool IsUnbound);

public partial class MainWindow : FluentWindow
{
    private readonly AppSettings _settings = AppSettings.Load();
    private RbrInstallation? _rbr;
    private DeviceCatalog? _devices;
    private List<PluginBindingRow> _pluginRows = new();
    private UpdateInfo? _update;
    private bool _initializing = true;

    public MainWindow()
    {
        InitializeComponent();

        ThemeBox.SelectedIndex = (int)_settings.Theme;
        VersionText.Text = $"Version {UpdateService.CurrentVersion.ToString(3)} · github.com/{UpdateService.Repo}";

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
        Closed += (_, _) => _devices?.Dispose();
    }

    // ---------------------------------------------------------------- install & loading

    private void Open(string root)
    {
        _rbr = new RbrInstallation(root);
        _settings.RbrPath = root;
        _settings.Save();

        InstallText.Text = $"{root}   ·   RBR profile {RbrInstallation.RsfProfile} (the one RallySimFans always uses)";
        SettingsPathText.Text = root;

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

        ActionMapFile? acm = null;
        Try("Controls", () => acm = LoadGameBindings());
        Try("Force feedback", LoadFfb);
        Try("RBRControls", LoadPlugin);
        LoadAttached(acm);
        LoadProfiles();

        if (_devices.DirectInputError is not null)
            errors.Add($"DirectInput: {_devices.DirectInputError}");

        SetStatus(errors.Count == 0
            ? $"Loaded {DateTime.Now:HH:mm:ss} · {_devices.Attached.Count} controllers connected"
            : string.Join("   |   ", errors));
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private ActionMapFile? LoadGameBindings()
    {
        GameGrid.ItemsSource = null;
        ProfileDevicesGrid.ItemsSource = null;
        if (!File.Exists(_rbr!.Acm))
        {
            AcmInfo.Text = $"{_rbr.Acm} not found. Start RBR once through the RSF launcher so it creates its profile.";
            return null;
        }

        var acm = ActionMapFile.Load(_rbr.Acm);
        var input = IniFile.TryLoad(_rbr.InputIni);
        var devices = acm.Devices.Select(_devices!.Resolve).ToList();

        var rows = new List<GameBindingRow>();
        foreach (var action in RbrActions.All)
        {
            var binds = acm.Bindings.Where(b => b.ActionId == action.Id).ToList();
            string inverted = action.InvertKey is null ? "" :
                string.Equals(input?.Get("Settings", action.InvertKey), "true", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";

            if (binds.Count == 0)
            {
                rows.Add(new(action.Name, "—", "Unbound", "", "", LinkState.None, true));
                continue;
            }
            foreach (var b in binds)
                rows.Add(BindingRow(action.Name, b, devices, inverted));
        }
        // Bindings with action IDs outside the known table (e.g. added by plugins).
        foreach (var b in acm.Bindings.Where(b => b.ActionId >= RbrActions.All.Count))
            rows.Add(BindingRow(RbrActions.Get(b.ActionId).Name, b, devices, ""));

        GameGrid.ItemsSource = rows;
        ProfileDevicesGrid.ItemsSource = devices
            .Select((d, i) => new ProfileDeviceRow(i, d.Name, d.VidPid, d.InstanceGuid.ToString("B").ToUpperInvariant(),
                d.Status, d.Connected ? LinkState.Ok : LinkState.Missing))
            .ToList();

        AcmInfo.Text = $"{acm.Path} · {acm.Bindings.Count} bindings · {acm.Devices.Count} devices. " +
                       "Unbound = the game uses its default keyboard key, or a plugin handles it (e.g. shifting in RBRControls).";
        return acm;
    }

    private GameBindingRow BindingRow(string action, ActionBinding b, List<DeviceInfo> devices, string inverted)
    {
        if (b.DeviceIndex < 0 || b.DeviceIndex >= devices.Count)
            return new(action, $"Invalid device index ({b.DeviceIndex})", $"0x{b.ObjectType:X8}", inverted, "Error", LinkState.Missing, false);

        var dev = devices[b.DeviceIndex];
        bool keyboard = dev.InstanceGuid == DirectInputNames.SysKeyboard;
        string generic = DirectInputNames.Describe(b.ObjectType, keyboard);
        string? real = keyboard ? null : _devices!.ObjectName(dev.InstanceGuid, b.ObjectType);
        string inputText = real is null ? generic : $"{real}  ({generic})";

        return new(action, dev.Name, inputText, inverted, dev.Status, dev.Connected ? LinkState.Ok : LinkState.Missing, false);
    }

    private void LoadFfb()
    {
        var rows = FfbSettings.Load(_rbr!);
        rows.AddRange(FfbSettings.LoadPerCar(_rbr!));
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SettingRow.Group)));
        FfbGrid.ItemsSource = view;
    }

    private void LoadPlugin()
    {
        _pluginRows = new();
        ShifterGrid.ItemsSource = null;
        var ini = IniFile.TryLoad(_rbr!.RbrControlsIni);
        if (ini is null)
        {
            ApplyPluginFilter();
            throw new FileNotFoundException("Not installed (Plugins\\RBRControls\\RBRControls.ini is missing)");
        }

        var attachedNames = _devices!.Attached.Select(d => d.InstanceName.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var b in RbrControlsConfig.LoadBindings(ini))
        {
            if (!b.IsBound)
            {
                _pluginRows.Add(new(b.Page, b.Function, "—", "Unbound", b.Trigger, b.GameMode, "", LinkState.None, true));
                continue;
            }
            bool connected = b.Device == RbrControlsConfig.KeyboardDevice || attachedNames.Contains(b.Device);
            _pluginRows.Add(new(b.Page, b.Function, b.Device.Length > 0 ? b.Device : b.RawBinding, b.Input, b.Trigger, b.GameMode,
                connected ? "Connected" : "Not connected", connected ? LinkState.Ok : LinkState.Missing, false));
        }
        ShifterGrid.ItemsSource = RbrControlsConfig.LoadShifterSettings(ini);
        ApplyPluginFilter();
    }

    private void ApplyPluginFilter()
    {
        var rows = OnlyBoundBox.IsChecked == true ? _pluginRows.Where(r => !r.IsUnbound).ToList() : _pluginRows;
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PluginBindingRow.Page)));
        PluginGrid.ItemsSource = view;
    }

    private void LoadAttached(ActionMapFile? acm)
    {
        var inProfile = acm?.Devices.ToHashSet() ?? new HashSet<Guid>();
        AttachedGrid.ItemsSource = _devices!.Attached
            .OrderBy(d => d.InstanceName)
            .Select(d => new AttachedDeviceRow(d.InstanceName.Trim(), d.ProductName.Trim(),
                d.InstanceGuid.ToString("B").ToUpperInvariant(), inProfile.Contains(d.InstanceGuid) ? "Yes" : "",
                "Connected", LinkState.Ok))
            .ToList();
    }

    // ---------------------------------------------------------------- profiles

    private void LoadProfiles()
    {
        var profiles = ProfileStore.LoadAll();
        ProfileList.ItemsSource = profiles;
        NoProfilesText.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProfileButtons();
    }

    private InputProfile? SelectedProfile => ProfileList.SelectedItem as InputProfile;

    private void UpdateProfileButtons()
    {
        bool has = SelectedProfile is not null && _rbr is not null;
        ApplyProfileButton.IsEnabled = UpdateProfileButton.IsEnabled = DeleteProfileButton.IsEnabled = has;
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
        if (ProfileStore.Exists(name) &&
            !await Confirm("Overwrite profile?", $"A profile called \"{name}\" already exists. Replace it with the current setup?", "Overwrite"))
            return;

        await Run(() =>
        {
            ProfileStore.SaveCurrent(_rbr, name, _devices);
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
            LoadProfiles();
            SetStatus($"Profile \"{p.Name}\" updated.");
        });
    }

    private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_rbr is null || SelectedProfile is not { } p) return;
        if (!await EnsureGameClosed()) return;
        if (!await Confirm("Apply profile?",
                $"This replaces the game's current bindings, axis inversion, RBRControls binds and force feedback values with \"{p.Name}\".\n\n" +
                "Save the current setup as a profile first if you want to keep it. You can always go back to the original files from Settings.",
                "Apply"))
            return;

        await Run(() =>
        {
            ProfileStore.Apply(_rbr, p);
            Reload();
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
        if (!await EnsureGameClosed()) return;
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

    // ---------------------------------------------------------------- updates

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            _update = await UpdateService.CheckAsync();
            if (_update is null)
            {
                if (userInitiated) await Info("Updates", $"You have the latest version ({UpdateService.CurrentVersion.ToString(3)}).");
                return;
            }
            UpdateText.Text = $"RBR Input Manager {_update.Version.ToString(3)} is available (you have {UpdateService.CurrentVersion.ToString(3)}).";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
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

    private void ApplyTheme(ThemePreference pref)
    {
        if (pref == ThemePreference.System)
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
            ApplicationThemeManager.ApplySystemTheme(true);
        }
        else
        {
            SystemThemeWatcher.UnWatch(this);
            ApplicationThemeManager.Apply(pref == ThemePreference.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                WindowBackdropType.Mica, true);
        }
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

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    private void OnlyBound_Click(object sender, RoutedEventArgs e) => ApplyPluginFilter();

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

    private static async Task<bool> Confirm(string title, string text, string action)
    {
        var box = new MessageBox { Title = title, Content = text, PrimaryButtonText = action, CloseButtonText = "Cancel" };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
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
