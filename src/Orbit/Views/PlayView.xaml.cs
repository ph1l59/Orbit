using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
        _account = account;

        if (account == null)
        {
            PickerName.Text = "No account";
            PickerUser.Text = "Add one under Accounts";
            PickerAvatar.Source = null;
            NameTag.Text = "—";
            AvatarImage.Source = null;
            return;
        }

        PickerName.Text = account.Title;
        PickerUser.Text = account.Subtitle;
        PickerAvatar.Source = Load(account.AvatarUrl);
        NameTag.Text = account.Title;

        if (save)
        {
            AppState.Settings.PlayAccountId = account.UserId;
            AppState.SaveSettings();
        }

        if (changed || AvatarImage.Source == null) _ = LoadFullBodyAsync(account);
    }

    private async Task LoadFullBodyAsync(Account account)
    {
        var version = ++_avatarVersion;
        AvatarImage.Source = null;
        AvatarHint.Text = "Loading avatar…";
        AvatarHint.Visibility = Visibility.Visible;

        if (!FullBodyCache.TryGetValue(account.UserId, out var url))
        {
            url = await RobloxApi.GetFullBodyAvatarAsync(account.UserId) ?? "";
            if (url.Length > 0) FullBodyCache[account.UserId] = url;
        }
        if (version != _avatarVersion) return; // another account was picked meanwhile

        AvatarImage.Source = Load(url);
        if (AvatarImage.Source != null) AvatarHint.Visibility = Visibility.Collapsed;
        else AvatarHint.Text = "Avatar couldn't be loaded";
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

    private async void Launch_Click(object sender, RoutedEventArgs e)
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
            window.SetStatus("Starting Roblox…");
            window.HideCancel();
            var before = RobloxPaths.RunningCount();

            await RobloxLauncher.LaunchAppAsync(account, exe);
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
}
