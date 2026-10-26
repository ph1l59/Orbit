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
    private const string ShowSignalName = "Orbit_by_Floshiii_Show";
    private static EventWaitHandle? _showSignal;

    /// <summary>A second Orbit start sets this signal; the running Orbit then shows its window.</summary>
    private void ListenForShowSignal()
    {
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            var thread = new Thread(() =>
            {
                while (_showSignal.WaitOne())
                    Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.ShowFromTray());
            }) { IsBackground = true, Name = "Orbit show signal" };
            thread.Start();
        }
        catch { /* not essential */ }
    }
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
            // Orbit is already running (maybe hidden in the tray): bring that window to the front instead.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowSignalName);
                show.Set();
            }
            catch
            {
                Dialog.Info(null, "Already running", "Orbit is already open – look for it in the taskbar or the tray.");
            }
            Shutdown();
            return;
        }

        ListenForShowSignal();

        Updater.CleanupLeftovers();
        _ = StartSafelyAsync();
    }

    private async Task StartSafelyAsync()
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            Dialog.Info(null, "Orbit couldn't start", ex.Message);
            Shutdown();
        }
    }

    /// <summary>Normal start: loading window with the real startup steps, then the main window.</summary>
    private async Task StartAsync()
    {
        var splash = new SplashWindow();
        splash.Show();
        await splash.StepAsync("Starting…", 5);

        await splash.StepAsync("Loading settings and accounts…", 12);
        var loadError = AppState.Load();
        var s = AppState.Settings;

        await splash.StepAsync("Preparing Multi-Roblox…", 35);
        string? multiError = null;
        if (s.MultiRoblox && !MultiInstance.TryEnable(out multiError))
        {
            s.MultiRoblox = false;
            AppState.SaveSettings();
        }

        await splash.StepAsync("Checking Roblox setup…", 50);
        ProtocolHandler.RefreshIfRegistered(s);
        Cleaner.RunIfDue(s);

        await splash.StepAsync("Building the interface…", 65);
        var window = new MainWindow();
        MainWindow = window;

        await splash.StepAsync("Loading avatars…", 85);
        try { await AppState.RefreshAvatarsAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch { /* slow connection: avatars appear a bit later */ }

        await splash.StepAsync("Ready", 100);
        await Task.Delay(150);

        window.Show();
        window.Activate();
        splash.CloseSoftly();

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

        var window = new LaunchWindow(null, null);
        window.Show();

        try
        {
            var progress = new Progress<BootstrapProgress>(p =>
            {
                window.SetStatus(p.Message);
                window.SetProgress(p.Percent);
            });
            var exe = await RobloxLauncher.PrepareAsync(s, progress);
            if (window.CancelRequested)
            {
                window.Close();
                Shutdown();
                return;
            }
            window.SetProgress(null);
            window.SetStatus("Starting Roblox…");
            window.HideCancel();
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
