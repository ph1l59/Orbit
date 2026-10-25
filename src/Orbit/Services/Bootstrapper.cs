using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Orbit.Services;

public readonly record struct BootstrapProgress(string Message, double Percent);

/// <summary>
/// Installs and updates Orbit's own copy of the Roblox player from Roblox's CDN,
/// the same way Bloxstrap and Fishstrap do. Lives in %LocalAppData%\Orbit\Versions.
/// </summary>
public static class Bootstrapper
{
    private const string Cdn = "https://setup.rbxcdn.com/";
    private const string CompleteMarker = ".orbit-complete";

    /// <summary>Which folder each Roblox package is extracted to.</summary>
    private static readonly Dictionary<string, string> PackageDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RobloxApp.zip"] = "",
        ["redist.zip"] = "",
        ["shaders.zip"] = @"shaders\",
        ["ssl.zip"] = @"ssl\",
        ["WebView2.zip"] = "",
        ["WebView2RuntimeInstaller.zip"] = @"WebView2RuntimeInstaller\",
        ["content-avatar.zip"] = @"content\avatar\",
        ["content-configs.zip"] = @"content\configs\",
        ["content-fonts.zip"] = @"content\fonts\",
        ["content-sky.zip"] = @"content\sky\",
        ["content-sounds.zip"] = @"content\sounds\",
        ["content-textures2.zip"] = @"content\textures\",
        ["content-models.zip"] = @"content\models\",
        ["content-platform-fonts.zip"] = @"PlatformContent\pc\fonts\",
        ["content-platform-dictionaries.zip"] = @"PlatformContent\pc\shared_compression_dictionaries\",
        ["content-terrain.zip"] = @"PlatformContent\pc\terrain\",
        ["content-textures3.zip"] = @"PlatformContent\pc\textures\",
        ["extracontent-luapackages.zip"] = @"ExtraContent\LuaPackages\",
        ["extracontent-translations.zip"] = @"ExtraContent\translations\",
        ["extracontent-models.zip"] = @"ExtraContent\models\",
        ["extracontent-textures.zip"] = @"ExtraContent\textures\",
        ["extracontent-places.zip"] = @"ExtraContent\places\",
    };

    private const string AppSettingsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
        "<Settings>\r\n" +
        "\t<ContentFolder>content</ContentFolder>\r\n" +
        "\t<BaseUrl>http://www.roblox.com</BaseUrl>\r\n" +
        "</Settings>\r\n";

    private static readonly SemaphoreSlim Lock = new(1, 1);

    private sealed record Package(string Name, string Md5, long Size);

    /// <summary>The installed managed version folder, or null.</summary>
    public static string? InstalledVersionDir
    {
        get
        {
            try
            {
                return Directory.EnumerateDirectories(AppPaths.VersionsDir)
                    .Where(d => File.Exists(Path.Combine(d, CompleteMarker)) && File.Exists(Path.Combine(d, RobloxPaths.PlayerExe)))
                    .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, CompleteMarker)))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }

    public static string? InstalledVersion => InstalledVersionDir is { } dir ? Path.GetFileName(dir) : null;

    /// <summary>
    /// Makes sure the latest Roblox is installed and returns its folder.
    /// Falls back to the existing install when Roblox's servers can't be reached.
    /// </summary>
    public static async Task<string> EnsureLatestAsync(IProgress<BootstrapProgress>? progress = null, CancellationToken ct = default)
    {
        await Lock.WaitAsync(ct);
        try
        {
            progress?.Report(new("Checking for Roblox updates…", 0));

            string version;
            try
            {
                version = await RobloxApi.GetClientVersionAsync();
            }
            catch when (InstalledVersionDir is { } existing)
            {
                return existing;
            }

            var dir = Path.Combine(AppPaths.VersionsDir, version);
            if (File.Exists(Path.Combine(dir, CompleteMarker)) && File.Exists(Path.Combine(dir, RobloxPaths.PlayerExe)))
                return dir;

            await InstallAsync(version, dir, progress, ct);
            RemoveOldVersions(dir);
            return dir;
        }
        finally
        {
            Lock.Release();
        }
    }

    private static async Task InstallAsync(string version, string dir, IProgress<BootstrapProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new($"Downloading Roblox {version}…", 0));

        var manifest = await RobloxApi.Http.GetStringAsync($"{Cdn}{version}-rbxPkgManifest.txt", ct);
        var packages = ParseManifest(manifest).Where(p => PackageDirs.ContainsKey(p.Name)).ToList();
        if (packages.Count == 0 || packages.All(p => p.Name != "RobloxApp.zip"))
            throw new InvalidDataException("The Roblox package list looks incomplete. Try again in a minute.");

        var partial = dir + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        Directory.CreateDirectory(partial);

        var total = packages.Sum(p => p.Size);
        long done = 0;

        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();
            var zipPath = Path.Combine(AppPaths.DownloadsDir, package.Md5 + ".zip");

            if (!File.Exists(zipPath) || !HashMatches(zipPath, package.Md5))
            {
                var baseDone = done;
                await DownloadAsync($"{Cdn}{version}-{package.Name}", zipPath, read =>
                {
                    var pct = total > 0 ? (baseDone + read) * 100.0 / total : 0;
                    progress?.Report(new($"Downloading Roblox… {pct:0}%", pct * 0.9));
                }, ct);

                if (!HashMatches(zipPath, package.Md5))
                {
                    File.Delete(zipPath);
                    throw new InvalidDataException($"Download of {package.Name} was corrupted. Please try again.");
                }
            }

            done += package.Size;
            progress?.Report(new($"Installing {package.Name}…", total > 0 ? done * 90.0 / total : 0));
            Extract(zipPath, Path.Combine(partial, PackageDirs[package.Name]));
        }

        await File.WriteAllTextAsync(Path.Combine(partial, "AppSettings.xml"), AppSettingsXml, ct);

        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(partial, dir);
        await File.WriteAllTextAsync(Path.Combine(dir, CompleteMarker), DateTime.UtcNow.ToString("O"), ct);
        progress?.Report(new("Roblox is up to date.", 100));
    }

    private static List<Package> ParseManifest(string text)
    {
        // Format: "v0", then blocks of 4 lines: name, md5, packed size, unpacked size.
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var result = new List<Package>();
        for (var i = 1; i + 2 < lines.Count; i += 4)
        {
            long.TryParse(lines[i + 2], out var size);
            result.Add(new Package(lines[i], lines[i + 1].ToLowerInvariant(), size));
        }
        return result;
    }

    private static async Task DownloadAsync(string url, string path, Action<long> onProgress, CancellationToken ct)
    {
        var tmp = path + ".download";
        using (var response = await RobloxApi.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(tmp);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            var lastReport = Environment.TickCount64;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (Environment.TickCount64 - lastReport > 150)
                {
                    lastReport = Environment.TickCount64;
                    onProgress(read);
                }
            }
        }
        File.Move(tmp, path, true);
    }

    private static bool HashMatches(string path, string md5)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
        return hash == md5;
    }

    private static void Extract(string zipPath, string targetDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(targetDir);
        foreach (var entry in zip.Entries)
        {
            var relative = entry.FullName.Replace('/', '\\').TrimStart('\\');
            if (relative.Length == 0) continue;

            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard

            if (relative.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }

    private static void RemoveOldVersions(string keep)
    {
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.VersionsDir))
        {
            if (string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, true); } catch { /* in use by a running client */ }
        }
    }

    /// <summary>Deletes Orbit's Roblox install so it is downloaded fresh on the next launch.</summary>
    public static void Reinstall()
    {
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.VersionsDir))
            Directory.Delete(dir, true);
    }
}
