namespace Orbit.Services;

/// <summary>
/// Roblox only allows one client at a time by creating a named singleton object.
/// If Orbit owns objects with those names first, every new client starts normally
/// and the already running ones are left alone.
/// </summary>
public static class MultiInstance
{
    private static readonly string[] SingletonNames = { "ROBLOX_singletonEvent", "ROBLOX_singletonMutex" };
    private static readonly List<Mutex> Held = new();

    public static bool IsActive => Held.Count > 0;

    public static bool TryEnable(out string? error)
    {
        error = null;
        if (IsActive) return true;

        if (RobloxPaths.IsRobloxRunning())
        {
            error = "Close all Roblox windows first, then turn on Multi-Roblox again.";
            return false;
        }

        try
        {
            foreach (var name in SingletonNames)
                Held.Add(new Mutex(true, name));
            return true;
        }
        catch (Exception ex)
        {
            Disable();
            error = "Multi-Roblox could not be turned on: " + ex.Message +
                    " Close every Roblox process (also in the background) and try again.";
            return false;
        }
    }

    public static void Disable()
    {
        foreach (var mutex in Held)
        {
            try { mutex.ReleaseMutex(); } catch { /* not owned by this thread */ }
            mutex.Dispose();
        }
        Held.Clear();
    }
}
