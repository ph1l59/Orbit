using System.Diagnostics;
using System.Runtime.InteropServices;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>
/// High-precision auto clicker (up to 100 clicks per second).
/// Clicks where the mouse is or at a fixed spot on the screen.
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
            Math.Clamp(s.ClickerCps, 1, 100),
            s.ClickerButton,
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

    private sealed record Options(int Cps, string Button, bool RobloxOnly, string Target, int X, int Y);

    private void Run(Options o)
    {
        Native.timeBeginPeriod(1);
        try
        {
            var interval = Stopwatch.Frequency / (double)o.Cps;
            var watch = Stopwatch.StartNew();
            double next = 0;

            while (!_stop)
            {
                if (!o.RobloxOnly || Native.IsRobloxForeground())
                {
                    if (o.Target == "fixed") Native.SetCursorPos(o.X, o.Y);
                    Native.Click(o.Button);
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
