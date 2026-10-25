using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Orbit.Services;

/// <summary>
/// Shows the account's name (alias, otherwise display name) instead of "Roblox" on the windows
/// Orbit launched, so they can be told apart in the taskbar. Only the window title is changed.
/// </summary>
public static class WindowTitles
{
    private const string DefaultTitle = "Roblox";

    private sealed class Tracked
    {
        public int Pid { get; set; }
        public long UserId { get; set; }
        /// <summary>Process start time, so a re-used process id after a reboot is never mistaken for ours.</summary>
        public long StartedTicks { get; set; }
    }

    private static readonly List<Tracked> Windows = new();
    /// <summary>Launches whose Roblox process hasn't been found yet (id + launch time).</summary>
    private static readonly List<(long UserId, DateTime LaunchedUtc)> Pending = new();
    private static DispatcherTimer? _timer;
    private static bool _loaded;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "windows.json");

    /// <summary>Starts keeping the titles up to date (call once from the UI thread).</summary>
    public static void Start()
    {
        if (_timer != null) return;
        Load();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    /// <summary>Remember that Orbit just launched Roblox for this account.</summary>
    public static void Track(long userId, int? pid)
    {
        if (pid is { } id && TryGetStart(id, out var started))
            Add(new Tracked { Pid = id, UserId = userId, StartedTicks = started });
        else
            Pending.Add((userId, DateTime.UtcNow));
    }

    /// <summary>The setting was switched off: put "Roblox" back on every window Orbit renamed.</summary>
    public static void RestoreAll()
    {
        foreach (var t in Windows)
            foreach (var hwnd in WindowsOf(t.Pid))
                if (GetTitle(hwnd) != DefaultTitle) SetWindowText(hwnd, DefaultTitle);
    }

    private static void Tick()
    {
        // Drop windows that were closed.
        var before = Windows.Count;
        foreach (var gone in Windows.Where(t => !IsSameProcess(t)).ToList())
        {
            Windows.Remove(gone);
            // Gone within a minute of starting: probably handed off to a new Roblox process – look for that one.
            var startedUtc = new DateTime(gone.StartedTicks, DateTimeKind.Local).ToUniversalTime();
            if (DateTime.UtcNow - startedUtc < TimeSpan.FromMinutes(1))
                Pending.Add((gone.UserId, startedUtc));
        }
        Pending.RemoveAll(p => DateTime.UtcNow - p.LaunchedUtc > TimeSpan.FromMinutes(2));

        // A launch that handed off to another Roblox process: take the newest one nobody owns yet.
        if (Pending.Count > 0)
        {
            foreach (var proc in RobloxPaths.RunningClients())
            {
                using (proc)
                {
                    if (Pending.Count == 0 || Windows.Any(t => t.Pid == proc.Id)) continue;
                    if (!TryGetStart(proc.Id, out var started)) continue;
                    var startedUtc = new DateTime(started, DateTimeKind.Local).ToUniversalTime();
                    var match = Pending.FindIndex(p => startedUtc >= p.LaunchedUtc.AddSeconds(-2));
                    if (match < 0) continue;
                    Add(new Tracked { Pid = proc.Id, UserId = Pending[match].UserId, StartedTicks = started });
                    Pending.RemoveAt(match);
                }
            }
        }

        if (Windows.Count != before) Save();
        if (!AppState.Settings.ShowAccountNameOnWindows) return;

        foreach (var t in Windows)
        {
            var account = AppState.Accounts.FirstOrDefault(a => a.UserId == t.UserId);
            if (account == null) continue;
            var title = account.Title;
            if (string.IsNullOrWhiteSpace(title)) title = account.Username;

            // Roblox sets its title again now and then (e.g. after a teleport), so keep re-applying it.
            foreach (var hwnd in WindowsOf(t.Pid))
                if (GetTitle(hwnd) != title) SetWindowText(hwnd, title);
        }
    }

    private static void Add(Tracked t)
    {
        Windows.RemoveAll(w => w.Pid == t.Pid);
        Windows.Add(t);
        Save();
    }

    // ---------- Remembering windows across an Orbit restart / update ----------

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<Tracked>>(File.ReadAllText(FilePath));
            if (list != null) Windows.AddRange(list.Where(IsSameProcess));
        }
        catch { /* start fresh */ }
    }

    private static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Windows)); }
        catch { /* cosmetic only */ }
    }

    // ---------- Processes and windows ----------

    private static bool TryGetStart(int pid, out long ticks)
    {
        ticks = 0;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            ticks = p.StartTime.Ticks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameProcess(Tracked t) => TryGetStart(t.Pid, out var ticks) && ticks == t.StartedTicks;

    private static List<IntPtr> WindowsOf(int pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == (uint)pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
                result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const uint GW_OWNER = 4;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hwnd, string text);
}
