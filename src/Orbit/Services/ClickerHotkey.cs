using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Orbit.Services;

/// <summary>Global hotkey for the auto clicker (works while Roblox has focus).</summary>
public static class ClickerHotkey
{
    private const int HotkeyId = 0x0B17;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static HwndSource? _source;
    private static Thread? _holdThread;
    private static volatile bool _holdStop;

    public static readonly string[] Keys =
        { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" };

    public static uint VirtualKey(string name)
        => name.Length > 1 && name[0] == 'F' && int.TryParse(name[1..], out var n) && n is >= 1 and <= 12
            ? (uint)(0x70 + n - 1)
            : 0x75; // F6

    public static void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        Apply();
    }

    /// <summary>Re-registers the hotkey after the settings changed. Returns false if the key is taken.</summary>
    public static bool Apply()
    {
        var s = AppState.Settings;
        StopHoldWatcher();
        if (_source == null) return false;

        UnregisterHotKey(_source.Handle, HotkeyId);
        var vk = VirtualKey(s.ClickerHotkey);

        if (s.ClickerMode == "hold")
        {
            StartHoldWatcher(vk);
            return true;
        }

        return RegisterHotKey(_source.Handle, HotkeyId, MOD_NOREPEAT, vk);
    }

    public static void Detach()
    {
        StopHoldWatcher();
        if (_source != null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            AutoClicker.Instance.Toggle(AppState.Settings);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void StartHoldWatcher(uint vk)
    {
        _holdStop = false;
        _holdThread = new Thread(() =>
        {
            while (!_holdStop)
            {
                var down = (AutoClicker.Native.GetAsyncKeyState((int)vk) & 0x8000) != 0;
                var clicker = AutoClicker.Instance;
                if (down && !clicker.IsRunning) clicker.Start(AppState.Settings);
                else if (!down && clicker.IsRunning) clicker.Stop();
                Thread.Sleep(10);
            }
        })
        { IsBackground = true, Name = "Orbit hotkey" };
        _holdThread.Start();
    }

    private static void StopHoldWatcher()
    {
        _holdStop = true;
        _holdThread = null;
    }
}
