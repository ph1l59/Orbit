using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;

namespace Orbit.Services;

/// <summary>
/// Finds out what makes Orbit stutter: notes every piece of work on the UI thread that takes long,
/// with its name, plus times the UI thread didn't react at all. Writes only when something was slow,
/// to %AppData%\Orbit\performance.log (kept small).
/// </summary>
public static class PerfLog
{
    private const double SlowMs = 40;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<DispatcherOperation, double> Started = new();
    private static readonly object FileLock = new();
    private static volatile string _current = "";
    private static double _currentSince;
    private static int _lastGen2;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "performance.log");

    public static void Start(Dispatcher ui)
    {
        Write($"---- Orbit {Updater.DisplayVersion} started, {Environment.ProcessorCount} cores ----");
        _lastGen2 = GC.CollectionCount(2);

        ui.Hooks.OperationStarted += (_, e) =>
        {
            var now = Clock.Elapsed.TotalMilliseconds;
            Started[e.Operation] = now;
            _currentSince = now;
            _current = "?"; // named lazily – only when it turns out slow
            _currentOp = e.Operation;
        };
        ui.Hooks.OperationCompleted += (_, e) => Done(e.Operation);
        ui.Hooks.OperationAborted += (_, e) => Started.Remove(e.Operation);

        // Watchdog: if the UI thread doesn't answer for a while, note what it is busy with.
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(100);
                var since = _currentSince;
                var op = _currentOp;
                if (op != null && since > 0 && Clock.Elapsed.TotalMilliseconds - since > 250 && op == _currentOp && !_reportedStuck)
                {
                    _reportedStuck = true;
                    Write($"UI thread busy for {Clock.Elapsed.TotalMilliseconds - since:0} ms (still running) – {NameOf(op)} | {Context()}");
                }
            }
        }) { IsBackground = true, Name = "Orbit perf watchdog", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private static DispatcherOperation? _currentOp;
    private static volatile bool _reportedStuck;

    private static void Done(DispatcherOperation op)
    {
        if (op == _currentOp) { _currentOp = null; _currentSince = 0; _reportedStuck = false; }
        if (!Started.Remove(op, out var start)) return;
        var took = Clock.Elapsed.TotalMilliseconds - start;

        var gen2 = GC.CollectionCount(2);
        var gc = gen2 != _lastGen2 ? "  (full garbage collection happened)" : "";
        _lastGen2 = gen2;

        if (took >= SlowMs)
            Write($"slow {took,6:0} ms  {NameOf(op)}{gc} | {Context()}");
    }

    private static string Context()
    {
        var s = AppState.Settings;
        return $"saver {(s.ResourceSaver ? "on" : "off")}, cap {s.SaverCpuCap} %, eco {(s.SaverEco ? "on" : "off")}, " +
               $"roblox {RobloxPaths.ClientIds().Length}, ram {GC.GetTotalMemory(false) / 1024 / 1024} MB";
    }

    // ---------- What an operation is ----------

    private static readonly FieldInfo? MethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TickField = typeof(DispatcherTimer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .FirstOrDefault(f => f.FieldType == typeof(EventHandler));

    private static string NameOf(DispatcherOperation op)
    {
        try
        {
            if (MethodField?.GetValue(op) is not Delegate d) return op.Priority.ToString();
            // A timer: name the code that runs on its tick.
            if (d.Target is DispatcherTimer timer && TickField?.GetValue(timer) is EventHandler tick)
                return "timer " + timer.Interval.TotalSeconds + " s → " + string.Join(", ", tick.GetInvocationList().Select(Describe));
            return Describe(d);
        }
        catch
        {
            return op.Priority.ToString();
        }
    }

    private static string Describe(Delegate d)
    {
        var owner = d.Target?.GetType() ?? d.Method.DeclaringType;
        var name = owner?.FullName ?? "?";
        // Lambdas live in compiler classes like "Orbit.Views.PlayView+<>c__DisplayClass5_0" – the method name says where.
        return $"{name.Replace("Orbit.", "")}.{d.Method.Name}";
    }

    private static void Write(string line)
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 256 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch { /* best effort */ }
        }
    }
}
