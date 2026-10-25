using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

public partial class AccountsView : UserControl
{
    private readonly ICollectionView _view;
    private bool _launching;

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

        var target = JoinTarget.Parse(PlaceBox.Text, ServerBox.Text, out var error);
        if (target == null)
        {
            ShowStatus(error, true);
            return;
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

    private void Menu_Up_Click(object sender, RoutedEventArgs e) => Move(AccountFrom(sender), -1);
    private void Menu_Down_Click(object sender, RoutedEventArgs e) => Move(AccountFrom(sender), +1);

    private static void Move(Account? account, int direction)
    {
        if (account == null) return;
        var index = AppState.Accounts.IndexOf(account);
        var newIndex = index + direction;
        if (index < 0 || newIndex < 0 || newIndex >= AppState.Accounts.Count) return;
        AppState.Accounts.Move(index, newIndex);
        AppState.SaveAccounts();
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
