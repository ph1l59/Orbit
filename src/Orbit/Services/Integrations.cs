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
        RefreshDisconnectWatcher();
        _presenceTimer =new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) => await RefreshPresenceAsync(), ui);
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

        var appId = DiscordAppId(s);
        var wantDiscord = s.ActivityTracking && s.DiscordRichPresence;
        if (!wantDiscord || _discordAppId != appId)
        {
            var old = _discord;
            _discord = null;
            _discordAppId = null;
            if (old != null) Task.Run(old.Dispose);
        }
        if (wantDiscord && _discord == null)
        {
            _discordAppId = appId;
            _discord = new DiscordRpc(_discordAppId);
        }

        if (_watcher?.Current is { } current) _ = UpdateDiscordAsync(current);
        else ShowLauncherStatus();
    }

    // ---------------- Discord status while only Orbit is open ----------------

    private const string LogoUrl = "https://raw.githubusercontent.com/ph1l59/Orbit/main/docs/logo.png";
    private static bool _launcherOpen;
    private static DateTime _launcherSince = DateTime.UtcNow;
    private static DispatcherTimer? _launcherTimer;

    /// <summary>The Orbit window was shown (true) or hidden to the tray (false).</summary>
    public static void SetLauncherOpen(bool open)
    {
        if (open == _launcherOpen && (_launcherTimer != null || _ui == null)) return;
        if (open && !_launcherOpen) _launcherSince = DateTime.UtcNow;
        _launcherOpen = open;

        // Discord may start after Orbit – keep offering the status every few seconds while the window is open.
        if (_launcherTimer == null && _ui != null)
            _launcherTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => ShowLauncherStatus(), _ui);
        if (open) _launcherTimer?.Start();
        else _launcherTimer?.Stop();

        ShowLauncherStatus();
    }

    /// <summary>"In the launcher" with the Orbit logo while the window is open and no game is running; otherwise nothing.</summary>
    private static void ShowLauncherStatus()
    {
        var discord = _discord;
        if (discord == null || _watcher?.Current != null) return;

        object? activity = null;
        if (_launcherOpen)
        {
            activity = new Dictionary<string, object?>
            {
                ["type"] = 0,
                ["details"] = "In the launcher",
                ["timestamps"] = new { start = new DateTimeOffset(_launcherSince).ToUnixTimeSeconds() },
                ["assets"] = new Dictionary<string, string>
                {
                    ["large_image"] = LogoUrl,
                    ["large_text"] = "Orbit Launcher",
                },
                ["buttons"] = new[] { new { label = "Get Orbit Launcher", url = "https://github.com/ph1l59/Orbit" } },
            };
        }
        Task.Run(() => discord.SetActivity(activity));
    }

    /// <summary>Orbit's own Discord app ("Roblox - Orbit Launcher"), used unless the user entered their own.</summary>
    public const string DefaultDiscordAppId = "1564248525800214629";

    public static string DiscordAppId(Models.AppSettings s) =>
        string.IsNullOrWhiteSpace(s.DiscordAppId) ? DefaultDiscordAppId : s.DiscordAppId.Trim();

    // ---------------- Disconnect notification ----------------

    private static DisconnectWatcher? _disconnects;

    /// <summary>Shows a notification (title, text, picture) – set by the UI.</summary>
    public static Action<string, string, string?>? NotifyWithImage { get; set; }

    /// <summary>Starts or stops watching for lost connections according to the setting.</summary>
    public static void RefreshDisconnectWatcher()
    {
        if (AppState.Settings.NotifyDisconnect && _disconnects == null && _ui != null)
        {
            _disconnects = new DisconnectWatcher();
            _disconnects.Disconnected += info => _ui?.InvokeAsync(() => OnDisconnected(info));
        }
        else if (!AppState.Settings.NotifyDisconnect && _disconnects != null)
        {
            _disconnects.Dispose();
            _disconnects = null;
        }
    }

    private static async void OnDisconnected(DisconnectInfo info)
    {
        if (!AppState.Settings.NotifyDisconnect) return;
        var account = AppState.Accounts.FirstOrDefault(a => a.UserId == info.UserId);
        var who = account == null ? "A Roblox window" : string.IsNullOrWhiteSpace(account.Title) ? account.Username : account.Title;

        string? game = null;
        if (info.PlaceId > 0)
        {
            try { game = (await RobloxApi.GetGameInfoAsync(info.PlaceId))?.Name; }
            catch { /* name is optional */ }
        }

        var text = game == null ? $"{who} was disconnected." : $"{who} was disconnected from {game}.";
        if (info.Reason != null) text += "\n" + info.Reason;
        NotifyWithImage?.Invoke("Disconnected", text, account?.AvatarUrl);
    }

    public static void Stop()
    {
        _disconnects?.Dispose();
        _disconnects = null;
        _robloxTimer?.Stop();
        _presenceTimer?.Stop();
        _launcherTimer?.Stop();
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

    private static void OnLeft() => ShowLauncherStatus(); // back to "In the launcher" (or nothing)

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
