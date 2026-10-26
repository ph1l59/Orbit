using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Orbit.Services;

namespace Orbit.Views;

public partial class AboutView : UserControl
{
    private const string RepoUrl = "https://github.com/ph1l59/Orbit";

    public AboutView()
    {
        InitializeComponent();
        VersionText.Text = "VERSION " + Updater.DisplayVersion.ToUpperInvariant();
        UpdatesToggle.IsChecked = AppState.Settings.CheckForUpdates;
        TrayToggle.IsChecked = AppState.Settings.CloseToTray;
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.CloseToTray = TrayToggle.IsChecked == true;
        AppState.SaveSettings();
    }

    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.CheckForUpdates = UpdatesToggle.IsChecked == true;
        AppState.SaveSettings();
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void GitHub_Click(object sender, RoutedEventArgs e) => Open(RepoUrl);
    private void Releases_Click(object sender, RoutedEventArgs e) => Open(RepoUrl + "/releases");
    private void Issues_Click(object sender, RoutedEventArgs e) => Open(RepoUrl + "/issues");

    private void Data_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
}
