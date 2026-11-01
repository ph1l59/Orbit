using System.Runtime.InteropServices;

namespace Orbit.Services;

/// <summary>
/// NVIDIA's "Digital Vibrance" (NVIDIA Control Panel → Adjust desktop color settings), set through
/// NVIDIA's driver interface (NVAPI). Orbit raises it only while Roblox is in front and puts your
/// own setting back right after. Does nothing on PCs without an NVIDIA graphics card.
/// </summary>
public static class NvidiaVibrance
{
    private static bool _tried, _ready;
    private static InitializeFn? _initialize;
    private static EnumDisplayFn? _enumDisplay;
    private static GetDvcFn? _getDvc;
    private static SetDvcFn? _setDvc;

    /// <summary>Display handle → the level the user had before Orbit changed it.</summary>
    private static readonly Dictionary<IntPtr, int> Original = new();
    private static int _applied;

    public static bool Available => Init();

    /// <summary>0 = the user's own setting, 100 = full vibrance.</summary>
    public static void Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent == _applied) return;
        if (!Init()) return;
        try
        {
            for (var i = 0; i < 16; i++)
            {
                if (_enumDisplay!(i, out var display) != 0) break;
                var info = new DVC_INFO { version = (uint)(Marshal.SizeOf<DVC_INFO>() | (1 << 16)) };
                if (_getDvc!(display, 0, ref info) != 0) continue;

                if (!Original.ContainsKey(display)) Original[display] = info.currentLevel;
                var own = Original[display];
                var level = percent == 0 ? own : Math.Max(own, info.minLevel + (int)Math.Round((info.maxLevel - info.minLevel) * percent / 100.0));
                _setDvc!(display, 0, level);
                if (percent == 0) Original.Remove(display);
            }
            _applied = percent;
        }
        catch { /* driver doesn't support it */ }
    }

    /// <summary>Orbit closes: give every display its own setting back.</summary>
    public static void Restore() => Set(0);

    private static bool Init()
    {
        if (_tried) return _ready;
        _tried = true;
        try
        {
            _initialize = Get<InitializeFn>(0x0150E828);
            _enumDisplay = Get<EnumDisplayFn>(0x9ABDD40D);
            _getDvc = Get<GetDvcFn>(0x4085DE45);
            _setDvc = Get<SetDvcFn>(0x172409B4);
            _ready = _initialize != null && _enumDisplay != null && _getDvc != null && _setDvc != null && _initialize() == 0;
        }
        catch
        {
            _ready = false; // no NVIDIA driver (nvapi64.dll missing)
        }
        return _ready;
    }

    private static T? Get<T>(uint id) where T : Delegate
    {
        var ptr = QueryInterface(id);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DVC_INFO { public uint version; public int currentLevel, minLevel, maxLevel; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumDisplayFn(int index, out IntPtr display);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDvcFn(IntPtr display, int output, ref DVC_INFO info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetDvcFn(IntPtr display, int output, int level);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);
}
