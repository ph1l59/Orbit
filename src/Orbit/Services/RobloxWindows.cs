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
    /// <summary>Roblox doesn't get much smaller than this (it refuses smaller sizes itself).</summary>
    private const int MinWidth = 400, MinHeight = 300;

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
    /// Puts every Roblox window into a grid on the given screen (index into Screen.AllScreens).
    /// If they don't all fit at Roblox's smallest size, they overlap a little like cards,
    /// so every title bar stays visible. Returns the number of windows arranged.
    /// </summary>
    public static int Arrange(int screenIndex)
    {
        var windows = All();
        if (windows.Count == 0) return 0;

        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = screenIndex >= 0 && screenIndex < screens.Length ? screens[screenIndex] : System.Windows.Forms.Screen.PrimaryScreen!;
        var area = screen.WorkingArea;

        var n = windows.Count;
        var (cols, rows) = BestGrid(n, area.Width, area.Height);
        var cellW = area.Width / cols;
        var cellH = area.Height / rows;
        var w = Math.Max(cellW, MinWidth);
        var h = Math.Max(cellH, MinHeight);
        w = Math.Min(w, area.Width);
        h = Math.Min(h, area.Height);
        // Spread over the whole area; with enough room the steps equal the cell size (no overlap).
        var stepX = cols > 1 ? (area.Width - w) / (double)(cols - 1) : 0;
        var stepY = rows > 1 ? (area.Height - h) / (double)(rows - 1) : 0;

        for (var i = 0; i < n; i++)
        {
            var hwnd = windows[i].Handle;
            if (!Original.ContainsKey(hwnd) && GetWindowRect(hwnd, out var rect))
                Original[hwnd] = (rect, IsZoomed(hwnd));

            var col = i % cols;
            var row = i / cols;
            var x = area.Left + (int)Math.Round(col * stepX);
            var y = area.Top + (int)Math.Round(row * stepY);

            if (IsIconic(hwnd) || IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            MoveVisible(hwnd, x, y, w, h);
        }

        // Bring them to the front in order, so later (lower) cards lie on top of earlier ones.
        foreach (var win in windows)
            SetWindowPos(win.Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        return n;
    }

    /// <summary>Columns × rows that give each window the biggest 16:9 picture.</summary>
    private static (int Cols, int Rows) BestGrid(int n, int width, int height)
    {
        var best = (Cols: 1, Rows: n);
        double bestScore = -1;
        for (var cols = 1; cols <= n; cols++)
        {
            var rows = (int)Math.Ceiling(n / (double)cols);
            double cw = width / (double)cols, ch = height / (double)rows;
            var picture = Math.Min(cw, ch * 16 / 9);            // width of a 16:9 picture that fits the cell
            var score = picture * picture * 9 / 16 - (cols * rows - n) * 50; // a few empty cells are ok, but less is better
            if (score > bestScore) { bestScore = score; best = (cols, rows); }
        }
        return best;
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

    /// <summary>Number of windows that are slowed down right now.</summary>
    public static int ThrottledCount => Throttled.Count(kv => kv.Value);

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
            foreach (var p in clients)
            {
                var slow = clients.Length > 1 && p.Id != _lastPlayed;
                if (Throttled.TryGetValue(p.Id, out var current) && current == slow) continue;
                if (SetSlow(p, slow)) { Throttled[p.Id] = slow; changed = true; }
            }

            foreach (var gone in Throttled.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                Throttled.Remove(gone);
                changed = true;
            }
            if (changed) SaverChanged?.Invoke();
        }
        finally
        {
            foreach (var p in clients) p.Dispose();
        }
    }

    /// <summary>Background window: lower priority and only some of the CPU cores. Otherwise normal.</summary>
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
            return true;
        }
        catch
        {
            return false; // the window closed or Windows didn't allow it
        }
    }

    private static void RestoreAllPriorities()
    {
        if (Throttled.Count == 0) return;
        foreach (var p in RobloxPaths.RunningClients())
            using (p)
                if (Throttled.ContainsKey(p.Id)) SetSlow(p, false);
        Throttled.Clear();
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
