using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Orbit.Services;

/// <summary>
/// Roblox disconnects players after 20 minutes without input. When switched on, Orbit presses Space
/// in every Roblox window now and then: each window comes to the front for a moment and gets a real
/// key press, then your window gets the focus back – only while you haven't touched mouse or keyboard
/// for a few seconds.
/// Nothing is read from or written into the game.
/// </summary>
public static class AntiAfk
{
    private static DispatcherTimer? _timer;
    private static DateTime _next = DateTime.MaxValue;
    private static bool _running;

    public static event Action? Changed;

    /// <summary>When the next key press is due (null = off).</summary>
    public static DateTime? Next => AppState.Settings.AntiAfk ? _next : null;

    /// <summary>Number of windows that got the last key press.</summary>
    public static int LastCount { get; private set; }

    public static void Start(Dispatcher ui)
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await TickAsync(), ui);
        _timer.Start();
        Refresh();
    }

    /// <summary>The setting changed: plan the next press from now.</summary>
    public static void Refresh()
    {
        var s = AppState.Settings;
        _next = s.AntiAfk ? DateTime.Now.AddMinutes(Math.Clamp(s.AntiAfkMinutes, 1, 19)) : DateTime.MaxValue;
        Changed?.Invoke();
    }

    /// <summary>Presses the key right now (the "Test" button).</summary>
    public static Task<int> RunNowAsync() => PressAllAsync(force: true);

    private static async Task TickAsync()
    {
        var s = AppState.Settings;
        if (!s.AntiAfk || _running || DateTime.Now < _next) return;

        // Focus mode waits until you're away from mouse and keyboard; try again in 30 s.
        if (IdleTime() < TimeSpan.FromSeconds(5))
        {
            _next = DateTime.Now.AddSeconds(30);
            Changed?.Invoke();
            return;
        }

        await PressAllAsync(force: false);
        _next = DateTime.Now.AddMinutes(Math.Clamp(s.AntiAfkMinutes, 1, 19));
        Changed?.Invoke();
    }

    private static async Task<int> PressAllAsync(bool force)
    {
        _running = true;
        try
        {
            var windows = RobloxWindows.All();
            var count = 0;

            {
                MarkInjected();
                var before = GetForegroundWindow();
                foreach (var w in windows)
                {
                    if (IsIconic(w.Handle)) continue; // minimized windows can't take the focus without popping up
                    if (!force && UserCameBack()) break; // you touched mouse or keyboard – stop

                    RobloxWindows.BringToFront(w.Handle);
                    MarkInjected();
                    // Wait until Windows really gave it the focus, then a bit more so Roblox notices it.
                    for (var i = 0; i < 25 && GetForegroundWindow() != w.Handle; i++) await Task.Delay(20);
                    if (GetForegroundWindow() != w.Handle) continue;
                    await Task.Delay(250);

                    // Space, by scan code like a real keyboard – held for a moment, Roblox reads keys once per frame.
                    SendKey(0x39, up: false);
                    MarkInjected();
                    await Task.Delay(150);
                    SendKey(0x39, up: true);
                    MarkInjected();
                    count++;
                    await Task.Delay(120);
                }
                if (before != IntPtr.Zero)
                {
                    RobloxWindows.BringToFront(before);
                    MarkInjected();
                }
            }

            LastCount = count;
            return count;
        }
        finally
        {
            _running = false;
        }
    }

    private static void SendKey(ushort scan, bool up)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // Our own key presses count as input for Windows too – remember when we sent the last one,
    // so only input that came after it means you're back.
    private static int _lastInjected;
    private static void MarkInjected() => _lastInjected = Environment.TickCount;

    private static bool UserCameBack()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return false;
        return unchecked((int)info.dwTime - _lastInjected) > 100;
    }

    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    // ---------------- Win32 ----------------

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    /// <summary>INPUT with the keyboard part; padded to the size of the biggest union member (MOUSEINPUT).</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] private MOUSEPAD pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEPAD { public int dx, dy; public uint data, flags, time; public IntPtr extra; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
