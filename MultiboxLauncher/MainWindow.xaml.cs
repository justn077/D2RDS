using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Drawing;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MultiboxLauncher;

// Main UI for configuration, launching, and broadcasting control.
public partial class MainWindow : Window
{
    // Tracks launched processes so broadcast can target selected accounts.
    private LauncherConfig _config = new();
    private readonly Dictionary<string, int> _accountProcessIds = new();
    private readonly BroadcastManager _broadcastManager;
    private BroadcastStatusWindow? _broadcastStatusWindow;
    private bool _broadcastInitialized;
    private readonly Dictionary<string, bool> _broadcastSelectionCache = new();
    private bool _broadcastSelectionCacheReady;
    private NotifyIcon? _trayIcon;
    private readonly DispatcherTimer _monitorTimer = new();
    private bool _monitorTrackingInitialized;
    private readonly Dictionary<string, WindowMonitorState> _windowMonitorStates = new();
    private const string DriverWindowTitle = "Diablo II: Resurrected";
    private bool _handlePromptedThisSession;
    private IntPtr _primaryWindowHandle;
    private int _lastLayoutHandleCount;
    private string _lastLayoutGridDevice = "";
    private IntPtr _lastLayoutForegroundHandle;
    private DateTime _lastLayoutUtc = DateTime.MinValue;
    private DateTime _lastUnresolvedBroadcastLogUtc = DateTime.MinValue;
    private string _lastUnresolvedBroadcastLog = "";
    private const double GridAspectRatio = 16.0 / 9.0;

    private sealed class WindowMonitorState
    {
        public IntPtr Monitor { get; set; }
        public ProcessLauncher.Rect LastRect { get; set; }
        public DateTime LastMoveUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class MonitorOption
    {
        public string DeviceName { get; }
        public string Label { get; }
        public bool IsPrimary { get; }

        public MonitorOption(Screen screen)
        {
            DeviceName = screen.DeviceName;
            IsPrimary = screen.Primary;
            var bounds = screen.WorkingArea;
            var primarySuffix = IsPrimary ? " (Primary)" : "";
            Label = $"Display {screen.DeviceName} {bounds.Width}x{bounds.Height}{primarySuffix}";
        }

        public override string ToString() => Label;
    }

    private sealed class AccountLaunchMonitorOption
    {
        public string DeviceName { get; }
        public string Label { get; }

        public AccountLaunchMonitorOption(string deviceName, string label)
        {
            DeviceName = deviceName;
            Label = label;
        }

        public override string ToString() => Label;
    }

    private sealed class AccountRegionOption
    {
        public string RegionName { get; }
        public string Label { get; }

        public AccountRegionOption(string regionName, string label)
        {
            RegionName = regionName ?? "";
            Label = label;
        }

        public override string ToString() => Label;
    }

    public MainWindow()
    {
        InitializeComponent();
        _broadcastManager = new BroadcastManager(
            () => _config.Broadcast,
            GetBroadcastTargets,
            IsForegroundD2R,
            IsClassicModeWindow);
        _broadcastManager.ToggleBroadcastRequested += ToggleBroadcastEnabled;
        _broadcastManager.ToggleModeRequested += ToggleBroadcastMode;
        BtnReload.Click += (_, _) => LoadButtons();
        BtnEdit.Click += (_, _) => EditConfig();
        BtnAddAccount.Click += (_, _) => AddAccount();
        BtnUpdate.Click += async (_, _) => await CheckForUpdatesAsync();
        BtnExportProfile.Click += (_, _) => ExportLayoutBroadcastProfile();
        BtnImportProfile.Click += (_, _) => ImportLayoutBroadcastProfile();
        BtnBrowseInstall.Click += (_, _) => BrowseInstallPath();
        CmbRegion.SelectionChanged += (_, _) => SaveRegionSelection();
        TxtInstallPath.LostFocus += (_, _) => SaveInstallPath();
        ChkLockOrder.Checked += (_, _) => SaveLockOrder(true);
        ChkLockOrder.Unchecked += (_, _) => SaveLockOrder(false);
        ChkMinimizeToTaskbar.Checked += (_, _) => SaveMinimizeToTaskbar(true);
        ChkMinimizeToTaskbar.Unchecked += (_, _) => SaveMinimizeToTaskbar(false);
        ChkBroadcastEnabled.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastEnabled.Unchecked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastAll.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastAll.Unchecked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastIncludeMain.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastIncludeMain.Unchecked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastSound.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastSound.Unchecked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastKeyboard.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastKeyboard.Unchecked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastMouse.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastMouse.Unchecked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastVerticalStack.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastVerticalStack.Unchecked += (_, _) => SaveBroadcastSettings();
        TxtBroadcastHotkey.LostFocus += (_, _) => SaveBroadcastSettings();
        TxtBroadcastModeHotkey.LostFocus += (_, _) => SaveBroadcastSettings();
        CmbBroadcastEngine.SelectionChanged += (_, _) => SaveBroadcastSettings();
        TxtBroadcastHotkey.PreviewKeyDown += OnHotkeyBoxKeyDown;
        TxtBroadcastModeHotkey.PreviewKeyDown += OnHotkeyBoxKeyDown;
        ChkSwapLayout.Checked += (_, _) => SaveLayoutSettings();
        ChkSwapLayout.Unchecked += (_, _) => SaveLayoutSettings();
        CmbLayoutMonitor.SelectionChanged += (_, _) => SaveLayoutSettings();
        _monitorTimer.Tick += (_, _) => TrackWindowMonitors();
        Loaded += (_, _) =>
        {
            if (!_broadcastInitialized)
            {
                _broadcastManager.Initialize(this);
                _broadcastInitialized = true;
            }
            LoadButtons();
        };
        Closing += (_, _) => SaveSettingsFromUiOnClose();
        StateChanged += (_, _) => HandleMinimizeToTray();
    }

    private void SetStatus(string text) => TxtStatus.Text = text;

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _broadcastStatusWindow?.Close();
        _trayIcon?.Dispose();
        _broadcastManager.Dispose();
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }

    private void EditConfig()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = ConfigLoader.DefaultConfigPath,
            UseShellExecute = true
        });
    }

    private void ExportLayoutBroadcastProfile()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Layout/Broadcast Profile",
                Filter = "D2RDS profile (*.d2rdsprofile.json)|*.d2rdsprofile.json|JSON (*.json)|*.json",
                FileName = $"D2RDS-profile-{DateTime.Now:yyyyMMdd-HHmm}.d2rdsprofile.json"
            };
            if (dialog.ShowDialog(this) != true)
                return;

            var bundle = new LayoutBroadcastProfile
            {
                Name = $"Export {DateTime.Now:yyyy-MM-dd HH:mm}",
                ExportedAtUtc = DateTime.UtcNow,
                Broadcast = _config.Broadcast,
                WindowLayout = _config.WindowLayout,
                Accounts = _config.Accounts.Select(a => new AccountLayoutBroadcastBinding
                {
                    Id = a.Id,
                    Email = a.Email,
                    Nickname = a.Nickname,
                    BroadcastEnabled = a.BroadcastEnabled,
                    ClassicMode = a.ClassicMode,
                    LaunchMonitorDevice = a.LaunchMonitorDevice
                }).ToList()
            };

            var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);
            SetStatus($"Exported profile: {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Export profile error");
        }
    }

    private void ImportLayoutBroadcastProfile()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Layout/Broadcast Profile",
                Filter = "D2RDS profile (*.d2rdsprofile.json)|*.d2rdsprofile.json|JSON (*.json)|*.json"
            };
            if (dialog.ShowDialog(this) != true)
                return;

            var json = File.ReadAllText(dialog.FileName);
            var bundle = JsonSerializer.Deserialize<LayoutBroadcastProfile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (bundle is null)
                throw new InvalidOperationException("Failed to parse selected profile file.");

            _config.Broadcast = bundle.Broadcast ?? new BroadcastSettings();
            _config.WindowLayout = bundle.WindowLayout ?? new WindowLayoutSettings();

            if (bundle.Accounts is not null)
            {
                foreach (var src in bundle.Accounts)
                {
                    var account = _config.Accounts.FirstOrDefault(a =>
                        (!string.IsNullOrWhiteSpace(src.Id) && string.Equals(a.Id, src.Id, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(src.Email) && string.Equals(a.Email, src.Email, StringComparison.OrdinalIgnoreCase)));
                    if (account is null)
                        continue;

                    account.BroadcastEnabled = src.BroadcastEnabled;
                    account.ClassicMode = src.ClassicMode;
                    account.LaunchMonitorDevice = src.LaunchMonitorDevice ?? "";
                }
            }

            ConfigLoader.Save(_config);
            LoadButtons();
            SetStatus($"Imported profile: {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Import profile error");
        }
    }

    private void LoadButtons()
    {
        try
        {
            ProfilesPanel.Children.Clear();
            _config = ConfigLoader.LoadOrCreate();

            EnsureRegionSelected();
            EnsureInstallPathSelected();
            LoadSettings();
            var accountMonitorOptions = BuildAccountLaunchMonitorOptions();
            var accountRegionOptions = BuildAccountRegionOptions();

            for (var i = 0; i < _config.Accounts.Count; i++)
            {
                var account = _config.Accounts[i];
                var displayName = string.IsNullOrWhiteSpace(account.Nickname) ? account.Email : account.Nickname;

                var row = new System.Windows.Controls.Grid
                {
                    Margin = new Thickness(0, 0, 0, 6)
                };
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(220) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(170) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(70) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(80) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(130) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(190) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(70) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(70) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(55) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(55) });

                var launchButton = new System.Windows.Controls.Button
                {
                    Content = $"Launch {displayName}",
                    Height = 36,
                    Width = 210
                };
                launchButton.Click += async (_, _) => await RunAccountAsync(account);
                System.Windows.Controls.Grid.SetColumn(launchButton, 0);

                var emailText = new TextBlock
                {
                    Text = account.Email,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                    Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
                };
                System.Windows.Controls.Grid.SetColumn(emailText, 1);

                var broadcastToggle = new System.Windows.Controls.CheckBox
                {
                    Content = "Bcast",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsChecked = account.BroadcastEnabled,
                    ToolTip = "Include this account when All is off and broadcasting is enabled."
                };
                broadcastToggle.Checked += (_, _) => ToggleAccountBroadcast(account, true);
                broadcastToggle.Unchecked += (_, _) => ToggleAccountBroadcast(account, false);
                System.Windows.Controls.Grid.SetColumn(broadcastToggle, 2);

                var classicToggle = new System.Windows.Controls.CheckBox
                {
                    Content = "Classic",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsChecked = account.ClassicMode,
                    ToolTip = "Assume classic 4:3 viewport for mouse broadcast scaling."
                };
                classicToggle.Checked += (_, _) => ToggleAccountClassic(account, true);
                classicToggle.Unchecked += (_, _) => ToggleAccountClassic(account, false);
                System.Windows.Controls.Grid.SetColumn(classicToggle, 3);

                var selectedRegionOption = accountRegionOptions.FirstOrDefault(o =>
                    string.Equals(o.RegionName, account.Region, StringComparison.OrdinalIgnoreCase))
                    ?? accountRegionOptions[0];
                var accountRegionCombo = new System.Windows.Controls.ComboBox
                {
                    ItemsSource = accountRegionOptions,
                    SelectedItem = selectedRegionOption,
                    Height = 30,
                    Width = 122,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Per-account launch region for fast relaunch trading."
                };
                accountRegionCombo.SelectionChanged += (_, _) =>
                {
                    if (accountRegionCombo.SelectedItem is AccountRegionOption selected)
                        ToggleAccountRegion(account, selected.RegionName);
                };
                System.Windows.Controls.Grid.SetColumn(accountRegionCombo, 4);

                var selectedMonitorOption = accountMonitorOptions.FirstOrDefault(o =>
                    string.Equals(o.DeviceName, account.LaunchMonitorDevice, StringComparison.OrdinalIgnoreCase))
                    ?? accountMonitorOptions[0];
                var launchMonitorCombo = new System.Windows.Controls.ComboBox
                {
                    ItemsSource = accountMonitorOptions,
                    SelectedItem = selectedMonitorOption,
                    Height = 30,
                    Width = 182,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Choose which display this account should snap to after launch. Auto keeps current monitor."
                };
                launchMonitorCombo.SelectionChanged += (_, _) =>
                {
                    if (launchMonitorCombo.SelectedItem is AccountLaunchMonitorOption selected)
                        ToggleAccountLaunchMonitor(account, selected.DeviceName);
                };
                System.Windows.Controls.Grid.SetColumn(launchMonitorCombo, 5);

                var editButton = new System.Windows.Controls.Button
                {
                    Content = "Edit",
                    Height = 34,
                    Width = 60,
                    Margin = new Thickness(0)
                };
                editButton.Click += (_, _) => EditAccount(account);
                System.Windows.Controls.Grid.SetColumn(editButton, 6);

                var deleteButton = new System.Windows.Controls.Button
                {
                    Content = "Delete",
                    Height = 34,
                    Width = 60,
                    Margin = new Thickness(0)
                };
                deleteButton.Click += (_, _) => DeleteAccount(account);
                System.Windows.Controls.Grid.SetColumn(deleteButton, 7);

                var upButton = new System.Windows.Controls.Button
                {
                    Content = "▲",
                    Height = 34,
                    Width = 45,
                    Margin = new Thickness(0),
                    IsEnabled = !_config.LockOrder && i > 0
                };
                upButton.Click += (_, _) => MoveAccount(account, -1);
                System.Windows.Controls.Grid.SetColumn(upButton, 8);

                var downButton = new System.Windows.Controls.Button
                {
                    Content = "▼",
                    Height = 34,
                    Width = 50,
                    Margin = new Thickness(0),
                    IsEnabled = !_config.LockOrder && i < _config.Accounts.Count - 1
                };
                downButton.Click += (_, _) => MoveAccount(account, 1);
                System.Windows.Controls.Grid.SetColumn(downButton, 9);

                row.Children.Add(launchButton);
                row.Children.Add(emailText);
                row.Children.Add(broadcastToggle);
                row.Children.Add(classicToggle);
                row.Children.Add(accountRegionCombo);
                row.Children.Add(launchMonitorCombo);
                row.Children.Add(editButton);
                row.Children.Add(deleteButton);
                row.Children.Add(upButton);
                row.Children.Add(downButton);
                ProfilesPanel.Children.Add(row);
            }

            BtnAddAccount.IsEnabled = _config.Accounts.Count < 7;
            SetStatus($"Loaded {_config.Accounts.Count} accounts");
        }
        catch (Exception ex)
        {
            SetStatus("Config error");
            System.Windows.MessageBox.Show(ex.Message, "Config error");
        }
    }

    private void LoadSettings()
    {
        CmbRegion.ItemsSource = RegionOptions.All;
        var selected = RegionOptions.FindByName(_config.Region);
        if (selected is not null)
            CmbRegion.SelectedItem = selected;

        TxtInstallPath.Text = _config.InstallPath;
        ChkLockOrder.IsChecked = _config.LockOrder;
        ChkMinimizeToTaskbar.IsChecked = _config.MinimizeToTaskbar;
        ChkBroadcastEnabled.IsChecked = _config.Broadcast.Enabled;
        ChkBroadcastAll.IsChecked = _config.Broadcast.BroadcastAll;
        ChkBroadcastIncludeMain.IsChecked = _config.Broadcast.IncludeMainWindowInSelected;
        ChkBroadcastSound.IsChecked = _config.Broadcast.ActivationSoundEnabled;
        ChkBroadcastKeyboard.IsChecked = _config.Broadcast.Keyboard;
        ChkBroadcastMouse.IsChecked = _config.Broadcast.Mouse;
        ChkBroadcastVerticalStack.IsChecked = _config.Broadcast.VerticalMonitorStackMode;
        TxtBroadcastHotkey.Text = _config.Broadcast.ToggleBroadcastHotkey;
        TxtBroadcastModeHotkey.Text = _config.Broadcast.ToggleModeHotkey;
        CmbBroadcastEngine.ItemsSource = new[]
        {
            "LegacyWindowMessages",
            "IsbStyleProcessFanout"
        };
        CmbBroadcastEngine.SelectedItem = _config.Broadcast.InputEngine;
        ChkSwapLayout.IsChecked = _config.WindowLayout.Enabled;
        LoadLayoutMonitors();
        TxtVersion.Text = $"v{UpdateService.CurrentVersion}";
        _broadcastManager.UpdateHotkeys();
        _broadcastManager.UpdateBroadcastState(_config.Broadcast);

        EnsureBroadcastStatusWindow();
        ApplyBroadcastOverlaySettings();
        UpdateBroadcastStatusWindow();
        UpdateBroadcastDiagnostics();
        ApplyMinimizeBehavior();
        ConfigureMonitorTracking();
        CheckHandleRequirementOnStartup();
    }

    private void EnsureRegionSelected()
    {
        if (RegionOptions.FindByName(_config.Region) is not null)
            return;

        var picker = new RegionPickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedRegion is not null)
        {
            _config.Region = picker.SelectedRegion.Name;
            ConfigLoader.Save(_config);
        }
    }

    private void SaveRegionSelection()
    {
        if (CmbRegion.SelectedItem is RegionOption option)
        {
            _config.Region = option.Name;
            ConfigLoader.Save(_config);
        }
    }

    private void BrowseInstallPath()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select your Diablo II Resurrected install folder",
            SelectedPath = Directory.Exists(_config.InstallPath) ? _config.InstallPath : Defaults.DefaultInstallPath
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtInstallPath.Text = dialog.SelectedPath;
            SaveInstallPath();
        }
    }

    private void EnsureInstallPathSelected()
    {
        var path = _config.InstallPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var pick = System.Windows.MessageBox.Show("Select your Diablo II Resurrected install folder now?", "Install path missing", MessageBoxButton.YesNo);
            if (pick == MessageBoxResult.Yes)
                BrowseInstallPath();
            return;
        }

        var d2rExe = System.IO.Path.Combine(path, "D2R.exe");
        if (!File.Exists(d2rExe))
        {
            var pick = System.Windows.MessageBox.Show("D2R.exe was not found in the selected install path. Select the correct folder now?", "Install path invalid", MessageBoxButton.YesNo);
            if (pick == MessageBoxResult.Yes)
                BrowseInstallPath();
        }
    }

    private void SaveInstallPath()
    {
        var path = TxtInstallPath.Text.Trim();
        _config.InstallPath = path;
        ConfigLoader.Save(_config);
    }

    private void SaveLockOrder(bool locked)
    {
        _config.LockOrder = locked;
        ConfigLoader.Save(_config);
        LoadButtons();
    }

    private void SaveMinimizeToTaskbar(bool enabled)
    {
        _config.MinimizeToTaskbar = enabled;
        ConfigLoader.Save(_config);
        ApplyMinimizeBehavior();
    }

    private void SaveBroadcastSettings()
    {
        var wasBroadcastActive = IsBroadcastActive(_config.Broadcast);
        _config.Broadcast.Enabled = ChkBroadcastEnabled.IsChecked == true;
        var broadcastAll = ChkBroadcastAll.IsChecked == true;
        ApplyBroadcastAllMode(broadcastAll, saveAfter: false, refreshUi: false);
        _config.Broadcast.IncludeMainWindowInSelected = ChkBroadcastIncludeMain.IsChecked == true;
        _config.Broadcast.ActivationSoundEnabled = ChkBroadcastSound.IsChecked != false;
        _config.Broadcast.Keyboard = ChkBroadcastKeyboard.IsChecked == true;
        _config.Broadcast.Mouse = ChkBroadcastMouse.IsChecked == true;
        _config.Broadcast.VerticalMonitorStackMode = ChkBroadcastVerticalStack.IsChecked == true;
        _config.Broadcast.InputEngine = CmbBroadcastEngine.SelectedItem?.ToString() ?? "LegacyWindowMessages";
        // Advanced mapping stays internal/automatic.
        _config.Broadcast.MouseTransformMode = "Viewport";
        _config.Broadcast.UseRepeaterRegions = false;
        _config.Broadcast.SourceRepeaterRegion = "";
        _config.Broadcast.TargetRepeaterRegion = "";

        _config.Broadcast.ToggleBroadcastHotkey = TxtBroadcastHotkey.Text.Trim();
        _config.Broadcast.ToggleModeHotkey = TxtBroadcastModeHotkey.Text.Trim();

        ConfigLoader.Save(_config);
        _broadcastManager.UpdateHotkeys();
        _broadcastManager.UpdateBroadcastState(_config.Broadcast);
        UpdateBroadcastStatusWindow();
        UpdateBroadcastDiagnostics();

        var isBroadcastActive = IsBroadcastActive(_config.Broadcast);
        if (_config.Broadcast.ActivationSoundEnabled && wasBroadcastActive != isBroadcastActive)
            PlayBroadcastStateAlert(isBroadcastActive);
    }

    private void LoadLayoutMonitors()
    {
        var options = Screen.AllScreens.Select(screen => new MonitorOption(screen)).ToList();
        CmbLayoutMonitor.ItemsSource = options;

        MonitorOption? selected = null;
        if (!string.IsNullOrWhiteSpace(_config.WindowLayout.GridMonitorDevice))
            selected = options.FirstOrDefault(o => string.Equals(o.DeviceName, _config.WindowLayout.GridMonitorDevice, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
            selected = options.FirstOrDefault(o => !o.IsPrimary) ?? options.FirstOrDefault();

        if (selected is not null)
        {
            CmbLayoutMonitor.SelectedItem = selected;
            if (!string.Equals(_config.WindowLayout.GridMonitorDevice, selected.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                _config.WindowLayout.GridMonitorDevice = selected.DeviceName;
                ConfigLoader.Save(_config);
            }
        }
    }

    private static List<AccountLaunchMonitorOption> BuildAccountLaunchMonitorOptions()
    {
        var options = new List<AccountLaunchMonitorOption>
        {
            new("", "Auto (Current Display)")
        };

        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.WorkingArea;
            var primarySuffix = screen.Primary ? " (Primary)" : "";
            var label = $"Display {screen.DeviceName} {bounds.Width}x{bounds.Height}{primarySuffix}";
            options.Add(new AccountLaunchMonitorOption(screen.DeviceName, label));
        }

        return options;
    }

    private static List<AccountRegionOption> BuildAccountRegionOptions()
    {
        var options = new List<AccountRegionOption>
        {
            new("", "Global (App default)")
        };

        foreach (var option in RegionOptions.All)
            options.Add(new AccountRegionOption(option.Name, option.Name));

        return options;
    }

    private void SaveLayoutSettings()
    {
        _config.WindowLayout.Enabled = ChkSwapLayout.IsChecked == true;
        // Keep layout region model internal/automatic.
        _config.WindowLayout.UseRegionModel = false;
        _config.WindowLayout.InstantSwap = true;
        _config.WindowLayout.ForegroundRegion = "";
        _config.WindowLayout.BackgroundRegion = "";
        if (CmbLayoutMonitor.SelectedItem is MonitorOption option)
            _config.WindowLayout.GridMonitorDevice = option.DeviceName;
        ConfigLoader.Save(_config);

        _lastLayoutGridDevice = "";
        _lastLayoutHandleCount = 0;
        _lastLayoutForegroundHandle = IntPtr.Zero;
        _lastLayoutUtc = DateTime.MinValue;
        UpdateBroadcastDiagnostics();
    }

    private void SaveSettingsFromUiOnClose()
    {
        try
        {
            if (CmbRegion.SelectedItem is RegionOption selectedRegion)
                _config.Region = selectedRegion.Name;

            _config.InstallPath = TxtInstallPath.Text.Trim();
            _config.LockOrder = ChkLockOrder.IsChecked == true;
            _config.MinimizeToTaskbar = ChkMinimizeToTaskbar.IsChecked == true;

            _config.Broadcast.Enabled = ChkBroadcastEnabled.IsChecked == true;
            _config.Broadcast.BroadcastAll = ChkBroadcastAll.IsChecked == true;
            _config.Broadcast.IncludeMainWindowInSelected = ChkBroadcastIncludeMain.IsChecked == true;
            _config.Broadcast.ActivationSoundEnabled = ChkBroadcastSound.IsChecked != false;
            _config.Broadcast.Keyboard = ChkBroadcastKeyboard.IsChecked == true;
            _config.Broadcast.Mouse = ChkBroadcastMouse.IsChecked == true;
            _config.Broadcast.VerticalMonitorStackMode = ChkBroadcastVerticalStack.IsChecked == true;
            _config.Broadcast.ToggleBroadcastHotkey = TxtBroadcastHotkey.Text.Trim();
            _config.Broadcast.ToggleModeHotkey = TxtBroadcastModeHotkey.Text.Trim();
            _config.Broadcast.InputEngine = CmbBroadcastEngine.SelectedItem?.ToString() ?? "LegacyWindowMessages";
            _config.Broadcast.MouseTransformMode = "Viewport";
            _config.Broadcast.UseRepeaterRegions = false;
            _config.Broadcast.SourceRepeaterRegion = "";
            _config.Broadcast.TargetRepeaterRegion = "";

            _config.WindowLayout.Enabled = ChkSwapLayout.IsChecked == true;
            _config.WindowLayout.UseRegionModel = false;
            _config.WindowLayout.InstantSwap = true;
            _config.WindowLayout.ForegroundRegion = "";
            _config.WindowLayout.BackgroundRegion = "";
            if (CmbLayoutMonitor.SelectedItem is MonitorOption layoutMonitor)
                _config.WindowLayout.GridMonitorDevice = layoutMonitor.DeviceName;

            ConfigLoader.Save(_config);
        }
        catch (Exception ex)
        {
            Log.Info($"Failed to persist settings on close: {ex.Message}");
        }
    }

    private void OnHotkeyBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box)
            return;

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            SaveBroadcastSettings();
            box.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
            return;
        }
        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        var hotkey = FormatHotkey(System.Windows.Input.Keyboard.Modifiers, key);
        if (!string.IsNullOrWhiteSpace(hotkey))
        {
            box.Text = hotkey;
            e.Handled = true;
            SaveBroadcastSettings();
        }
    }

    private static bool IsModifierKey(System.Windows.Input.Key key)
    {
        return key == System.Windows.Input.Key.LeftCtrl || key == System.Windows.Input.Key.RightCtrl
            || key == System.Windows.Input.Key.LeftAlt || key == System.Windows.Input.Key.RightAlt
            || key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift
            || key == System.Windows.Input.Key.LWin || key == System.Windows.Input.Key.RWin;
    }

    private static string FormatHotkey(System.Windows.Input.ModifierKeys modifiers, System.Windows.Input.Key key)
    {
        if (key == System.Windows.Input.Key.None)
            return "";

        var parts = new List<string>();
        if ((modifiers & System.Windows.Input.ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & System.Windows.Input.ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & System.Windows.Input.ModifierKeys.Windows) != 0) parts.Add("Win");

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private void AddAccount()
    {
        if (_config.Accounts.Count >= 7)
        {
            System.Windows.MessageBox.Show("You can add up to 7 accounts.", "Add Account");
            return;
        }

        var dialog = new AddAccountWindow { Owner = this };
        dialog.RequirePassword = true;
        dialog.AllowPasswordChange = false;
        dialog.SetDialogMode("Add Account", "Add");
        if (dialog.ShowDialog() != true)
            return;

        var accountId = Guid.NewGuid().ToString("N");
        var credentialId = $"D2RDS:{accountId}";
        CredentialStore.Save(credentialId, dialog.Email, dialog.Password);

        _config.Accounts.Add(new AccountProfile
        {
            Id = accountId,
            Email = dialog.Email,
            Nickname = dialog.Nickname,
            CredentialId = credentialId,
            Region = _config.Region
        });

        ConfigLoader.Save(_config);
        LoadButtons();
    }

    private void EditAccount(AccountProfile account)
    {
        var dialog = new AddAccountWindow { Owner = this, RequirePassword = false, AllowPasswordChange = true };
        dialog.SetDialogMode("Edit Account", "Save");
        dialog.SetInitialValues(account.Email, account.Nickname);

        if (dialog.ShowDialog() != true)
            return;

        var newEmail = dialog.Email;
        var newNickname = dialog.Nickname;
        var newPassword = dialog.Password;

        if (!dialog.ChangePassword)
        {
            var credential = CredentialStore.Read(account.CredentialId);
            if (credential is null)
            {
                System.Windows.MessageBox.Show("Stored credentials not found. Re-add the account.", "Edit Account");
                return;
            }

            CredentialStore.Save(account.CredentialId, newEmail, credential.Value.Secret);
        }
        else
        {
            CredentialStore.Save(account.CredentialId, newEmail, newPassword);
        }

        account.Email = newEmail;
        account.Nickname = newNickname;
        ConfigLoader.Save(_config);
        LoadButtons();
    }

    private void DeleteAccount(AccountProfile account)
    {
        var label = string.IsNullOrWhiteSpace(account.Nickname) ? account.Email : account.Nickname;
        var result = System.Windows.MessageBox.Show($"Delete account '{label}'? This cannot be undone.", "Delete Account", MessageBoxButton.YesNo);
        if (result != MessageBoxResult.Yes)
            return;

        CredentialStore.Delete(account.CredentialId);
        _config.Accounts.Remove(account);
        ConfigLoader.Save(_config);
        LoadButtons();
    }

    private void MoveAccount(AccountProfile account, int direction)
    {
        if (_config.LockOrder)
            return;

        var currentIndex = _config.Accounts.IndexOf(account);
        if (currentIndex < 0)
            return;

        var newIndex = currentIndex + direction;
        if (newIndex < 0 || newIndex >= _config.Accounts.Count)
            return;

        _config.Accounts.RemoveAt(currentIndex);
        _config.Accounts.Insert(newIndex, account);
        ConfigLoader.Save(_config);
        LoadButtons();
    }

    private void ToggleAccountBroadcast(AccountProfile account, bool enabled)
    {
        account.BroadcastEnabled = enabled;
        _broadcastSelectionCache[account.Id] = enabled;
        _broadcastSelectionCacheReady = true;
        if (_config.Broadcast.BroadcastAll && _config.Accounts.Any(a => !a.BroadcastEnabled))
        {
            _config.Broadcast.BroadcastAll = false;
            ChkBroadcastAll.IsChecked = false;
        }
        ConfigLoader.Save(_config);
        UpdateBroadcastStatusWindow();
        UpdateBroadcastDiagnostics();
    }

    private void ToggleAccountClassic(AccountProfile account, bool enabled)
    {
        account.ClassicMode = enabled;
        ConfigLoader.Save(_config);
        UpdateBroadcastDiagnostics();
    }

    private void ToggleAccountRegion(AccountProfile account, string regionName)
    {
        account.Region = regionName?.Trim() ?? "";
        ConfigLoader.Save(_config);
    }

    private async void ToggleAccountLaunchMonitor(AccountProfile account, string deviceName)
    {
        account.LaunchMonitorDevice = deviceName ?? "";
        ConfigLoader.Save(_config);
        UpdateBroadcastDiagnostics();
        await ApplyMonitorSelectionToRunningAccountAsync(account);
    }

    private async Task ApplyMonitorSelectionToRunningAccountAsync(AccountProfile account)
    {
        if (account is null || string.IsNullOrWhiteSpace(account.Id))
            return;

        // Swap layout owns window placement; don't fight it here.
        if (_config.WindowLayout.Enabled)
            return;

        if (!TryResolveAccountWindowHandle(account, out var handle))
            return;
        if (!ProcessLauncher.IsWindowResponsive(handle))
            return;

        ProcessLauncher.TryApplyBorderlessStyle(handle, allowResize: false);
        FitWindowToConfiguredMonitorWorkArea(handle, account.LaunchMonitorDevice);
        await Task.Delay(700);
        FitWindowToConfiguredMonitorWorkArea(handle, account.LaunchMonitorDevice);

        if (_windowMonitorStates.TryGetValue(account.Id, out var state))
        {
            var monitor = ProcessLauncher.GetMonitorHandle(handle);
            if (monitor != IntPtr.Zero)
                state.Monitor = monitor;

            if (ProcessLauncher.TryGetWindowRect(handle, out var rect))
                state.LastRect = rect;

            state.LastMoveUtc = DateTime.UtcNow;
        }
    }

    private bool TryResolveAccountWindowHandle(AccountProfile account, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (account is null)
            return false;

        if (_accountProcessIds.TryGetValue(account.Id, out var pid))
        {
            if (ProcessLauncher.TryGetProcessMainWindowHandle(pid, "D2R", out var bound))
            {
                handle = bound;
                return true;
            }

            _accountProcessIds.Remove(account.Id);
        }

        foreach (var candidate in GetOrderedD2RHandles())
        {
            if (candidate == IntPtr.Zero)
                continue;
            if (!IsLikelyAccountTitleMatch(candidate, account))
                continue;

            handle = candidate;
            var resolvedPid = ProcessLauncher.GetWindowProcessId(candidate);
            if (resolvedPid != 0)
                _accountProcessIds[account.Id] = resolvedPid;
            return true;
        }

        return false;
    }

    private void ToggleBroadcastEnabled()
    {
        var wasBroadcastActive = IsBroadcastActive(_config.Broadcast);
        _config.Broadcast.Enabled = !_config.Broadcast.Enabled;
        ConfigLoader.Save(_config);
        Dispatcher.Invoke(() =>
        {
            ChkBroadcastEnabled.IsChecked = _config.Broadcast.Enabled;
            UpdateBroadcastStatusWindow();
            UpdateBroadcastDiagnostics();
            _broadcastManager.UpdateBroadcastState(_config.Broadcast);

            var isBroadcastActive = IsBroadcastActive(_config.Broadcast);
            if (_config.Broadcast.ActivationSoundEnabled && wasBroadcastActive != isBroadcastActive)
                PlayBroadcastStateAlert(isBroadcastActive);
        });
    }

    private void ToggleBroadcastMode()
    {
        ApplyBroadcastAllMode(!_config.Broadcast.BroadcastAll, saveAfter: true, refreshUi: true);
    }

    private void ApplyBroadcastAllMode(bool broadcastAll, bool saveAfter, bool refreshUi)
    {
        var previous = _config.Broadcast.BroadcastAll;
        if (previous == broadcastAll)
            return;

        if (broadcastAll)
            CacheBroadcastSelection();

        _config.Broadcast.BroadcastAll = broadcastAll;

        if (!broadcastAll)
            RestoreBroadcastSelection();

        if (refreshUi)
        {
            Dispatcher.Invoke(() =>
            {
                ChkBroadcastAll.IsChecked = broadcastAll;
                LoadButtons();
                UpdateBroadcastStatusWindow();
                UpdateBroadcastDiagnostics();
            });
        }

        if (saveAfter)
            ConfigLoader.Save(_config);
    }

    private void CacheBroadcastSelection()
    {
        _broadcastSelectionCache.Clear();
        foreach (var account in _config.Accounts)
            _broadcastSelectionCache[account.Id] = account.BroadcastEnabled;
        _broadcastSelectionCacheReady = true;
    }

    private void RestoreBroadcastSelection()
    {
        if (!_broadcastSelectionCacheReady)
            return;

        foreach (var account in _config.Accounts)
        {
            if (_broadcastSelectionCache.TryGetValue(account.Id, out var enabled))
                account.BroadcastEnabled = enabled;
        }
    }

    private static bool IsBroadcastActive(BroadcastSettings settings)
        => settings.Enabled && (settings.Keyboard || settings.Mouse);

    private static void PlayBroadcastStateAlert(bool isActive)
    {
        _ = Task.Run(() =>
        {
            if (isActive)
            {
                TryPlayAlertTone(1319, 180);
                TryPlayAlertTone(988, 180);
                TryPlayAlertTone(1319, 220);
                SystemSounds.Hand.Play();
                return;
            }

            TryPlayAlertTone(880, 160);
            TryPlayAlertTone(659, 180);
            TryPlayAlertTone(523, 220);
            SystemSounds.Asterisk.Play();
        });
    }

    private static void TryPlayAlertTone(int frequency, int durationMs)
    {
        try
        {
            Console.Beep(frequency, durationMs);
        }
        catch
        {
            SystemSounds.Hand.Play();
        }
    }


    private void EnsureBroadcastStatusWindow()
    {
        if (_broadcastStatusWindow is null)
        {
            _broadcastStatusWindow = new BroadcastStatusWindow
            {
                ShowActivated = false
            };
            _broadcastStatusWindow.OverlayStateChanged += OnBroadcastOverlayStateChanged;
            _broadcastStatusWindow.Closed += (_, _) => _broadcastStatusWindow = null;
        }

        _broadcastStatusWindow.EnsureVisible();
    }

    private void ApplyBroadcastOverlaySettings()
    {
        if (_broadcastStatusWindow is null)
            return;

        _broadcastStatusWindow.ApplyPlacement(
            _config.Broadcast.OverlayLeft,
            _config.Broadcast.OverlayTop,
            _config.Broadcast.OverlayLocked);
    }

    private void OnBroadcastOverlayStateChanged(double left, double top, bool isLocked)
    {
        _config.Broadcast.OverlayLeft = left;
        _config.Broadcast.OverlayTop = top;
        _config.Broadcast.OverlayLocked = isLocked;
        ConfigLoader.Save(_config);
    }

    private void UpdateBroadcastStatusWindow()
    {
        EnsureBroadcastStatusWindow();
        _broadcastStatusWindow?.UpdateStatus(_config.Broadcast);
    }

    private void UpdateBroadcastDiagnostics()
    {
        if (!_config.Broadcast.DiagnosticsEnabled)
        {
            TxtBroadcastDiagnostics.Text = "";
            return;
        }

        try
        {
            var sb = new StringBuilder();
            var foreground = ProcessLauncher.GetForegroundWindowHandle();
            var foregroundTitle = ProcessLauncher.GetWindowTitle(foreground);
            ProcessLauncher.TryGetMonitorDeviceName(foreground, out var foregroundMonitor);
            var isD2R = foreground != IntPtr.Zero && ProcessLauncher.IsWindowProcessName(foreground, "D2R");
            sb.Append("Diagnostics: ");
            sb.Append(isD2R ? "Foreground=D2R" : "Foreground=Other");
            if (!string.IsNullOrWhiteSpace(foregroundTitle))
                sb.Append($" ({foregroundTitle})");
            if (!string.IsNullOrWhiteSpace(foregroundMonitor))
                sb.Append($" @{foregroundMonitor}");

            var targets = GetBroadcastTargets();
            sb.Append($" | Targets={targets.Count}");

            var details = new List<string>();
            foreach (var target in targets.Take(4))
            {
                var title = ProcessLauncher.GetWindowTitle(target.Handle);
                ProcessLauncher.TryGetMonitorDeviceName(target.Handle, out var monitor);
                details.Add($"{title}@{monitor}");
            }

            if (details.Count > 0)
                sb.Append(" | " + string.Join(" ; ", details));

            TxtBroadcastDiagnostics.Text = sb.ToString();
        }
        catch
        {
            // keep diagnostics best-effort only
        }
    }

    private void EnsureOverlayVisible()
    {
        if (_broadcastStatusWindow is null)
            return;

        if (!_broadcastStatusWindow.IsVisible)
            _broadcastStatusWindow.Show();
        _broadcastStatusWindow.Topmost = true;
    }

    private void ApplyMinimizeBehavior()
    {
        // Keep normal window style; minimize behavior handled in StateChanged.
        if (AllowsTransparency)
        {
            // Ensure a compatible window style to avoid startup popups.
            AllowsTransparency = false;
        }
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = true;
    }

    private void HandleMinimizeToTray()
    {
        EnsureOverlayVisible();
        if (!_config.MinimizeToTaskbar)
            return;

        if (WindowState == WindowState.Minimized)
        {
            ShowInTaskbar = false;
            Hide();
            EnsureTrayIcon();
            _trayIcon!.Visible = true;
        }
        else
        {
            ShowInTaskbar = true;
            if (_trayIcon is not null)
                _trayIcon.Visible = false;
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
            return;

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        var icon = System.IO.File.Exists(iconPath)
            ? new System.Drawing.Icon(iconPath)
            : System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName);

        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "D2RDS",
            Visible = false
        };

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Restore", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null)
            _trayIcon.Visible = false;
    }

    private void ConfigureMonitorTracking()
    {
        if (!_monitorTrackingInitialized)
        {
            _monitorTrackingInitialized = true;
        }

        _monitorTimer.Interval = TimeSpan.FromMilliseconds(ProcessLauncher.DefaultMonitorCheckIntervalMs);
        _monitorTimer.Start();
    }

    private void TrackWindowMonitors()
    {
        if (_config.WindowLayout.Enabled)
        {
            UpdateSwapLayout();
            UpdateBroadcastDiagnostics();
            return;
        }

        var debounceMs = ProcessLauncher.DefaultMoveDebounceMs;

        foreach (var kvp in _accountProcessIds)
        {
            var accountId = kvp.Key;
            var pid = kvp.Value;
            var hwnd = ProcessLauncher.TryGetMainWindowHandle(pid);
            if (hwnd == IntPtr.Zero)
                continue;
            if (!ProcessLauncher.IsWindowResponsive(hwnd))
                continue;

            var monitor = ProcessLauncher.GetMonitorHandle(hwnd);
            if (monitor == IntPtr.Zero)
                continue;

            if (!ProcessLauncher.TryGetWindowRect(hwnd, out var rect))
                continue;

            if (!_windowMonitorStates.TryGetValue(accountId, out var state))
            {
                _windowMonitorStates[accountId] = new WindowMonitorState
                {
                    Monitor = monitor,
                    LastRect = rect,
                    LastMoveUtc = DateTime.UtcNow
                };
                continue;
            }

            if (!RectEquals(state.LastRect, rect))
            {
                state.LastRect = rect;
                state.LastMoveUtc = DateTime.UtcNow;
            }

            if (monitor != state.Monitor)
            {
                var elapsed = DateTime.UtcNow - state.LastMoveUtc;
                if (elapsed.TotalMilliseconds >= debounceMs)
                {
                    state.Monitor = monitor;
                    ProcessLauncher.FitWindowToMonitorWorkArea(hwnd);
                }
            }
        }
        UpdateBroadcastDiagnostics();
    }

    private void UpdateSwapLayout()
    {
        var handles = GetOrderedD2RHandles();
        if (handles.Count == 0)
            return;

        var primaryScreen = Screen.PrimaryScreen;
        if (primaryScreen is null)
            return;

        var gridScreen = GetGridScreen();
        if (gridScreen is null)
            return;

        var foreground = ProcessLauncher.GetForegroundWindowHandle();
        var foregroundD2R = ProcessLauncher.IsWindowProcessName(foreground, "D2R") ? foreground : IntPtr.Zero;

        if (foregroundD2R != IntPtr.Zero)
            _primaryWindowHandle = foregroundD2R;
        else if (_primaryWindowHandle == IntPtr.Zero || !handles.Contains(_primaryWindowHandle))
            _primaryWindowHandle = handles[0];

        if (_primaryWindowHandle == IntPtr.Zero)
            return;

        var handleCount = handles.Count;
        var gridDevice = gridScreen.DeviceName;
        var foregroundChanged = foregroundD2R != _lastLayoutForegroundHandle;
        var shouldLayout =
            handleCount != _lastLayoutHandleCount ||
            !string.Equals(gridDevice, _lastLayoutGridDevice, StringComparison.OrdinalIgnoreCase) ||
            (_config.WindowLayout.InstantSwap && foregroundChanged) ||
            (DateTime.UtcNow - _lastLayoutUtc).TotalSeconds > 2;

        _lastLayoutHandleCount = handleCount;
        _lastLayoutGridDevice = gridDevice;
        _lastLayoutForegroundHandle = foregroundD2R;
        _lastLayoutUtc = DateTime.UtcNow;

        if (!shouldLayout)
            return;

        ProcessLauncher.TryApplyBorderlessStyle(_primaryWindowHandle, allowResize: false);
        var primaryWork = primaryScreen.WorkingArea;
        if (_config.WindowLayout.UseRegionModel &&
            TryApplyNormalizedRegion(primaryWork, _config.WindowLayout.ForegroundRegion, out var primaryRegion))
        {
            primaryWork = primaryRegion;
        }
        var primaryRect = RectFromWorkingArea(primaryWork);
        MoveWindowIfNeeded(_primaryWindowHandle, primaryRect);

        var gridHandles = handles.Where(h => h != _primaryWindowHandle).ToList();
        if (gridHandles.Count == 0)
            return;

        if (string.Equals(gridScreen.DeviceName, primaryScreen.DeviceName, StringComparison.OrdinalIgnoreCase))
            return;

        var gridWork = gridScreen.WorkingArea;
        if (_config.WindowLayout.UseRegionModel &&
            TryApplyNormalizedRegion(gridWork, _config.WindowLayout.BackgroundRegion, out var backgroundRegion))
        {
            gridWork = backgroundRegion;
        }

        var gridRects = LayoutGrid(gridHandles.Count, gridWork, GridAspectRatio);
        for (var i = 0; i < gridHandles.Count; i++)
        {
            var handle = gridHandles[i];
            if (handle == IntPtr.Zero)
                continue;

            ProcessLauncher.TryApplyBorderlessStyle(handle, allowResize: false);
            MoveWindowIfNeeded(handle, gridRects[i]);
        }
    }

    private Screen? GetGridScreen()
    {
        if (!string.IsNullOrWhiteSpace(_config.WindowLayout.GridMonitorDevice))
        {
            var screen = Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, _config.WindowLayout.GridMonitorDevice, StringComparison.OrdinalIgnoreCase));
            if (screen is not null)
                return screen;
        }

        return Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen;
    }

    private static Screen? GetScreenByDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return null;

        return Screen.AllScreens.FirstOrDefault(s =>
            string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryApplyNormalizedRegion(System.Drawing.Rectangle baseRect, string spec, out System.Drawing.Rectangle result)
    {
        result = baseRect;
        if (string.IsNullOrWhiteSpace(spec))
            return false;

        var parts = spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            return false;

        if (!double.TryParse(parts[0], out var x) ||
            !double.TryParse(parts[1], out var y) ||
            !double.TryParse(parts[2], out var w) ||
            !double.TryParse(parts[3], out var h))
            return false;

        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        w = Math.Clamp(w, 0.01, 1);
        h = Math.Clamp(h, 0.01, 1);
        if (x + w > 1)
            w = 1 - x;
        if (y + h > 1)
            h = 1 - y;
        if (w <= 0 || h <= 0)
            return false;

        var left = baseRect.Left + (int)Math.Round(baseRect.Width * x);
        var top = baseRect.Top + (int)Math.Round(baseRect.Height * y);
        var width = Math.Max(1, (int)Math.Round(baseRect.Width * w));
        var height = Math.Max(1, (int)Math.Round(baseRect.Height * h));
        if (left + width > baseRect.Right)
            width = Math.Max(1, baseRect.Right - left);
        if (top + height > baseRect.Bottom)
            height = Math.Max(1, baseRect.Bottom - top);

        result = new System.Drawing.Rectangle(left, top, width, height);
        return true;
    }

    private static void FitWindowToConfiguredMonitorWorkArea(IntPtr handle, string? monitorDeviceName)
    {
        var screen = GetScreenByDeviceName(monitorDeviceName);
        if (screen is null)
        {
            ProcessLauncher.FitWindowToMonitorWorkArea(handle);
            return;
        }

        var target = RectFromWorkingArea(screen.Bounds);
        ProcessLauncher.MoveWindowToRect(handle, target, noActivate: true);
    }

    private static ProcessLauncher.Rect RectFromWorkingArea(System.Drawing.Rectangle rect)
    {
        return new ProcessLauncher.Rect
        {
            Left = rect.Left,
            Top = rect.Top,
            Right = rect.Right,
            Bottom = rect.Bottom
        };
    }

    private static void MoveWindowIfNeeded(IntPtr handle, ProcessLauncher.Rect target)
    {
        if (!ProcessLauncher.TryGetWindowRect(handle, out var current))
        {
            ProcessLauncher.MoveWindowToRect(handle, target, noActivate: true);
            return;
        }

        var same = current.Left == target.Left &&
                   current.Top == target.Top &&
                   current.Right == target.Right &&
                   current.Bottom == target.Bottom;
        if (!same)
            ProcessLauncher.MoveWindowToRect(handle, target, noActivate: true);
    }

    private static List<ProcessLauncher.Rect> LayoutGrid(int count, System.Drawing.Rectangle workArea, double aspectRatio)
    {
        if (count <= 0)
            return new List<ProcessLauncher.Rect>();

        var bestRows = 1;
        var bestCols = count;
        var bestArea = 0.0;

        for (var rows = 1; rows <= count; rows++)
        {
            var cols = (int)Math.Ceiling(count / (double)rows);
            var cellWidth = workArea.Width / (double)cols;
            var cellHeight = workArea.Height / (double)rows;

            var width = Math.Min(cellWidth, cellHeight * aspectRatio);
            var height = width / aspectRatio;
            var area = width * height;

            if (area > bestArea)
            {
                bestArea = area;
                bestRows = rows;
                bestCols = cols;
            }
        }

        var targetRects = new List<ProcessLauncher.Rect>(count);
        var slotWidth = workArea.Width / (double)bestCols;
        var slotHeight = workArea.Height / (double)bestRows;
        var winWidth = Math.Min(slotWidth, slotHeight * aspectRatio);
        var winHeight = winWidth / aspectRatio;

        for (var index = 0; index < count; index++)
        {
            var row = index / bestCols;
            var col = index % bestCols;
            var cellLeft = workArea.Left + (int)Math.Round(col * slotWidth);
            var cellTop = workArea.Top + (int)Math.Round(row * slotHeight);

            var x = cellLeft + (int)Math.Round((slotWidth - winWidth) / 2);
            var y = cellTop + (int)Math.Round((slotHeight - winHeight) / 2);

            targetRects.Add(new ProcessLauncher.Rect
            {
                Left = x,
                Top = y,
                Right = x + (int)Math.Round(winWidth),
                Bottom = y + (int)Math.Round(winHeight)
            });
        }

        return targetRects;
    }

    private static bool IsDeliverableD2RWindow(IntPtr handle)
    {
        return handle != IntPtr.Zero &&
               ProcessLauncher.IsWindowProcessName(handle, "D2R") &&
               ProcessLauncher.IsWindowResponsive(handle);
    }

    private IReadOnlyList<IntPtr> GetOrderedD2RHandles()
    {
        var ordered = new List<IntPtr>();
        var seen = new HashSet<IntPtr>();

        for (var i = 0; i < _config.Accounts.Count; i++)
        {
            var account = _config.Accounts[i];
            IntPtr handle = IntPtr.Zero;

            // Strong anchor: first account (driver/manual client) prefers the default D2R title.
            if (i == 0)
            {
                var driverHandles = BroadcastManager.FindWindowsByTitleExact(DriverWindowTitle)
                    .Where(IsDeliverableD2RWindow)
                    .ToList();
                if (driverHandles.Count > 0)
                    handle = driverHandles[0];
            }

            if (_accountProcessIds.TryGetValue(account.Id, out var pid))
            {
                if (ProcessLauncher.TryGetProcessMainWindowHandle(pid, "D2R", out var bound))
                {
                    if (handle == IntPtr.Zero)
                        handle = bound;
                }
                else
                {
                    _accountProcessIds.Remove(account.Id);
                }
            }

            if (handle == IntPtr.Zero && (!string.IsNullOrWhiteSpace(account.Nickname) || !string.IsNullOrWhiteSpace(account.Email)))
            {
                var title = !string.IsNullOrWhiteSpace(account.Nickname) ? account.Nickname : account.Email;
                var matches = BroadcastManager.FindWindowsByTitleExact(title);
                if (matches.Count > 0 && IsDeliverableD2RWindow(matches[0]))
                    handle = matches[0];
            }

            if (IsDeliverableD2RWindow(handle) && seen.Add(handle))
                ordered.Add(handle);
        }

        foreach (var handle in ProcessLauncher.GetMainWindowHandlesByProcessName("D2R"))
        {
            if (IsDeliverableD2RWindow(handle) && seen.Add(handle))
                ordered.Add(handle);
        }

        return ordered;
    }

    private static bool RectEquals(ProcessLauncher.Rect a, ProcessLauncher.Rect b)
    {
        return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    }

    private IReadOnlyList<BroadcastManager.BroadcastTarget> GetBroadcastTargets()
    {
        var foreground = ProcessLauncher.GetForegroundWindowHandle();
        var allD2RHandles = GetOrderedD2RHandles();

        if (_config.Broadcast.BroadcastAll)
        {
            EnsureDriverWindowBound();
            var targets = new List<BroadcastManager.BroadcastTarget>();
            foreach (var handle in allD2RHandles)
            {
                if (!IsDeliverableD2RWindow(handle))
                    continue;
                targets.Add(new BroadcastManager.BroadcastTarget(handle, IsClassicModeWindow(handle)));
            }
            return targets;
        }

        var selectedAccounts = _config.Accounts.Where(a => a.BroadcastEnabled).ToList();
        var includeMainWindowInSelected = _config.Broadcast.IncludeMainWindowInSelected;
        var targetsList = new List<BroadcastManager.BroadcastTarget>();
        var seenHandles = new HashSet<IntPtr>();
        var unresolvedAccounts = new List<AccountProfile>();

        foreach (var account in selectedAccounts)
        {
            var resolved = false;

            // Prefer exact title matches first for selected mode.
            if (!string.IsNullOrWhiteSpace(account.Nickname) || !string.IsNullOrWhiteSpace(account.Email))
            {
                var title = !string.IsNullOrWhiteSpace(account.Nickname) ? account.Nickname : account.Email;
                foreach (var handle in BroadcastManager.FindWindowsByTitleExact(title))
                {
                    if (handle == IntPtr.Zero || handle == foreground || seenHandles.Contains(handle))
                        continue;
                    if (!IsDeliverableD2RWindow(handle))
                        continue;
                    if (!includeMainWindowInSelected && IsMainDriverWindowHandle(handle))
                        continue;

                    seenHandles.Add(handle);
                    targetsList.Add(new BroadcastManager.BroadcastTarget(handle, account.ClassicMode));
                    resolved = true;
                    break;
                }
            }

            if (_accountProcessIds.TryGetValue(account.Id, out var pid))
            {
                if (!ProcessLauncher.TryGetProcessMainWindowHandle(pid, "D2R", out var handle))
                {
                    _accountProcessIds.Remove(account.Id);
                }
                else if (!resolved &&
                         handle != foreground &&
                         !seenHandles.Contains(handle) &&
                         IsDeliverableD2RWindow(handle) &&
                         (includeMainWindowInSelected || !IsMainDriverWindowHandle(handle)) &&
                         IsHandleLikelyForAccount(handle, account))
                {
                    seenHandles.Add(handle);
                    targetsList.Add(new BroadcastManager.BroadcastTarget(handle, account.ClassicMode));
                    resolved = true;
                }
            }

            if (!resolved)
            {
                var best = FindBestHandleForAccount(account, allD2RHandles, foreground, seenHandles, includeMainWindowInSelected);
                if (best != IntPtr.Zero && seenHandles.Add(best))
                {
                    targetsList.Add(new BroadcastManager.BroadcastTarget(best, account.ClassicMode));
                    resolved = true;
                }
            }

            if (!resolved)
                unresolvedAccounts.Add(account);
        }

        if (unresolvedAccounts.Count > 0)
        {
            var unresolvedLabels = unresolvedAccounts
                .Select(a => string.IsNullOrWhiteSpace(a.Nickname) ? a.Email : a.Nickname)
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var joined = string.Join(", ", unresolvedLabels);
            var now = DateTime.UtcNow;
            if (!string.Equals(joined, _lastUnresolvedBroadcastLog, StringComparison.Ordinal) ||
                (now - _lastUnresolvedBroadcastLogUtc).TotalSeconds >= 5)
            {
                Log.Info($"Selected broadcast skipped unresolved accounts (no safe window match): {joined}");
                _lastUnresolvedBroadcastLog = joined;
                _lastUnresolvedBroadcastLogUtc = now;
            }
        }

        return targetsList;
    }

    private static IntPtr FindBestHandleForAccount(
        AccountProfile account,
        IReadOnlyList<IntPtr> allD2RHandles,
        IntPtr foreground,
        HashSet<IntPtr> seenHandles,
        bool includeMainWindowInSelected)
    {
        var candidates = allD2RHandles
            .Where(h =>
                h != IntPtr.Zero &&
                h != foreground &&
                !seenHandles.Contains(h) &&
                IsDeliverableD2RWindow(h) &&
                (includeMainWindowInSelected || !IsMainDriverWindowHandle(h)))
            .ToList();
        if (candidates.Count == 0)
            return IntPtr.Zero;

        var onPreferred = candidates
            .Where(h => IsOnPreferredMonitor(h, account.LaunchMonitorDevice))
            .ToList();

        foreach (var handle in onPreferred)
        {
            if (IsExactAccountTitleMatch(handle, account))
                return handle;
        }

        foreach (var handle in candidates)
        {
            if (IsExactAccountTitleMatch(handle, account))
                return handle;
        }

        foreach (var handle in onPreferred)
        {
            if (IsLikelyAccountTitleMatch(handle, account))
                return handle;
        }

        foreach (var handle in candidates)
        {
            if (IsLikelyAccountTitleMatch(handle, account))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static bool IsMainDriverWindowHandle(IntPtr handle)
    {
        if (!IsDeliverableD2RWindow(handle))
            return false;
        var title = ProcessLauncher.GetWindowTitle(handle).Trim();
        return string.Equals(title, DriverWindowTitle, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureDriverWindowBound()
    {
        if (_config.Accounts.Count == 0)
            return;

        var driver = _config.Accounts[0];
        if (_accountProcessIds.TryGetValue(driver.Id, out var boundPid))
        {
            if (ProcessLauncher.IsProcessIdForName(boundPid, "D2R"))
                return;
            _accountProcessIds.Remove(driver.Id);
        }

        var defaultTitleHandles = BroadcastManager.FindWindowsByTitleExact(DriverWindowTitle)
            .Where(IsDeliverableD2RWindow)
            .ToList();
        if (defaultTitleHandles.Count > 0)
        {
            var titlePid = ProcessLauncher.GetWindowProcessId(defaultTitleHandles[0]);
            if (titlePid != 0)
            {
                _accountProcessIds[driver.Id] = titlePid;
                Log.Info($"Bound driver window by default title to account: {driver.Email}");
                return;
            }
        }

        var foreground = ProcessLauncher.GetForegroundWindowHandle();
        if (foreground != IntPtr.Zero && IsDeliverableD2RWindow(foreground))
        {
            var foregroundPid = ProcessLauncher.GetWindowProcessId(foreground);
            if (foregroundPid != 0 && !_accountProcessIds.Values.Contains(foregroundPid))
            {
                _accountProcessIds[driver.Id] = foregroundPid;
                Log.Info($"Bound driver window from foreground D2R to account: {driver.Email}");
                return;
            }
        }

        var handles = BroadcastManager.FindWindowsByTitleExact(DriverWindowTitle);
        if (handles.Count == 0)
            return;

        var handle = handles[0];
        if (!IsDeliverableD2RWindow(handle))
            return;

        var pid = ProcessLauncher.GetWindowProcessId(handle);
        if (pid == 0 || _accountProcessIds.Values.Contains(pid))
            return;

        _accountProcessIds[driver.Id] = pid;
        Log.Info($"Bound driver window to account: {driver.Email}");
    }

    private static bool IsLikelyAccountTitleMatch(IntPtr handle, AccountProfile account)
    {
        if (handle == IntPtr.Zero)
            return false;

        var title = ProcessLauncher.GetWindowTitle(handle).Trim();
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var nickname = account.Nickname?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(nickname) &&
            string.Equals(title, nickname, StringComparison.OrdinalIgnoreCase))
            return true;

        var email = account.Email?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(email) &&
            string.Equals(title, email, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(nickname) &&
            title.Contains(nickname, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(email) &&
            title.Contains(email, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsExactAccountTitleMatch(IntPtr handle, AccountProfile account)
    {
        if (handle == IntPtr.Zero)
            return false;

        var title = ProcessLauncher.GetWindowTitle(handle).Trim();
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var nickname = account.Nickname?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(nickname) &&
            string.Equals(title, nickname, StringComparison.OrdinalIgnoreCase))
            return true;

        var email = account.Email?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(email) &&
            string.Equals(title, email, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsHandleLikelyForAccount(IntPtr handle, AccountProfile account)
    {
        if (handle == IntPtr.Zero)
            return false;

        if (IsExactAccountTitleMatch(handle, account))
            return true;

        return IsLikelyAccountTitleMatch(handle, account);
    }

    private static bool IsOnPreferredMonitor(IntPtr handle, string preferredDeviceName)
    {
        if (handle == IntPtr.Zero || string.IsNullOrWhiteSpace(preferredDeviceName))
            return false;

        if (!ProcessLauncher.TryGetMonitorDeviceName(handle, out var deviceName))
            return false;

        return string.Equals(deviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForegroundD2R()
    {
        return ProcessLauncher.IsForegroundProcess("D2R");
    }

    private bool IsClassicModeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var pid = ProcessLauncher.GetWindowProcessId(hwnd);
        if (pid == 0)
            return false;

        var account = _config.Accounts.FirstOrDefault(a => _accountProcessIds.TryGetValue(a.Id, out var id) && id == pid);
        if (account is not null)
            return account.ClassicMode;

        var title = ProcessLauncher.GetWindowTitle(hwnd);
        account = _config.Accounts.FirstOrDefault(a =>
            (!string.IsNullOrWhiteSpace(a.Nickname) && string.Equals(a.Nickname, title, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(a.Email, title, StringComparison.OrdinalIgnoreCase));

        return account?.ClassicMode ?? false;
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            BtnUpdate.IsEnabled = false;
            TxtStatus.Text = "Checking for updates...";

            var latest = await UpdateService.CheckLatestAsync(_config.UpdateToken);
            if (latest is null)
            {
                System.Windows.MessageBox.Show("Unable to check for updates right now.", "Updates");
                return;
            }

            var current = UpdateService.CurrentVersion;
            if (!UpdateService.IsNewer(current, latest.Version))
            {
                System.Windows.MessageBox.Show($"You're up to date (v{current}).", "Updates");
                return;
            }

            var result = System.Windows.MessageBox.Show(
                $"Update available: v{latest.Version} (current v{current}).\n\nDownload and install now?",
                "Update available",
                MessageBoxButton.YesNo);
            if (result != MessageBoxResult.Yes)
                return;

            await UpdateService.DownloadAndInstallAsync(latest, _config.UpdateToken);
            System.Windows.MessageBox.Show("Update downloaded. The app will close and restart.", "Updating");
            Close();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            System.Windows.MessageBox.Show("Update download failed (404). If the repo is private, add a GitHub token to config.json (updateToken).", "Update error");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Update error");
        }
        finally
        {
            BtnUpdate.IsEnabled = true;
            TxtStatus.Text = "";
        }
    }

    private static void OpenHandleDownloadPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://learn.microsoft.com/en-us/sysinternals/downloads/handle",
            UseShellExecute = true
        });
    }

    private void CheckHandleRequirementOnStartup()
    {
        if (_handlePromptedThisSession)
            return;

        if (!_config.PreLaunch.Enabled || string.IsNullOrWhiteSpace(_config.PreLaunch.Path))
            return;

        var scriptPath = ProcessLauncher.TryResolvePreLaunchScript(_config.PreLaunch.Path!);
        if (!ProcessLauncher.IsD2RKillaScript(scriptPath))
            return;

        var handlePath = ProcessLauncher.ResolveHandlePath(_config.PreLaunch.HandlePath);
        if (!string.IsNullOrWhiteSpace(handlePath))
            return;

        _handlePromptedThisSession = true;

        var message =
            "handle64.exe is required for the pre-launch cleanup script.\n\n" +
            "To fix:\n" +
            "1) Download Sysinternals Handle\n" +
            "2) Extract handle64.exe\n" +
            "3) Either place handle64.exe next to D2RDS.exe or set preLaunch.handlePath in config.json\n" +
            "   (you can also add its folder to PATH)\n\n" +
            "Open the download page now?";

        var result = System.Windows.MessageBox.Show(message, "handle64.exe missing", MessageBoxButton.YesNo);
        if (result == MessageBoxResult.Yes)
            OpenHandleDownloadPage();
    }

    private async Task RunAccountAsync(AccountProfile account)
    {
        SetBusy(true);
        SetStatus($"Running {account.Email}...");

        try
        {
            Log.Info($"Clicked: {account.Email}");
            var config = ConfigLoader.LoadOrCreate();

            if (string.IsNullOrWhiteSpace(config.InstallPath))
                throw new InvalidOperationException("Select a valid install path before launching.");

            var region = ResolveLaunchRegion(config, account);
            if (region is null)
                throw new InvalidOperationException("Select a region before launching.");

            var d2rExe = System.IO.Path.Combine(config.InstallPath, "D2R.exe");
            if (!File.Exists(d2rExe))
            {
                var pick = System.Windows.MessageBox.Show("D2R.exe not found. Select the correct install folder now?", "Install path invalid", MessageBoxButton.YesNo);
                if (pick == MessageBoxResult.Yes)
                {
                    BrowseInstallPath();
                    config = ConfigLoader.LoadOrCreate();
                    d2rExe = System.IO.Path.Combine(config.InstallPath, "D2R.exe");
                    if (!File.Exists(d2rExe))
                        throw new FileNotFoundException($"D2R.exe not found at: {d2rExe}");
                }
                else
                {
                    throw new FileNotFoundException($"D2R.exe not found at: {d2rExe}");
                }
            }

            // Pre-launch only applies once a D2R process exists; skip for the first instance.
            if (config.PreLaunch.Enabled && !string.IsNullOrWhiteSpace(config.PreLaunch.Path))
            {
                if (!ProcessLauncher.TryValidatePreLaunchPath(config.PreLaunch.Path!, out var error))
                {
                    var disable = System.Windows.MessageBox.Show($"Pre-launch path invalid: {error}\n\nDisable pre-launch?", "Pre-launch error", MessageBoxButton.YesNo);
                    if (disable == MessageBoxResult.Yes)
                    {
                        config.PreLaunch.Enabled = false;
                        ConfigLoader.Save(config);
                    }
                }

                if (ProcessLauncher.IsProcessRunning("D2R"))
                {
                    var scriptPath = ProcessLauncher.TryResolvePreLaunchScript(config.PreLaunch.Path!);
                    if (ProcessLauncher.IsD2RKillaScript(scriptPath))
                    {
                        var handlePath = ProcessLauncher.ResolveHandlePath(config.PreLaunch.HandlePath);
                        if (string.IsNullOrWhiteSpace(handlePath))
                        {
                            var result = System.Windows.MessageBox.Show(
                                "handle64.exe is required for the pre-launch cleanup script.\n\nPlace handle64.exe next to D2RDS.exe, set preLaunch.handlePath in config.json, or add its folder to PATH.\n\nOpen the Sysinternals Handle download page now?",
                                "handle64.exe missing",
                                MessageBoxButton.YesNoCancel);
                            if (result == MessageBoxResult.Yes)
                            {
                                OpenHandleDownloadPage();
                                var retry = System.Windows.MessageBox.Show(
                                    "After downloading, place handle64.exe next to D2RDS.exe, set preLaunch.handlePath in config.json, or add its folder to PATH, then click OK to continue.",
                                    "Waiting for handle64.exe",
                                    MessageBoxButton.OKCancel);
                                if (retry == MessageBoxResult.OK && string.IsNullOrWhiteSpace(ProcessLauncher.ResolveHandlePath(config.PreLaunch.HandlePath)))
                                {
                                    System.Windows.MessageBox.Show("handle64.exe is still missing. Pre-launch will be skipped for this run.", "Pre-launch skipped");
                                    goto ContinueLaunch;
                                }
                                if (retry != MessageBoxResult.OK)
                                {
                                    goto ContinueLaunch;
                                }
                            }
                            else if (result == MessageBoxResult.No || result == MessageBoxResult.Cancel)
                            {
                                goto ContinueLaunch;
                            }
                        }
                    }

                    Log.Info($"Pre-launch starting: {config.PreLaunch.Path}");
                    await ProcessLauncher.RunPreLaunchAsync(config.PreLaunch.Path!);
                    Log.Info("Pre-launch finished");
                    await Task.Delay(750);
                }
                else
                {
                    Log.Info("Pre-launch skipped: D2R not running");
                }
            }

        ContinueLaunch:
            var displayName = string.IsNullOrWhiteSpace(account.Nickname) ? account.Email : account.Nickname;
            var process = await LaunchAccountProcessAsync(config, account, d2rExe, region.Address);
            if (process is not null)
                _accountProcessIds[account.Id] = process.Id;
            await ProcessLauncher.TrySetWindowTitleAsync(process, displayName);
            await ProcessLauncher.WaitForMainWindowHandleAsync(process);
            Log.Info("Launch triggered");

            SetStatus($"Done: {account.Email}");
        }
        catch (Exception ex)
        {
            Log.Info($"ERROR: {ex.Message}");
            SetStatus($"Failed: {account.Email}");
            System.Windows.MessageBox.Show(ex.Message, "Launcher error");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<Process?> LaunchAccountProcessAsync(LauncherConfig config, AccountProfile account, string d2rExe, string regionAddress)
    {
        var customLaunchPath = ResolveCustomLaunchPath(config, account);
        if (!string.IsNullOrWhiteSpace(customLaunchPath))
        {
            Log.Info($"Launching via custom path: {customLaunchPath}");
            var existingD2rIds = SnapshotProcessIds("D2R");
            ProcessLauncher.LaunchShortcutOrFile(customLaunchPath);
            var launched = await WaitForNewProcessAsync("D2R", existingD2rIds, 45000);
            if (launched is null)
                Log.Info("No new D2R process detected after custom launch; continuing with best-effort window binding.");
            return launched;
        }

        var credential = CredentialStore.Read(account.CredentialId);
        if (credential is null)
            throw new InvalidOperationException("Stored credentials not found. Re-add the account.");

        var args = BuildLaunchArguments(account.Email, credential.Value.Secret, regionAddress);
        Log.Info($"Launching: {d2rExe}");
        return await LaunchD2RWithRetryAsync(d2rExe, args, config.InstallPath);
    }

    private static string ResolveCustomLaunchPath(LauncherConfig config, AccountProfile account)
    {
        if (!string.IsNullOrWhiteSpace(account.LaunchPath))
            return account.LaunchPath.Trim();

        var profiles = config.Profiles ?? new List<LaunchProfile>();
        if (profiles.Count == 0)
            return "";

        static LaunchProfile? FindByName(IEnumerable<LaunchProfile> candidates, string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;

            var trimmed = key.Trim();
            return candidates.FirstOrDefault(p => string.Equals(p.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
        }

        var byId = FindByName(profiles, account.Id);
        if (!string.IsNullOrWhiteSpace(byId?.Path))
            return byId.Path.Trim();

        var byNickname = FindByName(profiles, account.Nickname);
        if (!string.IsNullOrWhiteSpace(byNickname?.Path))
            return byNickname.Path.Trim();

        var byEmail = FindByName(profiles, account.Email);
        if (!string.IsNullOrWhiteSpace(byEmail?.Path))
            return byEmail.Path.Trim();

        var accountIndex = config.Accounts.FindIndex(a => string.Equals(a.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        if (accountIndex >= 0 && accountIndex < profiles.Count && !string.IsNullOrWhiteSpace(profiles[accountIndex].Path))
            return profiles[accountIndex].Path.Trim();

        return "";
    }

    private static HashSet<int> SnapshotProcessIds(string processName)
    {
        var ids = new HashSet<int>();
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
                ids.Add(process.Id);
        }
        catch
        {
            // Best-effort only.
        }

        return ids;
    }

    private static async Task<Process?> WaitForNewProcessAsync(string processName, HashSet<int> existingIds, int timeoutMs, int pollMs = 250)
    {
        var timeout = Math.Max(1000, timeoutMs);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeout)
        {
            try
            {
                var candidates = Process.GetProcessesByName(processName)
                    .Where(p => !existingIds.Contains(p.Id))
                    .ToList();
                var launched = candidates
                    .OrderByDescending(p =>
                    {
                        try { return p.StartTime; } catch { return DateTime.MinValue; }
                    })
                    .FirstOrDefault();

                if (launched is not null)
                    return launched;
            }
            catch
            {
                // Best-effort polling.
            }

            await Task.Delay(pollMs);
        }

        return null;
    }

    private static string BuildLaunchArguments(string email, string password, string address)
    {
        // Let D2R keep the user's in-game display mode (do not force windowed).
        return $"-username {QuoteArg(email)} -password {QuoteArg(password)} -address {QuoteArg(address)}";
    }

    private static RegionOption? ResolveLaunchRegion(LauncherConfig config, AccountProfile account)
    {
        if (account is not null && !string.IsNullOrWhiteSpace(account.Region))
        {
            var perAccount = RegionOptions.FindByName(account.Region);
            if (perAccount is not null)
                return perAccount;
        }

        return RegionOptions.FindByName(config.Region);
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static async Task RefitWindowAsync(Process? process, IntPtr initialHandle, string? monitorDeviceName)
    {
        if (process is null || initialHandle == IntPtr.Zero)
            return;

        await Task.Delay(1500);
        if (process.HasExited)
            return;

        var handle = process.MainWindowHandle;
        if (handle == IntPtr.Zero)
            handle = ProcessLauncher.TryGetMainWindowHandle(process.Id);
        if (handle == IntPtr.Zero)
            return;

        ProcessLauncher.TryApplyBorderlessStyle(handle, allowResize: false);
        FitWindowToConfiguredMonitorWorkArea(handle, monitorDeviceName);
    }

    private static async Task<Process?> LaunchD2RWithRetryAsync(string exePath, string args, string workingDirectory)
    {
        var process = ProcessLauncher.LaunchExecutable(exePath, args, workingDirectory);
        if (process is null)
            return null;

        await Task.Delay(1200);
        if (!process.HasExited)
            return process;

        Log.Info("Launch exited early; retrying once.");
        await Task.Delay(1000);
        return ProcessLauncher.LaunchExecutable(exePath, args, workingDirectory);
    }
}
