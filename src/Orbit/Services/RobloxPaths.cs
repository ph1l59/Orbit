using System.Diagnostics;
using System.IO;

namespace Orbit.Services;

/// <summary>Finds Roblox player installations (official client and Bloxstrap/Fishstrap copies).</summary>
public static class RobloxPaths
{
    public const string PlayerExe = "RobloxPlayerBeta.exe";

    private static IEnumerable<string> OfficialRoots()
    {
        yield return Path.Combine(AppPaths.LocalAppData, "Roblox", "Versions");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions");
    }

    private static IEnumerable<string> BootstrapperRoots()
    {
        yield return Path.Combine(AppPaths.LocalAppData, "Bloxstrap", "Versions");
        yield return Path.Combine(AppPaths.LocalAppData, "Fishstrap", "Versions");
    }

    private static List<string> FoldersIn(IEnumerable<string> roots)
    {
        var result = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (File.Exists(Path.Combine(dir, PlayerExe)))
                        result.Add(dir);
                }
            }
            catch { /* no access - skip */ }
        }
        return result;
    }

    /// <summary>Every folder that contains a RobloxPlayerBeta.exe.</summary>
    public static List<string> FindPlayerFolders()
        => FoldersIn(OfficialRoots().Concat(BootstrapperRoots()));

    /// <summary>The newest RobloxPlayerBeta.exe, preferring the official installation.</summary>
    public static string? FindPlayerExe()
    {
        var folders = FoldersIn(OfficialRoots());
        if (folders.Count == 0) folders = FoldersIn(BootstrapperRoots());
        if (folders.Count == 0) return null;

        return folders
            .Select(f => Path.Combine(f, PlayerExe))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .First();
    }

    public static string GlobalBasicSettingsFile
        => Path.Combine(AppPaths.LocalAppData, "Roblox", "GlobalBasicSettings_13.xml");

    public static Process[] RunningClients() => Process.GetProcessesByName("RobloxPlayerBeta");

    public static bool IsRobloxRunning()
    {
        var running = RunningClients();
        foreach (var p in running) p.Dispose();
        return running.Length > 0;
    }

    public static int CloseAllClients()
    {
        var count = 0;
        foreach (var p in RunningClients())
        {
            try { p.Kill(); count++; } catch { /* already gone */ }
            finally { p.Dispose(); }
        }
        return count;
    }
}
