using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Orbit.Views;

/// <summary>
/// Smooth mouse-wheel scrolling for every scroll area in Orbit: instead of jumping, the content
/// glides to its new position and eases out.
/// </summary>
public static class SmoothScroll
{
    private const double Step = 110;      // pixels per wheel notch
    private const double Speed = 14;      // higher = settles faster

    private sealed class State
    {
        public double Target;
        public bool Running;
        public TimeSpan LastFrame;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    /// <summary>Call once at start.</summary>
    public static void Enable() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnWheel));

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer) return;

        // The wheel event passes through every scroll area from the outside in;
        // only the innermost one that can scroll takes it.
        if (Innermost(e.OriginalSource as DependencyObject) != viewer) return;

        // Lists that scroll item by item (virtualizing) keep their normal behaviour.
        if (viewer.CanContentScroll && IsVirtualizing(viewer)) return;
        viewer.CanContentScroll = false; // scroll by pixels, not by whole items

        var state = States.GetOrCreateValue(viewer);
        if (!state.Running) state.Target = viewer.VerticalOffset;

        var target = Math.Clamp(state.Target - Math.Sign(e.Delta) * Step * Math.Max(1, Math.Abs(e.Delta) / 120.0), 0, viewer.ScrollableHeight);

        // At the very top/bottom: let the outer area scroll instead.
        if (Math.Abs(target - state.Target) < 0.5 && !state.Running) return;

        e.Handled = true;
        state.Target = target;
        if (state.Running) return;

        state.Running = true;
        state.LastFrame = TimeSpan.Zero;
        EventHandler? tick = null;
        tick = (_, args) =>
        {
            var now = ((RenderingEventArgs)args).RenderingTime;
            var dt = state.LastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - state.LastFrame).TotalSeconds, 0, 0.05);
            state.LastFrame = now;
            if (dt == 0) return;

            var current = viewer.VerticalOffset;
            var next = current + (state.Target - current) * (1 - Math.Exp(-Speed * dt));
            if (Math.Abs(state.Target - next) < 0.5 || !viewer.IsLoaded)
            {
                viewer.ScrollToVerticalOffset(state.Target);
                state.Running = false;
                CompositionTarget.Rendering -= tick;
                return;
            }
            viewer.ScrollToVerticalOffset(next);
        };
        CompositionTarget.Rendering += tick;
    }

    private static ScrollViewer? Innermost(DependencyObject? source)
    {
        for (var d = source; d != null; d = Parent(d))
            if (d is ScrollViewer { ScrollableHeight: > 0 } sv && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                return sv;
        return null;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static bool IsVirtualizing(ScrollViewer viewer)
    {
        if (viewer.TemplatedParent is not ItemsControl items) return false;
        return VirtualizingPanel.GetIsVirtualizing(items) && items.Items.Count > 50;
    }
}
