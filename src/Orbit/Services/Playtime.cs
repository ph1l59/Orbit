using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Orbit.Services;

/// <summary>
/// Counts how long Roblox has been open (at least one window), per day.
/// Several windows at the same time count once. Saved in %AppData%\Orbit\playtime.json.
/// </summary>
public static class Playtime
{
    private sealed class Data
    {
        public double TotalSeconds { get; set; }
        /// <summary>"yyyy-MM-dd" (local day) → seconds.</summary>
        public Dictionary<string, double> Days { get; set; } = new();
    }

    private static Data _data = new();
    private static DispatcherTimer? _timer;
    private static bool _loaded;
    private static DateTime _lastTick;
    private static bool _wasRunning;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "playtime.json");

    /// <summary>Raised (on the UI thread) when the numbers changed.</summary>
    public static event Action? Changed;

    public static void Start(Dispatcher ui)
    {
        if (_timer != null) return;
        EnsureLoaded();
        _lastTick = DateTime.UtcNow;
        _wasRunning = RobloxPaths.IsRobloxRunning();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Tick(), ui);
        _timer.Start();
        Changed?.Invoke();
    }

    public static void Stop()
    {
        _timer?.Stop();
        Tick();
    }

    private static void Tick()
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Clamp((now - _lastTick).TotalSeconds, 0, 60); // PC was asleep: don't count that
        _lastTick = now;

        var running = RobloxPaths.IsRobloxRunning();
        // Count the time between two checks only if Roblox was open at both.
        if (running && _wasRunning && elapsed > 0)
        {
            var day = DateTime.Now.ToString("yyyy-MM-dd");
            _data.TotalSeconds += elapsed;
            _data.Days[day] = _data.Days.GetValueOrDefault(day) + elapsed;
            Save();
            Changed?.Invoke();
        }
        _wasRunning = running;
    }

    public static TimeSpan Today => Get(DateTime.Now.Date);

    private static TimeSpan Get(DateTime day)
    {
        EnsureLoaded();
        return TimeSpan.FromSeconds(_data.Days.GetValueOrDefault(day.ToString("yyyy-MM-dd")));
    }

    /// <summary>Since Monday.</summary>
    public static TimeSpan ThisWeek
    {
        get
        {
            var today = DateTime.Now.Date;
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var sum = TimeSpan.Zero;
            for (var d = monday; d <= today; d = d.AddDays(1))
                sum += Get(d);
            return sum;
        }
    }

    public static TimeSpan Total
    {
        get
        {
            EnsureLoaded();
            return TimeSpan.FromSeconds(_data.TotalSeconds);
        }
    }

    /// <summary>"2h 05m", "14m", "0m"</summary>
    public static string Format(TimeSpan t)
    {
        var hours = (int)t.TotalHours;
        return hours > 0 ? $"{hours}h {t.Minutes:00}m" : $"{t.Minutes}m";
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
                _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch { _data = new Data(); }
    }

    private static void Save()
    {
        try
        {
            // Keep about a year of days.
            if (_data.Days.Count > 400)
                foreach (var old in _data.Days.Keys.OrderBy(k => k).Take(_data.Days.Count - 400).ToList())
                    _data.Days.Remove(old);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data));
        }
        catch { /* not essential */ }
    }
}
