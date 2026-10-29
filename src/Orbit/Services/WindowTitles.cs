using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Orbit.Services;

/// <summary>
/// Shows the account's name (alias, otherwise display name) instead of "Roblox" on the windows
/// Orbit launched, so they can be told apart in the taskbar. Only the window title is changed.
/// </summary>
public static class WindowTitles
{
    private const string DefaultTitle = "Roblox";

    private sealed class Tracked
    {
        public int Pid { get; set; }
        public long UserId { get; set; }
        /// <summary>Process start time, so a re-used process id after a reboot is never mistaken for ours.</summary>
        public long StartedTicks { get; set; }
    }

    private static readonly List<Tracked> Windows = new();
    /// <summary>Launches whose Roblox process hasn't been found yet (id + launch time).</summary>
    private static readonly List<(long UserId, DateTime LaunchedUtc)> Pending = new();
    private static DispatcherTimer? _timer;
    private static bool _loaded;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "windows.json");

    /// <summary>Starts keeping the titles up to date (call once from the UI thread).</summary>
    public static void Start()
    {
        if (_timer != null) return;
        Load();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    /// <summary>Remember that Orbit just launched Roblox for this account.</summary>
    public static void Track(long userId, int? pid)
    {
        if (pid is { } id && TryGetStart(id, out var started))
            Add(new Tracked { Pid = id, UserId = userId, StartedTicks = started });
        else
            Pending.Add((userId, DateTime.UtcNow));
    }

    /// <summary>Account a Roblox process was launched for (0 if Orbit didn't launch it).</summary>
    public static long UserIdOf(int pid) => Windows.FirstOrDefault(t => t.Pid == pid)?.UserId ?? 0;

    /// <summary>The setting was switched off: put "Roblox" back on every window Orbit renamed.</summary>
    public static void RestoreAll()
    {
        var pids = Windows.Select(t => t.Pid).ToList();
        Worker.Add(() =>
        {
            foreach (var pid in pids)
                foreach (var hwnd in WindowsOf(pid))
                    if (GetTitle(hwnd) != DefaultTitle) SetTitle(hwnd, DefaultTitle);
        });
    }

    private static void Tick()
    {
        // Drop windows that were closed.
        var before = Windows.Count;
        foreach (var gone in Windows.Where(t => !IsSameProcess(t)).ToList())
        {
            Windows.Remove(gone);
            // Gone within a minute of starting: probably handed off to a new Roblox process – look for that one.
            var startedUtc = new DateTime(gone.StartedTicks, DateTimeKind.Local).ToUniversalTime();
            if (DateTime.UtcNow - startedUtc < TimeSpan.FromMinutes(1))
                Pending.Add((gone.UserId, startedUtc));
        }
        Pending.RemoveAll(p => DateTime.UtcNow - p.LaunchedUtc > TimeSpan.FromMinutes(2));

        // A launch that handed off to another Roblox process: take the newest one nobody owns yet.
        if (Pending.Count > 0)
        {
            foreach (var proc in RobloxPaths.RunningClients())
            {
                using (proc)
                {
                    if (Pending.Count == 0 || Windows.Any(t => t.Pid == proc.Id)) continue;
                    if (!TryGetStart(proc.Id, out var started)) continue;
                    var startedUtc = new DateTime(started, DateTimeKind.Local).ToUniversalTime();
                    var match = Pending.FindIndex(p => startedUtc >= p.LaunchedUtc.AddSeconds(-2));
                    if (match < 0) continue;
                    Add(new Tracked { Pid = proc.Id, UserId = Pending[match].UserId, StartedTicks = started });
                    Pending.RemoveAt(match);
                }
            }
        }

        if (Windows.Count != before) Save();

        var s = AppState.Settings;
        if (!s.ShowAccountNameOnWindows && !s.UseOrbitIconOnWindows) return;
        if (s.UseOrbitIconOnWindows) LoadIcons();

        // What to apply (read here on the UI thread)...
        var showNames = s.ShowAccountNameOnWindows;
        var useIcon = s.UseOrbitIconOnWindows && _smallIcon != IntPtr.Zero;
        var work = Windows.Select(t =>
        {
            var account = AppState.Accounts.FirstOrDefault(a => a.UserId == t.UserId);
            var title = account == null ? null : string.IsNullOrWhiteSpace(account.Title) ? account.Username : account.Title;
            return (t.Pid, t.UserId, Title: title);
        }).ToList();
        if (work.Count == 0) return;

        // ...and applied on Orbit's own worker thread: talking to a Roblox window means waiting for it to answer,
        // and a window slowed down by the resource saver answers late. On the UI thread that froze Orbit.
        if (Interlocked.Exchange(ref _applying, 1) == 1) return; // the last round is still busy
        Worker.Add(() =>
        {
            try
            {
                foreach (var (pid, userId, title) in work)
                    // Roblox sets its title/icon again now and then (e.g. after a teleport), so keep re-applying them.
                    foreach (var hwnd in WindowsOf(pid))
                    {
                        if (showNames && title != null && GetTitle(hwnd) != title)
                            SetTitle(hwnd, title);
                        if (useIcon) ApplyIcon(hwnd, userId);
                    }
            }
            finally { Volatile.Write(ref _applying, 0); }
        });
    }

    private static int _applying;

    /// <summary>One background thread for everything that sends messages to Roblox windows.</summary>
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> Worker = StartWorker();

    private static System.Collections.Concurrent.BlockingCollection<Action> StartWorker()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var job in queue.GetConsumingEnumerable())
                try { job(); } catch { /* cosmetic only */ }
        })
        { IsBackground = true, Name = "Orbit window titles", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA); // the taskbar property store is COM
        thread.Start();
        return queue;
    }

    /// <summary>Hwnd → when its icon was last checked (worker thread only).</summary>
    private static readonly Dictionary<IntPtr, long> IconChecked = new();

    // ---------- Icon ----------

    private static IntPtr _smallIcon, _bigIcon;
    private static bool _iconsTried;
    private static string _iconTag = "";

    /// <summary>Loads Assets\roblox-window.ico (16 px for the title bar/taskbar, 32 px for Alt+Tab).</summary>
    private static void LoadIcons()
    {
        if (_iconsTried) return;
        _iconsTried = true;
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/roblox-window.ico"));
            if (resource == null) { Log("icon resource not found"); return; }
            byte[] ico;
            using (var input = resource.Stream)
            using (var buffer = new MemoryStream())
            {
                input.CopyTo(buffer);
                ico = buffer.ToArray();
            }

            _smallIcon = IconFromIco(ico, 16);
            _bigIcon = IconFromIco(ico, 32);

            // Content hash in the filename and the AppUserModel.ID means Windows' Shell Icon Cache
            // never serves stale pixels after we ship a new icon: the taskbar sees a fresh identity.
            _iconTag = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(ico)).Substring(0, 10).ToLowerInvariant();

            // Lets the taskbar find the icon by file too (see SetTaskbarIdentity).
            _iconFile = Path.Combine(AppPaths.DataDir, $"roblox-window.{_iconTag}.ico");
            File.WriteAllBytes(_iconFile, ico);
            CleanOldIconFiles(_iconFile);

            Log($"icons loaded: small={_smallIcon} big={_bigIcon} tag={_iconTag}");
        }
        catch (Exception ex)
        {
            Log("loading icons failed: " + ex.Message);
        }
    }

    /// <summary>Deletes previously written roblox-window*.ico files (old hashes and the pre-hash name).</summary>
    private static void CleanOldIconFiles(string keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.DataDir, "roblox-window*.ico"))
                if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }

    /// <summary>Makes an icon handle from the .ico frame that fits <paramref name="size"/> best.</summary>
    private static IntPtr IconFromIco(byte[] ico, int size)
    {
        var count = BitConverter.ToUInt16(ico, 4);
        int best = -1, bestDiff = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var w = ico[6 + i * 16] == 0 ? 256 : ico[6 + i * 16];
            var diff = w >= size ? w - size : (size - w) * 4; // prefer scaling down over scaling up
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        if (best < 0) return IntPtr.Zero;

        var entry = 6 + best * 16;
        var length = BitConverter.ToInt32(ico, entry + 8);
        var offset = BitConverter.ToInt32(ico, entry + 12);
        var image = new byte[length];
        Array.Copy(ico, offset, image, 0, length);
        return CreateIconFromResourceEx(image, (uint)length, true, 0x00030000, size, size, 0);
    }

    private static string? _iconFile;
    private static readonly HashSet<IntPtr> IdentitySet = new();
    private static readonly HashSet<IntPtr> Logged = new();

    private static void ApplyIcon(IntPtr hwnd, long userId)
    {
        // Title bar + Alt+Tab – checked every 10 s per window, that's plenty.
        var now = Environment.TickCount64;
        if (!IconChecked.TryGetValue(hwnd, out var last) || now - last > 10_000)
        {
            IconChecked[hwnd] = now;
            if (IconChecked.Count > 200) IconChecked.Clear();
            if (Send(hwnd, WM_GETICON, ICON_SMALL, IntPtr.Zero) != _smallIcon) Send(hwnd, WM_SETICON, ICON_SMALL, _smallIcon);
            if (Send(hwnd, WM_GETICON, ICON_BIG, IntPtr.Zero) != _bigIcon) Send(hwnd, WM_SETICON, ICON_BIG, _bigIcon);
        }

        // Taskbar: Roblox registers its own app id, and Windows then shows the icon of Roblox's shortcut
        // instead of the window's. Giving the window an id of its own makes the taskbar use our icon.
        if (IdentitySet.Add(hwnd))
        {
            // AUMID carries the icon tag so Windows treats each icon version as a new identity
            // and never caches an old icon against this window's taskbar button.
            // It's the same for every account, so Windows groups all Roblox windows into one taskbar button.
            var ok = SetTaskbarIdentity(hwnd, $"Orbit.Roblox.{_iconTag}", _iconFile);
            Log($"window {hwnd}: taskbar identity {(ok ? "set" : "failed")}");
        }

        if (Logged.Add(hwnd))
            Log($"window {hwnd}: small icon now {Send(hwnd, WM_GETICON, ICON_SMALL, IntPtr.Zero)} (wanted {_smallIcon})");
    }

    /// <summary>Sets the app id (and relaunch icon) the taskbar uses for one window.</summary>
    private static bool SetTaskbarIdentity(IntPtr hwnd, string? appId, string? iconFile)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return false;
            try
            {
                var idKey = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);  // System.AppUserModel.ID
                var iconKey = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3); // RelaunchIconResource
                SetString(store, idKey, appId);
                if (appId != null && iconFile != null) SetString(store, iconKey, iconFile + ",0");
                else SetString(store, iconKey, null);
                store.Commit();
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception ex)
        {
            Log("taskbar identity error: " + ex.Message);
            return false;
        }
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string? value)
    {
        var pv = new PropVariant();
        if (value != null)
        {
            pv.vt = 31; // VT_LPWSTR
            pv.pointer = Marshal.StringToCoTaskMemUni(value);
        }
        try { store.SetValue(ref key, ref pv); }
        finally { if (pv.pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pv.pointer); }
    }

    /// <summary>Small troubleshooting log: %AppData%\Orbit\window-icons.log</summary>
    private static void Log(string line)
    {
        try
        {
            var file = Path.Combine(AppPaths.DataDir, "window-icons.log");
            if (File.Exists(file) && new FileInfo(file).Length > 64 * 1024) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    /// <summary>The icon setting was switched off: give the windows their Roblox icon back.</summary>
    public static void RestoreIcons()
    {
        var pids = Windows.Select(t => t.Pid).ToList();
        Worker.Add(() =>
        {
            foreach (var pid in pids)
                foreach (var hwnd in WindowsOf(pid))
                {
                    // Removing the window's own icon makes Windows fall back to Roblox's built-in one.
                    Send(hwnd, WM_SETICON, ICON_SMALL, IntPtr.Zero);
                    Send(hwnd, WM_SETICON, ICON_BIG, IntPtr.Zero);
                    SetTaskbarIdentity(hwnd, null, null);
                    IdentitySet.Remove(hwnd);
                    IconChecked.Remove(hwnd);
                }
        });
    }

    private static void Add(Tracked t)
    {
        Windows.RemoveAll(w => w.Pid == t.Pid);
        Windows.Add(t);
        Save();
    }

    // ---------- Remembering windows across an Orbit restart / update ----------

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<Tracked>>(File.ReadAllText(FilePath));
            if (list != null) Windows.AddRange(list.Where(IsSameProcess));
        }
        catch { /* start fresh */ }
    }

    private static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Windows)); }
        catch { /* cosmetic only */ }
    }

    // ---------- Processes and windows ----------

    private static bool TryGetStart(int pid, out long ticks)
    {
        ticks = 0;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            ticks = p.StartTime.Ticks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameProcess(Tracked t) => TryGetStart(t.Pid, out var ticks) && ticks == t.StartedTicks;

    private static List<IntPtr> WindowsOf(int pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == (uint)pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
                result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Sends a message without hanging Orbit if the Roblox window is busy.</summary>
    private static IntPtr Send(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        SendMessageTimeout(hwnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, 500, out var result);
        return result;
    }

    private static void SetTitle(IntPtr hwnd, string title)
    {
        var text = Marshal.StringToHGlobalUni(title);
        try { SendMessageTimeout(hwnd, WM_SETTEXT, IntPtr.Zero, text, SMTO_ABORTIFHUNG, 500, out _); }
        finally { Marshal.FreeHGlobal(text); }
    }

    private const uint WM_SETTEXT = 0x000C, WM_GETICON = 0x007F, WM_SETICON = 0x0080;
    private static readonly IntPtr ICON_SMALL = 0, ICON_BIG = 1;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint GW_OWNER = 4;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, bool icon, uint version, int cx, int cy, uint flags);
    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FormatId = fmtid;
        public uint PropertyId = pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
