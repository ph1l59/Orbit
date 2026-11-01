using System.Runtime.InteropServices;

namespace Orbit.Services;

/// <summary>
/// NVIDIA's "Digital Vibrance" (NVIDIA Control Panel → Adjust desktop color settings), set through
/// NVIDIA's driver interface (NVAPI). Orbit raises it only while Roblox is in front and puts your
/// own setting back right after. Every couple of seconds it checks the value again, in case another
/// program (NVIDIA app, VibranceGUI …) changed it back. Everything the driver answers goes to game-filter.log.
/// </summary>
public static class NvidiaVibrance
{
    private static bool _tried, _ready;
    private static InitializeFn? _initialize;
    private static EnumDisplayFn? _enumDisplay;
    private static GetDvcFn? _getDvc;
    private static SetDvcFn? _setDvc;
    // Newer interface (what NVIDIA's own tools use): the full 0..100 % range of the control panel.
    private static GetDvcExFn? _getDvcEx;
    private static SetDvcExFn? _setDvcEx;

    /// <summary>Display handle → the level the user had before Orbit changed it.</summary>
    private static readonly Dictionary<IntPtr, int> Original = new();
    private static int _applied;
    private static DateTime _lastApply;

    public static bool Available => Init();
    public static string InitError { get; private set; } = "";

    /// <summary>
    /// 0 = leave the user's own setting, 1..100 = the "Digital vibrance" value of the NVIDIA Control Panel
    /// (50 % = normal, 100 % = maximum). Cheap to call often.
    /// </summary>
    public static void Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var recheck = percent > 0 && DateTime.UtcNow - _lastApply > TimeSpan.FromSeconds(2);
        if (percent == _applied && !recheck) return;
        if (!Init())
        {
            if (percent > 0) FilterLog.Sometimes("dvc-init", TimeSpan.FromMinutes(5), "vibrance: can't use the NVIDIA driver – " + InitError);
            return;
        }
        _lastApply = DateTime.UtcNow;

        var changed = percent != _applied;
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var status = _enumDisplay!(i, out var display);
                if (status != 0)
                {
                    if (i == 0 && changed)
                        FilterLog.Write($"vibrance: no NVIDIA display found (NvAPI_EnumNvidiaDisplayHandle = {Describe(status)}) – is the monitor plugged into the NVIDIA card?");
                    break;
                }

                if (!Read(display, out var current, out var min, out var max, out var api))
                {
                    FilterLog.Sometimes("dvc-get" + i, TimeSpan.FromMinutes(1), $"vibrance: display {i}: reading failed ({api})");
                    continue;
                }

                if (!Original.ContainsKey(display)) Original[display] = current;
                var own = Original[display];
                var level = percent == 0 ? own : ToLevel(percent, min, max, api);

                if (!changed && current == level) continue; // still right
                if (!changed)
                    FilterLog.Sometimes("dvc-reset" + i, TimeSpan.FromSeconds(30),
                        $"vibrance: display {i}: something changed it to {current} – setting {level} again (another program, like the NVIDIA app or VibranceGUI?)");

                var result = Write(display, level, api);
                Read(display, out var now, out _, out _, out _);
                if (changed || result != 0 || now != level)
                    FilterLog.Write($"vibrance: display {i} [{api}]: {(percent == 0 ? "back to own setting" : percent + " %")} → level {level} " +
                                    $"(range {min}..{max}, was {current}, own {own}) · set = {Describe(result)} · now {now}" +
                                    (now != level ? "  ← DIDN'T TAKE" : ""));
                if (percent == 0) Original.Remove(display);
            }
            _applied = percent;
        }
        catch (Exception ex)
        {
            FilterLog.Sometimes("dvc-ex", TimeSpan.FromMinutes(1), "vibrance error: " + ex.Message);
        }
    }

    /// <summary>Control-panel percent → the driver's level. The old interface only knows 50..100 % (as 0..max).</summary>
    private static int ToLevel(int percent, int min, int max, string api) => api == "ex"
        ? min + (int)Math.Round((max - min) * percent / 100.0)
        : Math.Max(min, (int)Math.Round(max * (percent - 50) / 50.0));

    private static bool Read(IntPtr display, out int current, out int min, out int max, out string api)
    {
        if (_getDvcEx != null)
        {
            var ex = new DVC_INFO_EX { version = (uint)(Marshal.SizeOf<DVC_INFO_EX>() | (1 << 16)) };
            var status = _getDvcEx(display, 0, ref ex);
            if (status == 0)
            {
                (current, min, max, api) = (ex.currentLevel, ex.minLevel, ex.maxLevel, "ex");
                return true;
            }
            FilterLog.Sometimes("dvc-ex-read", TimeSpan.FromMinutes(5), $"vibrance: new interface can't read ({Describe(status)}) – using the old one");
        }
        var info = new DVC_INFO { version = (uint)(Marshal.SizeOf<DVC_INFO>() | (1 << 16)) };
        var legacy = _getDvc!(display, 0, ref info);
        (current, min, max, api) = (info.currentLevel, info.minLevel, info.maxLevel, legacy == 0 ? "old" : "old: " + Describe(legacy));
        return legacy == 0;
    }

    private static int Write(IntPtr display, int level, string api)
    {
        if (api == "ex" && _setDvcEx != null)
        {
            var ex = new DVC_INFO_EX { version = (uint)(Marshal.SizeOf<DVC_INFO_EX>() | (1 << 16)), currentLevel = level };
            return _setDvcEx(display, 0, ref ex);
        }
        return _setDvc!(display, 0, level);
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
            _getDvcEx = Get<GetDvcExFn>(0x0E45002D);
            _setDvcEx = Get<SetDvcExFn>(0x4A82C2B1);
            if (_getDvcEx == null || _setDvcEx == null) { _getDvcEx = null; _setDvcEx = null; }
            if (_initialize == null || _enumDisplay == null || _getDvc == null || _setDvc == null)
            {
                InitError = "the driver doesn't offer the vibrance functions";
                return _ready = false;
            }
            var status = _initialize();
            _ready = status == 0;
            if (!_ready) InitError = "NvAPI_Initialize = " + Describe(status);
        }
        catch (DllNotFoundException)
        {
            InitError = "no NVIDIA driver (nvapi64.dll missing)";
            _ready = false;
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            _ready = false;
        }
        return _ready;
    }

    private static string Describe(int status) => status switch
    {
        0 => "OK",
        -1 => "-1 error",
        -3 => "-3 not initialized",
        -4 => "-4 library not found",
        -6 => "-6 no NVIDIA GPU",
        -7 => "-7 end of list",
        -8 => "-8 invalid display",
        -9 => "-9 incompatible version",
        -104 => "-104 not supported",
        _ => status.ToString(),
    };

    private static T? Get<T>(uint id) where T : Delegate
    {
        var ptr = QueryInterface(id);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DVC_INFO { public uint version; public int currentLevel, minLevel, maxLevel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DVC_INFO_EX { public uint version; public int currentLevel, minLevel, maxLevel, defaultLevel; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDvcExFn(IntPtr display, int output, ref DVC_INFO_EX info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetDvcExFn(IntPtr display, int output, ref DVC_INFO_EX info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumDisplayFn(int index, out IntPtr display);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDvcFn(IntPtr display, int output, ref DVC_INFO info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetDvcFn(IntPtr display, int output, int level);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);
}
