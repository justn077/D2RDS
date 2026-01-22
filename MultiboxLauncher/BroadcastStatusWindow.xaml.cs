using System;
using System.Windows;

namespace MultiboxLauncher;

public partial class BroadcastStatusWindow : Window
{
    public BroadcastStatusWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionNearTopLeft();
    }

    public void UpdateStatus(BroadcastSettings settings)
    {
        var mode = settings.BroadcastAll ? "All" : "Selected";
        var state = settings.Enabled ? "ON" : "OFF";
        TxtStatus.Text = $"BCAST: {state} ({mode})";
    }

    private void PositionNearTopLeft()
    {
        Left = 10;
        Top = 10;
    }
}
