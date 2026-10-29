using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Services;

/// <param name="UserId">Account that was playing (0 if the log didn't say).</param>
/// <param name="Reason">e.g. "Error 277 · Lost connection to the server", or null.</param>
public sealed record DisconnectInfo(long UserId, long PlaceId, string? Reason);

/// <summary>
/// Reads the logs of every open Roblox window and notices when one of them loses its game
/// (disconnected, kicked, server shut down) – but not when you leave or teleport yourself.
/// </summary>
public sealed class DisconnectWatcher : IDisposable
{
    private static readonly Regex JoiningRegex = new(@"! Joining game '[0-9a-fA-F\-]{36}' place ([0-9]+)", RegexOptions.Compiled);
    private static readonly Regex UserIdRegex = new(@"userid:\s*([0-9]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ErrorCodeRegex = new(@"Error Code:\s*([0-9]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReasonCodeRegex = new(@"disconnect(?:ion)?(?: notification\.?)? (?:with )?reason[: ]+([0-9]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KickRegex = new(@"kicked from (?:this experience|the game)[:\s]*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string DisconnectEntry = "Time to disconnect replication data";
    private const string LeaveEntry = "leaveUGCGameInternal";
    private const string TeleportEntry = "initiateTeleport";

    private sealed class LogState
    {
        public long Position;
        public readonly StringBuilder Partial = new();
        public long UserId;
        public long PlaceId;
        public bool InGame;
        public DateTime? Teleporting;
        public DateTime? DisconnectedAt;
        public int RunningAtDisconnect;
        public string? Reason;
        public DateTime LastSeen = DateTime.UtcNow;
    }

    private readonly Dictionary<string, LogState> _logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _timer;
    private int _busy;
    private bool _firstScan = true;

    public event Action<DisconnectInfo>? Disconnected;

    /// <summary>Accounts whose window lost its game and hasn't joined again since.</summary>
    private readonly HashSet<long> _disconnected = new();

    /// <summary>What every account is doing right now: user id → (place id or 0, disconnected).</summary>
    public Dictionary<long, (long PlaceId, bool Disconnected)> Snapshot()
    {
        lock (_logs)
        {
            var result = new Dictionary<long, (long, bool)>();
            foreach (var st in _logs.Values.OrderBy(v => v.LastSeen))
                if (st.UserId > 0) result[st.UserId] = (st.InGame ? st.PlaceId : 0, _disconnected.Contains(st.UserId));
            return result;
        }
    }

    public DisconnectWatcher()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.5));
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            lock (_logs) PollLocked();
        }
        catch { /* a log is locked or gone – next tick */ }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void PollLocked()
    {
        {
            var dir = RobloxPaths.LogsDir;
            if (!Directory.Exists(dir)) return;

            // Logs that are still being written (= Roblox windows that are open).
            var recent = new DirectoryInfo(dir).EnumerateFiles("*Player*.log")
                .Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-5))
                .ToList();

            foreach (var file in recent)
            {
                if (!_logs.TryGetValue(file.FullName, out var state))
                {
                    state = new LogState();
                    _logs[file.FullName] = state;
                    // Logs that were already open when Orbit started: read them to know who plays where,
                    // but don't report anything that happened before.
                    Read(file.FullName, state, silent: _firstScan);
                    continue;
                }
                state.LastSeen = DateTime.UtcNow;
                Read(file.FullName, state, silent: false);
            }
            _firstScan = false;

            // Forget logs nobody writes to anymore.
            foreach (var old in _logs.Where(kv => DateTime.UtcNow - kv.Value.LastSeen > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
                _logs.Remove(old);

            CheckPending();
        }
    }

    private void Read(string path, LogState state, bool silent)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Position) state.Position = 0;
        if (stream.Length == state.Position) return;
        if (silent && stream.Length > 20 * 1024 * 1024) state.Position = stream.Length - 4 * 1024 * 1024; // huge old log: only the end

        stream.Seek(state.Position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        state.Position = stream.Length;

        state.Partial.Append(text);
        var all = state.Partial.ToString();
        var lastNewline = all.LastIndexOf('\n');
        if (lastNewline < 0) return;
        state.Partial.Clear().Append(all[(lastNewline + 1)..]);

        foreach (var line in all[..lastNewline].Split('\n'))
            Handle(line, state);

        if (silent)
        {
            state.DisconnectedAt = null;
            state.Reason = null;
        }
    }

    private void Handle(string line, LogState state)
    {
        var userId = UserIdRegex.Match(line);
        if (userId.Success && long.TryParse(userId.Groups[1].Value, out var id) && id > 0) state.UserId = id;

        var joining = JoiningRegex.Match(line);
        if (joining.Success)
        {
            state.InGame = true;
            state.PlaceId = long.Parse(joining.Groups[1].Value);
            if (state.UserId > 0) _disconnected.Remove(state.UserId);
            state.Teleporting = null;
            state.DisconnectedAt = null;
            state.Reason = null;
            return;
        }

        if (line.Contains(TeleportEntry))
        {
            state.Teleporting = DateTime.UtcNow;
            state.DisconnectedAt = null;
            return;
        }

        if (line.Contains(LeaveEntry))
        {
            // Left the game on purpose.
            state.InGame = false;
            state.DisconnectedAt = null;
            state.Reason = null;
            return;
        }

        if (!state.InGame && state.DisconnectedAt == null) return;

        var reason = Reason(line);
        if (reason != null) state.Reason = reason;

        if (line.Contains(DisconnectEntry) && state.InGame)
        {
            state.InGame = false;
            var teleporting = state.Teleporting is { } t && DateTime.UtcNow - t < TimeSpan.FromSeconds(60);
            if (teleporting) return;
            state.DisconnectedAt = DateTime.UtcNow;
            state.RunningAtDisconnect = RobloxPaths.RunningCount();
        }
    }

    private static string? Reason(string line)
    {
        var kick = KickRegex.Match(line);
        if (kick.Success)
        {
            var message = kick.Groups[1].Value.Trim().TrimEnd('"', ')').Trim();
            if (message.Length > 90) message = message[..90] + "…";
            return message.Length > 0 ? "Kicked: " + message : "Kicked from the game";
        }

        var code = ErrorCodeRegex.Match(line);
        if (!code.Success) code = ReasonCodeRegex.Match(line);
        if (!code.Success || !int.TryParse(code.Groups[1].Value, out var number) || number == 0) return null;
        var text = number switch
        {
            17 or 279 => "Couldn't connect to the server",
            260 or 261 or 262 or 266 => "The connection timed out",
            264 => "Same account launched the game on another device",
            267 => "Kicked by the game",
            268 => "Kicked unexpectedly",
            273 => "Logged in somewhere else",
            277 => "Lost connection to the server",
            288 => "The server shut down",
            529 => "Roblox is having problems",
            _ => null,
        };
        return text == null ? $"Error {number}" : $"Error {number} · {text}";
    }

    private void CheckPending()
    {
        foreach (var state in _logs.Values)
        {
            if (state.DisconnectedAt is not { } at || DateTime.UtcNow - at < TimeSpan.FromSeconds(5)) continue;
            state.DisconnectedAt = null;

            // The window was closed (fewer Roblox windows than before): that's not a disconnect.
            if (RobloxPaths.RunningCount() < state.RunningAtDisconnect) { state.Reason = null; continue; }

            if (state.UserId > 0) _disconnected.Add(state.UserId);
            var info = new DisconnectInfo(state.UserId, state.PlaceId, state.Reason);
            state.Reason = null;
            Disconnected?.Invoke(info);
        }
    }

    public void Dispose() => _timer.Dispose();
}
