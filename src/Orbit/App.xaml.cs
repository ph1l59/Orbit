using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.Services;
using Orbit.Views;

namespace Orbit;

public partial class App : Application
{
    private const string SingleInstanceName = "Orbit_by_Floshiii_SingleInstance";
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // Started by a roblox-player:// or roblox:// link (Orbit is the default launcher).
        var link = e.Args.FirstOrDefault(a =>
            a.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("roblox:", StringComparison.OrdinalIgnoreCase));
        if (link != null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunLinkLaunchAsync(link);
            return;
        }

        // Only one Orbit window at a time. Right after an update the old Orbit may still be closing,
        // so the new one waits for it instead of complaining.
        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirst);
        if (!isFirst && e.Args.Contains(Updater.UpdatedFlag))
        {
            try { isFirst = _singleInstance.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst)
        {
            Dialog.Info(null, "Already running", "Orbit is already open – look for it in the taskbar.");
            Shutdown();
            return;
        }

        Updater.CleanupLeftovers();

        var loadError = AppState.Load();
        var s = AppState.Settings;

        string? multiError = null;
        if (s.MultiRoblox && !MultiInstance.TryEnable(out multiError))
        {
            s.MultiRoblox = false;
            AppState.SaveSettings();
        }

        ProtocolHandler.RefreshIfRegistered(s);
        Cleaner.RunIfDue(s);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (loadError != null) Dialog.Info(window, "Accounts", loadError);
        if (multiError != null) Dialog.Info(window, "Multi-Roblox was turned off", multiError);
    }

    /// <summary>Launch from the Roblox website: prepare Roblox, start it, then quietly go away.</summary>
    private async Task RunLinkLaunchAsync(string link)
    {
        AppState.Load();
        var s = AppState.Settings;

        var mainRunning = Mutex.TryOpenExisting(SingleInstanceName, out var existing);
        existing?.Dispose();

        var heldMulti = false;
        if (!mainRunning && s.MultiRoblox && !RobloxPaths.IsRobloxRunning())
            heldMulti = MultiInstance.TryEnable(out _);

        if (!mainRunning && !heldMulti && !s.MultiRoblox && s.ConfirmLaunchWhenRunning && RobloxPaths.IsRobloxRunning())
        {
            if (!Dialog.Confirm(null, "Roblox is already running",
                    "Launching again will close the Roblox window that is open right now.", "Launch anyway"))
            {
                Shutdown();
                return;
            }
        }

        var (window, bar, text) = BuildStartWindow();
        window.Show();

        try
        {
            var progress = new Progress<BootstrapProgress>(p =>
            {
                bar.Value = p.Percent;
                text.Text = p.Message;
            });
            var exe = await RobloxLauncher.PrepareAsync(s, progress);
            text.Text = "Starting Roblox…";
            RobloxLauncher.Start(exe, link);
            Integrations.StartCustomIntegrations();
            await Task.Delay(1200);
        }
        catch (Exception ex)
        {
            window.Close();
            Dialog.Info(null, "Couldn't start Roblox", ex.Message);
            Shutdown();
            return;
        }

        window.Close();

        // Stay in the background while Roblox runs if something needs Orbit to be alive.
        var needsWatcher = !mainRunning && s.ActivityTracking && s.DiscordRichPresence;
        if (heldMulti || needsWatcher || Integrations.HasCustomProcesses)
        {
            Integrations.Start(Dispatcher, full: needsWatcher);
            await Task.Delay(TimeSpan.FromSeconds(20)); // give Roblox time to start
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += async (_, _) =>
            {
                if (RobloxPaths.IsRobloxRunning()) return;
                timer.Stop();
                await Task.Delay(4000); // let integrations close their programs
                Shutdown();
            };
            timer.Start();
        }
        else
        {
            Shutdown();
        }
    }

    private static (Window, ProgressBar, TextBlock) BuildStartWindow()
    {
        T R<T>(string key) => (T)Current.FindResource(key);

        var text = new TextBlock { Text = "Checking for Roblox updates…", Style = R<Style>("RowDesc"), Margin = new Thickness(0, 4, 0, 12) };
        var bar = new ProgressBar { Maximum = 100 };

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        info.Children.Add(new TextBlock { Text = "ORBIT // LAUNCHER", Style = R<Style>("Crumb"), Foreground = R<Brush>("SignalLightBrush") });
        info.Children.Add(new TextBlock { Text = "Starting Roblox", FontFamily = R<FontFamily>("DisplayFont"), FontSize = 19, FontWeight = FontWeights.Bold, Foreground = R<Brush>("TextBrush"), Margin = new Thickness(0, 4, 0, 0) });
        info.Children.Add(text);
        info.Children.Add(bar);

        var row = new DockPanel { Margin = new Thickness(22) };
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")), Width = 64, Height = 64 };
        DockPanel.SetDock(logo, Dock.Left);
        row.Children.Add(logo);
        row.Children.Add(info);

        var window = new Window
        {
            Title = "Orbit",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Background = R<Brush>("PanelBrush"),
            FontFamily = R<FontFamily>("UiFont"),
            Content = new Border { BorderBrush = R<Brush>("LineStrongBrush"), BorderThickness = new Thickness(1), Child = row },
        };
        window.MouseLeftButtonDown += (_, _) => { try { window.DragMove(); } catch { /* ignore */ } };
        return (window, bar, text);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Integrations.Stop();
        MultiInstance.Disable();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialog.Info(Current?.MainWindow is { IsVisible: true } w ? w : null, "Something went wrong", e.Exception.Message);
        e.Handled = true;
    }
}
