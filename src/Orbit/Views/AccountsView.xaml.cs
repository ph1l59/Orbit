using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

public partial class AccountsView : UserControl
{
    private readonly ICollectionView _view;
    private bool _launching;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private RobloxUser? _followUser;
    private int _previewVersion;

    public AccountsView()
    {
        InitializeComponent();

        AccountList.ItemsSource = AppState.Accounts;
        _view = CollectionViewSource.GetDefaultView(AppState.Accounts);
        _view.Filter = MatchesSearch;

        PlaceBox.Text = AppState.Settings.LastPlace;
        ServerBox.Text = AppState.Settings.LastServer;

        AppState.Accounts.CollectionChanged += OnAccountsChanged;
        foreach (var account in AppState.Accounts) account.PropertyChanged += OnAccountPropertyChanged;
        UpdateCounts();

        _previewTimer.Tick += async (_, _) => { _previewTimer.Stop(); await UpdateGamePreviewAsync(); };
        _followTimer.Tick += async (_, _) => { _followTimer.Stop(); await UpdateFollowPreviewAsync(); };
        _previewTimer.Start();
    }

    private Window Owner => Window.GetWindow(this)!;

    // ---------- List / selection ----------

    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Account a in e.NewItems) a.PropertyChanged += OnAccountPropertyChanged;
        if (e.OldItems != null)
            foreach (Account a in e.OldItems) a.PropertyChanged -= OnAccountPropertyChanged;
        UpdateCounts();
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Account.IsSelected) or nameof(Account.Presence)) UpdateCounts();
    }

    private void UpdateCounts()
    {
        var total = AppState.Accounts.Count;
        var selected = AppState.Accounts.Count(a => a.IsSelected);
        var online = AppState.Accounts.Count(a => a.IsOnline);

        var text = total == 1 ? "1 account" : $"{total} accounts";
        if (online > 0) text += $"  ·  {online} online";
        if (selected > 0) text += $"  ·  {selected} selected";
        CountText.Text = text;

        LaunchText.Text = selected switch
        {
            0 => "Launch",
            1 => "Launch 1 account",
            _ => $"Launch {selected} accounts",
        };

        EmptyState.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountList.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool MatchesSearch(object obj)
    {
        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query) || obj is not Account a) return true;
        return a.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
               || a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || a.Alias.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _view.Cast<Account>()) a.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in AppState.Accounts) a.IsSelected = false;
    }

    // ---------- Add account ----------

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var login = new LoginWindow { Owner = Owner };
        if (login.ShowDialog() != true || login.Result is not { } user) return;

        var existing = AppState.Accounts.FirstOrDefault(a => a.UserId == user.UserId);
        if (existing != null)
        {
            existing.Cookie = user.Cookie;
            existing.Username = user.Username;
            existing.DisplayName = user.DisplayName;
            existing.Status = "Login updated ✓";
        }
        else
        {
            AppState.Accounts.Add(user);
        }

        AppState.SaveAccounts();
        await AppState.RefreshAvatarsAsync();
        await Integrations.RefreshPresenceAsync();
    }

    // ---------- Launch ----------

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_launching) return;
        var s = AppState.Settings;

        var accounts = AppState.Accounts.Where(a => a.IsSelected).ToList();
        if (accounts.Count == 0)
        {
            ShowStatus("Select at least one account on the left.", true);
            return;
        }

        JoinTarget? target;
        if (FollowMode.IsChecked == true)
        {
            var name = FollowBox.Text.Trim();
            if (name.Length == 0)
            {
                ShowStatus("Enter the username of the player you want to join.", true);
                return;
            }
            var user = _followUser != null && _followUser.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? _followUser
                : await RobloxApi.GetUserByNameAsync(name);
            if (user == null)
            {
                ShowStatus($"There's no Roblox user called \"{name}\".", true);
                return;
            }
            target = JoinTarget.Follow(user.Id);
        }
        else
        {
            target = JoinTarget.Parse(PlaceBox.Text, ServerBox.Text, out var error);
            if (target == null)
            {
                ShowStatus(error, true);
                return;
            }
        }

        if (!MultiInstance.IsActive)
        {
            if (accounts.Count > 1)
            {
                var enable = Dialog.Confirm(Owner, "Multi-Roblox is off",
                    "Without Multi-Roblox every new account closes the previous Roblox window.\n\n" +
                    "Turn on Multi-Roblox now? All Roblox windows have to be closed for that.",
                    "Turn on");
                if (enable)
                {
                    if (!MultiInstance.TryEnable(out var multiError))
                    {
                        ShowStatus(multiError ?? "Multi-Roblox could not be turned on.", true);
                        return;
                    }
                    s.MultiRoblox = true;
                    AppState.SaveSettings();
                    AppState.NotifyMultiInstanceChanged();
                }
            }
            else if (s.ConfirmLaunchWhenRunning && RobloxPaths.IsRobloxRunning())
            {
                if (!Dialog.Confirm(Owner, "Roblox is already running",
                        "Launching another account will close the Roblox window that is open right now.", "Launch anyway"))
                    return;
            }
        }

        s.LastPlace = PlaceBox.Text.Trim();
        s.LastServer = ServerBox.Text.Trim();
        AppState.SaveSettings();

        _launching = true;
        LaunchButton.IsEnabled = false;
        LaunchProgress.Visibility = Visibility.Visible;
        LaunchProgress.Value = 0;

        try
        {
            var progress = new Progress<BootstrapProgress>(p =>
            {
                LaunchProgress.Value = p.Percent;
                ShowStatus(p.Message, false);
            });

            string exe;
            try
            {
                exe = await RobloxLauncher.PrepareAsync(s, progress);
            }
            catch (Exception ex)
            {
                ShowStatus("Couldn't prepare Roblox: " + ex.Message, true);
                return;
            }

            Integrations.StartCustomIntegrations();
            LaunchProgress.Visibility = Visibility.Collapsed;

            var delay = Math.Clamp(s.LaunchDelaySeconds, 1, 120);
            for (var i = 0; i < accounts.Count; i++)
            {
                var account = accounts[i];
                account.Status = "Launching…";
                ShowStatus($"Launching {account.Title} ({i + 1}/{accounts.Count})…", false);

                try
                {
                    await RobloxLauncher.LaunchAccountAsync(account, target, exe);
                    account.Status = "";
                    account.LastUsed = DateTime.UtcNow;
                    AppState.SaveAccounts();
                }
                catch (Exception ex)
                {
                    account.Status = "Error: " + ex.Message;
                }

                if (i < accounts.Count - 1)
                {
                    for (var sec = delay; sec > 0; sec--)
                    {
                        ShowStatus($"Next account in {sec}s…", false);
                        await Task.Delay(1000);
                    }
                }
            }

            var failed = accounts.Count(a => a.Status.StartsWith("Error"));
            ShowStatus(failed == 0
                ? (accounts.Count == 1 ? "Roblox is starting ✓" : $"{accounts.Count} accounts launched ✓")
                : $"{accounts.Count - failed} launched, {failed} failed – see the account card for details.", failed > 0);
        }
        finally
        {
            _launching = false;
            LaunchButton.IsEnabled = true;
            LaunchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStatus(string text, bool isWarning)
    {
        LaunchStatus.Text = text;
        LaunchStatus.Foreground = (System.Windows.Media.Brush)FindResource(isWarning ? "WarnBrush" : "DimBrush");
        LaunchStatus.Visibility = Visibility.Visible;
    }

    // ---------- Context menu ----------

    private static Account? AccountFrom(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } })
            return target.DataContext as Account;
        return (sender as FrameworkElement)?.DataContext as Account;
    }

    private void Menu_Alias_Click(object sender, RoutedEventArgs e)
    {
        if (AccountFrom(sender) is not { } account) return;
        var alias = Dialog.Ask(Owner, "Set alias",
            "A name of your own, shown instead of the display name. Leave empty to remove it.", account.Alias);
        if (alias == null) return;
        account.Alias = alias.Trim();
        AppState.SaveAccounts();
    }

    private void Menu_Profile_Click(object sender, RoutedEventArgs e)
    {
        if (AccountFrom(sender) is not { } account) return;
        Process.Start(new ProcessStartInfo($"https://www.roblox.com/users/{account.UserId}/profile") { UseShellExecute = true });
    }

    private async void Menu_Check_Click(object sender, RoutedEventArgs e)
    {
        if (AccountFrom(sender) is not { } account) return;
        account.Status = "Checking login…";
        try
        {
            var user = await RobloxApi.GetAuthenticatedUserAsync(account.Cookie);
            if (user == null)
            {
                account.Status = "Login expired – remove the account and add it again.";
                return;
            }
            account.Username = user.Name;
            account.DisplayName = user.DisplayName;
            account.Status = "Login is valid ✓";
            AppState.SaveAccounts();
        }
        catch (Exception ex)
        {
            account.Status = "Error: " + ex.Message;
        }
    }

    // ---------- Open browser ----------

    private void Menu_Browser_Click(object sender, RoutedEventArgs e)
    {
        if (AccountFrom(sender) is { } account) new BrowserWindow(account).Show();
    }

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        var selected = AppState.Accounts.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ShowStatus("Select an account first, then click Open browser.", true);
            return;
        }
        foreach (var account in selected.Take(4)) new BrowserWindow(account).Show();
    }

    // ---------- Drag to reorder ----------

    private Account? _dragging;
    private ListBoxItem? _dragContainer;

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement grip || grip.DataContext is not Account account) return;
        _dragging = account;
        _dragContainer = AccountList.ItemContainerGenerator.ContainerFromItem(account) as ListBoxItem;
        if (_dragContainer != null) _dragContainer.Opacity = 0.55;
        grip.CaptureMouse();
        e.Handled = true; // don't toggle the selection
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        var y = e.GetPosition(AccountList).Y;

        foreach (var item in _view.Cast<Account>())
        {
            if (item == _dragging) continue;
            if (AccountList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) continue;

            var top = container.TranslatePoint(new Point(0, 0), AccountList).Y;
            if (y < top || y > top + container.ActualHeight) continue;

            var from = AppState.Accounts.IndexOf(_dragging);
            var to = AppState.Accounts.IndexOf(item);
            var middle = top + container.ActualHeight / 2;
            // Only swap once the pointer passes the middle of the neighbouring card (prevents flicker).
            if ((to > from && y > middle) || (to < from && y < middle))
                AppState.Accounts.Move(from, to);
            break;
        }
    }

    private void Grip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging == null) return;
        (sender as UIElement)?.ReleaseMouseCapture();
        if (_dragContainer != null) _dragContainer.Opacity = 1;
        _dragging = null;
        _dragContainer = null;
        AppState.SaveAccounts();
        e.Handled = true;
    }

    // ---------- Join mode & previews ----------

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (GamePanel == null || FollowPanel == null) return; // during InitializeComponent
        var follow = FollowMode.IsChecked == true;
        GamePanel.Visibility = follow ? Visibility.Collapsed : Visibility.Visible;
        FollowPanel.Visibility = follow ? Visibility.Visible : Visibility.Collapsed;
        PanelTitle.Text = follow ? "Follow a player" : "Join a game";
    }

    private void Join_TextChanged(object sender, TextChangedEventArgs e)
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Follow_TextChanged(object sender, TextChangedEventArgs e)
    {
        _followTimer.Stop();
        _followTimer.Start();
    }

    private static ImageBrush? ImageFrom(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url);
            bitmap.DecodePixelWidth = 120;
            bitmap.EndInit();
            return new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateGamePreviewAsync()
    {
        var version = ++_previewVersion;
        var target = JoinTarget.Parse(PlaceBox.Text, ServerBox.Text, out _);
        if (target == null)
        {
            GameCard.Visibility = Visibility.Collapsed;
            return;
        }

        var placeId = target.PlaceId;
        if (target.ShareCode != null && AppState.Accounts.FirstOrDefault() is { } account)
        {
            try { placeId = (await RobloxApi.ResolveServerShareLinkAsync(account.Cookie, target.ShareCode)).PlaceId; }
            catch { /* keep the place id from the first box */ }
        }
        if (placeId <= 0 || version != _previewVersion)
        {
            if (placeId <= 0) GameCard.Visibility = Visibility.Collapsed;
            return;
        }

        var game = await RobloxApi.GetGameInfoAsync(placeId);
        if (version != _previewVersion) return;
        if (game == null)
        {
            GameCard.Visibility = Visibility.Collapsed;
            return;
        }

        GameName.Text = game.Name;
        GameCreator.Text = string.IsNullOrEmpty(game.Creator) ? "" : "by " + game.Creator;
        var server = target.ShareCode != null || target.LinkCode != null ? "PRIVATE SERVER"
            : target.JobId != null ? "SPECIFIC SERVER" : "ANY SERVER";
        GameMeta.Text = $"{game.Playing:N0} PLAYING  ·  {server}";
        GameImage.Background = ImageFrom(game.IconUrl) ?? (Brush)FindResource("Raised2Brush");
        GameCard.Visibility = Visibility.Visible;
    }

    private async Task UpdateFollowPreviewAsync()
    {
        var name = FollowBox.Text.Trim();
        _followUser = null;
        if (name.Length < 3)
        {
            UserCard.Visibility = Visibility.Collapsed;
            return;
        }

        RobloxUser? user;
        try { user = await RobloxApi.GetUserByNameAsync(name); }
        catch { user = null; }
        if (name != FollowBox.Text.Trim()) return;

        if (user == null)
        {
            UserCard.Visibility = Visibility.Visible;
            UserName.Text = "User not found";
            UserHandle.Text = "@" + name;
            UserPresence.Text = "";
            UserImage.Fill = null;
            UserDot.Fill = (Brush)FindResource("FaintBrush");
            return;
        }

        _followUser = user;
        UserName.Text = user.DisplayName;
        UserHandle.Text = "@" + user.Name;
        UserPresence.Text = "CHECKING…";
        UserCard.Visibility = Visibility.Visible;

        try
        {
            var shots = await RobloxApi.GetHeadshotsAsync(new[] { user.Id });
            UserImage.Fill = shots.TryGetValue(user.Id, out var url) ? ImageFrom(url) : null;
        }
        catch { /* cosmetic */ }

        var cookieAccount = AppState.Accounts.FirstOrDefault();
        if (cookieAccount == null)
        {
            UserPresence.Text = "ADD AN ACCOUNT TO SEE THE STATUS";
            return;
        }

        try
        {
            var presence = (await RobloxApi.GetPresenceAsync(cookieAccount.Cookie, new[] { user.Id })).FirstOrDefault();
            var type = presence?.Type ?? 0;
            UserPresence.Text = type switch
            {
                2 => string.IsNullOrEmpty(presence!.Location) ? "IN GAME" : "IN GAME · " + presence.Location.ToUpperInvariant(),
                1 => "ONLINE · NOT IN A GAME",
                3 => "IN STUDIO",
                _ => "OFFLINE",
            };
            UserDot.Fill = (Brush)FindResource(type > 0 ? "OkBrush" : "FaintBrush");
        }
        catch
        {
            UserPresence.Text = "";
        }
    }

    private void Menu_Remove_Click(object sender, RoutedEventArgs e)
    {
        if (AccountFrom(sender) is not { } account) return;
        if (!Dialog.Confirm(Owner, "Remove account",
                $"Remove {account.Title} (@{account.Username}) from Orbit?\n\nYour Roblox account itself is not affected.",
                "Remove", danger: true))
            return;

        AppState.Accounts.Remove(account);
        AppState.SaveAccounts();
    }
}
