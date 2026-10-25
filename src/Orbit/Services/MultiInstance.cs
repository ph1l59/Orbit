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
            error = "Bitte schließe zuerst alle Roblox-Fenster und aktiviere Multi-Roblox dann erneut.";
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
            error = "Multi-Roblox konnte nicht aktiviert werden: " + ex.Message +
                    " Schließe alle Roblox-Prozesse (auch im Hintergrund) und versuche es erneut.";
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
