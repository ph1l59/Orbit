namespace Orbit.Views;

/// <summary>
/// Whether Orbit's window can be seen at all (not minimized, not hidden in the tray).
/// Animations and live pictures only run while it can – otherwise they'd keep WPF drawing every frame for nothing.
/// </summary>
public static class RenderGate
{
    public static bool WindowShowing { get; private set; } = true;

    public static event Action? Changed;

    public static void Set(bool showing)
    {
        if (showing == WindowShowing) return;
        WindowShowing = showing;
        Changed?.Invoke();
    }
}
