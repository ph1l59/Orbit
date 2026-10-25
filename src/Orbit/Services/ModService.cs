using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>
/// Applies file mods to a Roblox installation. Original files are backed up and restored
/// automatically when a mod is removed.
/// </summary>
public static class ModService
{
    private const string ManifestName = ".orbit-mods.json";
    private const string BackupDirName = ".orbit-backup";

    public static string CustomFontFile => Path.Combine(AppPaths.PresetsDir, "CustomFont.ttf");
    public static string CustomCursorFile => Path.Combine(AppPaths.PresetsDir, "Cursor.png");

    private static readonly string[] CursorTargets =
    {
        @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png",
        @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png",
    };

    /// <summary>relative path inside the Roblox folder → source file (or generated content).</summary>
    private sealed class ModSet
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Generated { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static ModSet Collect(AppSettings s, string versionDir)
    {
        var set = new ModSet();

        // 1. Presets
        if (s.CustomFontEnabled && File.Exists(CustomFontFile))
        {
            set.Files[@"content\fonts\CustomFont.ttf"] = CustomFontFile;
            var families = Path.Combine(versionDir, "content", "fonts", "families");
            if (Directory.Exists(families))
            {
                foreach (var file in Directory.EnumerateFiles(families, "*.json"))
                {
                    var rewritten = RewriteFontFamily(file);
                    if (rewritten != null)
                        set.Generated[Path.GetRelativePath(versionDir, file)] = rewritten;
                }
            }
        }

        if (s.CustomCursorEnabled && File.Exists(CustomCursorFile))
            foreach (var target in CursorTargets)
                set.Files[target] = CustomCursorFile;

        // 2. The user's own mods win over presets.
        var mods = AppPaths.ModsDir;
        foreach (var file in Directory.EnumerateFiles(mods, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.')) continue;
            var relative = Path.GetRelativePath(mods, file);
            set.Generated.Remove(relative);
            set.Files[relative] = file;
        }

        return set;
    }

    private static string? RewriteFontFamily(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node?["faces"] is not JsonArray faces) return null;
            foreach (var face in faces)
                if (face is JsonObject obj) obj["assetId"] = "rbxasset://fonts/CustomFont.ttf";
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Brings the Roblox folder in line with the current mods. Returns the number of modded files.</summary>
    public static int Apply(AppSettings s, string versionDir)
    {
        var manifestPath = Path.Combine(versionDir, ManifestName);
        var backupRoot = Path.Combine(versionDir, BackupDirName);

        var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(manifestPath))
                previous.UnionWith(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(manifestPath)) ?? new());
        }
        catch { /* treat as empty */ }

        // Font family files must be generated from the ORIGINAL families, so restore everything first.
        foreach (var relative in previous)
            Restore(versionDir, backupRoot, relative);

        var set = Collect(s, versionDir);
        var applied = new List<string>();

        foreach (var relative in set.Files.Keys.Concat(set.Generated.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.Combine(versionDir, relative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var backup = Path.Combine(backupRoot, relative);
                if (File.Exists(target) && !File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup);
                }

                if (set.Generated.TryGetValue(relative, out var content))
                    File.WriteAllText(target, content);
                else
                    File.Copy(set.Files[relative], target, true);

                applied.Add(relative);
            }
            catch { /* file in use - skip */ }
        }

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(applied));
        ApplyCompatibility(s, Path.Combine(versionDir, RobloxPaths.PlayerExe));
        return applied.Count;
    }

    private static void Restore(string versionDir, string backupRoot, string relative)
    {
        var target = Path.Combine(versionDir, relative);
        var backup = Path.Combine(backupRoot, relative);
        try
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, target, true);
                File.Delete(backup);
            }
            else if (File.Exists(target))
            {
                File.Delete(target); // file only existed because of a mod
            }
        }
        catch { /* in use */ }
    }

    // ---------------- Windows compatibility settings ----------------

    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    public static void ApplyCompatibility(AppSettings s, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LayersKey, true);
            var flags = new List<string>();
            if (s.CompatHighDpi) flags.Add("HIGHDPIAWARE");
            if (s.CompatDisableFullscreenOptimizations) flags.Add("DISABLEDXMAXIMIZEDWINDOWEDMODE");

            if (flags.Count == 0) key.DeleteValue(exePath, false);
            else key.SetValue(exePath, "~ " + string.Join(" ", flags));
        }
        catch { /* registry not writable */ }
    }
}
