using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Orbit.Services;

public sealed record RobloxUser(long Id, string Name, string DisplayName);

public sealed record PrivateServerInvite(long PlaceId, string LinkCode);

public sealed class RobloxApiException(string message) : Exception(message);

/// <summary>Small wrapper around the public Roblox web APIs that Orbit needs.</summary>
public static class RobloxApi
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, string> CsrfTokens = new();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false, // we set the cookie header per request (one account = one cookie)
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 Orbit/1.0");
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

            response = await Http.SendAsync(request).ConfigureAwait(false);

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

    /// <summary>Returns the user behind a cookie, or null if the cookie is invalid/expired.</summary>
    public static async Task<RobloxUser?> GetAuthenticatedUserAsync(string cookie)
    {
        using var response = await SendAsync(
            () => Request(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated"), cookie).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (!response.IsSuccessStatusCode)
            throw new RobloxApiException($"Roblox hat mit {(int)response.StatusCode} geantwortet.");

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
            throw new RobloxApiException("Login abgelaufen. Bitte den Account entfernen und neu hinzufügen.");

        if (response.Headers.TryGetValues("rbx-authentication-ticket", out var values))
        {
            var ticket = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(ticket)) return ticket;
        }

        throw new RobloxApiException($"Kein Login-Ticket erhalten (HTTP {(int)response.StatusCode}).");
    }

    /// <summary>Avatar headshot URLs for the given user ids.</summary>
    public static async Task<Dictionary<long, string>> GetHeadshotsAsync(IEnumerable<long> userIds)
    {
        var result = new Dictionary<long, string>();
        var ids = userIds.Distinct().ToList();

        foreach (var chunk in ids.Chunk(90))
        {
            var url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" +
                      string.Join(",", chunk) + "&size=150x150&format=Png&isCircular=false";
            using var response = await SendAsync(() => Request(HttpMethod.Get, url), null).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (item.TryGetProperty("imageUrl", out var img) && img.ValueKind == JsonValueKind.String)
                    result[item.GetProperty("targetId").GetInt64()] = img.GetString()!;
            }
        }

        return result;
    }

    /// <summary>Resolves a "roblox.com/share?code=...&amp;type=Server" link to a private server.</summary>
    public static async Task<PrivateServerInvite> ResolveServerShareLinkAsync(string cookie, string code)
    {
        var body = JsonSerializer.Serialize(new { linkId = code, linkType = "Server" });
        using var response = await SendAsync(
            () => Request(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", body), cookie).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new RobloxApiException($"Share-Link konnte nicht aufgelöst werden (HTTP {(int)response.StatusCode}).");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("privateServerInviteData", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("placeId", out var placeId) &&
            data.TryGetProperty("linkCode", out var linkCode) &&
            linkCode.ValueKind == JsonValueKind.String)
        {
            return new PrivateServerInvite(placeId.GetInt64(), linkCode.GetString()!);
        }

        throw new RobloxApiException("Der Share-Link ist ungültig oder abgelaufen.");
    }
}
