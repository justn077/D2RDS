using System.Windows;

namespace MultiboxLauncher;

public partial class AddAccountWindow : Window
{
    public string Email => TxtEmail.Text.Trim();
    public string Password => ChkShowPassword.IsChecked == true ? TxtPasswordVisible.Text : TxtPassword.Password;
    public string Nickname => TxtNickname.Text.Trim();
    public bool RequirePassword { get; set; } = true;
    public bool AllowPasswordChange { get; set; } = true;
    public bool ChangePassword => ChkChangePassword.IsChecked == true;

    public AddAccountWindow()
    {
        InitializeComponent();
        BtnOk.Click += (_, _) => OnOk();
        BtnCancel.Click += (_, _) => DialogResult = false;
        ChkShowPassword.Checked += (_, _) => TogglePasswordVisibility(true);
        ChkShowPassword.Unchecked += (_, _) => TogglePasswordVisibility(false);
        ChkChangePassword.Checked += (_, _) => UpdatePasswordEnabledState();
        ChkChangePassword.Unchecked += (_, _) => UpdatePasswordEnabledState();
        Loaded += (_, _) => UpdatePasswordEnabledState();
    }

    public void SetInitialValues(string email, string nickname)
    {
        TxtEmail.Text = email ?? "";
        TxtNickname.Text = nickname ?? "";
    }

    public void SetDialogMode(string title, string okText)
    {
        Title = title;
        BtnOk.Content = okText;
    }

    private void OnOk()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            System.Windows.MessageBox.Show("Email is required.", "Add Account");
            return;
        }

        if (RequirePassword && string.IsNullOrWhiteSpace(Password))
        {
            System.Windows.MessageBox.Show("Password is required.", "Add Account");
            return;
        }

        if (AllowPasswordChange && ChangePassword && string.IsNullOrWhiteSpace(Password))
        {
            System.Windows.MessageBox.Show("Enter a new password or uncheck Change password.", "Add Account");
            return;
        }

        DialogResult = true;
    }

    private void TogglePasswordVisibility(bool show)
    {
        if (show)
        {
            TxtPasswordVisible.Text = TxtPassword.Password;
            TxtPasswordVisible.Visibility = Visibility.Visible;
            TxtPassword.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtPassword.Password = TxtPasswordVisible.Text;
            TxtPasswordVisible.Visibility = Visibility.Collapsed;
            TxtPassword.Visibility = Visibility.Visible;
        }
    }

    private void UpdatePasswordEnabledState()
    {
        if (!AllowPasswordChange)
        {
            ChkChangePassword.IsChecked = true;
            ChkChangePassword.IsEnabled = false;
        }

        var enabled = !AllowPasswordChange || ChkChangePassword.IsChecked == true;
        TxtPassword.IsEnabled = enabled;
        TxtPasswordVisible.IsEnabled = enabled;
        ChkShowPassword.IsEnabled = enabled;

        if (!enabled)
        {
            TxtPassword.Password = "";
            TxtPasswordVisible.Text = "";
            ChkShowPassword.IsChecked = false;
        }
    }
}
