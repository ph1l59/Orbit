using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media.Media3D;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>
/// Start page: pick one of your accounts and open the Roblox app on its home page,
/// like "Launch Roblox" in Fishstrap.
/// </summary>
public partial class PlayView : UserControl, IPage
{
    private Account? _account;
    private bool _launching;
    private int _avatarVersion;
    private static readonly Dictionary<long, string> FullBodyCache = new();

    public PlayView()
    {
        InitializeComponent();
        PickerList.ItemsSource = AppState.Accounts;
        InitFriends();
        AppState.Accounts.CollectionChanged += (_, _) => Dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    public void OnShown() => Refresh();

    private Window Owner => Window.GetWindow(this)!;

    // ---------- Account ----------

    private void Refresh()
    {
        var accounts = AppState.Accounts;
        var hasAny = accounts.Count > 0;
        EmptyState.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
        Stage.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        PickerButton.IsEnabled = hasAny;

        var wanted = accounts.FirstOrDefault(a => a.UserId == AppState.Settings.PlayAccountId)
                     ?? accounts.OrderByDescending(a => a.LastUsed ?? DateTime.MinValue).FirstOrDefault();
        Select(wanted, save: false);
    }

    private void Select(Account? account, bool save)
    {
        var changed = account?.UserId != _account?.UserId;
        if (_account != null) _account.PropertyChanged -= OnAccountChanged;
        _account = account;
        if (account != null) account.PropertyChanged += OnAccountChanged;

        if (account == null)
        {
            PickerName.Text = "No account";
            PickerUser.Text = "Add one under Accounts";
            PickerAvatar.ImageSource = null;
            NameTag.Text = "—";
            AvatarImage.Source = null;
            return;
        }

        PickerName.Text = account.Title;
        PickerUser.Text = account.Subtitle;
        PickerAvatar.ImageSource = Load(account.AvatarUrl);
        NameTag.Text = account.Title;

        if (save)
        {
            AppState.Settings.PlayAccountId = account.UserId;
            AppState.SaveSettings();
        }

        if (changed || AvatarImage.Source == null) _ = LoadFullBodyAsync(account);
        if (changed && FriendsPanel.Visibility == Visibility.Visible) _ = LoadFriendsAsync(account);
    }

    /// <summary>The profile picture often arrives a moment after the page is built (right after Orbit starts).</summary>
    private void OnAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Account account || account != _account) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (e.PropertyName == nameof(Account.AvatarUrl)) PickerAvatar.ImageSource = Load(account.AvatarUrl);
            if (e.PropertyName is nameof(Account.Alias) or nameof(Account.DisplayName) or nameof(Account.Title))
            {
                PickerName.Text = account.Title;
                NameTag.Text = account.Title;
            }
        });
    }

    private async Task LoadFullBodyAsync(Account account)
    {
        var version = ++_avatarVersion;
        ShowModel(null);
        AvatarImage.Source = null;
        AvatarHint.Text = "Loading avatar…";
        AvatarHint.Visibility = Visibility.Visible;

        // The flat picture comes first (fast) ...
        if (!FullBodyCache.TryGetValue(account.UserId, out var url))
        {
            url = await RobloxApi.GetFullBodyAvatarAsync(account.UserId) ?? "";
            if (url.Length > 0) FullBodyCache[account.UserId] = url;
        }
        if (version != _avatarVersion) return; // another account was picked meanwhile
        AvatarImage.Source = Load(url);
        if (AvatarImage.Source != null) AvatarHint.Visibility = Visibility.Collapsed;

        // ... then the turnable 3D model replaces it.
        var model = await Avatar3D.LoadAsync(account.UserId, account.Cookie);
        if (version != _avatarVersion) return;
        if (model != null)
        {
            ShowModel(model);
            AvatarHint.Visibility = Visibility.Collapsed;
        }
        else if (AvatarImage.Source == null)
        {
            AvatarHint.Text = "Avatar couldn't be loaded";
        }
    }

    // ---------- 3D avatar ----------

    private AvatarModel? _model;
    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _pitch = new(new Vector3D(1, 0, 0), 0);
    private double _yawAngle, _pitchAngle, _velocity;
    private Point? _dragLast;
    private DateTime _dragTime;
    private bool _spinning;

    private void ShowModel(AvatarModel? model)
    {
        _model = model;
        if (model == null)
        {
            AvatarVisual.Content = null;
            AvatarView.Visibility = Visibility.Collapsed;
            DragSurface.Visibility = Visibility.Collapsed;
            AvatarImage.Visibility = Visibility.Visible;
            return;
        }

        var b = model.Bounds;
        var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        var front = model.Front;
        var up = new Vector3D(0, 1, 0);
        var side = Vector3D.CrossProduct(up, front);
        side.Normalize();

        // Turn around the model's own middle: left/right around "up", a little tilt around the side axis.
        _yaw.Axis = up;
        _pitch.Axis = side;
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(_yaw, center));
        transform.Children.Add(new RotateTransform3D(_pitch, center));
        AvatarVisual.Content = model.Model;
        AvatarVisual.Transform = transform;
        ResetRotation();

        // Camera in front of the avatar, far enough away to fit it (plus some room for turning).
        var size = Math.Max(b.SizeY, Math.Max(b.SizeX, b.SizeZ) * 1.15);
        var distance = size / 2 / Math.Tan(AvatarCamera.FieldOfView / 2 * Math.PI / 180) * 1.12;
        // Slightly from above, like the avatar editor.
        const double elevation = 16 * Math.PI / 180;
        AvatarCamera.Position = center + front * (distance * Math.Cos(elevation)) + up * (distance * Math.Sin(elevation));
        AvatarCamera.LookDirection = center - AvatarCamera.Position;
        AvatarCamera.NearPlaneDistance = 0.1;
        AvatarCamera.FarPlaneDistance = distance * 10;

        // Main light from the front-top-left, a purple rim light from behind.
        KeyLight.Direction = -front + new Vector3D(0, -0.55, 0) + side * 0.35;
        RimLight.Direction = front + new Vector3D(0, -0.2, 0);

        AvatarImage.Visibility = Visibility.Collapsed;
        AvatarView.Visibility = Visibility.Visible;
        DragSurface.Visibility = Visibility.Visible;
    }

    /// <summary>Start view: turned a little to the side, so the avatar looks three-dimensional right away.</summary>
    private const double DefaultYaw = -20;

    private void ResetRotation()
    {
        _yawAngle = DefaultYaw;
        _pitchAngle = _velocity = 0;
        _yaw.Angle = DefaultYaw;
        _pitch.Angle = 0;
    }

    private void Avatar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_model == null) return;
        if (e.ClickCount == 2) { ResetRotation(); return; }
        _dragLast = e.GetPosition(DragSurface);
        _dragTime = DateTime.UtcNow;
        _velocity = 0;
        DragSurface.CaptureMouse();
    }

    private void Avatar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragLast is not { } last) return;
        var now = e.GetPosition(DragSurface);
        var dx = now.X - last.X;
        var dy = now.Y - last.Y;

        _yawAngle += dx * 0.6;
        _pitchAngle = Math.Clamp(_pitchAngle + dy * 0.25, -18, 18);
        _yaw.Angle = _yawAngle;
        _pitch.Angle = _pitchAngle;

        var seconds = Math.Max(0.001, (DateTime.UtcNow - _dragTime).TotalSeconds);
        _velocity = dx * 0.6 / seconds; // degrees per second, for the spin after letting go
        _dragTime = DateTime.UtcNow;
        _dragLast = now;
    }

    private void Avatar_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragSurface.ReleaseMouseCapture();

    private void Avatar_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragLast == null) return;
        _dragLast = null;
        // A quick flick keeps turning and slows down smoothly.
        if ((DateTime.UtcNow - _dragTime).TotalMilliseconds > 80) _velocity = 0;
        _velocity = Math.Clamp(_velocity, -900, 900);
        if (Math.Abs(_velocity) > 20 && !_spinning)
        {
            _spinning = true;
            _lastFrame = DateTime.UtcNow;
            System.Windows.Media.CompositionTarget.Rendering += Spin;
        }
    }

    private DateTime _lastFrame;

    private void Spin(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (_dragLast != null || Math.Abs(_velocity) < 5 || _model == null)
        {
            _spinning = false;
            System.Windows.Media.CompositionTarget.Rendering -= Spin;
            return;
        }
        _yawAngle += _velocity * dt;
        _yaw.Angle = _yawAngle;
        _velocity *= Math.Pow(0.08, dt); // friction
    }

    private static BitmapImage? Load(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void Picker_Click(object sender, RoutedEventArgs e) => PickerPopup.IsOpen = !PickerPopup.IsOpen;

    private void PickAccount_Click(object sender, RoutedEventArgs e)
    {
        PickerPopup.IsOpen = false;
        if ((sender as FrameworkElement)?.Tag is Account account) Select(account, save: true);
    }

    private void GoToAccounts_Click(object sender, RoutedEventArgs e) => (Owner as MainWindow)?.Navigate("accounts");

    // ---------- Launch ----------

    private void Launch_Click(object sender, RoutedEventArgs e) =>
        _ = RunLaunchAsync((account, exe) => RobloxLauncher.LaunchAppAsync(account, exe), "Starting Roblox…");

    /// <summary>Shared launch flow: update Roblox, then start it the given way, with the launch window on top.</summary>
    private async Task RunLaunchAsync(Func<Account, string, Task> start, string startingText)
    {
        if (_launching || _account is not { } account) return;
        var s = AppState.Settings;

        if (!MultiInstance.IsActive && s.ConfirmLaunchWhenRunning && RobloxPaths.IsRobloxRunning())
        {
            if (!Dialog.Confirm(Owner, "Roblox is already running",
                    "Launching will close the Roblox window that is open right now. Turn on Multi-Roblox to keep several open.",
                    "Launch anyway"))
                return;
        }

        _launching = true;
        LaunchButton.IsEnabled = false;
        StatusText.Text = "";
        var window = new LaunchWindow(Owner, account.Title);
        window.Show();

        try
        {
            window.SetStatus("Checking for Roblox updates…");
            var progress = new Progress<BootstrapProgress>(p =>
            {
                window.SetStatus(p.Message);
                window.SetProgress(p.Percent);
            });
            var exe = await RobloxLauncher.PrepareAsync(s, progress);
            if (window.CancelRequested) { window.CloseSoftly(); return; }

            window.SetProgress(null);
            window.SetStatus(startingText);
            window.HideCancel();
            var before = RobloxPaths.RunningCount();

            await start(account, exe);
            Integrations.StartCustomIntegrations();
            account.LastUsed = DateTime.UtcNow;
            AppState.SaveAccounts();

            // Stay up until Roblox has really started (at most ~15 s).
            for (var i = 0; i < 30 && RobloxPaths.RunningCount() <= before; i++)
                await Task.Delay(500);
            await Task.Delay(600);
            window.CloseSoftly();
        }
        catch (Exception ex)
        {
            window.Close();
            StatusText.Text = "Couldn't start Roblox: " + ex.Message;
        }
        finally
        {
            _launching = false;
            LaunchButton.IsEnabled = true;
        }
    }

    // ---------- Friends ----------

    public sealed class FriendItem
    {
        public long Id { get; init; }
        public string Title { get; init; } = "";
        public string Name { get; init; } = "";
        public string? AvatarUrl { get; set; }
        public int Presence { get; set; } = -1;
        public string Location { get; set; } = "";
        public bool IsRequest { get; init; }

        public string StatusText => IsRequest ? "@" + Name : Presence switch
        {
            2 => string.IsNullOrWhiteSpace(Location) ? "In game" : "Playing " + Location,
            3 => "In Studio",
            1 => "Online",
            _ => "Offline",
        };

        public Brush StatusBrush => IsRequest ? Res("DimBrush") : Presence switch
        {
            2 => Res("OkBrush"),
            3 => Res("WarnBrush"),
            1 => new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
            _ => Res("FaintBrush"),
        };

        public Visibility DotVisibility => IsRequest ? Visibility.Collapsed : Visibility.Visible;
        public Visibility JoinVisibility => !IsRequest && Presence == 2 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RequestVisibility => IsRequest ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Playing first, then Studio, online, offline – alphabetically within each group.</summary>
        public int SortGroup => Presence switch { 2 => 0, 3 => 1, 1 => 2, _ => 3 };

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    }

    private List<FriendItem> _friends = new();
    private List<FriendItem> _requests = new();
    private int _friendsVersion;
    private bool _friendsLoading;
    private readonly DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private void InitFriends()
    {
        FriendsPanel.Visibility = AppState.Settings.ShowFriendsPanel ? Visibility.Visible : Visibility.Collapsed;
        _presenceTimer.Tick += async (_, _) => await RefreshPresenceAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _presenceTimer.Start();
            else _presenceTimer.Stop();
        };
    }

    private void FriendsToggle_Click(object sender, RoutedEventArgs e)
    {
        var show = FriendsPanel.Visibility != Visibility.Visible;
        FriendsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AppState.Settings.ShowFriendsPanel = show;
        AppState.SaveSettings();
        if (show && _friends.Count == 0 && _account != null) _ = LoadFriendsAsync(_account);
    }

    private void FriendsRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_account != null) _ = LoadFriendsAsync(_account);
    }

    private async Task LoadFriendsAsync(Account account)
    {
        var version = ++_friendsVersion;
        _friendsLoading = true;
        FriendsRefresh.IsEnabled = false;
        _friends = new();
        _requests = new();
        ShowFriends("Loading friends…");

        try
        {
            var friendIds = await RobloxApi.GetFriendIdsAsync(account.UserId, account.Cookie);
            List<long> requestIds;
            try { requestIds = await RobloxApi.GetFriendRequestIdsAsync(account.Cookie); }
            catch { requestIds = new(); }

            var users = await RobloxApi.GetUsersAsync(friendIds.Concat(requestIds));
            var pictures = await RobloxApi.GetHeadshotsAsync(friendIds.Concat(requestIds));
            if (version != _friendsVersion) return;

            FriendItem Make(long id, bool request)
            {
                users.TryGetValue(id, out var u);
                return new FriendItem
                {
                    Id = id,
                    Name = u?.Name ?? id.ToString(),
                    Title = string.IsNullOrWhiteSpace(u?.DisplayName) ? u?.Name ?? id.ToString() : u!.DisplayName,
                    AvatarUrl = pictures.TryGetValue(id, out var pic) ? pic : null,
                    IsRequest = request,
                };
            }

            _friends = friendIds.Select(id => Make(id, false)).ToList();
            _requests = requestIds.Select(id => Make(id, true)).ToList();
            await RefreshPresenceAsync(version);
            if (version != _friendsVersion) return;
            ShowFriends(null);
        }
        catch (Exception ex)
        {
            if (version == _friendsVersion) ShowFriends("Couldn't load friends: " + ex.Message);
        }
        finally
        {
            if (version == _friendsVersion)
            {
                _friendsLoading = false;
                FriendsRefresh.IsEnabled = true;
            }
        }
    }

    private async Task RefreshPresenceAsync(int? version = null)
    {
        if (_account is not { } account || _friends.Count == 0) return;
        if (version == null && (_friendsLoading || FriendsPanel.Visibility != Visibility.Visible)) return;
        try
        {
            var presence = await RobloxApi.GetPresenceAsync(account.Cookie, _friends.Select(f => f.Id));
            var byId = presence.ToDictionary(p => p.UserId);
            foreach (var f in _friends)
            {
                if (!byId.TryGetValue(f.Id, out var p)) continue;
                f.Presence = p.Type == 4 ? 0 : p.Type; // 4 = invisible, shown as offline
                f.Location = p.Location;
            }
            if (version == null) ShowFriends(null);
        }
        catch { /* keep the last known status */ }
    }

    /// <summary>Fills the list for the open tab and search text.</summary>
    private void ShowFriends(string? message)
    {
        var online = _friends.Count(f => f.Presence > 0);
        FriendsTabText.Text = _friends.Count == 0 ? "Friends" : $"Friends ({online}/{_friends.Count})";
        RequestsTabText.Text = _requests.Count == 0 ? "Requests" : $"Requests ({_requests.Count})";
        RequestBadge.Visibility = _requests.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RequestBadgeText.Text = _requests.Count > 99 ? "99+" : _requests.Count.ToString();

        FriendsStatus.Text = message ?? "";
        FriendsStatus.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;

        var query = FriendSearch.Text.Trim();
        var showRequests = RequestsTab.IsChecked == true;
        var source = showRequests ? _requests : _friends;
        var items = source
            .Where(f => query.Length == 0 ||
                        f.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.IsRequest ? 0 : f.SortGroup)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FriendsList.ItemsSource = null;
        FriendsList.ItemsSource = items;

        var empty = items.Count == 0 && message == null;
        FriendsEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            (FriendsEmptyTitle.Text, FriendsEmptyText.Text) = (query.Length > 0, showRequests) switch
            {
                (true, _) => ("Nobody found", "TRY ANOTHER NAME"),
                (false, true) => ("No requests", "ALL CAUGHT UP"),
                _ => ("No friends yet", "ADD SOME!"),
            };
        }
    }

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && FriendsList != null) ShowFriends(FriendsStatus.Visibility == Visibility.Visible ? FriendsStatus.Text : null);
    }

    private void FriendSearch_TextChanged(object sender, TextChangedEventArgs e) => ShowFriends(null);

    private void Friend_Click(object sender, RoutedEventArgs e)
    {
        if (_account is { } account && (sender as FrameworkElement)?.Tag is FriendItem f)
            new BrowserWindow(account, $"https://www.roblox.com/users/{f.Id}/profile").Show();
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't also open the profile
        if ((sender as FrameworkElement)?.Tag is not FriendItem f) return;
        _ = RunLaunchAsync((account, exe) => RobloxLauncher.LaunchAccountAsync(account, JoinTarget.Follow(f.Id), exe),
            $"Joining {f.Title}…");
    }

    private async void Accept_Click(object sender, RoutedEventArgs e) => await AnswerAsync(sender, e, accept: true);
    private async void Decline_Click(object sender, RoutedEventArgs e) => await AnswerAsync(sender, e, accept: false);

    private async Task AnswerAsync(object sender, RoutedEventArgs e, bool accept)
    {
        e.Handled = true;
        if (_account is not { } account || (sender as FrameworkElement)?.Tag is not FriendItem f) return;
        if (sender is Button b) b.IsEnabled = false;
        try
        {
            await RobloxApi.AnswerFriendRequestAsync(account.Cookie, f.Id, accept);
            _requests.Remove(f);
            if (accept)
            {
                _friends.Add(new FriendItem { Id = f.Id, Name = f.Name, Title = f.Title, AvatarUrl = f.AvatarUrl });
                await RefreshPresenceAsync(_friendsVersion);
            }
            ShowFriends(null);
        }
        catch (Exception ex)
        {
            if (sender is Button b2) b2.IsEnabled = true;
            ShowFriends("Couldn't answer the request: " + ex.Message);
        }
    }

    private void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        if (_account is not { } account) return;
        var query = FriendSearch.Text.Trim();
        var url = query.Length > 0
            ? "https://www.roblox.com/search/users?keyword=" + Uri.EscapeDataString(query)
            : "https://www.roblox.com/search/users";
        new BrowserWindow(account, url).Show();
    }

    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (_account is { } account) new BrowserWindow(account, "https://www.roblox.com/home").Show();
    }
}
