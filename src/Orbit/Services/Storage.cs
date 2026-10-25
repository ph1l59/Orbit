using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orbit.Models;

namespace Orbit.Services;

public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbit");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string AccountsFile => Path.Combine(DataDir, "accounts.dat");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>User mods. Files here mirror the Roblox folder structure.</summary>
    public static string ModsDir => Ensure(Path.Combine(DataDir, "Modifications"));

    /// <summary>Files for built-in presets (custom font, cursor).</summary>
    public static string PresetsDir => Ensure(Path.Combine(DataDir, "Presets"));

    /// <summary>Orbit's own Roblox installation (bootstrapper).</summary>
    public static string VersionsDir => Ensure(Path.Combine(LocalAppData, "Orbit", "Versions"));

    public static string DownloadsDir => Ensure(Path.Combine(LocalAppData, "Orbit", "Downloads"));
}

/// <summary>
/// Stores accounts encrypted with Windows DPAPI (CurrentUser scope).
/// The file can only be decrypted by the same Windows user on the same PC.
/// </summary>
public static class AccountStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Orbit.by.Floshiii.accounts.v1");
    private static readonly object Gate = new();

    public static List<Account> Load(out string? error)
    {
        error = null;
        var path = AppPaths.AccountsFile;
        if (!File.Exists(path)) return new List<Account>();

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<Account>>(plain) ?? new List<Account>();
        }
        catch (Exception ex)
        {
            // Never overwrite a file we could not read - keep a copy so nothing gets lost.
            var backup = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(path, backup, true); } catch { /* ignore */ }
            error = "Your account file could not be read (" + ex.Message + "). " +
                    "A backup copy was saved to: " + backup;
            return new List<Account>();
        }
    }

    public static void Save(IEnumerable<Account> accounts)
    {
        lock (Gate)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(accounts.ToList());
            var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            var path = AppPaths.AccountsFile;
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, path, true);
        }
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            var path = AppPaths.SettingsFile;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var path = AppPaths.SettingsFile;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, path, true);
        }
        catch { /* not critical */ }
    }
}
