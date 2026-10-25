using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orbit.Services;

public sealed record UpdateInfo(Version Version, string Tag, string ExeUrl, long ExeSize, string? ChecksumUrl);

/// <summary>
/// Checks GitHub for a newer Orbit release and replaces the running Orbit.exe with it.
/// Only normal releases count (GitHub's "latest" skips drafts and pre-releases).
/// </summary>
public static class Updater
{
    private const string Repo = "ph1l59/Orbit";
    private const string AssetName = "Orbit.exe";
    public const string DownloadUrl = $"https://github.com/{Repo}/releases/latest/download/{AssetName}";

    /// <summary>Command-line flag the new Orbit is started with after an update.</summary>
    public const string UpdatedFlag = "--updated";

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>Version as shown in the app, e.g. "2.5.1-beta" (from &lt;Version&gt; in Orbit.csproj).</summary>
    public static string DisplayVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) return CurrentVersion.ToString();
            var plus = info.IndexOf('+'); // the build may append "+<commit>"
            return plus >= 0 ? info[..plus] : info;
        }
    }

    private static string? ExePath => Environment.ProcessPath;
    private static string OldExePath(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, "Orbit.old.exe");
    private static string NewExePath(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, "Orbit.new.exe");

    /// <summary>Returns the newer release, or null if Orbit is up to date (or GitHub can't be reached).</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await RobloxApi.Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseTag(tag);
            if (version == null || version <= CurrentVersion) return null;

            string? exeUrl = null, checksumUrl = null;
            long exeSize = 0;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = url;
                    exeSize = asset.GetProperty("size").GetInt64();
                }
                else if (string.Equals(name, AssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }

            // Only trust files that live in Orbit's own GitHub releases.
            if (exeUrl == null || !exeUrl.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
                return null;
            if (checksumUrl != null && !checksumUrl.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
                checksumUrl = null;

            return new UpdateInfo(version, tag, exeUrl, exeSize, checksumUrl);
        }
        catch
        {
            return null; // offline, rate limited, … – just try again next time
        }
    }

    /// <summary>"v2.4.1-beta" → 2.4.1</summary>
    private static Version? ParseTag(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var end = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (end >= 0) t = t[..end];
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>True if Orbit may write next to its own .exe (not the case in e.g. Program Files).</summary>
    public static bool CanUpdateInPlace()
    {
        var exe = ExePath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exe)!, ".orbit-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Downloads the update, checks it and swaps it in. Afterwards call <see cref="Restart"/>.</summary>
    public static async Task DownloadAndInstallAsync(UpdateInfo update, IProgress<double> progress)
    {
        var exe = ExePath ?? throw new InvalidOperationException("Orbit's location is unknown.");
        var newExe = NewExePath(exe);
        var oldExe = OldExePath(exe);

        try
        {
            // 1. Download next to the current .exe (same drive, so the swap below is a simple rename).
            using (var response = await RobloxApi.Http.GetAsync(update.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? update.ExeSize;
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(newExe);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0) progress.Report(Math.Min(100, done * 100.0 / total));
                }
            }

            // 2. Make sure it's the complete, untouched file.
            var info = new FileInfo(newExe);
            if (update.ExeSize > 0 && info.Length != update.ExeSize)
                throw new InvalidOperationException("The download is incomplete. Please try again.");

            if (update.ChecksumUrl != null)
            {
                var expected = (await RobloxApi.Http.GetStringAsync(update.ChecksumUrl)).Trim().Split(' ', '\t')[0];
                string actual;
                await using (var stream = File.OpenRead(newExe))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The download doesn't match the release's checksum. Please try again.");
            }

            // 3. Swap: a running .exe can't be overwritten, but it can be renamed.
            if (File.Exists(oldExe)) File.Delete(oldExe);
            File.Move(exe, oldExe);
            try
            {
                File.Move(newExe, exe);
            }
            catch
            {
                File.Move(oldExe, exe); // put the working version back
                throw;
            }
        }
        catch
        {
            try { if (File.Exists(newExe)) File.Delete(newExe); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>Starts the freshly installed Orbit. The caller then shuts this instance down.</summary>
    public static void Restart()
    {
        var exe = ExePath ?? throw new InvalidOperationException("Orbit's location is unknown.");
        Process.Start(new ProcessStartInfo(exe, UpdatedFlag) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>Removes Orbit.old.exe / Orbit.new.exe left over from an update (the old Orbit may still be closing).</summary>
    public static void CleanupLeftovers()
    {
        var exe = ExePath;
        if (exe == null) return;
        var files = new[] { OldExePath(exe), NewExePath(exe) };
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 15; i++)
            {
                var left = false;
                foreach (var f in files)
                {
                    try { if (File.Exists(f)) File.Delete(f); }
                    catch { left = true; }
                }
                if (!left) return;
                await Task.Delay(1000);
            }
        });
    }
}
