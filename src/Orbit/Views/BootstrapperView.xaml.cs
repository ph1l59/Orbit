using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orbit.Services;

namespace Orbit.Views;

public partial class BootstrapperView : UserControl, IPage
{
    private bool _loading = true;
    private bool _busy;

    public BootstrapperView()
    {
        InitializeComponent();

        DelayCombo.ItemsSource = new List<Option>
        {
            new("3 seconds", 3), new("5 seconds", 5), new("8 seconds", 8), new("10 seconds", 10),
            new("15 seconds", 15), new("20 seconds", 20), new("30 seconds", 30),
        };
        CleanerCombo.ItemsSource = new List<Option>
        {
            new("Never", 0), new("After 1 day", 1), new("After 1 week", 7), new("After 2 weeks", 14), new("After 1 month", 30),
        };

        AppState.MultiInstanceChanged += () => Dispatcher.InvokeAsync(Load);
        Load();
    }

    public void OnShown() => Load();

    private void Load()
    {
        _loading = true;
        var s = AppState.Settings;

        MultiToggle.IsChecked = MultiInstance.IsActive;
        ConfirmToggle.IsChecked = s.ConfirmLaunchWhenRunning;
        Option.Select(DelayCombo, s.LaunchDelaySeconds);
        ManagedToggle.IsChecked = s.UseManagedInstall;
        DefaultToggle.IsChecked = s.DefaultLauncher;
        Option.Select(CleanerCombo, s.CleanerDays);
        CacheToggle.IsChecked = s.CleanCache;
        LogsToggle.IsChecked = s.CleanLogs;
        DownloadsToggle.IsChecked = s.CleanDownloads;
        VersionsToggle.IsChecked = s.CleanOldVersions;

        UpdateVersionText();
        CleanerStatus.Text = s.LastCleanup is { } last
            ? "LAST RUN: " + last.ToLocalTime().ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()
            : "LAST RUN: NEVER";

        _loading = false;
    }

    private void UpdateVersionText()
    {
        var installed = Bootstrapper.InstalledVersion;
        VersionText.Text = installed != null
            ? "INSTALLED · " + installed.ToUpperInvariant()
            : "NOT INSTALLED YET · DOWNLOADED ON THE FIRST LAUNCH";
        UpdateButton.IsEnabled = !_busy && AppState.Settings.UseManagedInstall;
        ReinstallButton.IsEnabled = !_busy && installed != null;
    }

    private void Changed(object sender, EventArgs e)
    {
        if (_loading) return;
        var s = AppState.Settings;
        s.ConfirmLaunchWhenRunning = ConfirmToggle.IsChecked == true;
        s.LaunchDelaySeconds = Option.Get(DelayCombo, 8);
        s.UseManagedInstall = ManagedToggle.IsChecked == true;
        s.CleanerDays = Option.Get(CleanerCombo, 14);
        s.CleanCache = CacheToggle.IsChecked == true;
        s.CleanLogs = LogsToggle.IsChecked == true;
        s.CleanDownloads = DownloadsToggle.IsChecked == true;
        s.CleanOldVersions = VersionsToggle.IsChecked == true;
        AppState.SaveSettings();
        UpdateVersionText();
    }

    private void Multi_Click(object sender, RoutedEventArgs e)
    {
        if (MultiToggle.IsChecked == true)
        {
            if (!MultiInstance.TryEnable(out var error))
            {
                MultiToggle.IsChecked = false;
                ShowMulti(error ?? "Couldn't turn on Multi-Roblox.", true);
                return;
            }
            ShowMulti("On – you can now open several Roblox windows.", false);
        }
        else
        {
            MultiInstance.Disable();
            ShowMulti("Off – windows that are already open keep running.", false);
        }

        AppState.Settings.MultiRoblox = MultiInstance.IsActive;
        AppState.SaveSettings();
        AppState.NotifyMultiInstanceChanged();
    }

    private void ShowMulti(string text, bool warn)
    {
        MultiStatus.Text = text;
        MultiStatus.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "OkBrush");
        MultiStatus.Visibility = Visibility.Visible;
    }

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        try
        {
            if (DefaultToggle.IsChecked == true) ProtocolHandler.Register(s);
            else ProtocolHandler.Unregister(s);
            s.DefaultLauncher = DefaultToggle.IsChecked == true;
            AppState.SaveSettings();
        }
        catch (Exception ex)
        {
            DefaultToggle.IsChecked = s.DefaultLauncher;
            Dialog.Info(Window.GetWindow(this), "Couldn't change the launcher", ex.Message);
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        UpdateVersionText();
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<BootstrapProgress>(p =>
            {
                UpdateProgress.Value = p.Percent;
                VersionText.Text = p.Message.ToUpperInvariant();
            });
            await Bootstrapper.EnsureLatestAsync(progress);
            _busy = false;
            UpdateVersionText();
            VersionText.Text += " · UP TO DATE";
        }
        catch (Exception ex)
        {
            _busy = false;
            UpdateVersionText();
            Dialog.Info(Window.GetWindow(this), "Update failed", ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateButton.IsEnabled = AppState.Settings.UseManagedInstall;
        }
    }

    private async void Reinstall_Click(object sender, RoutedEventArgs e)
    {
        if (RobloxPaths.IsRobloxRunning())
        {
            Dialog.Info(Window.GetWindow(this), "Close Roblox first", "Roblox is running. Close every Roblox window, then try again.");
            return;
        }
        if (!Dialog.Confirm(Window.GetWindow(this), "Reinstall Roblox",
                "Delete Orbit's copy of Roblox and download it again? Use this if Roblox won't start.", "Reinstall"))
            return;

        try
        {
            await Task.Run(Bootstrapper.Reinstall);
        }
        catch (Exception ex)
        {
            Dialog.Info(Window.GetWindow(this), "Couldn't remove the old install", ex.Message);
            return;
        }
        Update_Click(sender, e);
    }

    private async void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        CleanerStatus.Text = "CLEANING…";
        var s = AppState.Settings;
        var freed = await Task.Run(() => Cleaner.Run(s, force: true));
        AppState.SaveSettings();
        CleanerStatus.Text = $"FREED {Cleaner.FormatSize(freed).ToUpperInvariant()}";
    }
}
