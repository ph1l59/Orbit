using System.IO;

namespace Orbit.Services;

/// <summary>
/// %AppData%\Orbit\game-filter.log – what the game filter, enhanced mode and digital vibrance did,
/// so problems on other PCs can be found. Always written (kept under 512 KB).
/// </summary>
public static class FilterLog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> LastOnce = new();

    public static string FilePath => Path.Combine(AppPaths.DataDir, "game-filter.log");

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                var file = FilePath;
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Writes a line at most once per <paramref name="every"/> for the same key (for things that repeat).</summary>
    public static void Sometimes(string key, TimeSpan every, string line)
    {
        lock (Gate)
        {
            if (LastOnce.TryGetValue(key, out var last) && DateTime.UtcNow - last < every) return;
            LastOnce[key] = DateTime.UtcNow;
        }
        Write(line);
    }
}
