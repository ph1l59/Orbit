using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>
/// All open Roblox windows as live tiles (drawn by Windows, so they cost almost nothing),
/// with the account, the game and controls – plus arranging, the resource saver and Anti-AFK.
/// </summary>
public partial class OverviewView : UserControl, IPage
{
    /// <summary>One tile: the window, its live picture and the texts under it.</summary>
    private sealed class Tile
    {
        public required RobloxWindow Window;
        public required Border Root;
        public required Border Screen;
        public required TextBlock Placeholder;
        public required Ellipse Avatar;
        public required TextBlock Name;
        public required TextBlock Detail;
        public required TextBlock Memory;
        public required Border SlowBadge;
        public LiveThumbnail? Thumb;
        public string? AvatarUrl;
    }

    private readonly Dictionary<IntPtr, Tile> _tiles = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<long, string> _gameNames = new();
    private bool _rendering;
    private bool _loading;
    /// <summary>Process id → CPU time and when it was read (for the CPU % on each tile).</summary>
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _cpu = new();

    public OverviewView()
    {
        InitializeComponent();

        _loading = true;
        var s = AppState.Settings;
        (s.OverviewTileSize switch { "small" => SizeSmall, "large" => SizeLarge, _ => SizeMedium }).IsChecked = true;
        SaverToggle.IsChecked = s.ResourceSaver;
        HideToggle.IsChecked = s.OverviewHideNames;
        EcoToggle.IsChecked = s.SaverEco;
        Select(CapBox, s.SaverCpuCap.ToString(CultureInfo.InvariantCulture));
        AfkToggle.IsChecked = s.AntiAfk;
        Select(AfkInterval, s.AntiAfkMinutes.ToString(CultureInfo.InvariantCulture));
        _loading = false;

        _refresh.Tick += (_, _) => { RefreshTiles(); UpdateAfkStatus(); };
        AntiAfk.Changed += () => Dispatcher.InvokeAsync(UpdateAfkStatus);
        IsVisibleChanged += (_, _) => OnVisibilityChanged();
        RenderGate.Changed += OnVisibilityChanged;
    }

    public void OnShown() => OnVisibilityChanged();

    private Window? Owner => Window.GetWindow(this);

    private static void Select(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    // =================== Showing / hiding ===================

    private void OnVisibilityChanged()
    {
        // Only while the page is open and Orbit isn't minimized or in the tray.
        if (IsVisible && RenderGate.WindowShowing)
        {
            SaverToggle.IsChecked = AppState.Settings.ResourceSaver; // may have been switched on the Accounts page
            RefreshTiles();
            _refresh.Start();
            if (!_rendering)
            {
                _rendering = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }
        else
        {
            _refresh.Stop();
            if (_rendering)
            {
                _rendering = false;
                CompositionTarget.Rendering -= OnRendering;
            }
            // Let go of the live pictures completely, so Windows doesn't keep them up to date for nothing.
            foreach (var t in _tiles.Values)
            {
                t.Thumb?.Dispose();
                t.Thumb = null;
            }
        }
        UpdateAfkStatus();
    }

    /// <summary>Every frame: keep the live pictures exactly on their tiles (scrolling, page animation, resizing).</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (Owner is not { } owner) return;
        var overlay = owner is MainWindow main && main.IsOverlayOpen;
        Rect clip;
        try { clip = TileScroll.TransformToAncestor(owner).TransformBounds(new Rect(0, 0, TileScroll.ViewportWidth, TileScroll.ViewportHeight)); }
        catch { return; }

        foreach (var t in _tiles.Values)
        {
            var minimized = RobloxWindows.IsMinimized(t.Window.Handle);
            t.Placeholder.Visibility = minimized || t.Thumb is not { IsValid: true } ? Visibility.Visible : Visibility.Collapsed;
            t.Thumb?.Update(t.Screen, owner, clip, IsVisible && !overlay && !minimized && owner.WindowState != WindowState.Minimized);
        }
    }

    private void Tiles_ScrollChanged(object sender, ScrollChangedEventArgs e) => OnRendering(null, EventArgs.Empty);

    // =================== Tiles ===================

    private double TileWidth => AppState.Settings.OverviewTileSize switch { "small" => 230, "large" => 420, _ => 310 };

    private bool _refreshing;

    private async void RefreshTiles()
    {
        if (_refreshing || Owner is not { } owner) return;
        _refreshing = true;
        try { await RefreshTilesAsync(owner); }
        finally { _refreshing = false; }
    }

    private async Task RefreshTilesAsync(Window owner)
    {
        var windows = RobloxWindows.All();
        var handles = windows.Select(w => w.Handle).ToHashSet();

        // Closed windows.
        foreach (var gone in _tiles.Keys.Where(h => !handles.Contains(h)).ToList())
        {
            _tiles[gone].Thumb?.Dispose();
            TilePanel.Children.Remove(_tiles[gone].Root);
            _tiles.Remove(gone);
        }

        // New windows.
        foreach (var w in windows.Where(w => !_tiles.ContainsKey(w.Handle)))
        {
            var tile = MakeTile(w);
            tile.Thumb = new LiveThumbnail(owner, w.Handle);
            _tiles[w.Handle] = tile;
        }
        // Live pictures are let go while the page is closed – connect them again.
        foreach (var t in _tiles.Values.Where(t => t.Thumb == null))
            t.Thumb = new LiveThumbnail(owner, t.Window.Handle);

        // Keep the order of the Accounts page.
        var order = windows.Select(w => _tiles[w.Handle].Root).ToList();
        if (!TilePanel.Children.Cast<UIElement>().SequenceEqual(order))
        {
            TilePanel.Children.Clear();
            foreach (var root in order) TilePanel.Children.Add(root);
        }

        // Texts.
        var games = Integrations.GameSnapshot();
        var hide = AppState.Settings.OverviewHideNames;
        long total = 0;
        var index = 0;
        foreach (var w in windows)
        {
            index++;
            var t = _tiles[w.Handle];
            var account = AppState.Accounts.FirstOrDefault(a => a.UserId == w.UserId);
            t.Name.Text = hide ? $"Account {index}"
                : account == null ? $"Roblox · PID {w.Pid}"
                : string.IsNullOrWhiteSpace(account.Title) ? account.Username : account.Title;

            var url = hide ? null : account?.AvatarUrl;
            if (url != t.AvatarUrl)
            {
                t.AvatarUrl = url;
                try { t.Avatar.Fill = url == null ? (Brush)FindResource("Raised2Brush") : new ImageBrush(new BitmapImage(new Uri(url))) { Stretch = Stretch.UniformToFill }; }
                catch { /* no picture */ }
            }

            var memory = RobloxWindows.MemoryOf(w.Pid);
            total += memory;
            var cpu = CpuPercent(w.Pid);
            t.Memory.Text = (cpu is { } c ? "CPU " + c.ToString(c < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " %  ·  " : "")
                            + (memory / 1024.0 / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            t.SlowBadge.Visibility = RobloxWindows.IsThrottled(w.Pid) ? Visibility.Visible : Visibility.Collapsed;

            if (w.UserId > 0 && games.TryGetValue(w.UserId, out var g))
            {
                if (g.Disconnected) { t.Detail.Text = "Disconnected"; t.Detail.Foreground = (Brush)FindResource("WarnBrush"); }
                else if (g.PlaceId > 0)
                {
                    t.Detail.Text = "Playing " + await GameNameAsync(g.PlaceId);
                    t.Detail.Foreground = (Brush)FindResource("OkBrush");
                }
                else { t.Detail.Text = "In the Roblox app"; t.Detail.Foreground = (Brush)FindResource("DimBrush"); }
            }
            else
            {
                t.Detail.Text = account == null ? "Not launched by Orbit" : hide ? "@••••••" : "@" + account.Username;
                t.Detail.Foreground = (Brush)FindResource("DimBrush");
            }
        }

        var n = windows.Count;
        CountText.Text = n == 0
            ? "All your Roblox windows live, on one screen."
            : $"{n} Roblox window{(n == 1 ? "" : "s")} open  ·  {(total / 1024.0 / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture)} GB RAM";
        EmptyState.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;

        ArrangeButton.IsEnabled = n > 0;
        PutBackButton.IsEnabled = RobloxWindows.CanRestore;
        foreach (var gone in _cpu.Keys.Where(pid => windows.All(w => w.Pid != pid)).ToList()) _cpu.Remove(gone);
    }

    /// <summary>CPU use of a process since the last check, in percent of the whole CPU (like Task Manager).</summary>
    private double? CpuPercent(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            double? result = null;
            if (_cpu.TryGetValue(pid, out var last) && now > last.At)
                result = Math.Max(0, (cpu - last.Cpu).TotalMilliseconds / (now - last.At).TotalMilliseconds / Environment.ProcessorCount * 100);
            _cpu[pid] = (cpu, now);
            return result;
        }
        catch
        {
            return null; // not allowed to read it
        }
    }

    private async Task<string> GameNameAsync(long placeId)
    {
        if (_gameNames.TryGetValue(placeId, out var name)) return name;
        try { name = (await RobloxApi.GetGameInfoAsync(placeId))?.Name ?? "a game"; }
        catch { name = "a game"; }
        _gameNames[placeId] = name;
        return name;
    }

    private Tile MakeTile(RobloxWindow w)
    {
        var width = TileWidth;

        var placeholder = new TextBlock
        {
            Text = "Minimized – no live picture.\nClick to show it.",
            TextAlignment = TextAlignment.Center,
            Style = (Style)FindResource("RowDesc"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var screen = new Border
        {
            Height = width * 9 / 16,
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x07, 0x06, 0x0A)),
            Cursor = Cursors.Hand,
            ToolTip = "Click to bring this window to the front",
            Child = placeholder,
        };

        var avatar = new Ellipse { Width = 30, Height = 30, Fill = (Brush)FindResource("Raised2Brush") };
        var name = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { FontSize = 11.5, Foreground = (Brush)FindResource("DimBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        var memory = new TextBlock { Style = (Style)FindResource("Crumb"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var slow = new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("SignalTintBrush"), BorderBrush = (Brush)FindResource("SignalBorderBrush"), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "SAVER", Style = (Style)FindResource("Crumb"), FontSize = 9.5, Foreground = (Brush)FindResource("SignalLightBrush") },
            Visibility = Visibility.Collapsed,
            ToolTip = "Slowed down by the resource saver",
        };

        Button IconButton(string glyph, string tip, RoutedEventHandler click)
        {
            var b = new Button
            {
                Style = (Style)FindResource("GhostButton"), Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), ToolTip = tip,
                Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 12 },
            };
            b.Click += click;
            return b;
        }

        var hwnd = w.Handle;
        var front = IconButton("", "Bring to front", (_, _) => FocusWindow(hwnd));
        var close = IconButton("", "Close this Roblox window", (_, _) => CloseWindow(hwnd));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(front);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);

        var texts = new StackPanel { Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(name);
        texts.Children.Add(detail);

        var info = new DockPanel { Margin = new Thickness(12, 10, 8, 0) };
        info.Children.Add(buttons);
        info.Children.Add(avatar);
        info.Children.Add(texts);

        // CPU / RAM and the saver badge get their own line, so they fit on small tiles too.
        DockPanel.SetDock(slow, Dock.Right);
        var stats = new DockPanel { Margin = new Thickness(12, 8, 12, 12), LastChildFill = true };
        stats.Children.Add(slow);
        stats.Children.Add(memory);

        var stack = new StackPanel();
        stack.Children.Add(screen);
        stack.Children.Add(info);
        stack.Children.Add(stats);

        var root = new Border
        {
            Width = width,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(11),
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        root.MouseEnter += (_, _) => root.BorderBrush = (Brush)FindResource("SignalBorderBrush");
        root.MouseLeave += (_, _) => root.BorderBrush = (Brush)FindResource("LineBrush");
        screen.MouseLeftButtonUp += (_, _) => FocusWindow(hwnd);

        return new Tile
        {
            Window = w, Root = root, Screen = screen, Placeholder = placeholder, Avatar = avatar,
            Name = name, Detail = detail, Memory = memory, SlowBadge = slow,
        };
    }

    private void FocusWindow(IntPtr hwnd)
    {
        RobloxWindows.BringToFront(hwnd);
    }

    private void CloseWindow(IntPtr hwnd)
    {
        var name = _tiles.TryGetValue(hwnd, out var t) ? t.Name.Text : "this Roblox window";
        if (Owner is { } owner && !Dialog.Confirm(owner, "Close Roblox window", $"Close the Roblox window of {name}?", "Close", danger: true)) return;
        RobloxWindows.Close(hwnd);
    }

    private void Size_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string size }) return;
        AppState.Settings.OverviewTileSize = size;
        AppState.SaveSettings();
        foreach (var t in _tiles.Values)
        {
            t.Root.Width = TileWidth;
            t.Screen.Height = TileWidth * 9 / 16;
        }
    }

    private void GoAccounts_Click(object sender, RoutedEventArgs e) => (Owner as MainWindow)?.Navigate("accounts");

    // =================== Arrange / hide names ===================

    private async void Arrange_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner) return;
        ArrangeButton.IsEnabled = false;
        try
        {
            // On the screen Orbit is on.
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle);
            var count = await RobloxWindows.ArrangeAsync(Array.IndexOf(System.Windows.Forms.Screen.AllScreens, screen));
            Toast.Show("Roblox windows", count == 0 ? "No Roblox window is open." : $"{count} window{(count == 1 ? "" : "s")} arranged.");
        }
        finally
        {
            ArrangeButton.IsEnabled = true;
            PutBackButton.IsEnabled = RobloxWindows.CanRestore;
        }
    }

    private void PutBack_Click(object sender, RoutedEventArgs e)
    {
        RobloxWindows.Restore();
        PutBackButton.IsEnabled = false;
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.OverviewHideNames = HideToggle.IsChecked == true;
        AppState.SaveSettings();
        RefreshTiles();
    }

    // =================== Resource saver ===================

    private void Saver_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.ResourceSaver = SaverToggle.IsChecked == true;
        AppState.SaveSettings();
        RobloxWindows.RefreshSaver(Dispatcher);
    }

    private void Cap_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(CapBox) is not { } tag) return;
        AppState.Settings.SaverCpuCap = int.Parse(tag, CultureInfo.InvariantCulture);
        AppState.SaveSettings();
        if (AppState.Settings.ResourceSaver) RobloxWindows.ReapplySaver(Dispatcher);
    }

    private void Eco_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.SaverEco = EcoToggle.IsChecked == true;
        AppState.SaveSettings();
        if (AppState.Settings.ResourceSaver) RobloxWindows.ReapplySaver(Dispatcher);
    }

    // =================== Anti-AFK ===================

    private void Afk_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.AntiAfk = AfkToggle.IsChecked == true;
        AppState.SaveSettings();
        AntiAfk.Refresh();
    }

    private void AfkInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(AfkInterval) is not { } tag) return;
        AppState.Settings.AntiAfkMinutes = int.Parse(tag, CultureInfo.InvariantCulture);
        AppState.SaveSettings();
        AntiAfk.Refresh();
    }

    private async void AfkTest_Click(object sender, RoutedEventArgs e)
    {
        var count = await AntiAfk.RunNowAsync();
        Toast.Show("Anti-AFK", count == 0 ? "No Roblox window to press a key in." : $"Pressed Space in {count} window{(count == 1 ? "" : "s")}.");
    }

    private void UpdateAfkStatus()
    {
        if (AntiAfk.Next is not { } next) { AfkStatus.Text = "OFF"; return; }
        var left = next - DateTime.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        AfkStatus.Text = $"ON · NEXT IN {(int)left.TotalMinutes}:{left.Seconds:00}";
    }
}
