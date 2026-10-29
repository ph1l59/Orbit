using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbit.Services;

/// <summary>Finds Roblox player installations (Orbit's own, the official client, Bloxstrap/Fishstrap copies).</summary>
public static class RobloxPaths
{
    public const string PlayerExe = "RobloxPlayerBeta.exe";

    private static IEnumerable<string> OfficialRoots()
    {
        yield return Path.Combine(AppPaths.LocalAppData, "Roblox", "Versions");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions");
    }

    private static IEnumerable<string> OtherRoots()
    {
        yield return AppPaths.VersionsDir;
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
    public static List<string> FindPlayerFolders() => FoldersIn(OfficialRoots().Concat(OtherRoots()));

    /// <summary>The newest official RobloxPlayerBeta.exe, or null.</summary>
    public static string? FindOfficialPlayerExe()
    {
        var folders = FoldersIn(OfficialRoots());
        if (folders.Count == 0) return null;
        return folders
            .Select(f => Path.Combine(f, PlayerExe))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .First();
    }

    public static string RobloxDataDir => Path.Combine(AppPaths.LocalAppData, "Roblox");
    public static string LogsDir => Path.Combine(RobloxDataDir, "logs");
    public static string GlobalBasicSettingsFile => Path.Combine(RobloxDataDir, "GlobalBasicSettings_13.xml");

    public static Process[] RunningClients() => Process.GetProcessesByName("RobloxPlayerBeta");

    // ---------- Cheap list of running Roblox processes ----------
    // Listing processes means Windows hands over every process on the PC – too slow to do on the UI thread
    // every second. So the ids are fetched on a background thread and are at most about a second old.

    private static int[] _ids = Array.Empty<int>();
    private static long _idsAt;
    private static int _idsRefreshing;

    /// <summary>Process ids of the running Roblox windows (refreshed in the background, at most ~1 s old).</summary>
    public static int[] ClientIds()
    {
        var at = Interlocked.Read(ref _idsAt);
        if (at == 0) RefreshIds(); // very first call: wait for it once
        else if (Environment.TickCount64 - at > 900 && Interlocked.Exchange(ref _idsRefreshing, 1) == 0)
            Task.Run(() =>
            {
                try { RefreshIds(); }
                finally { Volatile.Write(ref _idsRefreshing, 0); }
            });
        return Volatile.Read(ref _ids);
    }

    /// <summary>Something started or closed Roblox: fetch the list again on the next call.</summary>
    public static void InvalidateIds() => Interlocked.Exchange(ref _idsAt, 1);

    private static void RefreshIds()
    {
        var running = RunningClients();
        var ids = new int[running.Length];
        for (var i = 0; i < running.Length; i++) { ids[i] = running[i].Id; running[i].Dispose(); }
        Volatile.Write(ref _ids, ids);
        Interlocked.Exchange(ref _idsAt, Environment.TickCount64);
    }

    /// <summary>True if this process is a Roblox window – asks only for its file name, not the whole process list.</summary>
    public static bool IsRobloxProcess(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(1024);
            var size = name.Capacity;
            return QueryFullProcessImageName(handle, 0, name, ref size) &&
                   Path.GetFileName(name.ToString()).Equals(PlayerExe, StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    public static int RunningCount()
    {
        var running = RunningClients();
        foreach (var p in running) p.Dispose();
        return running.Length;
    }

    public static bool IsRobloxRunning() => RunningCount() > 0;

    public static int CloseAllClients()
    {
        var count = 0;
        foreach (var p in RunningClients())
        {
            try { p.Kill(); count++; } catch { /* already gone */ }
            finally { p.Dispose(); }
        }
        InvalidateIds();
        return count;
    }
}
