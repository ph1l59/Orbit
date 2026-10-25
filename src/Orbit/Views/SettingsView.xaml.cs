using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Orbit.Services;

namespace Orbit.Views;

public partial class SettingsView : UserControl
{
    private const string RepoUrl = "https://github.com/ph1l59/Orbit";
    private bool _loading = true;

    public SettingsView()
    {
        InitializeComponent();

        DelayCombo.ItemsSource = new List<Option>
        {
            new("3 Sekunden", 3),
            new("5 Sekunden", 5),
            new("8 Sekunden", 8),
            new("10 Sekunden", 10),
            new("15 Sekunden", 15),
            new("20 Sekunden", 20),
            new("30 Sekunden", 30),
        };

        var options = (List<Option>)DelayCombo.ItemsSource;
        DelayCombo.SelectedItem = options.FirstOrDefault(o => (int)o.Value == AppState.Settings.LaunchDelaySeconds)
                                  ?? options.First(o => (int)o.Value == 8);

        MultiToggle.IsChecked = MultiInstance.IsActive;
        DirectToggle.IsChecked = AppState.Settings.LaunchDirect;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null) AboutVersion.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";

        AppState.MultiInstanceChanged += SyncMultiToggle;
        Unloaded += (_, _) => AppState.MultiInstanceChanged -= SyncMultiToggle;
        Loaded += (_, _) =>
        {
            AppState.MultiInstanceChanged -= SyncMultiToggle;
            AppState.MultiInstanceChanged += SyncMultiToggle;
            SyncMultiToggle();
        };

        _loading = false;
    }

    private void SyncMultiToggle()
    {
        _loading = true;
        MultiToggle.IsChecked = MultiInstance.IsActive;
        _loading = false;
    }

    private void MultiToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (MultiToggle.IsChecked == true)
        {
            if (!MultiInstance.TryEnable(out var error))
            {
                ShowMultiStatus(error ?? "Konnte nicht aktiviert werden.", true);
                SyncMultiToggle();
                return;
            }
            ShowMultiStatus("Aktiv. Du kannst jetzt mehrere Roblox-Fenster gleichzeitig öffnen.", false);
        }
        else
        {
            MultiInstance.Disable();
            ShowMultiStatus("Ausgeschaltet. Bereits offene Fenster laufen weiter.", false);
        }

        AppState.Settings.MultiRoblox = MultiInstance.IsActive;
        AppState.SaveSettings();
        AppState.NotifyMultiInstanceChanged();
    }

    private void ShowMultiStatus(string text, bool isWarning)
    {
        MultiStatus.Text = text;
        MultiStatus.Foreground = (System.Windows.Media.Brush)FindResource(isWarning ? "WarningBrush" : "SuccessBrush");
        MultiStatus.Visibility = Visibility.Visible;
    }

    private void DelayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DelayCombo.SelectedItem is not Option { Value: int seconds }) return;
        AppState.Settings.LaunchDelaySeconds = seconds;
        AppState.SaveSettings();
    }

    private void DirectToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.LaunchDirect = DirectToggle.IsChecked == true;
        AppState.SaveSettings();
    }

    private void CloseRoblox_Click(object sender, RoutedEventArgs e)
    {
        if (!RobloxPaths.IsRobloxRunning())
        {
            RunningText.Text = "Es läuft gerade kein Roblox-Client.";
            return;
        }

        var answer = MessageBox.Show(Window.GetWindow(this)!, "Alle laufenden Roblox-Fenster jetzt beenden?", "Orbit",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var count = RobloxPaths.CloseAllClients();
        RunningText.Text = count == 1 ? "1 Roblox-Client beendet." : $"{count} Roblox-Clients beendet.";
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });

    private void GitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);
    private void Issues_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl + "/issues");

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
