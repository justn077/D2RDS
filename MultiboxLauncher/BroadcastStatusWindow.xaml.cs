using System;
using System.Windows;

namespace MultiboxLauncher;

// Small always-on-top overlay that shows current broadcast state.
public partial class BroadcastStatusWindow : Window
{
    public BroadcastStatusWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionNearTopLeft();
    }

    public void EnsureVisible()
    {
        if (!IsVisible)
            Show();
        Topmost = true;
        if (WindowStartupLocation == WindowStartupLocation.Manual)
            PositionNearTopLeft();
    }

    public void UpdateStatus(BroadcastSettings settings)
    {
        var mode = settings.BroadcastAll ? "All" : "Selected";
        var state = settings.Enabled ? "ON" : "OFF";
        TxtStatus.Text = $"BCAST: {state} ({mode})";
    }

    private void PositionNearTopLeft()
    {
        Left = 260;
        Top = 10;
    }
}
