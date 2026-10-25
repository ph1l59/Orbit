using System.Windows.Input;

namespace Orbit.Services;

/// <summary>
/// Global hotkey for the auto clicker. Polls the key state, so it works everywhere
/// (also while Roblox is focused) and supports mouse buttons 4/5 too.
/// </summary>
public static class ClickerHotkey
{
    private static Thread? _thread;
    private static volatile bool _stop;

    /// <summary>True while the user is choosing a new hotkey.</summary>
    public static volatile bool Suspended;

    public const int ModCtrl = 1, ModAlt = 2, ModShift = 4;

    public static void Start()
    {
        if (_thread is { IsAlive: true }) return;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Orbit hotkey" };
        _thread.Start();
    }

    public static void Stop() => _stop = true;

    private static bool Down(int vk) => (AutoClicker.Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsPressed(int vk, int mods)
    {
        if (vk == 0 || !Down(vk)) return false;
        if (Down(0x11) != ((mods & ModCtrl) != 0)) return false;  // Ctrl
        if (Down(0x12) != ((mods & ModAlt) != 0)) return false;   // Alt
        if (Down(0x10) != ((mods & ModShift) != 0)) return false; // Shift
        return true;
    }

    private static void Loop()
    {
        var wasDown = false;
        while (!_stop)
        {
            Thread.Sleep(8);
            if (Suspended)
            {
                wasDown = true; // ignore the key that was just assigned
                continue;
            }

            var s = AppState.Settings;
            var clicker = AutoClicker.Instance;
            var down = IsPressed(s.ClickerHotkeyVk, s.ClickerHotkeyMods);

            if (s.ClickerMode == "hold")
            {
                if (down && !clicker.IsRunning) clicker.Start(s);
                else if (!down && clicker.IsRunning) clicker.Stop();
            }
            else if (down && !wasDown)
            {
                clicker.Toggle(s);
            }

            wasDown = down;
        }
    }

    /// <summary>Human readable name, e.g. "Ctrl + F6" or "Mouse 4".</summary>
    public static string Describe(int vk, int mods)
    {
        var parts = new List<string>();
        if ((mods & ModCtrl) != 0) parts.Add("Ctrl");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }

    private static string KeyName(int vk) => vk switch
    {
        0x04 => "Middle mouse",
        0x05 => "Mouse 4",
        0x06 => "Mouse 5",
        _ => KeyInterop.KeyFromVirtualKey(vk) switch
        {
            Key.None => $"Key {vk}",
            var k when k >= Key.D0 && k <= Key.D9 => ((int)(k - Key.D0)).ToString(),
            var k => k.ToString(),
        },
    };
}
