using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Orbit.Services;

public sealed record RobloxUser(long Id, string Name, string DisplayName);

public sealed record PrivateServerInvite(long PlaceId, string LinkCode);

public sealed record UserPresence(long UserId, int Type, string Location);

/// <summary>A friend (or someone who sent a friend request).</summary>
public sealed record FriendInfo(long Id, string Name, string DisplayName);

/// <summary>Public profile details.</summary>
public sealed record ProfileInfo(long Id, string Name, string DisplayName, string Description, DateTime Created);

public enum FriendRequestResult { Sent, AlreadyFriends, NeedsCheck, Failed }

/// <summary>One privacy setting of the account, e.g. who can see that I'm online.</summary>
/// <param name="Editable">False when Roblox offers no choices for this account (e.g. set by parental controls).</param>
public sealed record PrivacySetting(string Key, string Current, IReadOnlyList<string> Options, bool Editable);

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
    /// <param name="extraCookies">More cookies after the login, e.g. Roblox's browser tracker ("name=value; name2=value2").</param>
    private static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, string? cookie, string? extraCookies = null)
    {
        var key = cookie ?? "";
        HttpResponseMessage? response = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            response?.Dispose();
            var request = makeRequest();
            if (!string.IsNullOrEmpty(cookie))
                request.Headers.TryAddWithoutValidation("Cookie",
                    ".ROBLOSECURITY=" + cookie + (string.IsNullOrEmpty(extraCookies) ? "" : "; " + extraCookies));
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

    /// <summary>Full-body avatar picture of one user (null if Roblox has none ready).</summary>
    public static async Task<string?> GetFullBodyAvatarAsync(long userId)
    {
        var url = $"https://thumbnails.roblox.com/v1/users/avatar?userIds={userId}&size=720x720&format=Png&isCircular=false";
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var doc = await GetJsonAsync(url).ConfigureAwait(false);
            var item = doc?.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
            if (item is { ValueKind: JsonValueKind.Object } it)
            {
                var state = it.TryGetProperty("state", out var st) ? st.GetString() : null;
                if (state == "Completed" && it.TryGetProperty("imageUrl", out var img) && img.ValueKind == JsonValueKind.String)
                    return img.GetString();
                if (state is "Blocked" or "Error") return null;
            }
            await Task.Delay(1200).ConfigureAwait(false); // Roblox is still rendering it
        }
        return null;
    }

    /// <summary>
    /// URL of the 3D avatar manifest (mesh, materials, textures). Since March 2026 Roblox only answers
    /// logged-in requests here, so this goes out with the account's own login.
    /// Returns (url, null) when ready, (null, reason) otherwise.
    /// </summary>
    public static async Task<(string? Url, string? Problem)> GetAvatar3DManifestAsync(long userId, string cookie)
    {
        var url = $"https://thumbnails.roblox.com/v1/users/avatar-3d?userId={userId}";
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var response = await SendAsync(() => Request(HttpMethod.Get, url), cookie).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"avatar-3d answered HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var state = doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() : null;
            if (state == "Completed" && doc.RootElement.TryGetProperty("imageUrl", out var img) && img.ValueKind == JsonValueKind.String)
                return (img.GetString(), null);
            if (state is "Blocked" or "Error") return (null, "avatar-3d state " + state);
            await Task.Delay(1500).ConfigureAwait(false); // still rendering
        }
        return (null, "avatar-3d still pending");
    }

    // ---------------- Friends ----------------

    /// <summary>Ids of all friends of a user (seen through that account's login).</summary>
    public static async Task<List<long>> GetFriendIdsAsync(long userId, string cookie)
    {
        var ids = new List<long>();

        // Newer paged endpoint first ...
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var url = $"https://friends.roblox.com/v1/users/{userId}/friends/find?limit=50" +
                      (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            using var response = await SendAsync(() => Request(HttpMethod.Get, url), cookie).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) break;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("PageItems", out var items)) break;
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("id", out var id)) ids.Add(id.GetInt64());
            cursor = doc.RootElement.TryGetProperty("NextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) return ids.Distinct().ToList();
        }
        if (ids.Count > 0) return ids.Distinct().ToList();

        // ... and the classic one as a fallback.
        using (var response = await SendAsync(
                   () => Request(HttpMethod.Get, $"https://friends.roblox.com/v1/users/{userId}/friends"), cookie).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
                throw new RobloxApiException($"Roblox answered with HTTP {(int)response.StatusCode}.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                ids.Add(item.GetProperty("id").GetInt64());
        }
        return ids.Distinct().ToList();
    }

    /// <summary>Names and display names for user ids.</summary>
    public static async Task<Dictionary<long, FriendInfo>> GetUsersAsync(IEnumerable<long> userIds, string? cookie = null)
    {
        var result = new Dictionary<long, FriendInfo>();
        foreach (var chunk in userIds.Distinct().Chunk(100))
        {
            var body = JsonSerializer.Serialize(new { userIds = chunk, excludeBannedUsers = false });

            // One quick retry; anything still missing is filled in later by the caller (no long waits here).
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) await Task.Delay(400).ConfigureAwait(false);
                try
                {
                    using var response = await SendAsync(
                        () => Request(HttpMethod.Post, "https://users.roblox.com/v1/users", body), cookie).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    foreach (var u in doc.RootElement.GetProperty("data").EnumerateArray())
                    {
                        var id = u.GetProperty("id").GetInt64();
                        result[id] = new FriendInfo(id, u.GetProperty("name").GetString() ?? "", u.GetProperty("displayName").GetString() ?? "");
                    }
                    break;
                }
                catch
                {
                    // network hiccup – next attempt
                }
            }
        }
        return result;
    }

    /// <summary>People who sent this account a friend request.</summary>
    public static async Task<List<long>> GetFriendRequestIdsAsync(string cookie)
    {
        var ids = new List<long>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var url = "https://friends.roblox.com/v1/my/friends/requests?limit=100" +
                      (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            using var response = await SendAsync(() => Request(HttpMethod.Get, url), cookie).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new RobloxApiException($"Roblox answered with HTTP {(int)response.StatusCode}.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                ids.Add(item.GetProperty("id").GetInt64());
            cursor = doc.RootElement.TryGetProperty("nextPageCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return ids;
    }

    /// <summary>Finds users by (part of) their name.</summary>
    public static async Task<List<FriendInfo>> SearchUsersAsync(string keyword, string cookie)
    {
        var url = "https://users.roblox.com/v1/users/search?limit=10&keyword=" + Uri.EscapeDataString(keyword);
        using var response = await SendAsync(() => Request(HttpMethod.Get, url), cookie).ConfigureAwait(false);
        if ((int)response.StatusCode == 429) throw new RobloxApiException("Too many searches – wait a few seconds.");
        if (!response.IsSuccessStatusCode) throw new RobloxApiException($"Roblox answered with HTTP {(int)response.StatusCode}.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var list = new List<FriendInfo>();
        foreach (var u in doc.RootElement.GetProperty("data").EnumerateArray())
            list.Add(new FriendInfo(u.GetProperty("id").GetInt64(), u.GetProperty("name").GetString() ?? "",
                u.GetProperty("displayName").GetString() ?? ""));
        return list;
    }

    /// <summary>Sends a friend request. Roblox sometimes wants a human check first – then the caller opens the profile.</summary>
    public static async Task<FriendRequestResult> SendFriendRequestAsync(string cookie, long targetId)
    {
        using var response = await SendAsync(() => Request(HttpMethod.Post,
            $"https://friends.roblox.com/v1/users/{targetId}/request-friendship",
            "{\"friendshipOriginSourceType\":\"PlayerSearch\"}"), cookie).ConfigureAwait(false);

        if (response.IsSuccessStatusCode) return FriendRequestResult.Sent;
        if (response.Headers.Contains("rblx-challenge-id") || response.Headers.Contains("rblx-challenge-type"))
            return FriendRequestResult.NeedsCheck;

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (body.Contains("already", StringComparison.OrdinalIgnoreCase)) return FriendRequestResult.AlreadyFriends;
        if (body.Contains("captcha", StringComparison.OrdinalIgnoreCase) || body.Contains("challenge", StringComparison.OrdinalIgnoreCase))
            return FriendRequestResult.NeedsCheck;
        return FriendRequestResult.Failed;
    }

    /// <summary>Name, about text and join date of a user.</summary>
    public static async Task<ProfileInfo?> GetProfileAsync(long userId)
    {
        using var doc = await GetJsonAsync($"https://users.roblox.com/v1/users/{userId}").ConfigureAwait(false);
        if (doc == null) return null;
        var r = doc.RootElement;
        return new ProfileInfo(userId, r.GetProperty("name").GetString() ?? "", r.GetProperty("displayName").GetString() ?? "",
            r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            r.TryGetProperty("created", out var c) && c.TryGetDateTime(out var created) ? created : DateTime.MinValue);
    }

    /// <summary>Friends, followers and followings count (-1 if unknown).</summary>
    public static async Task<(int Friends, int Followers, int Following)> GetCountsAsync(long userId)
    {
        async Task<int> Count(string url)
        {
            try
            {
                using var doc = await GetJsonAsync(url).ConfigureAwait(false);
                return doc != null && doc.RootElement.TryGetProperty("count", out var c) ? c.GetInt32() : -1;
            }
            catch { return -1; }
        }
        var friends = Count($"https://friends.roblox.com/v1/users/{userId}/friends/count");
        var followers = Count($"https://friends.roblox.com/v1/users/{userId}/followers/count");
        var following = Count($"https://friends.roblox.com/v1/users/{userId}/followings/count");
        return (await friends, await followers, await following);
    }

    private const string UserSettingsApi = "https://apis.roblox.com/user-settings-api/v1/user-settings";

    /// <summary>Usual choices, used when Roblox doesn't send its own list of options.</summary>
    private static readonly string[] DefaultPrivacyOptions =
        { "AllUsers", "FriendsFollowingAndFollowers", "FriendsAndFollowing", "Friends", "NoOne" };

    /// <summary>
    /// The account's privacy settings ("whoCan…") with their current value and the allowed values.
    /// Uses Roblox's user-settings API (the old accountsettings endpoints answer 403 now).
    /// </summary>
    public static async Task<List<PrivacySetting>> GetPrivacySettingsAsync(string cookie)
    {
        var result = new List<PrivacySetting>();

        // 1. Settings together with their options.
        using (var response = await SendAsync(() => Request(HttpMethod.Get, UserSettingsApi + "/settings-and-options"), cookie).ConfigureAwait(false))
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            PrivacyLog($"GET settings-and-options -> HTTP {(int)response.StatusCode}: {Shorten(text, 6000)}");
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var setting in doc.RootElement.EnumerateObject())
                {
                    if (!IsPrivacyKey(setting.Name) || setting.Value.ValueKind != JsonValueKind.Object) continue;
                    var current = setting.Value.TryGetProperty("currentValue", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() : null;
                    var options = new List<string>();
                    if (setting.Value.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                        foreach (var o in opts.EnumerateArray())
                            if (FindValue(o) is { } v) options.Add(v);
                    if (current == null) continue;
                    // No options = this account can't change it (Roblox decides, e.g. through parental controls).
                    result.Add(options.Count > 0
                        ? new PrivacySetting(setting.Name, current, options, true)
                        : new PrivacySetting(setting.Name, current, new[] { current }, false));
                }
            }
        }
        if (result.Count > 0) return result;

        // 2. Just the values.
        using (var response = await SendAsync(() => Request(HttpMethod.Get, UserSettingsApi), cookie).ConfigureAwait(false))
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            PrivacyLog($"GET user-settings -> HTTP {(int)response.StatusCode}: {Shorten(text, 3000)}");
            if (!response.IsSuccessStatusCode)
                throw new RobloxApiException($"HTTP {(int)response.StatusCode} – {ErrorMessage(text) ?? "settings unavailable"}.");
            using var doc = JsonDocument.Parse(text);
            foreach (var setting in doc.RootElement.EnumerateObject())
                if (IsPrivacyKey(setting.Name) && setting.Value.ValueKind == JsonValueKind.String)
                    result.Add(new PrivacySetting(setting.Name, setting.Value.GetString()!, DefaultPrivacyOptions, true));
        }
        return result;

        static bool IsPrivacyKey(string key) =>
            key.StartsWith("whoCan", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("updateFriendsAboutMyActivity", StringComparison.OrdinalIgnoreCase);

        // An option may be a plain string or an object such as { "option": { "optionValue": "Friends" } }.
        static string? FindValue(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String) return e.GetString();
            if (e.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Name.Contains("value", StringComparison.OrdinalIgnoreCase))
                    return p.Value.GetString();
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object && FindValue(p.Value) is { } inner) return inner;
            return null;
        }
    }

    /// <summary>Roblox's own error text from {"errors":[{"message":"…"}]}, if there is one.</summary>
    private static string? ErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var e in errors.EnumerateArray())
                    if (e.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } msg) return msg;
            if (doc.RootElement.TryGetProperty("message", out var top) && top.GetString() is { Length: > 0 } t) return t;
        }
        catch { /* not JSON */ }
        return null;
    }

    private static string Shorten(string text, int max = 600) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Troubleshooting log for privacy settings: %AppData%\Orbit\privacy.log (no login data in it).</summary>
    private static void PrivacyLog(string line)
    {
        try
        {
            var file = System.IO.Path.Combine(AppPaths.DataDir, "privacy.log");
            if (System.IO.File.Exists(file) && new System.IO.FileInfo(file).Length > 128 * 1024) System.IO.File.Delete(file);
            System.IO.File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    /// <summary>Changes one privacy setting, e.g. ("whoCanSeeMyOnlineStatus", "NoOne").</summary>
    /// <remarks>Roblox wants the browser tracker id (normally a browser cookie) with this request.</remarks>
    public static async Task SetPrivacySettingAsync(string cookie, long userId, long browserTrackerId, string key, string value)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value });
        var created = DateTime.UtcNow.ToString("M/d/yyyy h:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);
        var tracker = $"RBXEventTrackerV2=CreateDate={created}&rbxid={userId}&browserid={browserTrackerId}";
        using var response = await SendAsync(() =>
        {
            var request = Request(HttpMethod.Post, UserSettingsApi, body);
            request.Headers.TryAddWithoutValidation("Roblox-Browser-Tracker-Id", browserTrackerId.ToString());
            return request;
        }, cookie, tracker).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            PrivacyLog($"POST {key}={value} -> HTTP {(int)response.StatusCode}: {Shorten(text)}");
            throw new RobloxApiException($"HTTP {(int)response.StatusCode} – {ErrorMessage(text) ?? "Roblox didn't accept that value"}.");
        }
        PrivacyLog($"POST {key}={value} -> OK");
    }

    /// <summary>Reads one of the account's privacy settings (e.g. "inventory-privacy" → "inventoryPrivacy").</summary>
    public static async Task<string?> GetPrivacyAsync(string cookie, string endpoint, string field)
    {
        using var response = await SendAsync(
            () => Request(HttpMethod.Get, $"https://accountsettings.roblox.com/v1/{endpoint}"), cookie).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() : null;
    }

    /// <summary>Changes one of the account's privacy settings.</summary>
    public static async Task SetPrivacyAsync(string cookie, string endpoint, string field, string value)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = value });
        using var response = await SendAsync(
            () => Request(HttpMethod.Post, $"https://accountsettings.roblox.com/v1/{endpoint}", body), cookie).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new RobloxApiException($"Roblox answered with HTTP {(int)response.StatusCode}.");
    }

    /// <summary>Accepts or declines a friend request.</summary>
    public static async Task AnswerFriendRequestAsync(string cookie, long requesterId, bool accept)
    {
        var action = accept ? "accept-friend-request" : "decline-friend-request";
        using var response = await SendAsync(
            () => Request(HttpMethod.Post, $"https://friends.roblox.com/v1/users/{requesterId}/{action}", "{}"), cookie).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new RobloxApiException($"Roblox answered with HTTP {(int)response.StatusCode}.");
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
