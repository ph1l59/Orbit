using System.Windows;
using System.Windows.Threading;
using Orbit.Services;

namespace Orbit;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one Orbit at a time - two of them would fight over accounts and the Roblox singleton.
        _singleInstance = new Mutex(true, "Orbit_by_Floshiii_SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Orbit läuft bereits.", "Orbit", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        var loadError = AppState.Load();

        string? multiError = null;
        if (AppState.Settings.MultiRoblox && !MultiInstance.TryEnable(out multiError))
        {
            AppState.Settings.MultiRoblox = false;
            AppState.SaveSettings();
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (loadError != null)
            MessageBox.Show(window, loadError, "Orbit", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (multiError != null)
            MessageBox.Show(window, "Multi-Roblox wurde ausgeschaltet: " + multiError, "Orbit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MultiInstance.Disable();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Unerwarteter Fehler:\n\n" + e.Exception.Message, "Orbit",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
