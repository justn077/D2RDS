using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MultiboxLauncher;

public partial class MainWindow : Window
{
    private LauncherConfig _config = new();

    public MainWindow()
    {
        InitializeComponent();
        BtnReload.Click += (_, _) => LoadButtons();
        BtnEdit.Click += (_, _) => EditConfig();
        BtnAddAccount.Click += (_, _) => AddAccount();
        BtnBrowseInstall.Click += (_, _) => BrowseInstallPath();
        CmbRegion.SelectionChanged += (_, _) => SaveRegionSelection();
        TxtInstallPath.LostFocus += (_, _) => SaveInstallPath();
        Loaded += (_, _) => LoadButtons();
    }

    private void SetStatus(string text) => TxtStatus.Text = text;

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    protected override void OnClosed(EventArgs e)
    {
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
            LoadSettings();

            foreach (var account in _config.Accounts)
            {
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

                row.Children.Add(launchButton);
                row.Children.Add(emailText);
                row.Children.Add(editButton);
                row.Children.Add(deleteButton);
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

    private void SaveInstallPath()
    {
        var path = TxtInstallPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;

        _config.InstallPath = path;
        ConfigLoader.Save(_config);
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
                throw new FileNotFoundException($"D2R.exe not found at: {d2rExe}");

            if (config.PreLaunch.Enabled && !string.IsNullOrWhiteSpace(config.PreLaunch.Path))
            {
                Log.Info($"Pre-launch starting: {config.PreLaunch.Path}");
                await ProcessLauncher.RunPreLaunchAsync(config.PreLaunch.Path!);
                Log.Info("Pre-launch finished");
            }

            var credential = CredentialStore.Read(account.CredentialId);
            if (credential is null)
                throw new InvalidOperationException("Stored credentials not found. Re-add the account.");

            var args = BuildLaunchArguments(account.Email, credential.Value.Secret, region.Address);
            Log.Info($"Launching: {d2rExe}");
            ProcessLauncher.LaunchExecutable(d2rExe, args, config.InstallPath);
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
