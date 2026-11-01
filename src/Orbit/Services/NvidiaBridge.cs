using System.IO;
using System.Runtime.InteropServices;

namespace Orbit.Services;

/// <summary>
/// NVIDIA (game filters, overlay, Instant Replay) only knows Roblox in Roblox's own folder,
/// %LocalAppData%\Roblox\Versions\version-…. Orbit's copy of Roblox lives in its own folder, so NVIDIA
/// doesn't treat it as the game. The bridge mirrors Orbit's version folder into Roblox's folder with
/// hard links – the same files, no extra disk space, nothing is copied – and Orbit starts Roblox from there.
/// </summary>
public static class NvidiaBridge
{
    /// <summary>File that marks a version folder in Roblox's folder as Orbit's mirror.</summary>
    private const string Marker = ".orbit-bridge";

    private static string RobloxVersionsDir => Path.Combine(AppPaths.LocalAppData, "Roblox", "Versions");

    /// <summary>
    /// Mirrors <paramref name="sourceDir"/> (Orbit's version folder, with FastFlags and mods already applied)
    /// and returns the folder to start Roblox from. Falls back to the source folder if anything goes wrong.
    /// </summary>
    public static string Prepare(string sourceDir)
    {
        try
        {
            var version = Path.GetFileName(sourceDir.TrimEnd('\\', '/'));
            var target = Path.Combine(RobloxVersionsDir, version);

            // The official Roblox already has this exact version installed there: leave it alone.
            if (Directory.Exists(target) && !File.Exists(Path.Combine(target, Marker))) return sourceDir;

            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, Marker), "Mirror of Orbit's Roblox install (hard links). Safe to delete.");
            Sync(sourceDir, target);
            RemoveOldMirrors(target);
            return target;
        }
        catch (Exception ex)
        {
            Log("bridge failed, starting from Orbit's folder: " + ex.Message);
            return sourceDir;
        }
    }

    /// <summary>Makes every file of the target the same file as in the source (relinking changed ones, removing extra ones).</summary>
    private static void Sync(string source, string target)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Marker };
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            wanted.Add(relative);
            var dest = Path.Combine(target, relative);
            var src = new FileInfo(file);
            var have = new FileInfo(dest);
            // Hard links share size and time – if both match, it's already the same file.
            if (have.Exists && have.Length == src.Length && have.LastWriteTimeUtc == src.LastWriteTimeUtc) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                if (have.Exists) File.Delete(dest);
                if (!CreateHardLink(dest, file, IntPtr.Zero)) File.Copy(file, dest, true); // other drive: copy
            }
            catch { /* in use by a running Roblox – it's the same file anyway, try again next launch */ }
        }

        // Files the source doesn't have anymore (a mod was removed).
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            if (!wanted.Contains(Path.GetRelativePath(target, file)))
                try { File.Delete(file); } catch { /* in use */ }
    }

    /// <summary>Older mirrors (Roblox updated) – only folders Orbit made, never the official Roblox ones.</summary>
    private static void RemoveOldMirrors(string keep)
    {
        foreach (var dir in Directory.EnumerateDirectories(RobloxVersionsDir))
        {
            if (string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(dir, Marker))) continue;
            try { Directory.Delete(dir, true); } catch { /* still running */ }
        }
    }

    /// <summary>The setting was switched off: remove Orbit's mirrors from Roblox's folder.</summary>
    public static void RemoveAll()
    {
        try
        {
            if (!Directory.Exists(RobloxVersionsDir)) return;
            foreach (var dir in Directory.EnumerateDirectories(RobloxVersionsDir))
                if (File.Exists(Path.Combine(dir, Marker)))
                    try { Directory.Delete(dir, true); } catch { /* still running */ }
        }
        catch { /* ignore */ }
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "nvidia-bridge.log"), $"{DateTime.Now:u} {line}{Environment.NewLine}"); }
        catch { /* ignore */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
