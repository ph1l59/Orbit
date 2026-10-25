using Microsoft.Win32;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>Registers Orbit as the handler for roblox-player:// and roblox:// links (per user).</summary>
public static class ProtocolHandler
{
    private static readonly string[] Schemes = { "roblox-player", "roblox" };

    public static string ExePath => Environment.ProcessPath ?? "";

    private static string CommandKey(string scheme) => $@"Software\Classes\{scheme}\shell\open\command";

    private static string OurCommand => $"\"{ExePath}\" \"%1\"";

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(CommandKey("roblox-player"));
            return string.Equals(key?.GetValue("") as string, OurCommand, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Register(AppSettings s)
    {
        foreach (var scheme in Schemes)
        {
            using var command = Registry.CurrentUser.OpenSubKey(CommandKey(scheme));
            var existing = command?.GetValue("") as string;
            if (existing != null && !existing.Contains("Orbit", StringComparison.OrdinalIgnoreCase))
            {
                if (scheme == "roblox-player") s.PreviousPlayerHandler = existing;
                else s.PreviousRobloxHandler = existing;
            }

            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
            root.SetValue("", $"URL: {scheme} Protocol");
            root.SetValue("URL Protocol", "");
            using var icon = root.CreateSubKey("DefaultIcon");
            icon.SetValue("", ExePath);
            using var cmd = Registry.CurrentUser.CreateSubKey(CommandKey(scheme));
            cmd.SetValue("", OurCommand);
        }
    }

    public static void Unregister(AppSettings s)
    {
        Restore("roblox-player", s.PreviousPlayerHandler);
        Restore("roblox", s.PreviousRobloxHandler);
    }

    private static void Restore(string scheme, string? previous)
    {
        using var cmd = Registry.CurrentUser.CreateSubKey(CommandKey(scheme));
        var current = cmd.GetValue("") as string;
        if (current == null || !current.Contains("Orbit", StringComparison.OrdinalIgnoreCase)) return;

        if (!string.IsNullOrEmpty(previous)) cmd.SetValue("", previous);
        else cmd.DeleteValue("", false);
    }

    /// <summary>Keeps the registered path current if the user moved Orbit.exe.</summary>
    public static void RefreshIfRegistered(AppSettings s)
    {
        if (!s.DefaultLauncher || string.IsNullOrEmpty(ExePath)) return;
        try
        {
            if (!IsRegistered) Register(s);
        }
        catch { /* ignore */ }
    }
}
