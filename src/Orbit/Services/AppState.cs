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
            System.Windows.MessageBox.Show("Accounts konnten nicht gespeichert werden: " + ex.Message, "Orbit");
        }
    }

    public static void NotifyMultiInstanceChanged() => MultiInstanceChanged?.Invoke();

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
