using System.Diagnostics;
using System.Runtime.InteropServices;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>
/// High-precision auto clicker (up to ~1000 clicks per second).
/// Clicks where the mouse is, at a fixed spot, or – experimental – directly into
/// Roblox windows in the background (also when they are minimized or not focused).
/// </summary>
public sealed class AutoClicker
{
    public static AutoClicker Instance { get; } = new();

    private Thread? _thread;
    private volatile bool _stop;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Raised (on a background thread) when the clicker starts or stops.</summary>
    public event Action<bool>? RunningChanged;

    public void Toggle(AppSettings s)
    {
        if (IsRunning) Stop();
        else Start(s);
    }

    public void Start(AppSettings s)
    {
        if (IsRunning) return;
        _stop = false;

        var options = new Options(
            Math.Clamp(s.ClickerCps, 1, 1000),
            s.ClickerButton,
            s.ClickerDouble,
            Math.Max(0, s.ClickerLimit),
            s.ClickerRobloxOnly,
            s.ClickerTarget,
            s.ClickerX,
            s.ClickerY);

        _thread = new Thread(() => Run(options))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Orbit AutoClicker",
        };
        _thread.Start();
        RunningChanged?.Invoke(true);
    }

    public void Stop() => _stop = true;

    private sealed record Options(int Cps, string Button, bool Double, int Limit, bool RobloxOnly, string Target, int X, int Y);

    private void Run(Options o)
    {
        Native.timeBeginPeriod(1);
        try
        {
            var interval = Stopwatch.Frequency / (double)o.Cps;
            var watch = Stopwatch.StartNew();
            double next = 0;
            long clicks = 0;
            var windows = new List<IntPtr>();
            long windowsCheckedAt = -10_000;

            while (!_stop)
            {
                if (o.Limit > 0 && clicks >= o.Limit) break;

                if (o.Target == "background")
                {
                    // Refresh the list of Roblox windows once a second.
                    if (Environment.TickCount64 - windowsCheckedAt > 1000)
                    {
                        windows = Native.RobloxWindows();
                        windowsCheckedAt = Environment.TickCount64;
                    }
                    foreach (var hwnd in windows)
                    {
                        Native.PostClick(hwnd, o.Button, o.X, o.Y);
                        if (o.Double) Native.PostClick(hwnd, o.Button, o.X, o.Y);
                    }
                    clicks++;
                }
                else if (!o.RobloxOnly || Native.IsRobloxForeground())
                {
                    if (o.Target == "fixed") Native.SetCursorPos(o.X, o.Y);
                    Native.Click(o.Button);
                    if (o.Double) Native.Click(o.Button);
                    clicks++;
                }

                next += interval;
                // Sleep most of the wait, then spin for precision.
                while (!_stop)
                {
                    var remainingMs = (next - watch.ElapsedTicks) * 1000.0 / Stopwatch.Frequency;
                    if (remainingMs <= 0) break;
                    if (remainingMs > 2) Thread.Sleep((int)(remainingMs - 1.5));
                    else Thread.SpinWait(50);
                }

                // Don't try to "catch up" after a long pause.
                if (watch.ElapsedTicks - next > interval * 5) next = watch.ElapsedTicks;
            }
        }
        finally
        {
            Native.timeEndPeriod(1);
            RunningChanged?.Invoke(false);
        }
    }

    // ---------------- Win32 ----------------

    public static class Native
    {
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint period);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public POINT minPosition, maxPosition;
            public RECT normalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        public static void Click(string button)
        {
            var (down, up) = button switch
            {
                "right" => (0x0008u, 0x0010u),
                "middle" => (0x0020u, 0x0040u),
                _ => (0x0002u, 0x0004u),
            };
            var inputs = new[]
            {
                new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = down } },
                new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = up } },
            };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }

        /// <summary>Sends a click straight to a window without moving the mouse or focusing it.</summary>
        public static void PostClick(IntPtr hwnd, string button, int screenX, int screenY)
        {
            int x, y;
            if (screenX != 0 || screenY != 0)
            {
                var p = new POINT { X = screenX, Y = screenY };
                ScreenToClient(hwnd, ref p);
                (x, y) = (p.X, p.Y);
            }
            else if (!IsIconic(hwnd) && GetClientRect(hwnd, out var rect) && rect.Right > 0)
            {
                (x, y) = (rect.Right / 2, rect.Bottom / 2);
            }
            else
            {
                var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                GetWindowPlacement(hwnd, ref placement);
                var r = placement.normalPosition;
                (x, y) = (Math.Max(1, (r.Right - r.Left) / 2), Math.Max(1, (r.Bottom - r.Top) / 2));
            }

            var lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
            var (downMsg, upMsg, flag) = button switch
            {
                "right" => (0x0204u, 0x0205u, 0x0002),
                "middle" => (0x0207u, 0x0208u, 0x0010),
                _ => (0x0201u, 0x0202u, 0x0001),
            };
            PostMessage(hwnd, 0x0200, IntPtr.Zero, lParam); // WM_MOUSEMOVE
            PostMessage(hwnd, downMsg, (IntPtr)flag, lParam);
            PostMessage(hwnd, upMsg, IntPtr.Zero, lParam);
        }

        public static List<IntPtr> RobloxWindows()
        {
            var result = new List<IntPtr>();
            foreach (var p in RobloxPaths.RunningClients())
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) result.Add(p.MainWindowHandle); }
                catch { /* exited */ }
                finally { p.Dispose(); }
            }
            return result;
        }

        private static uint _robloxPid;
        private static long _checkedAt;

        public static bool IsRobloxForeground()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid == 0) return false;
            if (pid == _robloxPid) return true;
            if (Environment.TickCount64 - _checkedAt < 250) return false;
            _checkedAt = Environment.TickCount64;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                var isRoblox = p.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);
                if (isRoblox) _robloxPid = pid;
                return isRoblox;
            }
            catch
            {
                return false;
            }
        }
    }
}
