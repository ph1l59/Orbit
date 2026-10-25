using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Orbit.Services;
using Orbit.Views;

namespace Orbit;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();

    public MainWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null) VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}  ·  GPL-3.0";

        StateChanged += (_, _) => UpdateMaximizedState();
        AppState.MultiInstanceChanged += UpdateMultiStatus;
        UpdateMultiStatus();

        Nav.SelectedIndex = 0;
        Loaded += async (_, _) => await AppState.RefreshAvatarsAsync();
    }

    public void Navigate(string key)
    {
        foreach (var item in Nav.Items.OfType<ListBoxItem>())
            if ((string)item.Tag == key) { Nav.SelectedItem = item; return; }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem item) return;
        var key = (string)item.Tag;

        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "performance" => new PerformanceView(),
                "settings" => new SettingsView(),
                _ => new AccountsView(),
            };
            _pages[key] = page;
        }

        ContentHost.Content = page;
    }

    private void UpdateMultiStatus()
    {
        var active = MultiInstance.IsActive;
        MultiDot.Fill = (System.Windows.Media.Brush)FindResource(active ? "SuccessBrush" : "SubTextBrush");
        MultiText.Text = active ? "Aktiv – mehrere Fenster erlaubt" : "Aus";
    }

    private void MultiPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Navigate("settings");

    // ---------- Custom title bar ----------

    private void UpdateMaximizedState()
    {
        // With WindowStyle=None a maximized window overhangs the screen by the resize border.
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        AppState.MultiInstanceChanged -= UpdateMultiStatus;
        base.OnClosed(e);
    }
}
