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

    private static readonly Dictionary<IntPtr, (RECT Rect, bool Maximized)> Original = new();

    public static bool CanRestore => Original.Keys.Any(IsWindow);

    /// <summary>
    /// Makes every Roblox window as small as Roblox allows and stacks them exactly on top of each other
    /// in the middle of the given screen (index into Screen.AllScreens). Returns the number arranged.
    /// </summary>
    public static int Arrange(int screenIndex)
    {
        var windows = All();
        if (windows.Count == 0) return 0;

        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = screenIndex >= 0 && screenIndex < screens.Length ? screens[screenIndex] : System.Windows.Forms.Screen.PrimaryScreen!;
        var area = screen.WorkingArea;

        // 1. Shrink every window. Roblox has its own smallest size and keeps the window at least that big,
        //    so ask for a tiny size and measure what we really got.
        int w = SmallestWidth, h = SmallestHeight;
        foreach (var win in windows)
        {
            var hwnd = win.Handle;
            if (!Original.ContainsKey(hwnd) && GetWindowRect(hwnd, out var rect))
                Original[hwnd] = (rect, IsZoomed(hwnd));
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

    /// <summary>Puts the windows back where they were before arranging.</summary>
    public static int Restore()
    {
        var count = 0;
        foreach (var (hwnd, (rect, maximized)) in Original)
        {
            if (!IsWindow(hwnd)) continue;
            SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, SWP_NOZORDER | SWP_NOACTIVATE);
            if (maximized) ShowWindow(hwnd, SW_MAXIMIZE);
            count++;
        }
        Original.Clear();
        return count;
    }

    // ---------------- Resource saver ----------------

    private static DispatcherTimer? _saverTimer;
    /// <summary>Process id → true if it is currently slowed down.</summary>
    private static readonly Dictionary<int, bool> Throttled = new();
    private static int _lastPlayed;

    /// <summary>Windows Windows didn't let Orbit change.</summary>
    private static readonly HashSet<int> Failed = new();
    private static DateTime _lastVerify;

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
            // Ask Windows again whether it really took the change.
            p.Refresh();
            return p.PriorityClass == (slow ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal);
        }
        catch
        {
            return false; // the window closed or Windows didn't allow it
        }
    }

    private static void RestoreAllPriorities()
    {
        if (Throttled.Count == 0 && Failed.Count == 0) return;
        foreach (var p in RobloxPaths.RunningClients())
            using (p)
                if (Throttled.ContainsKey(p.Id)) SetSlow(p, false);
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
