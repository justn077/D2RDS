using System.Windows;

namespace MultiboxLauncher;

public partial class RegionPickerWindow : Window
{
    public RegionOption? SelectedRegion => CmbRegion.SelectedItem as RegionOption;

    public RegionPickerWindow()
    {
        InitializeComponent();
        CmbRegion.ItemsSource = RegionOptions.All;
        CmbRegion.SelectedIndex = 0;
        BtnOk.Click += (_, _) => DialogResult = true;
    }
}
