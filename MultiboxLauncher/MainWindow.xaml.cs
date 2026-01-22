using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

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

    public MainWindow()
    {
        InitializeComponent();
        _broadcastManager = new BroadcastManager(
            () => _config.Broadcast,
            GetBroadcastTargets,
            IsForegroundD2R);
        _broadcastManager.ToggleBroadcastRequested += ToggleBroadcastEnabled;
        _broadcastManager.ToggleModeRequested += ToggleBroadcastMode;
        _broadcastManager.ToggleWindowRequested += ToggleBroadcastForForegroundWindow;
        BtnReload.Click += (_, _) => LoadButtons();
        BtnEdit.Click += (_, _) => EditConfig();
        BtnAddAccount.Click += (_, _) => AddAccount();
        BtnUpdate.Click += async (_, _) => await CheckForUpdatesAsync();
        BtnBrowseInstall.Click += (_, _) => BrowseInstallPath();
        CmbRegion.SelectionChanged += (_, _) => SaveRegionSelection();
        TxtInstallPath.LostFocus += (_, _) => SaveInstallPath();
        ChkLockOrder.Checked += (_, _) => SaveLockOrder(true);
        ChkLockOrder.Unchecked += (_, _) => SaveLockOrder(false);
        ChkBroadcastEnabled.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastEnabled.Unchecked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastAll.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastAll.Unchecked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastKeyboard.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastKeyboard.Unchecked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastMouse.Checked += (_, _) => SaveBroadcastSettings();
        ChkBroadcastMouse.Unchecked += (_, _) => SaveBroadcastSettings();
        TxtBroadcastHotkey.LostFocus += (_, _) => SaveBroadcastSettings();
        TxtBroadcastModeHotkey.LostFocus += (_, _) => SaveBroadcastSettings();
        TxtBroadcastWindowHotkey.LostFocus += (_, _) => SaveBroadcastSettings();
        Loaded += (_, _) =>
        {
            if (!_broadcastInitialized)
            {
                _broadcastManager.Initialize(this);
                _broadcastInitialized = true;
            }
            LoadButtons();
        };
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

    private void LoadButtons()
    {
        try
        {
            ProfilesPanel.Children.Clear();
            _config = ConfigLoader.LoadOrCreate();

            EnsureRegionSelected();
            EnsureInstallPathSelected();
            LoadSettings();

            for (var i = 0; i < _config.Accounts.Count; i++)
            {
                var account = _config.Accounts[i];
                var displayName = string.IsNullOrWhiteSpace(account.Nickname) ? account.Email : account.Nickname;

                var row = new System.Windows.Controls.Grid
                {
                    Margin = new Thickness(0, 0, 0, 6)
                };
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(220) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(200) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(70) });
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

                var editButton = new System.Windows.Controls.Button
                {
                    Content = "Edit",
                    Height = 34,
                    Width = 60,
                    Margin = new Thickness(0)
                };
                editButton.Click += (_, _) => EditAccount(account);
                System.Windows.Controls.Grid.SetColumn(editButton, 3);

                var deleteButton = new System.Windows.Controls.Button
                {
                    Content = "Delete",
                    Height = 34,
                    Width = 60,
                    Margin = new Thickness(0)
                };
                deleteButton.Click += (_, _) => DeleteAccount(account);
                System.Windows.Controls.Grid.SetColumn(deleteButton, 4);

                var upButton = new System.Windows.Controls.Button
                {
                    Content = "▲",
                    Height = 34,
                    Width = 45,
                    Margin = new Thickness(0),
                    IsEnabled = !_config.LockOrder && i > 0
                };
                upButton.Click += (_, _) => MoveAccount(account, -1);
                System.Windows.Controls.Grid.SetColumn(upButton, 5);

                var downButton = new System.Windows.Controls.Button
                {
                    Content = "▼",
                    Height = 34,
                    Width = 50,
                    Margin = new Thickness(0),
                    IsEnabled = !_config.LockOrder && i < _config.Accounts.Count - 1
                };
                downButton.Click += (_, _) => MoveAccount(account, 1);
                System.Windows.Controls.Grid.SetColumn(downButton, 6);

                row.Children.Add(launchButton);
                row.Children.Add(emailText);
                row.Children.Add(broadcastToggle);
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
        ChkBroadcastEnabled.IsChecked = _config.Broadcast.Enabled;
        ChkBroadcastAll.IsChecked = _config.Broadcast.BroadcastAll;
        ChkBroadcastKeyboard.IsChecked = _config.Broadcast.Keyboard;
        ChkBroadcastMouse.IsChecked = _config.Broadcast.Mouse;
        TxtBroadcastHotkey.Text = _config.Broadcast.ToggleBroadcastHotkey;
        TxtBroadcastModeHotkey.Text = _config.Broadcast.ToggleModeHotkey;
        TxtBroadcastWindowHotkey.Text = _config.Broadcast.ToggleWindowHotkey;
        TxtVersion.Text = $"v{UpdateService.CurrentVersion}";
        _broadcastManager.UpdateHotkeys();
        _broadcastManager.UpdateBroadcastState(_config.Broadcast);

        EnsureBroadcastStatusWindow();
        UpdateBroadcastStatusWindow();
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
        if (string.IsNullOrWhiteSpace(path))
            return;

        _config.InstallPath = path;
        ConfigLoader.Save(_config);
    }

    private void SaveLockOrder(bool locked)
    {
        _config.LockOrder = locked;
        ConfigLoader.Save(_config);
        LoadButtons();
    }

    private void SaveBroadcastSettings()
    {
        _config.Broadcast.Enabled = ChkBroadcastEnabled.IsChecked == true;
        _config.Broadcast.BroadcastAll = ChkBroadcastAll.IsChecked == true;
        _config.Broadcast.Keyboard = ChkBroadcastKeyboard.IsChecked == true;
        _config.Broadcast.Mouse = ChkBroadcastMouse.IsChecked == true;

        var toggleHotkey = TxtBroadcastHotkey.Text.Trim();
        var modeHotkey = TxtBroadcastModeHotkey.Text.Trim();
        var windowHotkey = TxtBroadcastWindowHotkey.Text.Trim();
        if (!string.IsNullOrWhiteSpace(toggleHotkey))
            _config.Broadcast.ToggleBroadcastHotkey = toggleHotkey;
        if (!string.IsNullOrWhiteSpace(modeHotkey))
            _config.Broadcast.ToggleModeHotkey = modeHotkey;
        if (!string.IsNullOrWhiteSpace(windowHotkey))
            _config.Broadcast.ToggleWindowHotkey = windowHotkey;

        ConfigLoader.Save(_config);
        _broadcastManager.UpdateHotkeys();
        _broadcastManager.UpdateBroadcastState(_config.Broadcast);
        UpdateBroadcastStatusWindow();
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
            CredentialId = credentialId
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
        ConfigLoader.Save(_config);
    }

    private void ToggleBroadcastEnabled()
    {
        _config.Broadcast.Enabled = !_config.Broadcast.Enabled;
        ConfigLoader.Save(_config);
        Dispatcher.Invoke(() =>
        {
            ChkBroadcastEnabled.IsChecked = _config.Broadcast.Enabled;
            UpdateBroadcastStatusWindow();
            _broadcastManager.UpdateBroadcastState(_config.Broadcast);
        });
    }

    private void ToggleBroadcastMode()
    {
        _config.Broadcast.BroadcastAll = !_config.Broadcast.BroadcastAll;
        ConfigLoader.Save(_config);
        Dispatcher.Invoke(() =>
        {
            ChkBroadcastAll.IsChecked = _config.Broadcast.BroadcastAll;
            UpdateBroadcastStatusWindow();
        });
    }

    // Hotkey: toggle broadcast for the currently focused D2R window only.
    private void ToggleBroadcastForForegroundWindow()
    {
        var foreground = ProcessLauncher.GetForegroundWindowHandle();
        if (foreground == IntPtr.Zero)
            return;

        var pid = ProcessLauncher.GetWindowProcessId(foreground);
        var account = _config.Accounts.FirstOrDefault(a => _accountProcessIds.TryGetValue(a.Id, out var id) && id == pid);
        if (account is null)
        {
            var title = ProcessLauncher.GetWindowTitle(foreground);
            account = _config.Accounts.FirstOrDefault(a =>
                string.Equals(a.Nickname, title, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Email, title, StringComparison.OrdinalIgnoreCase));
        }

        if (account is null)
            return;

        account.BroadcastEnabled = !account.BroadcastEnabled;
        ConfigLoader.Save(_config);
        Dispatcher.Invoke(LoadButtons);
    }

    private void EnsureBroadcastStatusWindow()
    {
        if (_broadcastStatusWindow is not null)
            return;

        _broadcastStatusWindow = new BroadcastStatusWindow
        {
            Owner = this,
            ShowActivated = false
        };
        _broadcastStatusWindow.Show();
    }

    private void UpdateBroadcastStatusWindow()
    {
        _broadcastStatusWindow?.UpdateStatus(_config.Broadcast);
    }

    private IReadOnlyList<IntPtr> GetBroadcastTargets()
    {
        if (_config.Broadcast.BroadcastAll)
        {
            return ProcessLauncher.GetMainWindowHandlesByProcessName("D2R");
        }

        var handles = new List<IntPtr>();
        foreach (var account in _config.Accounts.Where(a => a.BroadcastEnabled))
        {
            if (_accountProcessIds.TryGetValue(account.Id, out var pid))
            {
                var handle = ProcessLauncher.TryGetMainWindowHandle(pid);
                if (handle != IntPtr.Zero)
                    handles.Add(handle);
            }
            else if (!string.IsNullOrWhiteSpace(account.Nickname))
            {
                handles.AddRange(BroadcastManager.FindWindowsByTitleExact(account.Nickname));
            }
        }
        return handles;
    }

    private static bool IsForegroundD2R()
    {
        return ProcessLauncher.IsForegroundProcess("D2R");
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

            await UpdateService.DownloadAndInstallAsync(latest);
            System.Windows.MessageBox.Show("Update downloaded. The app will close and restart.", "Updating");
            Close();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            System.Windows.MessageBox.Show("Update check failed (404). If the repo is private, add a GitHub token to config.json (updateToken).", "Update error");
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

    private async Task RunAccountAsync(AccountProfile account)
    {
        SetBusy(true);
        SetStatus($"Running {account.Email}...");

        try
        {
            Log.Info($"Clicked: {account.Email}");
            var config = ConfigLoader.LoadOrCreate();

            var region = RegionOptions.FindByName(config.Region);
            if (region is null)
                throw new InvalidOperationException("Select a region before launching.");

            if (string.IsNullOrWhiteSpace(config.InstallPath))
                throw new InvalidOperationException("Select a valid install path before launching.");

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
                    Log.Info($"Pre-launch starting: {config.PreLaunch.Path}");
                    await ProcessLauncher.RunPreLaunchAsync(config.PreLaunch.Path!);
                    Log.Info("Pre-launch finished");
                }
                else
                {
                    Log.Info("Pre-launch skipped: D2R not running");
                }
            }

            var credential = CredentialStore.Read(account.CredentialId);
            if (credential is null)
                throw new InvalidOperationException("Stored credentials not found. Re-add the account.");

            var args = BuildLaunchArguments(account.Email, credential.Value.Secret, region.Address);
            var displayName = string.IsNullOrWhiteSpace(account.Nickname) ? account.Email : account.Nickname;
            Log.Info($"Launching: {d2rExe}");
            var process = ProcessLauncher.LaunchExecutable(d2rExe, args, config.InstallPath);
            if (process is not null)
                _accountProcessIds[account.Id] = process.Id;
            await ProcessLauncher.TrySetWindowTitleAsync(process, displayName);
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

    private static string BuildLaunchArguments(string email, string password, string address)
    {
        return $"-username {QuoteArg(email)} -password {QuoteArg(password)} -address {QuoteArg(address)}";
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
