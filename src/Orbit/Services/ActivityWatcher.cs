using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Services;

public sealed record GameSession(long PlaceId, string JobId, string ServerIp, DateTime Joined);

/// <summary>
/// Follows the newest Roblox log file to find out which game and server the player is in.
/// </summary>
public sealed class ActivityWatcher : IDisposable
{
    private static readonly Regex JoiningRegex =
        new(@"! Joining game '([0-9a-fA-F\-]{36})' place ([0-9]+) at ([0-9\.]+)", RegexOptions.Compiled);
    private static readonly Regex UdmuxRegex =
        new(@"UDMUX Address = ([0-9\.]+), Port = [0-9]+ \| RCC Server Address = ([0-9\.]+)", RegexOptions.Compiled);

    private const string DisconnectEntry = "[FLog::Network] Time to disconnect replication data";
    private const string LeaveEntry = "leaveUGCGameInternal";

    private readonly System.Threading.Timer _timer;
    private string? _file;
    private long _position;
    private GameSession? _pending;
    private readonly StringBuilder _partialLine = new();
    private int _busy;

    public GameSession? Current { get; private set; }

    public event Action<GameSession>? Joined;
    public event Action? Left;

    public ActivityWatcher()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var newest = NewestLog();
            if (newest == null) return;

            if (!string.Equals(newest, _file, StringComparison.OrdinalIgnoreCase))
            {
                var first = _file == null;
                _file = newest;
                _partialLine.Clear();
                // On startup skip the existing content of an old log; a fresh log is read from the start.
                _position = first && File.GetLastWriteTimeUtc(newest) < DateTime.UtcNow.AddSeconds(-30)
                    ? new FileInfo(newest).Length
                    : 0;
                if (Current != null) EndSession();
            }

            ReadNewLines();

            if (Current != null && RobloxPaths.ClientIds().Length == 0) EndSession();
        }
        catch { /* log locked or deleted - try again next tick */ }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static string? NewestLog()
    {
        var dir = RobloxPaths.LogsDir;
        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir).EnumerateFiles("*Player*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    private void ReadNewLines()
    {
        using var stream = new FileStream(_file!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _position) _position = 0;
        if (stream.Length == _position) return;

        stream.Seek(_position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        _position = stream.Length;

        _partialLine.Append(text);
        var all = _partialLine.ToString();
        var lastNewline = all.LastIndexOf('\n');
        if (lastNewline < 0) return;

        var complete = all[..lastNewline];
        _partialLine.Clear().Append(all[(lastNewline + 1)..]);

        foreach (var line in complete.Split('\n'))
            Handle(line);
    }

    private void Handle(string line)
    {
        var joining = JoiningRegex.Match(line);
        if (joining.Success)
        {
            if (Current != null) EndSession();
            _pending = new GameSession(long.Parse(joining.Groups[2].Value), joining.Groups[1].Value,
                joining.Groups[3].Value, DateTime.UtcNow);
            Current = _pending;
            Joined?.Invoke(_pending);
            return;
        }

        var udmux = UdmuxRegex.Match(line);
        if (udmux.Success && Current != null && Current.ServerIp != udmux.Groups[1].Value)
        {
            // The real public server address is only known after the UDMUX line.
            Current = Current with { ServerIp = udmux.Groups[1].Value };
            Joined?.Invoke(Current);
            return;
        }

        if (Current != null && (line.Contains(DisconnectEntry) || line.Contains(LeaveEntry)))
            EndSession();
    }

    private void EndSession()
    {
        Current = null;
        _pending = null;
        Left?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
