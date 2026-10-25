using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>What to join: a place, optionally a specific server or a private server.</summary>
public sealed record JoinTarget(long PlaceId, string? JobId, string? LinkCode, string? ShareCode)
{
    private static readonly Regex GuidRegex =
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    public static JoinTarget? Parse(string? placeInput, string? jobInput, out string error)
    {
        error = "";
        var place = (placeInput ?? "").Trim();
        var job = (jobInput ?? "").Trim();

        if (place.Length == 0)
        {
            error = "Bitte gib eine Place-ID oder einen Spiel-Link ein.";
            return null;
        }

        long placeId = 0;
        string? linkCode = null, shareCode = null;

        if (!long.TryParse(place, out placeId))
        {
            var game = Regex.Match(place, @"games/(\d+)", RegexOptions.IgnoreCase);
            if (game.Success) placeId = long.Parse(game.Groups[1].Value);
            else
            {
                var query = Regex.Match(place, @"[?&]placeId=(\d+)", RegexOptions.IgnoreCase);
                if (query.Success) placeId = long.Parse(query.Groups[1].Value);
            }

            var link = Regex.Match(place, @"privateServerLinkCode=([^&#\s]+)", RegexOptions.IgnoreCase);
            if (link.Success) linkCode = Uri.UnescapeDataString(link.Groups[1].Value);

            var share = Regex.Match(place, @"share\?code=([^&#\s]+)", RegexOptions.IgnoreCase);
            if (share.Success)
            {
                if (!Regex.IsMatch(place, @"type=Server", RegexOptions.IgnoreCase))
                {
                    error = "Dieser Share-Link ist kein Server-Link. Unterstützt werden Privatserver-Links.";
                    return null;
                }
                shareCode = Uri.UnescapeDataString(share.Groups[1].Value);
            }
        }

        if (placeId <= 0 && shareCode == null)
        {
            error = "Im Link wurde keine Place-ID gefunden. Kopiere die Adresse der Spielseite (roblox.com/games/…).";
            return null;
        }

        string? jobId = null;
        if (job.Length > 0)
        {
            var match = GuidRegex.Match(job);
            if (!match.Success)
            {
                error = "Die Job-ID sieht ungültig aus. Sie hat das Format xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.";
                return null;
            }
            jobId = match.Value;
        }

        if (jobId != null && (linkCode != null || shareCode != null))
        {
            error = "Bei Privatserver-Links bitte keine Job-ID angeben.";
            return null;
        }

        return new JoinTarget(placeId, jobId, linkCode, shareCode);
    }
}

public static class RobloxLauncher
{
    public static async Task LaunchAsync(Account account, JoinTarget target, AppSettings settings)
    {
        var placeId = target.PlaceId;
        var linkCode = target.LinkCode;

        if (target.ShareCode != null)
        {
            var invite = await RobloxApi.ResolveServerShareLinkAsync(account.Cookie, target.ShareCode);
            placeId = invite.PlaceId;
            linkCode = invite.LinkCode;
        }

        var ticket = await RobloxApi.GetAuthTicketAsync(account.Cookie);

        if (account.BrowserTrackerId == 0)
            account.BrowserTrackerId = Random.Shared.NextInt64(100_000_000_000, 999_999_999_999);

        var tracker = account.BrowserTrackerId;
        var attempt = Guid.NewGuid().ToString();

        string launcherUrl;
        if (linkCode != null)
        {
            launcherUrl = "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame" +
                          $"&browserTrackerId={tracker}&placeId={placeId}&accessCode=&linkCode={Uri.EscapeDataString(linkCode)}" +
                          $"&joinAttemptId={attempt}&joinAttemptOrigin=PlayButton";
        }
        else if (target.JobId != null)
        {
            launcherUrl = "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGameJob" +
                          $"&browserTrackerId={tracker}&placeId={placeId}&gameId={target.JobId}&isPlayTogetherGame=false" +
                          $"&joinAttemptId={attempt}&joinAttemptOrigin=PlayButton";
        }
        else
        {
            launcherUrl = "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame" +
                          $"&browserTrackerId={tracker}&placeId={placeId}&isPlayTogetherGame=false" +
                          $"&joinAttemptId={attempt}&joinAttemptOrigin=PlayButton";
        }

        var launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var uri = "roblox-player:1+launchmode:play" +
                  $"+gameinfo:{ticket}" +
                  $"+launchtime:{launchTime}" +
                  $"+placelauncherurl:{Uri.EscapeDataString(launcherUrl)}" +
                  $"+browsertrackerid:{tracker}" +
                  "+robloxLocale:de_de+gameLocale:de_de+channel:+LaunchExp:InApp";

        StartClient(uri, settings.LaunchDirect);
    }

    private static void StartClient(string uri, bool direct)
    {
        var exe = direct ? RobloxPaths.FindPlayerExe() : null;

        if (exe != null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = uri,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            })?.Dispose();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(
                "Roblox wurde nicht gefunden. Installiere Roblox von roblox.com/download und versuche es erneut.");
        }
    }
}
