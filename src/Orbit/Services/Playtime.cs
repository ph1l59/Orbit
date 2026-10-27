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
    private static DateTime _lastTick;
    private static bool _wasRunning;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "playtime.json");

    /// <summary>Raised (on the UI thread) when the numbers changed.</summary>
    public static event Action? Changed;

    public static void Start(Dispatcher ui)
    {
        if (_timer != null) return;
        Load();
        _lastTick = DateTime.UtcNow;
        _wasRunning = RobloxPaths.IsRobloxRunning();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Tick(), ui);
        _timer.Start();
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

    public static TimeSpan Today => TimeSpan.FromSeconds(_data.Days.GetValueOrDefault(DateTime.Now.ToString("yyyy-MM-dd")));

    /// <summary>Since Monday.</summary>
    public static TimeSpan ThisWeek
    {
        get
        {
            var today = DateTime.Now.Date;
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            double sum = 0;
            for (var d = monday; d <= today; d = d.AddDays(1))
                sum += _data.Days.GetValueOrDefault(d.ToString("yyyy-MM-dd"));
            return TimeSpan.FromSeconds(sum);
        }
    }

    public static TimeSpan Total => TimeSpan.FromSeconds(_data.TotalSeconds);

    /// <summary>"2h 05m", "14m", "0m"</summary>
    public static string Format(TimeSpan t)
    {
        var hours = (int)t.TotalHours;
        return hours > 0 ? $"{hours}h {t.Minutes:00}m" : $"{t.Minutes}m";
    }

    private static void Load()
    {
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
