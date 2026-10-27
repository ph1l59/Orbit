using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Orbit.Services;

/// <summary>
/// Live connection to Roblox's notification service – the same one the Roblox website uses.
/// Roblox pushes "friend is online now", "new friend request" and similar right away,
/// so Orbit doesn't have to wait for the next check. If it can't connect, Orbit simply keeps
/// checking every 30 seconds as before.
/// </summary>
public sealed class RobloxRealtime : IDisposable
{
    private const string Host = "realtime-signalr.roblox.com";
    private const char RecordSeparator = '\u001e';

    private readonly string _cookie;
    private readonly CancellationTokenSource _stop = new();
    private ClientWebSocket? _socket;

    /// <summary>A notification arrived: namespace (e.g. "PresenceBulkNotifications") and its details.</summary>
    public event Action<string, JsonElement>? Notification;
    /// <summary>Connected (true) or lost the connection (false).</summary>
    public event Action<bool>? StateChanged;

    public bool IsConnected { get; private set; }

    public RobloxRealtime(string cookie)
    {
        _cookie = cookie;
        _ = Task.Run(RunAsync);
    }

    /// <summary>Connects, and reconnects with growing pauses (2 s … 5 min) whenever the connection drops.</summary>
    private async Task RunAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(_stop.Token);
                failures = 0;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log("connection error: " + ex.Message);
                failures++;
            }
            SetConnected(false);
            if (_stop.IsCancellationRequested) return;

            var wait = TimeSpan.FromSeconds(Math.Min(300, 2 * Math.Pow(2, Math.Min(failures, 8))));
            try { await Task.Delay(wait, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken stop)
    {
        // 1. Ask for a connection id (SignalR "negotiate"). Some setups skip this – then connect directly.
        var url = $"wss://{Host}/userhub";
        var token = await NegotiateAsync(stop);
        if (token != null) url += "?id=" + Uri.EscapeDataString(token);

        using var socket = new ClientWebSocket();
        try
        {
            var cookies = new System.Net.CookieContainer();
            cookies.Add(new System.Net.Cookie(".ROBLOSECURITY", _cookie, "/", ".roblox.com"));
            socket.Options.Cookies = cookies;
        }
        catch
        {
            socket.Options.SetRequestHeader("Cookie", ".ROBLOSECURITY=" + _cookie);
        }
        socket.Options.SetRequestHeader("Origin", "https://www.roblox.com");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _socket = socket;

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await socket.ConnectAsync(new Uri(url), connectTimeout.Token);
        }

        // 2. SignalR handshake: JSON messages, version 1.
        await SendAsync(socket, "{\"protocol\":\"json\",\"version\":1}", stop);

        // 3. Keep the connection alive with pings while reading.
        using var pingStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var pinger = PingLoopAsync(socket, pingStop.Token);
        try
        {
            await ReadLoopAsync(socket, stop);
        }
        finally
        {
            pingStop.Cancel();
            try { await pinger; } catch { /* stopped */ }
            _socket = null;
        }
    }

    private async Task<string?> NegotiateAsync(CancellationToken stop)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/userhub/negotiate?negotiateVersion=1");
            request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + _cookie);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
            request.Content = new StringContent("", Encoding.UTF8, "text/plain");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await RobloxApi.Http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                Log($"negotiate: HTTP {(int)response.StatusCode} – connecting directly");
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = doc.RootElement;
            if (root.TryGetProperty("connectionToken", out var t) && t.GetString() is { Length: > 0 } token) return token;
            if (root.TryGetProperty("connectionId", out var c) && c.GetString() is { Length: > 0 } id) return id;
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            Log("negotiate failed: " + ex.Message + " – connecting directly");
        }
        return null;
    }

    private static async Task PingLoopAsync(ClientWebSocket socket, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stop);
            await SendAsync(socket, "{\"type\":6}", stop);
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[16 * 1024];
        var pending = new StringBuilder();
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, stop);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log($"closed by Roblox: {result.CloseStatus} {result.CloseStatusDescription}");
                    return;
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            pending.Append(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));

            // SignalR messages end with the record separator; one frame can hold several.
            var text = pending.ToString();
            var end = text.LastIndexOf(RecordSeparator);
            if (end < 0) continue;
            pending.Clear().Append(text[(end + 1)..]);
            foreach (var part in text[..end].Split(RecordSeparator))
                if (part.Length > 0) Handle(part);
        }
    }

    private void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Handshake answer: {} (or {"error": "..."}).
            if (!root.TryGetProperty("type", out var typeProp))
            {
                if (root.TryGetProperty("error", out var error)) Log("handshake error: " + error.GetString());
                else
                {
                    Log("connected");
                    SetConnected(true);
                }
                return;
            }

            switch (typeProp.GetInt32())
            {
                case 1: // invocation: target "notification", arguments [namespace, detail (JSON text), sequence]
                    if (!root.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 2) return;
                    var ns = args[0].GetString() ?? "";
                    var detailRaw = args[1];
                    JsonElement detail;
                    if (detailRaw.ValueKind == JsonValueKind.String)
                    {
                        using var detailDoc = JsonDocument.Parse(detailRaw.GetString() ?? "null");
                        detail = detailDoc.RootElement.Clone();
                    }
                    else detail = detailRaw.Clone();
                    Log("notification: " + ns);
                    Notification?.Invoke(ns, detail);
                    break;
                case 7: // close
                    Log("server asked to close: " + json);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log("couldn't read a message: " + ex.Message);
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string json, CancellationToken stop) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json + RecordSeparator), WebSocketMessageType.Text, true, stop);

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        IsConnected = connected;
        StateChanged?.Invoke(connected);
    }

    /// <summary>Small troubleshooting log: %AppData%\Orbit\realtime.log</summary>
    private static void Log(string line)
    {
        try
        {
            var file = Path.Combine(AppPaths.DataDir, "realtime.log");
            if (File.Exists(file) && new FileInfo(file).Length > 64 * 1024) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _socket?.Abort(); } catch { /* ignore */ }
        SetConnected(false);
    }
}
