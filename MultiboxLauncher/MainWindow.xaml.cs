using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MultiboxLauncher;

public partial class MainWindow : Window
{
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
        BtnReload.Click += (_, _) => LoadButtons();
        BtnEdit.Click += (_, _) => EditConfig();
        BtnAddAccount.Click += (_, _) => AddAccount();
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

                var row = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var launchButton = new System.Windows.Controls.Button
                {
                    Content = $"Launch {displayName}",
                    Height = 40,
                    Width = 220
                };
                launchButton.Click += async (_, _) => await RunAccountAsync(account);

                var emailText = new TextBlock
                {
                    Text = account.Email,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 8, 0)
                };

                var broadcastToggle = new System.Windows.Controls.CheckBox
                {
                    Content = "Bcast",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsChecked = account.BroadcastEnabled
                };
                broadcastToggle.Checked += (_, _) => ToggleAccountBroadcast(account, true);
                broadcastToggle.Unchecked += (_, _) => ToggleAccountBroadcast(account, false);

                var editButton = new System.Windows.Controls.Button
                {
                    Content = "Edit",
                    Height = 40,
                    Width = 60,
                    Margin = new Thickness(0, 0, 0, 0)
                };
                editButton.Click += (_, _) => EditAccount(account);

                var deleteButton = new System.Windows.Controls.Button
                {
                    Content = "Delete",
                    Height = 40,
                    Width = 70,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                deleteButton.Click += (_, _) => DeleteAccount(account);

                var upButton = new System.Windows.Controls.Button
                {
                    Content = "Up",
                    Height = 40,
                    Width = 45,
                    Margin = new Thickness(8, 0, 0, 0),
                    IsEnabled = !_config.LockOrder && i > 0
                };
                upButton.Click += (_, _) => MoveAccount(account, -1);

                var downButton = new System.Windows.Controls.Button
                {
                    Content = "Down",
                    Height = 40,
                    Width = 55,
                    Margin = new Thickness(8, 0, 0, 0),
                    IsEnabled = !_config.LockOrder && i < _config.Accounts.Count - 1
                };
                downButton.Click += (_, _) => MoveAccount(account, 1);

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
        _broadcastManager.UpdateHotkeys();

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
        if (!string.IsNullOrWhiteSpace(toggleHotkey))
            _config.Broadcast.ToggleBroadcastHotkey = toggleHotkey;
        if (!string.IsNullOrWhiteSpace(modeHotkey))
            _config.Broadcast.ToggleModeHotkey = modeHotkey;

        ConfigLoader.Save(_config);
        _broadcastManager.UpdateHotkeys();
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
        dialog.SetDialogMode("Add Account", "Add");
        dialog.RequirePassword = true;
        dialog.AllowPasswordChange = false;
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
