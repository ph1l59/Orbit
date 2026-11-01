using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Orbit.Models;
using Orbit.Views;

namespace Orbit.Services;

/// <summary>
/// Orbit's own game filter. Roblox itself is never touched: Windows applies a color matrix to the
/// picture on the screen (the same feature the Windows color filters use), and only while a Roblox
/// window is in front. Vignette and letterbox are a click-through layer over the Roblox window.
/// Hotkeys: Ctrl+Alt+F on/off, Ctrl+Alt+G next profile.
/// </summary>
public static class GameFilter
{
    private static DispatcherTimer? _timer;
    private static bool _magReady;
    private static float[]? _shown;
    private static FilterOverlay? _overlay;
    private static HwndSource? _hotkeySource;

    /// <summary>The Game filter page asks for the filter on the whole screen while you tune it.</summary>
    public static bool PreviewOnScreen { get; set; }

    /// <summary>Something changed (on/off, profile) – e.g. by hotkey.</summary>
    public static event Action? Changed;

    public static List<GameFilterProfile> Profiles
    {
        get
        {
            var s = AppState.Settings;
            if (s.GameFilterProfiles.Count == 0) s.GameFilterProfiles = GameFilterProfile.Defaults();
            return s.GameFilterProfiles;
        }
    }

    public static GameFilterProfile Current
    {
        get
        {
            var list = Profiles;
            var s = AppState.Settings;
            s.GameFilterIndex = Math.Clamp(s.GameFilterIndex, 0, list.Count - 1);
            return list[s.GameFilterIndex];
        }
    }

    public static void Start(Window main)
    {
        if (_timer != null) return;
        var st = AppState.Settings;
        FilterLog.Write($"---- Orbit {Updater.DisplayVersion} started · Windows {Environment.OSVersion.Version} · filter {(st.GameFilterEnabled ? "on" : "off")}, enhanced {(st.GameFilterEnhanced ? "on" : "off")}, profile \"{Current.Name}\" ----");
        FilterLog.Write("NVIDIA driver interface: " + (NvidiaVibrance.Available ? "found" : "not available (" + NvidiaVibrance.InitError + ")"));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Tick(), main.Dispatcher);
        _timer.Start();

        // Global hotkeys (work while Roblox is in front).
        var hwnd = new WindowInteropHelper(main).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _hotkeySource = HwndSource.FromHwnd(hwnd);
            _hotkeySource?.AddHook(HotkeyHook);
            RegisterHotKey(hwnd, HotkeyToggle, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)'F');
            RegisterHotKey(hwnd, HotkeyNext, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)'G');
        }
    }

    /// <summary>Settings changed: show the result right away.</summary>
    public static void Refresh() => Tick();

    public static void Stop()
    {
        _timer?.Stop();
        EnhancedFilter.Instance.Stop();
        NvidiaVibrance.Restore();
        Show(null);
        if (_magReady) { MagUninitialize(); _magReady = false; }
        _overlay?.Close();
        _overlay = null;
    }

    private static string _lastState = "";

    private static void Tick()
    {
        var s = AppState.Settings;
        IntPtr game = IntPtr.Zero;
        var inGame = RobloxInFront(out game);
        var want = s.GameFilterEnabled && (PreviewOnScreen || inGame);
        var profile = want ? Current : null;
        var state = $"{(s.GameFilterEnabled ? "on" : "off")}, Roblox {(inGame ? "in front" : "not in front")}{(PreviewOnScreen ? ", whole-screen preview" : "")}{(s.GameFilterEnhanced ? ", enhanced" : "")}";
        if (state != _lastState)
        {
            _lastState = state;
            FilterLog.Write("filter: " + state + (want ? $" → profile \"{Current.Name}\", vibrance {Current.DigitalVibrance} %" : ""));
        }

        // Enhanced mode: Orbit's own GPU filter draws over the game (it already includes the colors,
        // vignette and bars). Until it runs – or if it can't – the normal filter does the job.
        var enhanced = EnhancedFilter.Instance;
        var useEnhanced = profile != null && s.GameFilterEnhanced && inGame && !enhanced.Failed;
        enhanced.Update(useEnhanced ? game : IntPtr.Zero, useEnhanced ? profile : null);
        var enhancedDrawing = useEnhanced && enhanced.Running;

        Show(profile == null || enhancedDrawing ? null : GameFilterMath.Build(profile));
        UpdateOverlay(enhancedDrawing ? null : profile, game);
        NvidiaVibrance.Set(profile?.DigitalVibrance ?? 0);
    }

    private static bool RobloxInFront(out IntPtr window)
    {
        window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var pid);
        return Array.IndexOf(RobloxPaths.ClientIds(), (int)pid) >= 0;
    }

    /// <summary>Sets the screen's color matrix (null = no filter). Only calls Windows when it changes.</summary>
    private static void Show(float[]? matrix)
    {
        if (matrix != null && GameFilterMath.IsIdentity(matrix)) matrix = null;
        if (matrix == null && _shown == null) return;
        if (matrix != null && _shown != null && matrix.AsSpan().SequenceEqual(_shown)) return;

        try
        {
            if (!_magReady)
            {
                _magReady = MagInitialize();
                FilterLog.Write("color filter: magnification " + (_magReady ? "ready" : "couldn't start"));
            }
            if (!_magReady) return;
            var effect = new MAGCOLOREFFECT { transform = matrix ?? GameFilterMath.IdentityMatrix() };
            if (MagSetFullscreenColorEffect(ref effect))
            {
                if ((_shown == null) != (matrix == null)) FilterLog.Write("color filter " + (matrix == null ? "off" : "on"));
                _shown = matrix;
            }
            else FilterLog.Sometimes("mag", TimeSpan.FromMinutes(1), "color filter: Windows refused it (MagSetFullscreenColorEffect failed)");
        }
        catch (Exception ex) { FilterLog.Sometimes("mag-ex", TimeSpan.FromMinutes(1), "color filter not supported: " + ex.Message); }
    }

    private static void UpdateOverlay(GameFilterProfile? profile, IntPtr game)
    {
        var needed = profile != null && game != IntPtr.Zero && (profile.Vignette > 0 || profile.Letterbox != "none");
        if (!needed)
        {
            _overlay?.Hide();
            return;
        }
        _overlay ??= new FilterOverlay();
        _overlay.ShowOver(game, profile!);
    }

    // ---------------- Hotkeys ----------------

    private const int HotkeyToggle = 0x4F01, HotkeyNext = 0x4F02, WM_HOTKEY = 0x0312;

    private static IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        var s = AppState.Settings;
        switch (wParam.ToInt32())
        {
            case HotkeyToggle:
                s.GameFilterEnabled = !s.GameFilterEnabled;
                Toast.Show("Game filter", s.GameFilterEnabled ? $"On · {Current.Name}" : "Off");
                break;
            case HotkeyNext:
                s.GameFilterIndex = (s.GameFilterIndex + 1) % Profiles.Count;
                s.GameFilterEnabled = true;
                Toast.Show("Game filter", Current.Name);
                break;
            default:
                return IntPtr.Zero;
        }
        handled = true;
        AppState.SaveSettings();
        Tick();
        Changed?.Invoke();
        return IntPtr.Zero;
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGCOLOREFFECT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] public float[] transform;
    }

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    [DllImport("magnification.dll")] private static extern bool MagInitialize();
    [DllImport("magnification.dll")] private static extern bool MagUninitialize();
    [DllImport("magnification.dll")] private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT effect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
}
