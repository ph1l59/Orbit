using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Orbit.Services;

/// <summary>
/// Roblox disconnects players after 20 minutes without input. When switched on, Orbit presses a key
/// in every Roblox window now and then:
/// - background: the key is sent to the window without moving the focus (works while you use the PC,
///   but not every game counts it as activity);
/// - focus: each window comes to the front for a moment and gets a real key press, then your window
///   gets the focus back – only while you haven't touched mouse or keyboard for a few seconds.
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
        if (s.AntiAfkMode == "focus" && IdleTime() < TimeSpan.FromSeconds(5))
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
            var focusMode = AppState.Settings.AntiAfkMode == "focus";
            var windows = RobloxWindows.All();
            var count = 0;

            if (!focusMode)
            {
                foreach (var w in windows)
                {
                    PostKey(w.Handle, VK_SPACE, 0x39);
                    count++;
                    await Task.Delay(40);
                }
            }
            else
            {
                var before = GetForegroundWindow();
                foreach (var w in windows)
                {
                    if (IsIconic(w.Handle)) continue; // minimized windows can't take the focus without popping up
                    if (!force && IdleTime() < TimeSpan.FromSeconds(3)) break; // you came back – stop
                    RobloxWindows.BringToFront(w.Handle);
                    await Task.Delay(80);
                    SendKey(0x39); // Space, by scan code like a real keyboard
                    count++;
                    await Task.Delay(60);
                }
                if (before != IntPtr.Zero) RobloxWindows.BringToFront(before);
            }

            LastCount = count;
            return count;
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Background mode: the window "gets activated" and receives the key, without the focus moving.</summary>
    private static void PostKey(IntPtr hwnd, int vk, uint scan)
    {
        PostMessage(hwnd, WM_ACTIVATE, (IntPtr)WA_ACTIVE, IntPtr.Zero);
        PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)(1 | (scan << 16)));
        PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)(1 | (scan << 16) | (1u << 30) | (1u << 31)));
    }

    private static void SendKey(ushort scan)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE } },
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    // ---------------- Win32 ----------------

    private const uint WM_ACTIVATE = 0x0006, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WA_ACTIVE = 1, VK_SPACE = 0x20;
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
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
