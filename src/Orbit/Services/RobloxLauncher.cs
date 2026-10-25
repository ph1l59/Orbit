using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>What to join: a place, optionally a specific server or a private server.</summary>
public sealed record JoinTarget(long PlaceId, string? JobId, string? LinkCode, string? ShareCode, long FollowUserId = 0)
{
    private static readonly Regex GuidRegex =
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private sealed class Parts
    {
        public long PlaceId;
        public string? JobId, LinkCode, ShareCode;
    }

    /// <summary>Reads place ids, game links, private server links, share links, share codes and job ids.</summary>
    private static bool ReadInto(string text, Parts parts, out string error)
    {
        error = "";
        if (text.Length == 0) return true;

        if (long.TryParse(text, out var id))
        {
            parts.PlaceId = id;
            return true;
        }

        var game = Regex.Match(text, @"games/(\d+)", RegexOptions.IgnoreCase);
        if (game.Success) parts.PlaceId = long.Parse(game.Groups[1].Value);
        var placeQuery = Regex.Match(text, @"[?&]placeId=(\d+)", RegexOptions.IgnoreCase);
        if (placeQuery.Success) parts.PlaceId = long.Parse(placeQuery.Groups[1].Value);

        var link = Regex.Match(text, @"privateServerLinkCode=([^&#\s]+)", RegexOptions.IgnoreCase);
        if (link.Success) parts.LinkCode = Uri.UnescapeDataString(link.Groups[1].Value);

        var instance = Regex.Match(text, @"gameInstanceId=([0-9a-fA-F\-]{36})", RegexOptions.IgnoreCase);
        if (instance.Success) parts.JobId = instance.Groups[1].Value;

        var share = Regex.Match(text, @"(?:^|[?&])code=([0-9a-zA-Z]+)", RegexOptions.IgnoreCase);
        if (share.Success)
        {
            var type = Regex.Match(text, @"type=(\w+)", RegexOptions.IgnoreCase);
            if (type.Success && !type.Groups[1].Value.Equals("Server", StringComparison.OrdinalIgnoreCase))
            {
                error = $"This is a share link of type \"{type.Groups[1].Value}\". Only server links can be joined.";
                return false;
            }
            parts.ShareCode = share.Groups[1].Value;
            return true;
        }

        // A bare share code, optionally followed by "&type=Server" (what you get when copying half a link).
        var bareCode = Regex.Match(text, @"^([0-9a-fA-F]{16,64})(?:&type=Server)?$", RegexOptions.IgnoreCase);
        if (bareCode.Success)
        {
            parts.ShareCode = bareCode.Groups[1].Value;
            return true;
        }

        if (parts.JobId == null && !link.Success && !game.Success && !placeQuery.Success)
        {
            var guid = GuidRegex.Match(text);
            if (guid.Success)
            {
                parts.JobId = guid.Value;
                return true;
            }

            error = "Couldn't read this. Paste a place ID, a game link, a private server link or a share link.";
            return false;
        }

        return true;
    }

    public static JoinTarget Follow(long userId) => new(0, null, null, null, userId);

    public static JoinTarget? Parse(string? placeInput, string? serverInput, out string error)
    {
        var parts = new Parts();
        var place = (placeInput ?? "").Trim();
        var server = (serverInput ?? "").Trim();

        if (place.Length == 0 && server.Length == 0)
        {
            error = "Enter a place ID or game link first.";
            return null;
        }

        if (!ReadInto(place, parts, out error)) return null;
        if (!ReadInto(server, parts, out error)) return null;

        if (parts.PlaceId <= 0 && parts.ShareCode == null)
        {
            error = "No place ID found. Copy the address of the game page (roblox.com/games/…).";
            return null;
        }

        if (parts.ShareCode != null || parts.LinkCode != null) parts.JobId = null;
        error = "";
        return new JoinTarget(parts.PlaceId, parts.JobId, parts.LinkCode, parts.ShareCode);
    }
}

/// <summary>Prepares Roblox (install/update, flags, mods) and starts clients.</summary>
public static class RobloxLauncher
{
    /// <summary>Installs/updates Roblox if needed, applies FastFlags and mods. Returns the exe to start.</summary>
    public static async Task<string> PrepareAsync(AppSettings s, IProgress<BootstrapProgress>? progress = null)
    {
        string dir;
        if (s.UseManagedInstall)
        {
            dir = await Bootstrapper.EnsureLatestAsync(progress);
        }
        else
        {
            var official = RobloxPaths.FindOfficialPlayerExe()
                           ?? throw new InvalidOperationException(
                               "Roblox isn't installed. Turn on \"Use Orbit's own Roblox install\" in Bootstrapper, or install Roblox from roblox.com.");
            dir = Path.GetDirectoryName(official)!;
        }

        progress?.Report(new("Applying settings…", 100));
        await Task.Run(() =>
        {
            FlagService.Apply(s, new[] { dir });
            ModService.Apply(s, dir);
        });

        return Path.Combine(dir, RobloxPaths.PlayerExe);
    }

    public static async Task LaunchAccountAsync(Account account, JoinTarget target, string exe)
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
        if (target.FollowUserId > 0)
        {
            launcherUrl = "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser" +
                          $"&browserTrackerId={tracker}&userId={target.FollowUserId}" +
                          $"&joinAttemptId={attempt}&joinAttemptOrigin=followUser";
        }
        else if (linkCode != null)
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

        var uri = "roblox-player:1+launchmode:play" +
                  $"+gameinfo:{ticket}" +
                  $"+launchtime:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" +
                  $"+placelauncherurl:{Uri.EscapeDataString(launcherUrl)}" +
                  $"+browsertrackerid:{tracker}" +
                  "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

        Start(exe, uri);
        Integrations.LastLaunchedAccount = account;
    }

    /// <summary>Starts the client with a roblox-player:/roblox: link (also used for links from the website).</summary>
    public static void Start(string exe, string uri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = uri,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        })?.Dispose();
    }
}
