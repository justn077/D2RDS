using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FormsScreen = System.Windows.Forms.Screen;
using WpfPoint = System.Windows.Point;

namespace MultiboxLauncher;

// Small always-on-top overlay that shows current broadcast state.
public partial class BroadcastStatusWindow : Window
{
    private bool _isLocked;

    public event Action<double, double, bool>? OverlayStateChanged;

    public BroadcastStatusWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (double.IsNaN(Left) || double.IsNaN(Top))
                PositionNearTopLeft();
            ClampToCurrentDisplay();
            UpdateOverlayToolTip();
        };
    }

    public void EnsureVisible()
    {
        if (!IsVisible)
            Show();
        Topmost = true;
        ClampToCurrentDisplay();
    }

    public void ApplyPlacement(double? left, double? top, bool isLocked)
    {
        _isLocked = isLocked;

        if (left.HasValue && top.HasValue)
        {
            Left = left.Value;
            Top = top.Value;
        }
        else
        {
            PositionNearTopLeft();
        }

        ClampToCurrentDisplay();
        UpdateOverlayToolTip();
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

    private void OverlayBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isLocked)
            return;

        try
        {
            DragMove();
        }
        catch
        {
            // Ignore drag exceptions caused by quick click/release.
        }

        ClampToCurrentDisplay();
        RaiseOverlayStateChanged();
        e.Handled = true;
    }

    private void OverlayBorder_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isLocked = !_isLocked;
        UpdateOverlayToolTip();
        RaiseOverlayStateChanged();
        e.Handled = true;
    }

    private void RaiseOverlayStateChanged()
    {
        OverlayStateChanged?.Invoke(Left, Top, _isLocked);
    }

    private void UpdateOverlayToolTip()
    {
        var lockState = _isLocked ? "Locked" : "Unlocked";
        OverlayBorder.ToolTip =
            $"Overlay: {lockState}\n" +
            "Left-click drag to move when unlocked.\n" +
            "Right-click to lock/unlock placement.";
    }

    private void ClampToCurrentDisplay()
    {
        var width = Math.Max(ActualWidth, Width);
        var height = Math.Max(ActualHeight, Height);
        if (width <= 0 || height <= 0)
            return;

        var center = PointToScreen(new WpfPoint(width / 2.0, height / 2.0));
        var screen = FormsScreen.FromPoint(new System.Drawing.Point((int)Math.Round(center.X), (int)Math.Round(center.Y)));
        var workArea = DeviceRectToDip(screen.WorkingArea);

        var maxLeft = Math.Max(workArea.Left, workArea.Right - width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - height);

        Left = Math.Clamp(Left, workArea.Left, maxLeft);
        Top = Math.Clamp(Top, workArea.Top, maxTop);
    }

    private Rect DeviceRectToDip(System.Drawing.Rectangle rect)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
            return new Rect(rect.Left, rect.Top, rect.Width, rect.Height);

        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new WpfPoint(rect.Left, rect.Top));
        var bottomRight = fromDevice.Transform(new WpfPoint(rect.Right, rect.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
