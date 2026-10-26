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
        new("accounts", "Accounts", "", true, () => new AccountsView()),
        new("global", "Global Settings", "", true, () => new GlobalSettingsView()),
        new("fastflags", "FastFlags", "", true, () => new FastFlagsView()),
        new("mods", "Mods", "", true, () => new ModsView()),
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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DrawerLayer.Visibility == Visibility.Visible) { CloseDrawer(); e.Handled = true; }
        };

        StateChanged += (_, _) => UpdateMaximizedState();
        AppState.MultiInstanceChanged += UpdateMultiStatus;
        UpdateMultiStatus();

        Integrations.Notify = (title, message) => Dispatcher.InvokeAsync(() => Toast.Show(title, message));

        Loaded += async (_, _) =>
        {
            ClickerHotkey.Start();
            Integrations.Start(Dispatcher);
            WindowTitles.Start();
            await AppState.RefreshAvatarsAsync();
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
        ContentHost.Content = page;
        if (page is IPage p) p.OnShown();
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

    protected override void OnClosed(EventArgs e)
    {
        AppState.MultiInstanceChanged -= UpdateMultiStatus;
        ClickerHotkey.Stop();
        AutoClicker.Instance.Stop();
        Integrations.Stop();
        base.OnClosed(e);
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
            Application.Current.Shutdown();
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
        Friends.SetAccount(_play.CurrentAccount);
    }

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
