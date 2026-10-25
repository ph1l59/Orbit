using System.IO;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>Removes old Roblox cache, logs and downloads to save disk space.</summary>
public static class Cleaner
{
    public static long Run(AppSettings s, bool force = false)
    {
        if (s.CleanerDays <= 0 && !force) return 0;
        var days = s.CleanerDays <= 0 ? 0 : s.CleanerDays;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        long freed = 0;

        if (s.CleanCache)
            freed += CleanFolder(Path.Combine(Path.GetTempPath(), "Roblox"), cutoff);

        if (s.CleanLogs)
            freed += CleanFolder(RobloxPaths.LogsDir, cutoff);

        if (s.CleanDownloads)
            freed += CleanFolder(AppPaths.DownloadsDir, cutoff);

        if (s.CleanOldVersions)
        {
            var current = Bootstrapper.InstalledVersionDir;
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.VersionsDir))
            {
                if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    freed += FolderSize(dir);
                    Directory.Delete(dir, true);
                }
                catch { /* in use */ }
            }
        }

        s.LastCleanup = DateTime.UtcNow;
        return freed;
    }

    /// <summary>Runs the cleaner at most once a day.</summary>
    public static void RunIfDue(AppSettings s)
    {
        if (s.CleanerDays <= 0) return;
        if (s.LastCleanup is { } last && DateTime.UtcNow - last < TimeSpan.FromDays(1)) return;
        Task.Run(() =>
        {
            try { Run(s); SettingsStore.Save(s); } catch { /* best effort */ }
        });
    }

    private static long CleanFolder(string dir, DateTime cutoff)
    {
        if (!Directory.Exists(dir)) return 0;
        long freed = 0;
        foreach (var file in SafeFiles(dir))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > cutoff) continue;
                var size = info.Length;
                info.Delete();
                freed += size;
            }
            catch { /* in use */ }
        }
        return freed;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static long FolderSize(string dir) => SafeFiles(dir).Sum(f =>
    {
        try { return new FileInfo(f).Length; } catch { return 0L; }
    });

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
