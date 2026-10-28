using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Orbit.Services;

public sealed record RamInfo(ulong Total, ulong Free)
{
    public ulong Used => Total - Free;
    public double UsedPercent => Total == 0 ? 0 : Used * 100.0 / Total;
}

/// <summary>Result of one cleanup: how much RAM became free and how many programs were cleaned.</summary>
public sealed record RamCleanResult(long FreedBytes, int Programs);

/// <summary>
/// Frees RAM by asking programs you're not using right now to give back memory they don't need.
/// The window you're in (and the game you play), Orbit itself and the allow list are never touched.
/// It's only really useful when RAM is nearly full – that's why "Needs cleaning" starts at 85 %.
/// </summary>
public static class RamCleaner
{
    /// <summary>From this usage on, the status says "Needs cleaning" (and the automatic mode cleans).</summary>
    public const double NeedsCleaningPercent = 85;
    public const double HighPercent = 70;

    /// <summary>Programs that are never cleaned: Windows itself and things that would only stutter.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "explorer", "fontdrvhost", "audiodg", "MsMpEng", "Memory Compression", "Orbit", "Orbit-Launcher",
    };

    public static event Action? Cleaned;

    public static RamInfo Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? new RamInfo(status.ullTotalPhys, status.ullAvailPhys) : new RamInfo(0, 0);
    }

    public static async Task<RamCleanResult> CleanAsync()
    {
        var before = Read();
        var s = AppState.Settings;
        var allow = new HashSet<string>(s.RamAllowList, StringComparer.OrdinalIgnoreCase);
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        var own = Environment.ProcessId;

        var cleaned = await Task.Run(() =>
        {
            var count = 0;
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        // The window you're in – or the one you used last before switching to Orbit – stays untouched.
                        if (p.Id == own || p.Id == foreground || p.Id == _lastOtherForeground || p.Id <= 4) continue;
                        var name = p.ProcessName;
                        if (Protected.Contains(name) || allow.Contains(name)) continue;
                        var roblox = name.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);
                        if (roblox && !s.RamIncludeRoblox) continue;

                        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                        if (handle == IntPtr.Zero) continue; // not ours / not allowed
                        try { if (EmptyWorkingSet(handle)) count++; }
                        finally { CloseHandle(handle); }
                    }
                    catch { /* the program closed meanwhile */ }
                }
            }
            return count;
        });

        await Task.Delay(400); // let Windows update its numbers
        var after = Read();
        s.RamLastClean = DateTime.Now;
        AppState.SaveSettings();
        var result = new RamCleanResult(Math.Max(0, (long)after.Free - (long)before.Free), cleaned);
        Cleaned?.Invoke();
        return result;
    }

    // ---------------- Automatic cleaning ----------------

    private static DispatcherTimer? _timer, _focusTimer;
    private static bool _busy;
    /// <summary>The program you were in before switching to Orbit (usually the game you play).</summary>
    private static int _lastOtherForeground;

    public static void Start(Dispatcher ui)
    {
        if (_timer != null) return;
        _focusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid != 0 && pid != Environment.ProcessId) _lastOtherForeground = pid;
        }, ui);
        _focusTimer.Start();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) => await AutoTickAsync(), ui);
        _timer.Start();
    }

    private static async Task AutoTickAsync()
    {
        var s = AppState.Settings;
        if (!s.RamAutoClean || _busy) return;
        var since = DateTime.Now - (s.RamLastClean ?? DateTime.MinValue);

        var due = s.RamAutoMode switch
        {
            "30" => since >= TimeSpan.FromMinutes(30),
            "60" => since >= TimeSpan.FromMinutes(60),
            // When RAM is nearly full – but at most every 10 minutes, cleaning again right away doesn't help.
            _ => Read().UsedPercent >= NeedsCleaningPercent && since >= TimeSpan.FromMinutes(10),
        };
        if (!due) return;

        _busy = true;
        try { await CleanAsync(); }
        catch { /* try again next time */ }
        finally { _busy = false; }
    }

    // ---------------- Win32 ----------------

    private const uint PROCESS_SET_QUOTA = 0x0100, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
