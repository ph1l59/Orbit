using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Orbit.Services;
using Orbit.Views;

namespace Orbit;

/// <summary>Implemented by pages that want to refresh when they are shown.</summary>
public interface IPage
{
    void OnShown();
}

public sealed record SearchHit(string Title, string PageTitle, string PageKey, FrameworkElement Element);

public partial class MainWindow : Window
{
    private sealed record PageInfo(string Key, string Title, string Glyph, bool InNav, Func<UserControl> Create);

    private static readonly PageInfo[] PageList =
    {
        new("play", "Play", "\uE768", true, () => new PlayView()),
        new("overview", "Overview", "\uECA5", true, () => new OverviewView()),
        new("accounts", "Accounts", "", true, () => new AccountsView()),
        new("global", "Global Settings", "", true, () => new GlobalSettingsView()),
        new("fastflags", "FastFlags", "", true, () => new FastFlagsView()),
        new("mods", "Mods", "", true, () => new ModsView()),
        new("tweaks", "Tweaks", "\uEC4A", true, () => new TweaksView()),
        new("bootstrapper", "Bootstrapper", "", true, () => new BootstrapperView()),
        new("integrations", "Integrations", "", true, () => new IntegrationsView()),
        new("clicker", "Auto Clicker", "", true, () => new AutoClickerView()),
        new("about", "About", "", false, () => new AboutView()),
        new("flageditor", "Fast Flag Editor", "", false, () => new FlagEditorView()),
    };

    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private UpdateInfo? _update;
    /// <summary>Version the user clicked "Not Now" on – only a newer one shows the notice again (until restart).</summary>
    private Version? _dismissedVersion;
    private bool _updating;
    private readonly List<SearchHit> _index = new();
    private bool _switching;

    public MainWindow()
    {
        InitializeComponent();

        VersionText.Text = "v" + Updater.DisplayVersion;

        foreach (var page in PageList.Where(p => p.InNav))
            Nav.Items.Add(new ListBoxItem { Tag = page.Key, Content = NavContent(page) });

        foreach (var page in PageList)
            _pages[page.Key] = page.Create();

        BuildSearchIndex();
        WireFriends();
        CreateTray();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DrawerLayer.Visibility == Visibility.Visible) { CloseDrawer(); e.Handled = true; }
        };

        StateChanged += (_, _) =>
        {
            UpdateMaximizedState();
            RenderGate.Set(IsVisible && WindowState != WindowState.Minimized);
        };
        // Discord shows "In the launcher" only while the window is open (not while Orbit sits in the tray).
        IsVisibleChanged += (_, _) =>
        {
            Integrations.SetLauncherOpen(IsVisible);
            RenderGate.Set(IsVisible && WindowState != WindowState.Minimized);
        };
        AppState.MultiInstanceChanged += UpdateMultiStatus;
        UpdateMultiStatus();

        Integrations.Notify = (title, message) => Dispatcher.InvokeAsync(() => Toast.Show(title, message));
        Integrations.NotifyWithImage = (title, message, image) => Dispatcher.InvokeAsync(() => Toast.Show(title, message, image, null, null));

        Loaded += async (_, _) =>
        {
            ClickerHotkey.Start();
            Integrations.Start(Dispatcher);
            Integrations.SetLauncherOpen(IsVisible);
            WindowTitles.Start();
            Playtime.Start(Dispatcher);
            RamCleaner.Start(Dispatcher);
            AntiAfk.Start(Dispatcher);
            RobloxWindows.RefreshSaver(Dispatcher);

            // "What's new" once after an update – a moment after the window is up.
            await Task.Delay(700);
            WhatsNewWindow.ShowIfUpdated(this, App.StartedAfterUpdate);

            await AppState.RefreshAvatarsAsync();
            await CheckLoginsAsync();
            await CheckForUpdateAsync();
        };

        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();

        Nav.SelectedIndex = 0;
    }

    private static UIElement NavContent(PageInfo page)
    {
        var dock = new DockPanel();
        dock.Children.Add(new TextBlock { Text = page.Glyph, Style = (Style)Application.Current.FindResource("Icon"), Width = 22 });
        dock.Children.Add(new TextBlock { Text = page.Title, Margin = new Thickness(8, 0, 0, 0), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        return dock;
    }

    // ---------------- Navigation ----------------

    public void Navigate(string key)
    {
        _switching = true;
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == key);
        BottomNav.SelectedItem = BottomNav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == key);
        _switching = false;
        Show(key);
    }

    private void Show(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        var changed = !ReferenceEquals(ContentHost.Content, page);
        ContentHost.Content = page;
        if (page is IPage p) p.OnShown();
        if (changed) Dispatcher.InvokeAsync(() => AnimateIn(page), DispatcherPriority.Loaded);
    }

    // ---------------- Page entrance ----------------

    /// <summary>
    /// The page glides up from 36 px below and fades in (half a second, gentle start and end).
    /// While it moves it's drawn from a cached picture, so the shadows don't have to be redrawn every frame.
    /// </summary>
    private void AnimateIn(FrameworkElement page)
    {
        var duration = TimeSpan.FromMilliseconds(500);
        var dpi = VisualTreeHelper.GetDpi(ContentHost).DpiScaleX;
        ContentHost.CacheMode = new BitmapCache(dpi) { SnapsToDevicePixels = true };

        var shift = new TranslateTransform(0, 36);
        ContentHost.RenderTransform = shift;

        // Even speed: starts gently, glides, settles gently – no fast jump at the beginning.
        var slide = new System.Windows.Media.Animation.DoubleAnimation(36, 0, duration)
        {
            EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
        };
        slide.Completed += (_, _) => ContentHost.CacheMode = null; // back to sharp, live drawing
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };

        shift.BeginAnimation(TranslateTransform.YProperty, slide);
        ContentHost.BeginAnimation(OpacityProperty, fade);
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || Nav.SelectedItem is not ListBoxItem item) return;
        _switching = true;
        BottomNav.SelectedItem = null;
        _switching = false;
        Show((string)item.Tag);
    }

    private void BottomNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || BottomNav.SelectedItem is not ListBoxItem item) return;
        _switching = true;
        Nav.SelectedItem = null;
        _switching = false;
        Show((string)item.Tag);
    }

    // ---------------- Search ----------------

    private void BuildSearchIndex()
    {
        _index.Clear();
        foreach (var info in PageList)
        {
            var page = _pages[info.Key];
            _index.Add(new SearchHit(info.Title, "Page", info.Key, page));
            Collect(page, info);
        }
    }

    private void Collect(DependencyObject node, PageInfo page)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is FrameworkElement { Tag: string tag } element && (child is Border || child is Button) && tag.Length > 0)
            {
                var title = tag.Split('|')[0];
                _index.Add(new SearchHit(title, page.Title.ToUpperInvariant(), page.Key, element) { });
                _keywords[element] = tag;
            }
            Collect(child, page);
        }
    }

    private readonly Dictionary<FrameworkElement, string> _keywords = new();

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            Nav.Visibility = Visibility.Visible;
            return;
        }

        var hits = _index.Where(h =>
                h.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (_keywords.TryGetValue(h.Element, out var k) && k.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(12)
            .ToList();

        SearchResults.ItemsSource = hits;
        SearchCount.Text = hits.Count == 0 ? "NO RESULTS" : $"{hits.Count} RESULT{(hits.Count == 1 ? "" : "S")}";
        SearchPanel.Visibility = Visibility.Visible;
        Nav.Visibility = Visibility.Collapsed;
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) SearchBox.Text = "";
        else if (e.Key == Key.Enter && SearchResults.Items.Count > 0) SearchResults.SelectedIndex = 0;
        else if (e.Key == Key.Down && SearchResults.Items.Count > 0)
        {
            SearchResults.SelectedIndex = 0;
            (SearchResults.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        }
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not SearchHit hit) return;
        SearchBox.Text = "";
        Navigate(hit.PageKey);

        if (hit.PageTitle == "Page") return;
        Dispatcher.InvokeAsync(() =>
        {
            hit.Element.BringIntoView();
            Highlight(hit.Element);
        }, DispatcherPriority.Loaded);
    }

    private void Highlight(FrameworkElement element)
    {
        if (element is not Border border) return;
        border.BorderBrush = (Brush)FindResource("SignalBrush");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            border.ClearValue(Border.BorderBrushProperty);
        };
        timer.Start();
    }

    // ---------------- Status ----------------

    private void UpdateMultiStatus()
    {
        var active = MultiInstance.IsActive;
        MultiDot.Fill = (Brush)FindResource(active ? "OkBrush" : "FaintBrush");
        MultiText.Text = active ? "ON · SEVERAL WINDOWS" : "OFF";
    }

    private void MultiPill_Click(object sender, MouseButtonEventArgs e) => Navigate("bootstrapper");

    // ---------------- Custom title bar ----------------

    private void UpdateMaximizedState()
    {
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- Tray ----------------

    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _exitRequested;
    private bool _trayHintShown;

    private void CreateTray()
    {
        if (_tray != null) return;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Orbit", null, (_, _) => Dispatcher.InvokeAsync(ShowFromTray));
        menu.Items.Add("Launch Roblox", null, (_, _) => Dispatcher.InvokeAsync(() =>
        {
            ShowFromTray();
            Navigate("play");
            _play?.LaunchFromOutside();
        }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.InvokeAsync(ExitApp));

        System.Drawing.Icon icon;
        try
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/orbit.ico"))!.Stream;
            icon = new System.Drawing.Icon(stream);
        }
        catch
        {
            icon = System.Drawing.SystemIcons.Application;
        }

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Orbit",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.InvokeAsync(ShowFromTray);
        };
    }

    /// <summary>Brings the window back from the tray (or to the front).</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // make sure it really comes to the front
        Topmost = false;
        Focus();
    }

    /// <summary>Really quits Orbit (tray menu "Exit").</summary>
    private void ExitApp()
    {
        _exitRequested = true;
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested && !Dispatcher.HasShutdownStarted && AppState.Settings.CloseToTray)
        {
            // Keep running in the tray (friend notifications, updates, …).
            e.Cancel = true;
            CloseDrawer();
            Hide();
            CreateTray();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Toast.Show("Orbit", "Orbit keeps running in the tray. Right-click the icon to quit.");
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        AppState.MultiInstanceChanged -= UpdateMultiStatus;
        ClickerHotkey.Stop();
        AutoClicker.Instance.Stop();
        Integrations.Stop();
        Playtime.Stop();
        RobloxWindows.StopSaver();
        base.OnClosed(e);
    }

    // ---------------- Login check ----------------

    /// <summary>Checks all saved logins once at start and says which accounts have to log in again.</summary>
    private async Task CheckLoginsAsync()
    {
        if (AppState.Accounts.Count == 0) return;
        List<Models.Account> expired;
        try { expired = await AppState.CheckLoginsAsync(); }
        catch { return; }
        if (expired.Count == 0) return;

        var names = string.Join(", ", expired.Take(3).Select(a => string.IsNullOrWhiteSpace(a.Title) ? a.Username : a.Title));
        if (expired.Count > 3) names += $" and {expired.Count - 3} more";
        var text = expired.Count == 1
            ? $"{names} has to log in again. Click Add account and log in with it."
            : $"{expired.Count} accounts have to log in again: {names}.";
        Toast.Show("Login expired", text, expired.Count == 1 ? expired[0].AvatarUrl : null, "Accounts", () =>
        {
            ShowFromTray();
            Navigate("accounts");
        });
    }

    // ---------------- Updates ----------------

    private async Task CheckForUpdateAsync()
    {
        if (!AppState.Settings.CheckForUpdates || _updating) return;

        var update = await Updater.CheckAsync();
        if (update == null || !AppState.Settings.CheckForUpdates || _updating) return;

        // Same version the user already said "Not Now" to (or an older one): stay quiet.
        if (_dismissedVersion != null && update.Version <= _dismissedVersion) return;
        // The notice already shows this version.
        if (_update != null && update.Version <= _update.Version) return;

        var wasShowing = _update != null;
        _update = update;
        UpdateTitle.Text = $"Orbit v{update.Version}";
        UpdateText.Text = $"A new version is ready. You're on v{Updater.DisplayVersion}.";
        UpdateNowButton.Content = "Update Now";
        if (!wasShowing) ShowUpdateCard(true); // already visible: just show the newer version
    }

    private void ShowUpdateCard(bool show)
    {
        var duration = TimeSpan.FromMilliseconds(show ? 420 : 260);
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = show ? System.Windows.Media.Animation.EasingMode.EaseOut : System.Windows.Media.Animation.EasingMode.EaseIn,
        };
        var slide = new System.Windows.Media.Animation.DoubleAnimation(show ? -380 : 0, show ? 0 : -380, duration) { EasingFunction = ease };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(show ? 0 : 1, show ? 1 : 0, duration);

        if (show) UpdateCard.Visibility = Visibility.Visible;
        else fade.Completed += (_, _) => UpdateCard.Visibility = Visibility.Collapsed;

        UpdateCardShift.BeginAnimation(TranslateTransform.XProperty, slide);
        UpdateCard.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        // This version stays hidden until Orbit is restarted; a newer release shows the notice again.
        _dismissedVersion = _update?.Version;
        _update = null;
        ShowUpdateCard(false);
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } update || _updating) return;

        if (!Updater.CanUpdateInPlace())
        {
            Dialog.Info(this, "Update",
                "Orbit can't update itself in this folder (no write access). The new version is downloaded in your browser instead – " +
                "replace your Orbit.exe with it.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Updater.DownloadUrl) { UseShellExecute = true });
            return;
        }

        _updating = true;
        UpdateButtons.Visibility = Visibility.Collapsed;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateText.Text = "Downloading…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                UpdateText.Text = $"Downloading… {p:0}%";
            });
            await Updater.DownloadAndInstallAsync(update, progress);

            UpdateText.Text = "Restarting Orbit…";
            await Task.Delay(400);
            Updater.Restart();
            ExitApp(); // really quit, don't go to the tray
        }
        catch (Exception ex)
        {
            _updating = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateButtons.Visibility = Visibility.Visible;
            UpdateNowButton.Content = "Try again";
            UpdateText.Text = "Update failed: " + ex.Message;
        }
    }

    // ---------------- Friends drawer ----------------

    private PlayView? _play;
    private readonly TranslateTransform DrawerShift = new(420, 0);

    private void WireFriends()
    {
        Friends.RenderTransform = DrawerShift;
        _play = _pages["play"] as PlayView;
        if (_play == null) return;
        _play.AccountChanged += account => Friends.SetAccount(account);
        _play.FriendsRequested += OpenDrawer;
        Friends.RequestCountChanged += count => _play.SetRequestCount(count);
        Friends.JoinRequested += friend => { CloseDrawer(); _play.Join(friend); };
        Friends.CloseRequested += CloseDrawer;
        Friends.FriendActivity += (friend, playing) =>
        {
            var who = friend.HasName ? friend.Title : "A friend";
            var text = playing
                ? (string.IsNullOrWhiteSpace(friend.Location) ? $"{who} started playing" : $"{who} is playing {friend.Location}")
                : $"{who} is online";
            Toast.Show("Friends", text, friend.AvatarUrl, playing ? "Join" : null, playing ? () =>
            {
                ShowFromTray();
                _play.Join(friend);
            } : null);
        };
        Friends.FriendRequestReceived += person =>
        {
            if (!AppState.Settings.NotifyFriendsOnline) return;
            Toast.Show("Friend request", $"{(person.HasName ? person.Title : "Someone")} sent you a friend request.", person.AvatarUrl, "Open", () =>
            {
                ShowFromTray();
                Navigate("play");
                OpenDrawer();
            });
        };
        Friends.SetAccount(_play.CurrentAccount);
    }

    /// <summary>True while something covers the page (friends drawer) – live window pictures hide then.</summary>
    public bool IsOverlayOpen => DrawerLayer.Visibility == Visibility.Visible;

    private readonly System.Windows.Media.Effects.BlurEffect _blur = new() { Radius = 0 };

    private void OpenDrawer()
    {
        DrawerLayer.Visibility = Visibility.Visible;
        SidebarRoot.Effect = _blur;
        ContentRoot.Effect = _blur;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(320);
        DrawerShift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(420, 0, duration) { EasingFunction = ease });
        DrawerShade.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration));
        _blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 10, duration));
        Friends.Opened();
    }

    private void CloseDrawer()
    {
        if (DrawerLayer.Visibility != Visibility.Visible) return;
        Friends.IsOpen = false;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(240);
        var slide = new System.Windows.Media.Animation.DoubleAnimation(0, 420, duration) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            DrawerLayer.Visibility = Visibility.Collapsed;
            SidebarRoot.Effect = null;
            ContentRoot.Effect = null;
        };
        DrawerShift.BeginAnimation(TranslateTransform.XProperty, slide);
        DrawerShade.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0, duration));
        _blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new System.Windows.Media.Animation.DoubleAnimation(10, 0, duration));
    }

    private void DrawerShade_Click(object sender, MouseButtonEventArgs e) => CloseDrawer();
}
