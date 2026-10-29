using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Orbit.Services;

/// <summary>One open Roblox window.</summary>
public sealed record RobloxWindow(IntPtr Handle, int Pid, long UserId);

/// <summary>
/// The open Roblox windows: arrange them side by side on one screen, put them back,
/// and the resource saver (windows in the background get less CPU than the one you play in).
/// </summary>
public static class RobloxWindows
{
    // ---------------- Finding the windows ----------------

    public static List<RobloxWindow> All()
    {
        var pids = new HashSet<int>();
        foreach (var p in RobloxPaths.RunningClients())
            using (p) pids.Add(p.Id);

        var result = new List<RobloxWindow>();
        if (pids.Count == 0) return result;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (pids.Contains((int)owner) && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero &&
                GetWindowTextLength(hwnd) > 0)
                result.Add(new RobloxWindow(hwnd, (int)owner, WindowTitles.UserIdOf((int)owner)));
            return true;
        }, IntPtr.Zero);

        // Same order as on the Accounts page; windows Orbit didn't launch come last.
        var order = AppState.Accounts.Select((a, i) => (a.UserId, i)).ToDictionary(x => x.UserId, x => x.i);
        return result.OrderBy(w => order.TryGetValue(w.UserId, out var i) ? i : int.MaxValue).ThenBy(w => w.Pid).ToList();
    }

    public static int Count => All().Count;

    // ---------------- Arrange / put back ----------------

    private static readonly Dictionary<IntPtr, (RECT Rect, bool Maximized, bool Fullscreen)> Original = new();

    public static bool CanRestore => Original.Keys.Any(IsWindow);

    /// <summary>
    /// Makes every Roblox window as small as Roblox allows and stacks them exactly on top of each other
    /// in the middle of the given screen (index into Screen.AllScreens). Returns the number arranged.
    /// </summary>
    public static async Task<int> ArrangeAsync(int screenIndex)
    {
        var windows = All();
        if (windows.Count == 0) return 0;

        // 0. Windows in fullscreen have no title bar to move or close them – take them out of fullscreen first.
        foreach (var win in windows)
            if (!Original.ContainsKey(win.Handle) && GetWindowRect(win.Handle, out var before))
                Original[win.Handle] = (before, IsZoomed(win.Handle), IsFullscreen(win.Handle));
        var fullscreen = windows.Where(w => IsFullscreen(w.Handle)).ToList();
        if (fullscreen.Count > 0) await ExitFullscreenAsync(fullscreen.Select(w => w.Handle).ToList());

        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = screenIndex >= 0 && screenIndex < screens.Length ? screens[screenIndex] : System.Windows.Forms.Screen.PrimaryScreen!;
        var area = screen.WorkingArea;

        // 1. Shrink every window. Roblox has its own smallest size and keeps the window at least that big,
        //    so ask for a tiny size and measure what we really got.
        int w = SmallestWidth, h = SmallestHeight;
        foreach (var win in windows)
        {
            var hwnd = win.Handle;
            if (IsIconic(hwnd) || IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            MoveVisible(hwnd, area.Left, area.Top, SmallestWidth, SmallestHeight);
            var size = VisibleSize(hwnd);
            w = Math.Max(w, size.Width);
            h = Math.Max(h, size.Height);
        }
        w = Math.Min(w, area.Width);
        h = Math.Min(h, area.Height);

        // 2. All exactly on top of each other in the middle of the screen.
        var x = area.Left + (area.Width - w) / 2;
        var y = area.Top + (area.Height - h) / 2;
        foreach (var win in windows)
            MoveVisible(win.Handle, x, y, w, h);

        // Bring them to the front in order.
        foreach (var win in windows)
            SetWindowPos(win.Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        return windows.Count;
    }

    /// <summary>Fullscreen = no title bar and the window covers its whole screen.</summary>
    private static bool IsFullscreen(IntPtr hwnd)
    {
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_CAPTION) == WS_CAPTION) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>
    /// Leaves Roblox's fullscreen: first the way Roblox itself does it (F11), and if the window
    /// still has no title bar, Orbit gives it a normal window frame back.
    /// </summary>
    private static async Task ExitFullscreenAsync(List<IntPtr> windows)
    {
        foreach (var hwnd in windows) PressF11(hwnd);

        for (var i = 0; i < 8 && windows.Any(IsFullscreen); i++)
            await Task.Delay(100);

        foreach (var hwnd in windows.Where(IsFullscreen))
        {
            var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
            style = (style & ~WS_POPUP) | WS_OVERLAPPEDWINDOW;
            SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    private static void PressF11(IntPtr hwnd)
    {
        const uint scan = 0x57;
        PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_F11, (IntPtr)(1 | (scan << 16)));
        PostMessage(hwnd, WM_KEYUP, (IntPtr)VK_F11, unchecked((IntPtr)(1 | (scan << 16) | (1u << 30) | (1u << 31))));
    }

    /// <summary>Smallest size Orbit asks for (Roblox makes it bigger if it needs to).</summary>
    private const int SmallestWidth = 320, SmallestHeight = 180;

    private static (int Width, int Height) VisibleSize(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>()) == 0)
            return (visible.Right - visible.Left, visible.Bottom - visible.Top);
        return GetWindowRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (SmallestWidth, SmallestHeight);
    }

    /// <summary>Moves a window so its visible part (without Windows' invisible resize border) lands exactly there.</summary>
    private static void MoveVisible(IntPtr hwnd, int x, int y, int w, int h)
    {
        int left = 0, top = 0, right = 0, bottom = 0;
        if (GetWindowRect(hwnd, out var outer) &&
            DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>()) == 0)
        {
            left = visible.Left - outer.Left;
            top = visible.Top - outer.Top;
            right = outer.Right - visible.Right;
            bottom = outer.Bottom - visible.Bottom;
        }
        SetWindowPos(hwnd, IntPtr.Zero, x - left, y - top, w + left + right, h + top + bottom, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Gives one arranged window its old size and place back.</summary>
    public static void RestoreOne(IntPtr hwnd)
    {
        if (!Original.Remove(hwnd, out var o) || !IsWindow(hwnd)) return;
        SetWindowPos(hwnd, IntPtr.Zero, o.Rect.Left, o.Rect.Top, o.Rect.Right - o.Rect.Left, o.Rect.Bottom - o.Rect.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        if (o.Maximized) ShowWindow(hwnd, SW_MAXIMIZE);
        if (o.Fullscreen) PressF11(hwnd);
    }

    /// <summary>Puts the windows back where they were before arranging.</summary>
    public static int Restore()
    {
        var count = 0;
        foreach (var (hwnd, (rect, maximized, fullscreen)) in Original)
        {
            if (!IsWindow(hwnd)) continue;
            SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, SWP_NOZORDER | SWP_NOACTIVATE);
            if (maximized) ShowWindow(hwnd, SW_MAXIMIZE);
            if (fullscreen) PressF11(hwnd); // back into Roblox's fullscreen
            count++;
        }
        Original.Clear();
        return count;
    }

    // ---------------- Single windows (Overview page) ----------------

    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    /// <summary>Brings a Roblox window to the front (restores it if it's minimized).</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        // Windows only lets the program in front hand over the focus; a short Alt press counts as input.
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Closes a Roblox window the normal way (like its X button).</summary>
    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Puts every Roblox window behind the given window (Orbit), without activating them.</summary>
    public static void PushBehind(IntPtr window)
    {
        foreach (var w in All())
            SetWindowPos(w.Handle, window, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Memory the Roblox process uses right now (bytes).</summary>
    public static long MemoryOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.WorkingSet64; }
        catch { return 0; }
    }

    // ---------------- Resource saver ----------------

    private static DispatcherTimer? _saverTimer;
    /// <summary>Process id → true if it is currently slowed down.</summary>
    private static readonly Dictionary<int, bool> Throttled = new();
    private static int _lastPlayed;

    /// <summary>Windows Windows didn't let Orbit change.</summary>
    private static readonly HashSet<int> Failed = new();
    private static DateTime _lastVerify;

    /// <summary>True if this Roblox process is slowed down right now.</summary>
    public static bool IsThrottled(int pid) => Throttled.TryGetValue(pid, out var slow) && slow;

    /// <summary>A saver setting changed (CPU limit, efficiency mode): apply it again to every window.</summary>
    public static void ReapplySaver(Dispatcher ui)
    {
        Throttled.Clear();
        RefreshSaver(ui);
    }

    /// <summary>Number of windows that are slowed down right now (checked: Windows reports "Below normal").</summary>
    public static int ThrottledCount => Throttled.Count(kv => kv.Value);

    /// <summary>Number of windows whose priority couldn't be changed.</summary>
    public static int FailedCount => Failed.Count;

    public static event Action? SaverChanged;

    /// <summary>Starts or stops the resource saver according to the setting.</summary>
    public static void RefreshSaver(Dispatcher ui)
    {
        if (AppState.Settings.ResourceSaver)
        {
            _saverTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SaverTick(), ui);
            _saverTimer.Start();
            SaverTick();
        }
        else
        {
            _saverTimer?.Stop();
            RestoreAllPriorities();
        }
    }

    private static void SaverTick()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        var clients = RobloxPaths.RunningClients();
        try
        {
            var ids = clients.Select(p => p.Id).ToHashSet();
            // The window you're in stays fast. While you're outside Roblox (e.g. in Orbit or a browser),
            // the Roblox window you used last stays fast.
            if (ids.Contains((int)foreground)) _lastPlayed = (int)foreground;
            else if (!ids.Contains(_lastPlayed)) _lastPlayed = clients.Length == 1 ? clients[0].Id : 0;

            var changed = false;
            // Every few seconds: check that the priority is really still set (Roblox could reset it).
            var verify = DateTime.UtcNow - _lastVerify > TimeSpan.FromSeconds(5);
            if (verify) _lastVerify = DateTime.UtcNow;

            foreach (var p in clients)
            {
                var slow = clients.Length > 1 && p.Id != _lastPlayed;
                if (Throttled.TryGetValue(p.Id, out var current) && current == slow && !(verify && slow && !IsSlow(p))) continue;
                var ok = SetSlow(p, slow);
                if (ok)
                {
                    changed |= !Throttled.TryGetValue(p.Id, out var was) || was != slow;
                    Throttled[p.Id] = slow;
                    changed |= Failed.Remove(p.Id);
                }
                else { Throttled.Remove(p.Id); changed |= Failed.Add(p.Id); }
            }

            foreach (var gone in Throttled.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                Throttled.Remove(gone);
                changed = true;
            }
            foreach (var gone in Jobs.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                CloseHandle(Jobs[gone]);
                Jobs.Remove(gone);
            }
            changed |= Failed.RemoveWhere(id => !ids.Contains(id)) > 0;
            if (changed) SaverChanged?.Invoke();
        }
        finally
        {
            foreach (var p in clients) p.Dispose();
        }
    }

    private static bool IsSlow(Process p)
    {
        try { p.Refresh(); return p.PriorityClass == ProcessPriorityClass.BelowNormal; }
        catch { return false; }
    }

    /// <summary>
    /// Background window: lower priority and only some of the CPU cores. Otherwise normal.
    /// Returns true only if Windows afterwards really reports the new priority.
    /// </summary>
    private static bool SetSlow(Process p, bool slow)
    {
        try
        {
            var cores = Environment.ProcessorCount;
            var all = cores >= 64 ? -1L : (1L << cores) - 1;
            p.PriorityClass = slow ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            if (cores >= 4 && cores < 64)
            {
                // The upper half of the cores (at least 2); the lower ones stay free for the game you play.
                var keep = Math.Max(2, cores / 2);
                var upper = all & ~((1L << (cores - keep)) - 1);
                p.ProcessorAffinity = (IntPtr)(slow ? upper : all);
            }
            ApplyLimits(p.Id, slow);
            // Ask Windows again whether it really took the change.
            p.Refresh();
            return p.PriorityClass == (slow ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal);
        }
        catch
        {
            return false; // the window closed or Windows didn't allow it
        }
    }

    // ---------------- CPU limit and efficiency mode ----------------

    /// <summary>Process id → Orbit's own job object that holds the CPU limit for that window.</summary>
    private static readonly Dictionary<int, IntPtr> Jobs = new();

    /// <summary>
    /// Background window: a hard CPU limit (a Windows job object – Roblox's frame rate drops with it)
    /// and Windows' efficiency mode. Foreground window: both off. Failures here are not fatal.
    /// </summary>
    private static void ApplyLimits(int pid, bool slow)
    {
        var s = AppState.Settings;
        var cap = slow ? Math.Clamp(s.SaverCpuCap, 0, 100) : 0;
        try
        {
            if (cap > 0 || Jobs.ContainsKey(pid))
            {
                if (!Jobs.TryGetValue(pid, out var job))
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var process = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE, false, pid);
                    var assigned = job != IntPtr.Zero && process != IntPtr.Zero && AssignProcessToJobObject(job, process);
                    if (process != IntPtr.Zero) CloseHandle(process);
                    if (!assigned) { if (job != IntPtr.Zero) CloseHandle(job); job = IntPtr.Zero; }
                    else Jobs[pid] = job;
                }
                if (job != IntPtr.Zero)
                {
                    var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                    {
                        ControlFlags = cap > 0 ? JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP : 0,
                        CpuRate = (uint)(cap * 100), // in 1/100 percent
                    };
                    SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>());
                }
            }

            var handle = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (handle != IntPtr.Zero)
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = slow && s.SaverEco ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
                };
                SetProcessInformation(handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                CloseHandle(handle);
            }
        }
        catch { /* optional extras */ }
    }

    private static void ReleaseJobs()
    {
        foreach (var (pid, job) in Jobs)
        {
            var off = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION();
            SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref off, (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>());
            CloseHandle(job);
        }
        Jobs.Clear();
    }

    private static void RestoreAllPriorities()
    {
        if (Throttled.Count == 0 && Failed.Count == 0 && Jobs.Count == 0) return;
        foreach (var p in RobloxPaths.RunningClients())
            using (p)
                if (Throttled.ContainsKey(p.Id)) SetSlow(p, false);
        ReleaseJobs();
        Throttled.Clear();
        Failed.Clear();
        SaverChanged?.Invoke();
    }

    /// <summary>Orbit quits: give every window its full speed back.</summary>
    public static void StopSaver()
    {
        _saverTimer?.Stop();
        RestoreAllPriorities();
    }

    // ---------------- Win32 ----------------

    private const uint WM_CLOSE = 0x0010, KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_MENU = 0x12;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private const uint PROCESS_TERMINATE = 0x0001, PROCESS_SET_QUOTA = 0x0100, PROCESS_SET_INFORMATION = 0x0200;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1, JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;
    private const int JobObjectCpuRateControlInformation = 15, ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { public uint ControlFlags; public uint CpuRate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info, uint length);
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint length);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000, WS_POPUP = 0x80000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint SWP_FRAMECHANGED = 0x0020, MONITOR_DEFAULTTONEAREST = 2;
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int VK_F11 = 0x7A;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private const uint GW_OWNER = 4;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const int SW_MAXIMIZE = 3, SW_RESTORE = 9;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
