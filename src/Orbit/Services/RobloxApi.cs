using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Orbit.Services;

public sealed record RobloxUser(long Id, string Name, string DisplayName);

public sealed record PrivateServerInvite(long PlaceId, string LinkCode);

public sealed record UserPresence(long UserId, int Type, string Location);

public sealed record GameInfo(long UniverseId, string Name, string Creator, string? IconUrl, long Playing);

public sealed class RobloxApiException(string message) : Exception(message);

/// <summary>Small wrapper around the public Roblox web APIs that Orbit needs.</summary>
public static class RobloxApi
{
    public static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, string> CsrfTokens = new();
    private static readonly ConcurrentDictionary<long, GameInfo> GameCache = new();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false, // we set the cookie header per request (one account = one cookie)
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 Orbit/2.0");
        return client;
    }

    /// <summary>Sends a request with the account cookie and handles Roblox's X-CSRF-TOKEN handshake.</summary>
    private static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, string? cookie)
    {
        var key = cookie ?? "";
        HttpResponseMessage? response = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            response?.Dispose();
            var request = makeRequest();
            if (!string.IsNullOrEmpty(cookie))
                request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            if (CsrfTokens.TryGetValue(key, out var token))
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden &&
                response.Headers.TryGetValues("x-csrf-token", out var values))
            {
                var newToken = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(newToken) && newToken != token)
                {
                    CsrfTokens[key] = newToken;
                    continue; // retry once with the fresh token
                }
            }
            break;
        }

        return response!;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Referrer = new Uri("https://www.roblox.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
        if (jsonBody != null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, string? cookie = null)
    {
        using var response = await SendAsync(() => Request(HttpMethod.Get, url), cookie).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    // ---------------- Accounts ----------------

    /// <summary>Returns the user behind a cookie, or null if the cookie is invalid/expired.</summary>
    public static async Task<RobloxUser?> GetAuthenticatedUserAsync(string cookie)
    {
        using var response = await SendAsync(
            () => Request(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated"), cookie).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (!response.IsSuccessStatusCode)
            throw new RobloxApiException($"Roblox answered with HTTP {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;
        return new RobloxUser(
            root.GetProperty("id").GetInt64(),
            root.GetProperty("name").GetString() ?? "",
            root.GetProperty("displayName").GetString() ?? "");
    }

    /// <summary>Gets a one-time authentication ticket that lets the Roblox client log in as this account.</summary>
    public static async Task<string> GetAuthTicketAsync(string cookie)
    {
        using var response = await SendAsync(() =>
        {
            var request = Request(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/", "{}");
            request.Headers.TryAddWithoutValidation("RBXAuthenticationNegotiation", "1");
            return request;
        }, cookie).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new RobloxApiException("Login expired. Remove the account and add it again.");

        if (response.Headers.TryGetValues("rbx-authentication-ticket", out var values))
        {
            var ticket = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(ticket)) return ticket;
        }

        throw new RobloxApiException($"Roblox did not return a login ticket (HTTP {(int)response.StatusCode}).");
    }

    /// <summary>Avatar headshot URLs for the given user ids.</summary>
    public static async Task<Dictionary<long, string>> GetHeadshotsAsync(IEnumerable<long> userIds)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in userIds.Distinct().Chunk(90))
        {
            var url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" +
                      string.Join(",", chunk) + "&size=150x150&format=Png&isCircular=false";
            using var doc = await GetJsonAsync(url).ConfigureAwait(false);
            if (doc == null) continue;

            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (item.TryGetProperty("imageUrl", out var img) && img.ValueKind == JsonValueKind.String)
                    result[item.GetProperty("targetId").GetInt64()] = img.GetString()!;
            }
        }
        return result;
    }

    /// <summary>Online status for the given users (needs any logged-in cookie).</summary>
    public static async Task<List<UserPresence>> GetPresenceAsync(string cookie, IEnumerable<long> userIds)
    {
        var result = new List<UserPresence>();
        foreach (var chunk in userIds.Distinct().Chunk(50))
        {
            var body = JsonSerializer.Serialize(new { userIds = chunk });
            using var response = await SendAsync(
                () => Request(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", body), cookie).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (var p in doc.RootElement.GetProperty("userPresences").EnumerateArray())
            {
                var location = p.TryGetProperty("lastLocation", out var loc) && loc.ValueKind == JsonValueKind.String
                    ? loc.GetString() ?? "" : "";
                result.Add(new UserPresence(p.GetProperty("userId").GetInt64(), p.GetProperty("userPresenceType").GetInt32(), location));
            }
        }
        return result;
    }

    /// <summary>Looks up a user by username. Returns null if nobody has that name.</summary>
    public static async Task<RobloxUser?> GetUserByNameAsync(string username)
    {
        var body = JsonSerializer.Serialize(new { usernames = new[] { username }, excludeBannedUsers = true });
        using var response = await SendAsync(
            () => Request(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users", body), null).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) return null;
        var u = data[0];
        return new RobloxUser(u.GetProperty("id").GetInt64(), u.GetProperty("name").GetString() ?? username,
            u.GetProperty("displayName").GetString() ?? username);
    }

    /// <summary>
    /// Asks Roblox whether a username can be used for a new account.
    /// Returns (true, …) if it's free, (false, reason) if not, or null if Roblox couldn't be reached.
    /// </summary>
    public static async Task<(bool Valid, string Message)?> ValidateUsernameAsync(string username, DateTime birthday)
    {
        try
        {
            var url = "https://auth.roblox.com/v1/usernames/validate" +
                      "?request.username=" + Uri.EscapeDataString(username) +
                      "&request.birthday=" + Uri.EscapeDataString(birthday.ToString("yyyy-MM-dd") + "T00:00:00.000Z") +
                      "&request.context=Signup";
            using var doc = await GetJsonAsync(url).ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("code", out var code)) return null;

            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return (code.GetInt32() == 0, message);
        }
        catch
        {
            return null;
        }
    }

    // ---------------- Joining ----------------

    /// <summary>Resolves a "roblox.com/share?code=...&amp;type=Server" link to a private server.</summary>
    public static async Task<PrivateServerInvite> ResolveServerShareLinkAsync(string cookie, string code)
    {
        var body = JsonSerializer.Serialize(new { linkId = code, linkType = "Server" });
        using var response = await SendAsync(
            () => Request(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", body), cookie).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new RobloxApiException($"The server link could not be resolved (HTTP {(int)response.StatusCode}).");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("privateServerInviteData", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("placeId", out var placeId) &&
            data.TryGetProperty("linkCode", out var linkCode) &&
            linkCode.ValueKind == JsonValueKind.String)
        {
            return new PrivateServerInvite(placeId.GetInt64(), linkCode.GetString()!);
        }

        throw new RobloxApiException("The server link is invalid or has expired.");
    }

    // ---------------- Games ----------------

    public static async Task<GameInfo?> GetGameInfoAsync(long placeId)
    {
        if (GameCache.TryGetValue(placeId, out var cached)) return cached;
        try
        {
            using var universeDoc = await GetJsonAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe").ConfigureAwait(false);
            if (universeDoc == null || !universeDoc.RootElement.TryGetProperty("universeId", out var uid) ||
                uid.ValueKind != JsonValueKind.Number) return null;
            var universeId = uid.GetInt64();

            string name = "Roblox", creator = "";
            long playing = 0;
            using (var gameDoc = await GetJsonAsync($"https://games.roblox.com/v1/games?universeIds={universeId}").ConfigureAwait(false))
            {
                var data = gameDoc?.RootElement.GetProperty("data");
                if (data is { } arr && arr.GetArrayLength() > 0)
                {
                    name = arr[0].GetProperty("name").GetString() ?? name;
                    if (arr[0].TryGetProperty("creator", out var c) && c.TryGetProperty("name", out var cn))
                        creator = cn.GetString() ?? "";
                    if (arr[0].TryGetProperty("playing", out var pl) && pl.ValueKind == JsonValueKind.Number)
                        playing = pl.GetInt64();
                }
            }

            string? icon = null;
            using (var iconDoc = await GetJsonAsync(
                       $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&returnPolicy=PlaceHolder&size=512x512&format=Png&isCircular=false").ConfigureAwait(false))
            {
                var data = iconDoc?.RootElement.GetProperty("data");
                if (data is { } arr && arr.GetArrayLength() > 0 &&
                    arr[0].TryGetProperty("imageUrl", out var img) && img.ValueKind == JsonValueKind.String)
                    icon = img.GetString();
            }

            var info = new GameInfo(universeId, name, creator, icon, playing);
            GameCache[placeId] = info;
            return info;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- Client deployment ----------------

    /// <summary>The current live Windows client version, e.g. "version-1a2b3c4d5e6f7a8b".</summary>
    public static async Task<string> GetClientVersionAsync()
    {
        using var doc = await GetJsonAsync("https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer").ConfigureAwait(false)
                        ?? throw new RobloxApiException("Could not get the current Roblox version.");
        return doc.RootElement.GetProperty("clientVersionUpload").GetString()
               ?? throw new RobloxApiException("Could not get the current Roblox version.");
    }

    // ---------------- Misc ----------------

    /// <summary>"City, Region, Country" for a server IP (via ipinfo.io).</summary>
    public static async Task<string?> GetServerLocationAsync(string ip)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://ipinfo.io/{ip}/json").ConfigureAwait(false);
            if (doc == null) return null;
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var parts = new[] { Get("city"), Get("region"), Get("country") }.Where(p => p.Length > 0).Distinct();
            var text = string.Join(", ", parts);
            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }
}
