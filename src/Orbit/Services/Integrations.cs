using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>
/// Everything that runs alongside Roblox: activity tracking, server location,
/// Discord Rich Presence, custom integrations and account presence polling.
/// </summary>
public static class Integrations
{
    private static Dispatcher? _ui;
    private static ActivityWatcher? _watcher;
    private static DiscordRpc? _discord;
    private static string? _discordAppId;
    private static readonly List<Process> CustomProcesses = new();
    private static DispatcherTimer? _robloxTimer;
    private static DispatcherTimer? _presenceTimer;
    private static bool _robloxWasRunning;

    /// <summary>The account Orbit launched most recently (shown in Discord if enabled).</summary>
    public static Account? LastLaunchedAccount { get; set; }

    public static GameSession? CurrentSession => _watcher?.Current;

    /// <summary>Shows a small notification. Set by the UI.</summary>
    public static Action<string, string>? Notify { get; set; }

    /// <param name="full">False = only watch for Roblox closing (to close custom integrations).</param>
    public static void Start(Dispatcher ui, bool full = true)
    {
        _ui = ui;
        _robloxWasRunning = RobloxPaths.IsRobloxRunning();
        _robloxTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => CheckRoblox(), ui);
        _robloxTimer.Start();
        if (!full) return;

        Refresh();
        _presenceTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) => await RefreshPresenceAsync(), ui);
        _presenceTimer.Start();
        _ = RefreshPresenceAsync();
    }

    /// <summary>Re-reads settings (call after the Integrations page changed something).</summary>
    public static void Refresh()
    {
        var s = AppState.Settings;
        var wantWatcher = s.ActivityTracking;

        if (wantWatcher && _watcher == null)
        {
            _watcher = new ActivityWatcher();
            _watcher.Joined += session => _ui?.InvokeAsync(() => OnJoined(session));
            _watcher.Left += () => _ui?.InvokeAsync(OnLeft);
        }
        else if (!wantWatcher && _watcher != null)
        {
            _watcher.Dispose();
            _watcher = null;
        }

        var wantDiscord = s.ActivityTracking && s.DiscordRichPresence && !string.IsNullOrWhiteSpace(s.DiscordAppId);
        if (!wantDiscord || _discordAppId != s.DiscordAppId.Trim())
        {
            var old = _discord;
            _discord = null;
            _discordAppId = null;
            if (old != null) Task.Run(old.Dispose);
        }
        if (wantDiscord && _discord == null)
        {
            _discordAppId = s.DiscordAppId.Trim();
            _discord = new DiscordRpc(_discordAppId);
        }

        if (_watcher?.Current is { } current) _ = UpdateDiscordAsync(current);
    }

    public static void Stop()
    {
        _robloxTimer?.Stop();
        _presenceTimer?.Stop();
        _watcher?.Dispose();
        _discord?.Dispose();
        _watcher = null;
        _discord = null;
    }

    // ---------------- Game sessions ----------------

    private static bool IsPublicIp(string ip)
        => !(ip.StartsWith("10.") || ip.StartsWith("192.168.") || ip.StartsWith("127.") ||
             System.Text.RegularExpressions.Regex.IsMatch(ip, @"^172\.(1[6-9]|2\d|3[01])\."));

    private static async void OnJoined(GameSession session)
    {
        await UpdateDiscordAsync(session);

        if (AppState.Settings.ShowServerLocation && IsPublicIp(session.ServerIp))
        {
            var location = await RobloxApi.GetServerLocationAsync(session.ServerIp);
            if (location != null) Notify?.Invoke("Connected to server", "Located in " + location);
        }
    }

    private static void OnLeft()
    {
        var discord = _discord;
        if (discord != null) Task.Run(() => discord.SetActivity(null));
    }

    private static async Task UpdateDiscordAsync(GameSession session)
    {
        var discord = _discord;
        if (discord == null) return;
        var s = AppState.Settings;

        var game = await RobloxApi.GetGameInfoAsync(session.PlaceId);
        var name = game?.Name ?? "Roblox";
        var creator = string.IsNullOrEmpty(game?.Creator) ? "" : "by " + game!.Creator;

        var account = s.DiscordShowAccount ? LastLaunchedAccount : null;
        var statusType = s.DiscordStatusDisplay switch { "state" => 1, "details" => 2, _ => 0 };

        var activity = new Dictionary<string, object?>
        {
            ["type"] = 0,
            ["status_display_type"] = statusType,
            ["details"] = name,
            ["state"] = creator.Length > 0 ? creator : null,
            ["timestamps"] = new { start = new DateTimeOffset(session.Joined).ToUnixTimeSeconds() },
            ["assets"] = new Dictionary<string, string?>
            {
                ["large_image"] = game?.IconUrl ?? "roblox",
                ["large_text"] = name,
                ["small_image"] = account?.AvatarUrl,
                ["small_text"] = account != null ? "Playing as " + account.Username : null,
            }.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value),
        };

        if (activity["state"] == null) activity.Remove("state");

        if (s.DiscordAllowJoin)
        {
            activity["buttons"] = new[]
            {
                new { label = "Join server", url = $"https://www.roblox.com/games/start?placeId={session.PlaceId}&gameInstanceId={session.JobId}" },
                new { label = "See game page", url = $"https://www.roblox.com/games/{session.PlaceId}" },
            };
        }
        else
        {
            activity["buttons"] = new[] { new { label = "See game page", url = $"https://www.roblox.com/games/{session.PlaceId}" } };
        }

        await Task.Run(() => discord.SetActivity(activity));
    }

    // ---------------- Custom integrations ----------------

    public static void StartCustomIntegrations()
    {
        foreach (var integration in AppState.Settings.CustomIntegrations)
        {
            if (string.IsNullOrWhiteSpace(integration.Path) || !File.Exists(integration.Path)) continue;

            // Don't start the same program twice.
            if (CustomProcesses.Any(p => !p.HasExited && SamePath(p, integration.Path))) continue;

            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = integration.Path,
                    Arguments = integration.Arguments,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(integration.Path)!,
                });
                if (process != null && integration.CloseWithRoblox) CustomProcesses.Add(process);
            }
            catch { /* user cancelled UAC etc. */ }
        }
    }

    private static bool SamePath(Process p, string path)
    {
        try { return string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static bool HasCustomProcesses => CustomProcesses.Count > 0;

    private static void CheckRoblox()
    {
        var running = RobloxPaths.IsRobloxRunning();
        if (_robloxWasRunning && !running)
        {
            foreach (var p in CustomProcesses)
            {
                try { if (!p.HasExited) p.CloseMainWindow(); } catch { /* ignore */ }
                try { if (!p.HasExited) p.Kill(); } catch { /* ignore */ }
                p.Dispose();
            }
            CustomProcesses.Clear();
        }
        _robloxWasRunning = running;
    }

    // ---------------- Account presence ----------------

    public static async Task RefreshPresenceAsync()
    {
        var accounts = AppState.Accounts.ToList();
        if (accounts.Count == 0) return;

        foreach (var cookieAccount in accounts.Take(3))
        {
            try
            {
                var presences = await RobloxApi.GetPresenceAsync(cookieAccount.Cookie, accounts.Select(a => a.UserId));
                if (presences.Count == 0) continue;
                foreach (var account in accounts)
                {
                    var p = presences.FirstOrDefault(x => x.UserId == account.UserId);
                    if (p == null) continue;
                    account.Presence = p.Type;
                    account.PresenceLocation = p.Location;
                }
                return;
            }
            catch { /* try the next cookie */ }
        }
    }
}
