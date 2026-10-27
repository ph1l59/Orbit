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
    /// <summary>False until Roblox has told Orbit the name (the row shows "Loading…" meanwhile).</summary>
    public bool HasName => !string.IsNullOrEmpty(Name);
    public string DisplayTitle => HasName ? Title : "Loading…";
    public string? AvatarUrl { get; set; }
    public int Presence { get; set; } = -1;
    public string Location { get; set; } = "";
    public bool IsRequest { get; init; }
    public bool IsSearchResult { get; init; }
    public string AddText { get; set; } = "Add";
    public bool CanAdd { get; set; } = true;

    public string StatusText => IsRequest || IsSearchResult ? (HasName ? "@" + Name : "") : Presence switch
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
    /// <summary>A friend came online (false) or started playing (true).</summary>
    public event Action<PersonItem, bool>? FriendActivity;
    /// <summary>Number of open friend requests (for the badge on the Play page).</summary>
    public event Action<int>? RequestCountChanged;
    /// <summary>Someone sent a new friend request (seen over the live connection).</summary>
    public event Action<PersonItem>? FriendRequestReceived;

    private Account? _account;
    private List<PersonItem> _friends = new();
    private List<PersonItem> _requests = new();
    private int _version;
    private bool _loading;
    private bool _privacyLoading;
    private readonly DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };

    public bool IsOpen { get; set; }

    public FriendsDrawer()
    {
        InitializeComponent();
        // Keeps running while the drawer is closed, so friend notifications work too.
        _presenceTimer.Tick += async (_, _) =>
        {
            if (IsOpen || AppState.Settings.NotifyFriendsOnline) await RefreshPresenceAsync();
        };
        _presenceTimer.Start();
        _searchDelay.Tick += async (_, _) => { _searchDelay.Stop(); await SearchUsersAsync(); };
        _liveDelay.Tick += async (_, _) => await LiveRefreshAsync();
    }

    // ---------- Live connection (friends online / requests right away; the 30 s check stays as a safety net) ----------

    private RobloxRealtime? _live;
    private readonly DispatcherTimer _liveDelay = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private bool _friendshipChanged;
    private DateTime _lastLiveRefresh = DateTime.MinValue;

    private void ConnectLive(Account? account)
    {
        var old = _live;
        _live = null;
        if (old != null) Task.Run(old.Dispose);
        LivePill.Visibility = Visibility.Collapsed;
        if (account == null || string.IsNullOrEmpty(account.Cookie)) return;

        var live = new RobloxRealtime(account.Cookie);
        live.StateChanged += connected => Dispatcher.InvokeAsync(() =>
        {
            if (_live == live) LivePill.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        });
        live.Notification += (ns, _) => Dispatcher.InvokeAsync(() =>
        {
            if (_live != live) return;
            if (ns == "FriendshipNotifications") _friendshipChanged = true;
            else if (ns != "PresenceBulkNotifications") return;
            // Several changes often come at once: wait a moment and check once.
            _liveDelay.Stop();
            _liveDelay.Start();
        });
        _live = live;
    }

    private async Task LiveRefreshAsync()
    {
        _liveDelay.Stop();
        // Not more often than every 3 seconds (Roblox limits how often Orbit may ask).
        var since = DateTime.UtcNow - _lastLiveRefresh;
        if (since < TimeSpan.FromSeconds(3))
        {
            _liveDelay.Interval = TimeSpan.FromSeconds(3) - since + TimeSpan.FromMilliseconds(100);
            _liveDelay.Start();
            return;
        }
        _liveDelay.Interval = TimeSpan.FromSeconds(1.2);
        _lastLiveRefresh = DateTime.UtcNow;

        if (_friendshipChanged)
        {
            _friendshipChanged = false;
            await ReloadQuietlyAsync();
        }
        else if (IsOpen || AppState.Settings.NotifyFriendsOnline)
        {
            await RefreshPresenceAsync();
        }
    }

    /// <summary>Friends or requests changed: update the lists without the "Loading…" screen.</summary>
    private async Task ReloadQuietlyAsync()
    {
        if (_account is not { } account || _loading) return;
        var version = _version;
        try
        {
            var friendIds = await RobloxApi.GetFriendIdsAsync(account.UserId, account.Cookie);
            List<long> requestIds;
            try { requestIds = await RobloxApi.GetFriendRequestIdsAsync(account.Cookie); }
            catch { requestIds = _requests.Select(r => r.Id).ToList(); }
            if (version != _version) return;

            var oldFriends = _friends.ToDictionary(f => f.Id);
            var oldRequests = _requests.Select(r => r.Id).ToHashSet();
            var newIds = friendIds.Concat(requestIds).Where(id => !oldFriends.ContainsKey(id) && !oldRequests.Contains(id)).Distinct().ToList();
            if (newIds.Count > 0)
            {
                try
                {
                    var users = await RobloxApi.GetUsersAsync(newIds, account.Cookie);
                    var pictures = await RobloxApi.GetHeadshotsAsync(newIds);
                    PeopleCache.Update(users, pictures);
                }
                catch { /* names are filled in later */ }
                if (version != _version) return;
            }

            _friends = friendIds.Select(id => oldFriends.TryGetValue(id, out var f) ? Refreshed(f) : Make(id, false)).ToList();
            _requests = requestIds.Select(id => Make(id, true)).ToList();
            RequestCountChanged?.Invoke(_requests.Count);
            await RefreshPresenceAsync(force: true);
            Fill(null);

            foreach (var request in _requests.Where(r => !oldRequests.Contains(r.Id)).Take(3))
                FriendRequestReceived?.Invoke(request);
        }
        catch { /* the 30 s check catches up */ }
    }

    // ---------- Account ----------

    /// <summary>Shows the friends of this account (loads in the background, also while the drawer is closed).</summary>
    public void SetAccount(Account? account)
    {
        if (account?.UserId == _account?.UserId && (_friends.Count > 0 || _loading)) return;
        if (account?.UserId != _account?.UserId) ConnectLive(account);
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
        NotifyToggle.IsChecked = AppState.Settings.NotifyFriendsOnline;
        ShowView("profile");
        _ = LoadProfileAsync(_account);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowView("list");

    private void NotifyToggle_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.NotifyFriendsOnline = NotifyToggle.IsChecked == true;
        AppState.SaveSettings();
    }

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

            var all = friendIds.Concat(requestIds).Distinct().ToList();
            if (version != _version) return;

            // 1. Show the list right away with what Orbit already knows (names from earlier visits).
            _friends = friendIds.Select(id => Make(id, false)).ToList();
            _requests = requestIds.Select(id => Make(id, true)).ToList();
            RequestCountChanged?.Invoke(_requests.Count);

            // 2. Fresh names, pictures and online status – all at the same time.
            var usersTask = RobloxApi.GetUsersAsync(all, account.Cookie);
            var picturesTask = RobloxApi.GetHeadshotsAsync(all);
            var presenceTask = RefreshPresenceAsync(force: true);
            if (_friends.Any(f => f.HasName)) Fill(null);

            Dictionary<long, FriendInfo> users;
            Dictionary<long, string> pictures;
            try { users = await usersTask; } catch { users = new(); }
            try { pictures = await picturesTask; } catch { pictures = new(); }
            await presenceTask;
            if (version != _version) return;

            PeopleCache.Update(users, pictures);
            _friends = _friends.Select(p => Refreshed(p)).ToList();
            _requests = _requests.Select(p => Refreshed(p)).ToList();
            Fill(null);

            if (_friends.Concat(_requests).Any(p => !p.HasName || p.AvatarUrl == null))
                _ = FillMissingLaterAsync(version);
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

    /// <summary>A person with the name/picture Orbit remembers (or a placeholder until Roblox answers).</summary>
    private static PersonItem Make(long id, bool request)
    {
        var known = PeopleCache.Get(id);
        return new PersonItem
        {
            Id = id,
            Name = known?.Name ?? "",
            Title = known == null ? "" : string.IsNullOrWhiteSpace(known.DisplayName) ? known.Name : known.DisplayName,
            AvatarUrl = known?.AvatarUrl,
            IsRequest = request,
        };
    }

    /// <summary>Same person with the latest cached name and picture, keeping the online status.</summary>
    private static PersonItem Refreshed(PersonItem p)
    {
        var fresh = Make(p.Id, p.IsRequest);
        if (!fresh.HasName && p.HasName) return p;
        fresh.Presence = p.Presence;
        fresh.Location = p.Location;
        fresh.AvatarUrl ??= p.AvatarUrl;
        return fresh;
    }

    /// <summary>Names/pictures that Roblox didn't deliver at first (busy right after start) are fetched again.</summary>
    private async Task FillMissingLaterAsync(int version)
    {
        var waits = new[] { 2, 4, 8, 15, 30, 60 };
        foreach (var wait in waits)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait));
            if (version != _version || _account is not { } account) return;

            var people = _friends.Concat(_requests).ToList();
            var noName = people.Where(p => !p.HasName).Select(p => p.Id).Distinct().ToList();
            var noPicture = people.Where(p => p.AvatarUrl == null).Select(p => p.Id).Distinct().ToList();
            if (noName.Count == 0 && noPicture.Count == 0) return;

            Dictionary<long, FriendInfo> users = new();
            Dictionary<long, string> pictures = new();
            try { if (noName.Count > 0) users = await RobloxApi.GetUsersAsync(noName, account.Cookie); } catch { }
            try { if (noPicture.Count > 0) pictures = await RobloxApi.GetHeadshotsAsync(noPicture); } catch { }
            if (version != _version) return;

            PeopleCache.Update(users, pictures);
            _friends = _friends.Select(p => Refreshed(p)).ToList();
            _requests = _requests.Select(p => Refreshed(p)).ToList();
            Fill(null);
        }
    }

    private async Task RefreshPresenceAsync(bool force = false)
    {
        if (_account is not { } account || _friends.Count == 0 || (_loading && !force)) return;
        try
        {
            var presence = await RobloxApi.GetPresenceAsync(account.Cookie, _friends.Select(f => f.Id));
            var byId = presence.ToDictionary(p => p.UserId);
            var news = new List<(PersonItem Friend, bool Playing)>();
            foreach (var f in _friends)
            {
                if (!byId.TryGetValue(f.Id, out var p)) continue;
                var before = f.Presence;
                f.Presence = p.Type == 4 ? 0 : p.Type; // 4 = invisible, shown as offline
                f.Location = p.Location;

                // Only real changes after the first check (before == -1 means "not known yet").
                if (before < 0) continue;
                if (f.Presence == 2 && before != 2) news.Add((f, true));
                else if (f.Presence == 1 && before == 0) news.Add((f, false));
            }
            if (!force) Fill(null);

            if (AppState.Settings.NotifyFriendsOnline)
                foreach (var (friend, playing) in news.Take(3)) // never a flood of popups
                    FriendActivity?.Invoke(friend, playing);
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

    private void Person_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Clicks on Join / Add / accept / decline are handled by those buttons.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = VisualTreeHelper.GetParent(d))
            if (d is Button) return;
        if (_account is { } account && (sender as FrameworkElement)?.Tag is PersonItem p)
            new BrowserWindow(account, $"https://www.roblox.com/users/{p.Id}/profile").Show();
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button b) b.IsEnabled = false; // one click is enough
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

        await LoadPrivacyAsync(account);
    }

    private static readonly Dictionary<string, string> SettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whoCanSeeMyOnlineStatus"] = "Who can see when I'm online",
        ["whoCanJoinMeInExperiences"] = "Who can join me in experiences",
        ["whoCanSeeMyInventory"] = "Who can see my inventory",
        ["whoCanTradeWithMe"] = "Who can trade with me",
        ["whoCanSeeMySocialNetworks"] = "Who can see my social links",
        ["whoCanChatWithMeInApp"] = "Who can chat with me in the app",
        ["whoCanChatWithMeInExperiences"] = "Who can chat with me in experiences",
        ["whoCanOneOnOnePartyWithMe"] = "Who can party with me (1 on 1)",
        ["whoCanGroupPartyWithMe"] = "Who can invite me to group parties",
        ["whoCanWhisperChatWithMeInExperiences"] = "Who can whisper to me in experiences",
        ["whoCanGroupChatWithMeInApp"] = "Who can add me to group chats",
        ["updateFriendsAboutMyActivity"] = "Tell friends what I'm playing",
    };

    private static readonly Dictionary<string, string> ValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AllUsers"] = "Everyone", ["All"] = "Everyone", ["Everyone"] = "Everyone",
        ["AllAuthenticatedUsers"] = "Everyone on Roblox", ["NoOne"] = "No one",
        ["FriendsFollowingAndFollowers"] = "Friends, following and followers",
        ["FriendsAndFollowing"] = "Friends and following", ["TopFriends"] = "Top friends",
        ["TrustedFriends"] = "Trusted friends", ["Yes"] = "Yes", ["No"] = "No",
    };

    /// <summary>"whoCanSeeMyThing" → "Who can see my thing"</summary>
    private static string Humanize(string key, Dictionary<string, string> known)
    {
        if (known.TryGetValue(key, out var name)) return name;
        var words = System.Text.RegularExpressions.Regex.Replace(key, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>Online status and join settings first, the rest after.</summary>
    private static int SettingOrder(string key) => key switch
    {
        "whoCanSeeMyOnlineStatus" => 0,
        "updateFriendsAboutMyActivity" => 1,
        "whoCanJoinMeInExperiences" => 1,
        "whoCanSeeMyInventory" => 2,
        "whoCanTradeWithMe" => 3,
        _ => 10,
    };

    private async Task LoadPrivacyAsync(Account account)
    {
        PrivacyList.Children.Clear();
        PrivacyStatus.Text = "Loading…";
        _privacyLoading = true;
        try
        {
            var settings = await RobloxApi.GetPrivacySettingsAsync(account.Cookie);
            // Settings this account can't change (Roblox offers no choices) are left out.
            foreach (var setting in settings.Where(x => x.Editable).OrderBy(x => SettingOrder(x.Key)).ThenBy(x => x.Key))
            {
                PrivacyList.Children.Add(new TextBlock
                {
                    Text = Humanize(setting.Key, SettingNames),
                    FontSize = 13.5,
                    Margin = new Thickness(0, PrivacyList.Children.Count == 0 ? 0 : 16, 0, 0),
                });
                var box = new ComboBox
                {
                    MinWidth = 0, Margin = new Thickness(0, 8, 0, 0), Tag = setting.Key, IsEnabled = setting.Editable,
                    ToolTip = setting.Editable ? null : "Roblox doesn't let this account change this setting.",
                };
                var options = setting.Options.Contains(setting.Current) ? setting.Options : setting.Options.Append(setting.Current);
                foreach (var option in options)
                    box.Items.Add(new ComboBoxItem { Content = Humanize(option, ValueNames), Tag = option });
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == setting.Current);
                box.SelectionChanged += Privacy_Changed;
                PrivacyList.Children.Add(box);
            }
            PrivacyStatus.Text = settings.Any(x => x.Editable) ? "" : "Roblox doesn't let this account change any privacy settings here.";
        }
        catch (Exception ex)
        {
            PrivacyStatus.Text = "Privacy settings couldn't be loaded: " + ex.Message;
        }
        finally
        {
            _privacyLoading = false;
        }
    }

    private async void Privacy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_privacyLoading || _account is not { } account || sender is not ComboBox { Tag: string key } box ||
            box.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        PrivacyStatus.Text = "Saving…";
        box.IsEnabled = false;
        try
        {
            if (account.BrowserTrackerId == 0)
            {
                account.BrowserTrackerId = Random.Shared.NextInt64(100_000_000_000, 999_999_999_999);
                AppState.SaveAccounts();
            }
            await RobloxApi.SetPrivacySettingAsync(account.Cookie, account.UserId, account.BrowserTrackerId, key, value);
            PrivacyStatus.Text = "Saved ✓";
        }
        catch (Exception ex)
        {
            PrivacyStatus.Text = "Couldn't save: " + ex.Message;
        }
        finally
        {
            box.IsEnabled = true;
        }
    }

    private void AllPrivacy_Click(object sender, RoutedEventArgs e)
    {
        if (_account is { } account) new BrowserWindow(account, "https://www.roblox.com/my/account#!/privacy").Show();
    }
}
