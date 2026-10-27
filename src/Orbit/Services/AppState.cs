using System.Collections.ObjectModel;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>Shared app state: settings and the account list.</summary>
public static class AppState
{
    public static AppSettings Settings { get; private set; } = new();
    public static ObservableCollection<Account> Accounts { get; } = new();

    /// <summary>Raised when Multi-Roblox is switched on or off.</summary>
    public static event Action? MultiInstanceChanged;

    public static string? Load()
    {
        Settings = SettingsStore.Load();

        // Discord Rich Presence is on by default now – switch it on once for people who had the old default (off).
        if (!Settings.DiscordDefaultApplied)
        {
            Settings.DiscordRichPresence = true;
            Settings.DiscordDefaultApplied = true;
            SaveSettings();
        }
        var list = AccountStore.Load(out var error);
        Accounts.Clear();
        foreach (var account in list) Accounts.Add(account);
        return error;
    }

    public static void SaveSettings() => SettingsStore.Save(Settings);

    public static void SaveAccounts()
    {
        try { AccountStore.Save(Accounts); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Accounts could not be saved: " + ex.Message, "Orbit");
        }
    }

    public static void NotifyMultiInstanceChanged() => MultiInstanceChanged?.Invoke();

    private static DateTime _lastRobux = DateTime.MinValue;

    /// <summary>
    /// Checks every saved login (at start) and loads the Robux balances.
    /// Returns the accounts whose login Roblox doesn't accept anymore.
    /// </summary>
    public static async Task<List<Account>> CheckLoginsAsync()
    {
        var expired = new List<Account>();
        using var gate = new SemaphoreSlim(4);
        var tasks = Accounts.ToList().Select(async account =>
        {
            await gate.WaitAsync();
            try
            {
                var user = await RobloxApi.GetAuthenticatedUserAsync(account.Cookie);
                if (user == null)
                {
                    account.LoginExpired = true;
                    account.Status = "Login expired – click Add account and log in again.";
                    lock (expired) expired.Add(account);
                    return;
                }
                account.LoginExpired = false;
                if (account.Status.StartsWith("Login expired")) account.Status = "";
                if (!string.IsNullOrEmpty(user.Name)) account.Username = user.Name;
                if (!string.IsNullOrEmpty(user.DisplayName)) account.DisplayName = user.DisplayName;
                account.Robux = await RobloxApi.GetRobuxAsync(account.UserId, account.Cookie);
            }
            catch { /* offline or rate limited: try again next start */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        _lastRobux = DateTime.UtcNow;
        if (Accounts.Count > 0) SaveAccounts(); // names may have changed
        return expired;
    }

    /// <summary>Reloads the Robux balances (at most once a minute).</summary>
    public static async Task RefreshRobuxAsync()
    {
        if (DateTime.UtcNow - _lastRobux < TimeSpan.FromMinutes(1)) return;
        _lastRobux = DateTime.UtcNow;
        foreach (var account in Accounts.Where(a => !a.LoginExpired).ToList())
        {
            try { account.Robux = await RobloxApi.GetRobuxAsync(account.UserId, account.Cookie) ?? account.Robux; }
            catch { /* keep the old number */ }
        }
    }

    public static async Task RefreshAvatarsAsync()
    {
        try
        {
            var urls = await RobloxApi.GetHeadshotsAsync(Accounts.Select(a => a.UserId));
            foreach (var account in Accounts)
                if (urls.TryGetValue(account.UserId, out var url))
                    account.AvatarUrl = url;
        }
        catch { /* avatars are cosmetic */ }
    }
}
