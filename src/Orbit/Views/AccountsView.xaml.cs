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
        JobBox.Text = AppState.Settings.LastJobId;

        AppState.Accounts.CollectionChanged += OnAccountsChanged;
        foreach (var account in AppState.Accounts) account.PropertyChanged += OnAccountPropertyChanged;
        UpdateCounts();
    }

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
        if (e.PropertyName == nameof(Account.IsSelected)) UpdateCounts();
    }

    private void UpdateCounts()
    {
        var total = AppState.Accounts.Count;
        var selected = AppState.Accounts.Count(a => a.IsSelected);

        CountText.Text = total == 1 ? "1 Account" : $"{total} Accounts";
        if (selected > 0) CountText.Text += $"  ·  {selected} ausgewählt";

        LaunchText.Text = selected switch
        {
            0 => "Starten",
            1 => "1 Account starten",
            _ => $"{selected} Accounts starten",
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
        var login = new LoginWindow { Owner = Window.GetWindow(this) };
        if (login.ShowDialog() != true || login.Result is not { } user) return;

        var existing = AppState.Accounts.FirstOrDefault(a => a.UserId == user.UserId);
        if (existing != null)
        {
            existing.Cookie = user.Cookie;
            existing.Username = user.Username;
            existing.DisplayName = user.DisplayName;
            existing.Status = "Login aktualisiert ✓";
        }
        else
        {
            AppState.Accounts.Add(user);
        }

        AppState.SaveAccounts();
        await AppState.RefreshAvatarsAsync();
    }

    // ---------- Launch ----------

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_launching) return;

        var accounts = AppState.Accounts.Where(a => a.IsSelected).ToList();
        if (accounts.Count == 0)
        {
            ShowStatus("Wähle links mindestens einen Account aus.", true);
            return;
        }

        var target = JoinTarget.Parse(PlaceBox.Text, JobBox.Text, out var error);
        if (target == null)
        {
            ShowStatus(error, true);
            return;
        }

        if (accounts.Count > 1 && !MultiInstance.IsActive)
        {
            var answer = MessageBox.Show(Window.GetWindow(this)!,
                "Multi-Roblox ist ausgeschaltet. Ohne Multi-Roblox schließt jeder neue Account das vorherige Roblox-Fenster.\n\n" +
                "Multi-Roblox jetzt aktivieren?\n(Dafür müssen alle Roblox-Fenster geschlossen sein.)",
                "Orbit", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                if (MultiInstance.TryEnable(out var multiError))
                {
                    AppState.Settings.MultiRoblox = true;
                    AppState.SaveSettings();
                    AppState.NotifyMultiInstanceChanged();
                }
                else
                {
                    ShowStatus(multiError ?? "Multi-Roblox konnte nicht aktiviert werden.", true);
                    return;
                }
            }
        }

        AppState.Settings.LastPlace = PlaceBox.Text.Trim();
        AppState.Settings.LastJobId = JobBox.Text.Trim();
        AppState.SaveSettings();

        _launching = true;
        LaunchButton.IsEnabled = false;
        try
        {
            // Performance settings are written once before the clients start.
            var applied = FlagService.ApplyAll(AppState.Settings);
            if (!applied.FpsOk) ShowStatus(applied.FpsMessage, true);

            var delay = Math.Clamp(AppState.Settings.LaunchDelaySeconds, 1, 120);
            for (var i = 0; i < accounts.Count; i++)
            {
                var account = accounts[i];
                account.Status = "Wird gestartet…";
                ShowStatus($"Starte {account.Title} ({i + 1}/{accounts.Count})…", false);

                try
                {
                    await RobloxLauncher.LaunchAsync(account, target, AppState.Settings);
                    account.Status = "";
                    account.LastUsed = DateTime.UtcNow;
                    AppState.SaveAccounts();
                }
                catch (Exception ex)
                {
                    account.Status = "Fehler: " + ex.Message;
                }

                if (i < accounts.Count - 1)
                {
                    for (var s = delay; s > 0; s--)
                    {
                        ShowStatus($"Nächster Account in {s} s…", false);
                        await Task.Delay(1000);
                    }
                }
            }

            var failed = accounts.Count(a => a.Status.StartsWith("Fehler"));
            ShowStatus(failed == 0
                ? (accounts.Count == 1 ? "Roblox wird gestartet ✓" : $"{accounts.Count} Accounts gestartet ✓")
                : $"{accounts.Count - failed} gestartet, {failed} fehlgeschlagen – Details stehen beim Account.", failed > 0);
        }
        finally
        {
            _launching = false;
            LaunchButton.IsEnabled = true;
        }
    }

    private void ShowStatus(string text, bool isWarning)
    {
        LaunchStatus.Text = text;
        LaunchStatus.Foreground = (System.Windows.Media.Brush)FindResource(isWarning ? "WarningBrush" : "SubTextBrush");
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
        var alias = InputDialog.Ask(Window.GetWindow(this)!, "Alias festlegen",
            "Eigener Name, der statt des Anzeigenamens gezeigt wird (leer lassen zum Entfernen):", account.Alias);
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
        account.Status = "Prüfe Login…";
        try
        {
            var user = await RobloxApi.GetAuthenticatedUserAsync(account.Cookie);
            if (user == null)
            {
                account.Status = "Login abgelaufen – bitte den Account neu hinzufügen.";
                return;
            }
            account.Username = user.Name;
            account.DisplayName = user.DisplayName;
            account.Status = "Login gültig ✓";
            AppState.SaveAccounts();
        }
        catch (Exception ex)
        {
            account.Status = "Fehler: " + ex.Message;
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
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{account.Title} (@{account.Username}) aus Orbit entfernen?\n\nDein Roblox-Account selbst bleibt natürlich bestehen.",
            "Account entfernen", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        AppState.Accounts.Remove(account);
        AppState.SaveAccounts();
    }
}
