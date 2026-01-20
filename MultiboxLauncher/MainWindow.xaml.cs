using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MultiboxLauncher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BtnReload.Click += (_, _) => LoadButtons();
        BtnEdit.Click += (_, _) => EditConfig();
        LoadButtons();
    }

    private void SetStatus(string text) => TxtStatus.Text = text;

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
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
            var config = ConfigLoader.Load();

            foreach (var profile in config.Profiles)
            {
                var button = new Button
                {
                    Content = profile.Name,
                    Height = 40,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                button.Click += async (_, _) => await RunProfileAsync(profile.Name);
                ProfilesPanel.Children.Add(button);
            }

            SetStatus($"Loaded {config.Profiles.Count} profiles");
        }
        catch (Exception ex)
        {
            SetStatus("Config error");
            MessageBox.Show(ex.Message, "Config error");
        }
    }

    private async Task RunProfileAsync(string profileName)
    {
        SetBusy(true);
        SetStatus($"Running {profileName}...");

        try
        {
            Log.Info($"Clicked: {profileName}");
            var config = ConfigLoader.Load();
            var profile = config.Profiles.Find(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
                throw new InvalidOperationException($"Unknown profile: {profileName}");

            if (config.PreLaunch is not null && config.PreLaunch.Enabled && !string.IsNullOrWhiteSpace(config.PreLaunch.Path))
            {
                Log.Info($"Pre-launch starting: {config.PreLaunch.Path}");
                await ProcessLauncher.RunPreLaunchAsync(config.PreLaunch.Path!);
                Log.Info("Pre-launch finished");
            }

            Log.Info($"Launching: {profile.Path}");
            ProcessLauncher.Launch(profile.Path);
            Log.Info("Launch triggered");

            SetStatus($"Done: {profileName}");
        }
        catch (Exception ex)
        {
            Log.Info($"ERROR: {ex.Message}");
            SetStatus($"Failed: {profileName}");
            MessageBox.Show(ex.Message, "Launcher error");
        }
        finally
        {
            SetBusy(false);
        }
    }
}
