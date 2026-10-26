using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>One person in the drawer: a friend, someone who sent a request, or a search result.</summary>
public sealed class PersonItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Name { get; init; } = "";
    public string? AvatarUrl { get; set; }
    public int Presence { get; set; } = -1;
    public string Location { get; set; } = "";
    public bool IsRequest { get; init; }
    public bool IsSearchResult { get; init; }
    public string AddText { get; set; } = "Add";
    public bool CanAdd { get; set; } = true;

    public string StatusText => IsRequest || IsSearchResult ? "@" + Name : Presence switch
    {
        2 => string.IsNullOrWhiteSpace(Location) ? "In game" : "Playing " + Location,
        3 => "In Studio",
        1 => "Online",
        _ => "Offline",
    };

    public Brush StatusBrush => IsRequest || IsSearchResult ? Res("DimBrush") : Presence switch
    {
        2 => Res("OkBrush"),
        3 => Res("WarnBrush"),
        1 => OnlineBrush,
        _ => Res("FaintBrush"),
    };

    public Visibility DotVisibility => IsRequest || IsSearchResult ? Visibility.Collapsed : Visibility.Visible;
    public Visibility JoinVisibility => !IsRequest && !IsSearchResult && Presence == 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RequestVisibility => IsRequest ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Playing first, then Studio, online, offline.</summary>
    public int SortGroup => Presence switch { 2 => 0, 3 => 1, 1 => 2, _ => 3 };

    public static readonly Brush OnlineBrush = Frozen(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}

/// <summary>
/// Friends drawer that slides in over the whole window: friends with online status and Join,
/// friend requests, adding friends, and the account's own profile and privacy settings.
/// </summary>
public partial class FriendsDrawer : UserControl
{
    public event Action? CloseRequested;
    public event Action<PersonItem>? JoinRequested;
    /// <summary>Number of open friend requests (for the badge on the Play page).</summary>
    public event Action<int>? RequestCountChanged;

    private Account? _account;
    private List<PersonItem> _friends = new();
    private List<PersonItem> _requests = new();
    private int _version;
    private bool _loading;
    private bool _privacyLoading;
    private readonly DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private static readonly (string Value, string Label)[] InventoryOptions =
    {
        ("AllUsers", "Everyone"), ("AllAuthenticatedUsers", "Everyone on Roblox"),
        ("FriendsFollowingAndFollowers", "Friends, following and followers"), ("FriendsAndFollowing", "Friends and following"),
        ("Friends", "Friends"), ("NoOne", "No one"),
    };

    private static readonly (string Value, string Label)[] TradeOptions =
    {
        ("All", "Everyone"), ("Followers", "Followers"), ("Following", "Following"), ("Friends", "Friends"),
        ("TopFriends", "Top friends"), ("NoOne", "No one"),
    };

    public bool IsOpen { get; set; }

    public FriendsDrawer()
    {
        InitializeComponent();
        foreach (var o in InventoryOptions) InventoryPrivacy.Items.Add(new ComboBoxItem { Content = o.Label, Tag = o.Value });
        foreach (var o in TradeOptions) TradePrivacy.Items.Add(new ComboBoxItem { Content = o.Label, Tag = o.Value });
        _presenceTimer.Tick += async (_, _) => { if (IsOpen) await RefreshPresenceAsync(); };
        _presenceTimer.Start();
        _searchDelay.Tick += async (_, _) => { _searchDelay.Stop(); await SearchUsersAsync(); };
    }

    // ---------- Account ----------

    /// <summary>Shows the friends of this account (loads in the background, also while the drawer is closed).</summary>
    public void SetAccount(Account? account)
    {
        if (account?.UserId == _account?.UserId && _friends.Count > 0) return;
        _account = account;
        AccountLine.Text = account == null ? "" : $"AS {account.Title.ToUpperInvariant()} · @{account.Username.ToUpperInvariant()}";
        ShowView("list");
        if (account != null) _ = LoadAsync(account);
        else
        {
            _friends = new();
            _requests = new();
            Fill(null);
        }
    }

    /// <summary>Called when the drawer opens: refresh the online status.</summary>
    public void Opened()
    {
        IsOpen = true;
        if (!_loading) _ = RefreshPresenceAsync();
    }

    // ---------- Header ----------

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_account == null) return;
        if (ProfilePage.Visibility == Visibility.Visible) _ = LoadProfileAsync(_account);
        else if (AddPage.Visibility == Visibility.Visible) _ = SearchUsersAsync();
        else _ = LoadAsync(_account);
    }

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        if (_account == null) return;
        ShowView("profile");
        _ = LoadProfileAsync(_account);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowView("list");

    private void ShowView(string view)
    {
        ListPage.Visibility = view == "list" ? Visibility.Visible : Visibility.Collapsed;
        AddPage.Visibility = view == "add" ? Visibility.Visible : Visibility.Collapsed;
        ProfilePage.Visibility = view == "profile" ? Visibility.Visible : Visibility.Collapsed;
        GearButton.Visibility = view == "list" ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = view == "list" ? Visibility.Collapsed : Visibility.Visible;
        HeaderTitle.Text = view switch { "add" => "ADD FRIEND", "profile" => "SETTINGS", _ => "FRIENDS" };
    }

    // ---------- Friends + requests ----------

    private async Task LoadAsync(Account account)
    {
        var version = ++_version;
        _loading = true;
        RefreshButton.IsEnabled = false;
        _friends = new();
        _requests = new();
        Fill("Loading friends…");

        try
        {
            var friendIds = await RobloxApi.GetFriendIdsAsync(account.UserId, account.Cookie);
            List<long> requestIds;
            try { requestIds = await RobloxApi.GetFriendRequestIdsAsync(account.Cookie); }
            catch { requestIds = new(); }

            var all = friendIds.Concat(requestIds).ToList();
            var users = await RobloxApi.GetUsersAsync(all);
            var pictures = await RobloxApi.GetHeadshotsAsync(all);
            if (version != _version) return;

            PersonItem Make(long id, bool request)
            {
                users.TryGetValue(id, out var u);
                return new PersonItem
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
            RequestCountChanged?.Invoke(_requests.Count);
            await RefreshPresenceAsync(force: true);
            if (version == _version) Fill(null);
        }
        catch (Exception ex)
        {
            if (version == _version) Fill("Couldn't load friends: " + ex.Message);
        }
        finally
        {
            if (version == _version)
            {
                _loading = false;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private async Task RefreshPresenceAsync(bool force = false)
    {
        if (_account is not { } account || _friends.Count == 0 || (_loading && !force)) return;
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
            if (!force) Fill(null);
        }
        catch { /* keep the last known status */ }
    }

    /// <summary>Fills the list for the open tab and search text.</summary>
    private void Fill(string? message)
    {
        var online = _friends.Count(f => f.Presence > 0);
        FriendsTabText.Text = _friends.Count == 0 ? "Friends" : $"Friends ({online}/{_friends.Count})";
        RequestsTabText.Text = _requests.Count == 0 ? "Requests" : $"Requests ({_requests.Count})";

        ListStatus.Text = message ?? "";
        ListStatus.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;

        var query = FriendSearch.Text.Trim();
        var showRequests = RequestsTab.IsChecked == true;
        var items = (showRequests ? _requests : _friends)
            .Where(f => query.Length == 0 ||
                        f.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.IsRequest ? 0 : f.SortGroup)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FriendsList.ItemsSource = null;
        FriendsList.ItemsSource = items;

        var empty = items.Count == 0 && message == null;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            (EmptyTitle.Text, EmptyText.Text) = (query.Length > 0, showRequests) switch
            {
                (true, _) => ("Nobody found", "TRY ANOTHER NAME"),
                (false, true) => ("No requests", "ALL CAUGHT UP"),
                _ => ("No friends yet", "ADD SOME!"),
            };
        }
    }

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && FriendsList != null) Fill(ListStatus.Visibility == Visibility.Visible ? ListStatus.Text : null);
    }

    private void FriendSearch_TextChanged(object sender, TextChangedEventArgs e) => Fill(null);

    private void Person_Click(object sender, RoutedEventArgs e)
    {
        if (_account is { } account && (sender as FrameworkElement)?.Tag is PersonItem p)
            new BrowserWindow(account, $"https://www.roblox.com/users/{p.Id}/profile").Show();
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't also open the profile
        if ((sender as FrameworkElement)?.Tag is PersonItem p) JoinRequested?.Invoke(p);
    }

    private async void Accept_Click(object sender, RoutedEventArgs e) => await AnswerAsync(sender, e, accept: true);
    private async void Decline_Click(object sender, RoutedEventArgs e) => await AnswerAsync(sender, e, accept: false);

    private async Task AnswerAsync(object sender, RoutedEventArgs e, bool accept)
    {
        e.Handled = true;
        if (_account is not { } account || (sender as FrameworkElement)?.Tag is not PersonItem p) return;
        if (sender is Button b) b.IsEnabled = false;
        try
        {
            await RobloxApi.AnswerFriendRequestAsync(account.Cookie, p.Id, accept);
            _requests.Remove(p);
            RequestCountChanged?.Invoke(_requests.Count);
            if (accept)
            {
                _friends.Add(new PersonItem { Id = p.Id, Name = p.Name, Title = p.Title, AvatarUrl = p.AvatarUrl });
                await RefreshPresenceAsync(force: true);
            }
            Fill(null);
        }
        catch (Exception ex)
        {
            if (sender is Button b2) b2.IsEnabled = true;
            Fill("Couldn't answer the request: " + ex.Message);
        }
    }

    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (_account is { } account) new BrowserWindow(account, "https://www.roblox.com/home").Show();
    }

    // ---------- Add friend ----------

    private void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        ShowView("add");
        UserSearch.Text = FriendSearch.Text.Trim();
        SearchList.ItemsSource = null;
        AddStatus.Text = UserSearch.Text.Length >= 3 ? "" : "Type at least 3 characters.";
        if (UserSearch.Text.Length >= 3) _ = SearchUsersAsync();
        Dispatcher.InvokeAsync(() => { UserSearch.Focus(); UserSearch.CaretIndex = UserSearch.Text.Length; }, DispatcherPriority.Input);
    }

    private void UserSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void UserSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _searchDelay.Stop();
        _ = SearchUsersAsync();
    }

    private async Task SearchUsersAsync()
    {
        if (_account is not { } account) return;
        var query = UserSearch.Text.Trim();
        if (query.Length < 3)
        {
            SearchList.ItemsSource = null;
            AddStatus.Text = "Type at least 3 characters.";
            return;
        }

        AddStatus.Text = "Searching…";
        try
        {
            var users = await RobloxApi.SearchUsersAsync(query, account.Cookie);
            if (UserSearch.Text.Trim() != query) return; // typed on meanwhile
            var pictures = await RobloxApi.GetHeadshotsAsync(users.Select(u => u.Id));
            var friendIds = _friends.Select(f => f.Id).ToHashSet();

            var items = users.Select(u =>
            {
                var self = u.Id == account.UserId;
                var friend = friendIds.Contains(u.Id);
                return new PersonItem
                {
                    Id = u.Id,
                    Name = u.Name,
                    Title = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Name : u.DisplayName,
                    AvatarUrl = pictures.TryGetValue(u.Id, out var pic) ? pic : null,
                    IsSearchResult = true,
                    AddText = self ? "You" : friend ? "Friends" : "Add",
                    CanAdd = !self && !friend,
                };
            }).ToList();

            SearchList.ItemsSource = items;
            AddStatus.Text = items.Count == 0 ? "Nobody found." : "";
        }
        catch (Exception ex)
        {
            AddStatus.Text = "Search failed: " + ex.Message;
        }
    }

    private async void SendRequest_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_account is not { } account || (sender as FrameworkElement)?.Tag is not PersonItem p) return;
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;

        var result = await RobloxApi.SendFriendRequestAsync(account.Cookie, p.Id);
        switch (result)
        {
            case FriendRequestResult.Sent:
                if (button != null) button.Content = "Sent";
                AddStatus.Text = $"Friend request sent to {p.Title}.";
                break;
            case FriendRequestResult.AlreadyFriends:
                if (button != null) button.Content = "Friends";
                AddStatus.Text = $"You're already friends with {p.Title} (or a request is open).";
                break;
            case FriendRequestResult.NeedsCheck:
                if (button != null) button.IsEnabled = true;
                AddStatus.Text = "Roblox wants a quick check first – finish it on the profile that just opened.";
                new BrowserWindow(account, $"https://www.roblox.com/users/{p.Id}/profile").Show();
                break;
            default:
                if (button != null) button.IsEnabled = true;
                AddStatus.Text = "Couldn't send the request. Try again in a moment.";
                break;
        }
    }

    // ---------- Your profile + privacy ----------

    private async Task LoadProfileAsync(Account account)
    {
        ProfileName.Text = account.Title;
        ProfileUser.Text = "@" + account.Username.ToUpperInvariant();
        ProfileAvatar.ImageSource = string.IsNullOrEmpty(account.AvatarUrl) ? null : new System.Windows.Media.Imaging.BitmapImage(new Uri(account.AvatarUrl));
        ProfileStatus.Text = "Checking status…";
        PrivacyStatus.Text = "";

        try
        {
            var presenceTask = RobloxApi.GetPresenceAsync(account.Cookie, new[] { account.UserId });
            var profileTask = RobloxApi.GetProfileAsync(account.UserId);
            var countsTask = RobloxApi.GetCountsAsync(account.UserId);

            var presence = (await presenceTask).FirstOrDefault();
            var me = new PersonItem { Presence = presence?.Type == 4 ? 0 : presence?.Type ?? 0, Location = presence?.Location ?? "" };
            ProfileStatus.Text = me.StatusText;
            ProfileStatus.Foreground = me.StatusBrush;
            ProfileDot.Fill = me.StatusBrush;

            var profile = await profileTask;
            ProfileJoined.Text = profile is { Created.Year: > 2000 } ? "ON ROBLOX SINCE " + profile.Created.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant() : "";
            ProfileAbout.Text = profile?.Description.Trim() ?? "";
            ProfileAbout.Visibility = string.IsNullOrWhiteSpace(ProfileAbout.Text) ? Visibility.Collapsed : Visibility.Visible;

            var (friends, followers, following) = await countsTask;
            CountFriends.Text = friends < 0 ? "–" : friends.ToString("N0");
            CountFollowers.Text = followers < 0 ? "–" : followers.ToString("N0");
            CountFollowing.Text = following < 0 ? "–" : following.ToString("N0");
        }
        catch
        {
            ProfileStatus.Text = "Status unavailable";
        }

        _privacyLoading = true;
        try
        {
            Select(InventoryPrivacy, await RobloxApi.GetPrivacyAsync(account.Cookie, "inventory-privacy", "inventoryPrivacy"));
            Select(TradePrivacy, await RobloxApi.GetPrivacyAsync(account.Cookie, "trade-privacy", "tradePrivacy"));
        }
        catch
        {
            PrivacyStatus.Text = "Privacy settings couldn't be loaded.";
        }
        finally
        {
            _privacyLoading = false;
        }
    }

    private static void Select(ComboBox box, string? value)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value);
        box.IsEnabled = value != null;
    }

    private async void Privacy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_privacyLoading || _account is not { } account || sender is not ComboBox box || box.SelectedItem is not ComboBoxItem item) return;
        var (endpoint, field) = box == InventoryPrivacy ? ("inventory-privacy", "inventoryPrivacy") : ("trade-privacy", "tradePrivacy");
        PrivacyStatus.Text = "Saving…";
        try
        {
            await RobloxApi.SetPrivacyAsync(account.Cookie, endpoint, field, (string)item.Tag);
            PrivacyStatus.Text = "Saved ✓";
        }
        catch (Exception ex)
        {
            PrivacyStatus.Text = "Couldn't save: " + ex.Message;
        }
    }

    private void AllPrivacy_Click(object sender, RoutedEventArgs e)
    {
        if (_account is { } account) new BrowserWindow(account, "https://www.roblox.com/my/account#!/privacy").Show();
    }
}
