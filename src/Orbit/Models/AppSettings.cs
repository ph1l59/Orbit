namespace Orbit.Models;

public sealed class CustomIntegration
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool CloseWithRoblox { get; set; } = true;
}

/// <summary>A saved launch: which accounts and where to (Accounts page).</summary>
public sealed class LaunchPreset
{
    public string Name { get; set; } = "";
    public List<long> AccountIds { get; set; } = new();
    /// <summary>True = follow a user, false = join a game.</summary>
    public bool Follow { get; set; }
    public string Place { get; set; } = "";
    public string Server { get; set; } = "";
    public string FollowUser { get; set; } = "";
    /// <summary>Game name shown on the preset (filled in when it was saved).</summary>
    public string GameName { get; set; } = "";
}

public sealed class AppSettings
{
    // ===== Launching =====
    public bool MultiRoblox { get; set; }
    public int LaunchDelaySeconds { get; set; } = 8;
    public bool ConfirmLaunchWhenRunning { get; set; } = true;
    public string LastPlace { get; set; } = "";
    public string LastServer { get; set; } = "";
    /// <summary>Account picked on the Play page.</summary>
    public long PlayAccountId { get; set; }
    /// <summary>Show a notification when a friend comes online or starts playing.</summary>
    public bool NotifyFriendsOnline { get; set; } = true;
    /// <summary>Closing the window keeps Orbit running in the tray.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Animated stars behind the Play page.</summary>
    public bool AnimatedBackground { get; set; } = true;
    /// <summary>Show the account's alias/display name instead of "Roblox" on windows Orbit launched.</summary>
    public bool ShowAccountNameOnWindows { get; set; } = true;
    /// <summary>Give Roblox windows Orbit launched the icon from Assets\roblox-window.ico.</summary>
    public bool UseOrbitIconOnWindows { get; set; } = true;
    /// <summary>Show a notification when a Roblox window loses the connection to its game.</summary>
    public bool NotifyDisconnect { get; set; } = true;
    /// <summary>Roblox windows in the background get less CPU time than the one you're playing in.</summary>
    public bool ResourceSaver { get; set; }
    /// <summary>Hard CPU limit for Roblox windows in the background, in percent of the whole CPU (0 = off).</summary>
    public int SaverCpuCap { get; set; } = 10;
    /// <summary>Windows' efficiency mode for Roblox windows in the background.</summary>
    public bool SaverEco { get; set; } = true;

    // ===== Overview / Anti-AFK =====
    /// <summary>Tile size on the Overview page: small | medium | large.</summary>
    public string OverviewTileSize { get; set; } = "medium";
    /// <summary>Hide account names and pictures on the Overview page (screenshots, streams).</summary>
    public bool OverviewHideNames { get; set; }
    /// <summary>Presses a key in every Roblox window now and then, so Roblox doesn't kick for being idle (off by default).</summary>
    public bool AntiAfk { get; set; }
    public int AntiAfkMinutes { get; set; } = 15;
    /// <summary>background = posts the key without moving the focus; focus = brings each window to the front for a moment (only while you're away).</summary>
    public string AntiAfkMode { get; set; } = "focus";
    public List<LaunchPreset> Presets { get; set; } = new();

    // ===== Tweaks: RAM cleaner =====
    /// <summary>Clean RAM automatically.</summary>
    public bool RamAutoClean { get; set; }
    /// <summary>high = when RAM usage goes above 85 %, 30 / 60 = every 30 / 60 minutes.</summary>
    public string RamAutoMode { get; set; } = "high";
    /// <summary>Also clean Roblox windows in the background (the one you play in is never touched).</summary>
    public bool RamIncludeRoblox { get; set; } = true;
    /// <summary>Process names (without .exe) that are never cleaned.</summary>
    public List<string> RamAllowList { get; set; } = new();
    public DateTime? RamLastClean { get; set; }

    // ===== Updates =====
    /// <summary>Version whose "What's new" window was already shown.</summary>
    public string LastSeenVersion { get; set; } = "";
    /// <summary>Look for a new Orbit release on GitHub at start (and every few hours).</summary>
    public bool CheckForUpdates { get; set; } = true;

    // ===== Create accounts =====
    /// <summary>Birthday filled into Roblox's sign-up form (entered once, used for every new account).</summary>
    public DateTime? SignupBirthday { get; set; }
    /// <summary>male | female | none</summary>
    public string SignupGender { get; set; } = "none";

    // ===== Bootstrapper =====
    /// <summary>Orbit installs and updates its own copy of Roblox (like Fishstrap).</summary>
    public bool UseManagedInstall { get; set; } = true;
    /// <summary>Orbit handles roblox-player:// links from the website.</summary>
    public bool DefaultLauncher { get; set; }
    public string? PreviousPlayerHandler { get; set; }
    public string? PreviousRobloxHandler { get; set; }

    // ===== Cleaner =====
    /// <summary>0 = never, otherwise files older than this many days are removed.</summary>
    public int CleanerDays { get; set; } = 14;
    public bool CleanCache { get; set; } = true;
    public bool CleanLogs { get; set; } = true;
    public bool CleanOldVersions { get; set; } = true;
    public bool CleanDownloads { get; set; } = true;
    public DateTime? LastCleanup { get; set; }

    // ===== FastFlags =====
    public bool ManageFastFlags { get; set; } = true;
    /// <summary>-1 = off, 0..4 = mesh level-of-detail preset.</summary>
    public int MeshDetail { get; set; } = -1;
    /// <summary>-1 = automatic, otherwise 1/2/4/8.</summary>
    public int MsaaSamples { get; set; } = -1;
    public bool PreserveRenderingQuality { get; set; }
    /// <summary>0 = off, 1..21 = forced FRM quality level.</summary>
    public int FrmQuality { get; set; }
    /// <summary>auto | d3d11 | vulkan | opengl</summary>
    public string RenderingMode { get; set; } = "auto";
    /// <summary>-1 = automatic, otherwise 0..3.</summary>
    public int TextureQuality { get; set; } = -1;
    public bool ExclusiveFullscreen { get; set; }
    public bool DisableGrass { get; set; }
    public bool GraySky { get; set; }
    public bool PauseVoxelizer { get; set; }
    public Dictionary<string, string> UserFlags { get; set; } = new();

    // ===== Mods =====
    public bool CompatHighDpi { get; set; }
    public bool CompatDisableFullscreenOptimizations { get; set; }
    public bool CustomFontEnabled { get; set; }
    public bool CustomCursorEnabled { get; set; }

    // ===== Integrations =====
    public bool ActivityTracking { get; set; } = true;
    public bool ShowServerLocation { get; set; }
    public bool DiscordRichPresence { get; set; } = true;
    /// <summary>Set once Discord Rich Presence was switched on by default (so it's done only once, not after the user turned it off).</summary>
    public bool DiscordDefaultApplied { get; set; }
    public string DiscordAppId { get; set; } = "";
    /// <summary>name | state | details</summary>
    public string DiscordStatusDisplay { get; set; } = "name";
    public bool DiscordAllowJoin { get; set; }
    public bool DiscordShowAccount { get; set; }
    public List<CustomIntegration> CustomIntegrations { get; set; } = new();

    // ===== Auto clicker =====
    public int ClickerCps { get; set; } = 10;
    /// <summary>left | right | middle</summary>
    public string ClickerButton { get; set; } = "left";
    /// <summary>toggle | hold</summary>
    public string ClickerMode { get; set; } = "toggle";
    /// <summary>Virtual-key code of the hotkey (keyboard key or mouse button 4/5). 0 = no hotkey.</summary>
    public int ClickerHotkeyVk { get; set; }
    /// <summary>1 = Ctrl, 2 = Alt, 4 = Shift.</summary>
    public int ClickerHotkeyMods { get; set; }
    /// <summary>Seconds to wait after pressing Start.</summary>
    public int ClickerStartDelay { get; set; } = 3;
    public bool ClickerRobloxOnly { get; set; }
    /// <summary>cursor | fixed</summary>
    public string ClickerTarget { get; set; } = "cursor";
    public int ClickerX { get; set; }
    public int ClickerY { get; set; }
}
