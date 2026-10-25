using System.Diagnostics;
using System.Runtime.InteropServices;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>High-precision auto clicker using SendInput (up to ~1000 clicks per second).</summary>
public sealed class AutoClicker
{
    public static AutoClicker Instance { get; } = new();

    private Thread? _thread;
    private volatile bool _stop;
    private long _clicks;

    public bool IsRunning => _thread is { IsAlive: true };
    public long Clicks => Interlocked.Read(ref _clicks);

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
        Interlocked.Exchange(ref _clicks, 0);

        var cps = Math.Clamp(s.ClickerCps, 1, 1000);
        var button = s.ClickerButton;
        var isDouble = s.ClickerDouble;
        var limit = Math.Max(0, s.ClickerLimit);
        var robloxOnly = s.ClickerRobloxOnly;
        var fixedPos = s.ClickerFixedPosition;
        var x = s.ClickerX;
        var y = s.ClickerY;

        _thread = new Thread(() => Run(cps, button, isDouble, limit, robloxOnly, fixedPos, x, y))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Orbit AutoClicker",
        };
        _thread.Start();
        RunningChanged?.Invoke(true);
    }

    public void Stop()
    {
        _stop = true;
    }

    private void Run(int cps, string button, bool isDouble, int limit, bool robloxOnly, bool fixedPos, int x, int y)
    {
        Native.timeBeginPeriod(1);
        try
        {
            var interval = Stopwatch.Frequency / (double)cps;
            var watch = Stopwatch.StartNew();
            double next = 0;

            while (!_stop)
            {
                if (limit > 0 && Clicks >= limit) break;

                if (!robloxOnly || Native.IsRobloxForeground())
                {
                    if (fixedPos) Native.SetCursorPos(x, y);
                    Native.Click(button);
                    if (isDouble) Native.Click(button);
                    Interlocked.Increment(ref _clicks);
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

                // Don't try to "catch up" after a long pause (e.g. window not focused).
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

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

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

        private const uint INPUT_MOUSE = 0;

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
                new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = down } },
                new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = up } },
            };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }

        private static uint _robloxPidCache;
        private static long _robloxPidCheckedAt;

        public static bool IsRobloxForeground()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid == 0) return false;
            if (pid == _robloxPidCache) return true;

            // Cache the lookup for a moment; Process.GetProcessById is comparatively slow.
            if (Environment.TickCount64 - _robloxPidCheckedAt < 250 && pid != _robloxPidCache) return false;
            _robloxPidCheckedAt = Environment.TickCount64;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                var isRoblox = p.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);
                if (isRoblox) _robloxPidCache = pid;
                return isRoblox;
            }
            catch
            {
                return false;
            }
        }
    }
}
