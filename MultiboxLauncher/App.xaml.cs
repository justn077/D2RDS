using System.Threading;
using System.Windows;

namespace MultiboxLauncher;

/// <summary>
/// WPF application entry; uses default startup.
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Local\\D2RDS.MultiboxLauncher";
        _instanceMutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            ProcessLauncher.TryActivateExistingInstance();
            MessageBox.Show("D2RDS is already running.", "D2RDS", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
