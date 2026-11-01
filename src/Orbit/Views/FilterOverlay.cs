using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Orbit.Models;

namespace Orbit.Views;

/// <summary>
/// A see-through, click-through layer exactly over the Roblox window: dark edges (vignette) and black
/// bars (letterbox). It never takes the focus and mouse clicks go straight through to the game.
/// </summary>
public sealed class FilterOverlay : Window
{
    private readonly Rectangle _vignette = new() { IsHitTestVisible = false };
    private readonly Border _top = new() { Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _bottom = new() { Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Bottom };
    private IntPtr _hwnd;
    private RECT _lastRect;
    private int _lastVignette = -1;
    private string _letterbox = "none";

    public FilterOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Width = Height = 10;
        Content = new Grid { IsHitTestVisible = false, Children = { _vignette, _top, _bottom } };
        SizeChanged += (_, _) => LayoutBars();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        };
    }

    /// <summary>Shows the layer over the game window's picture with the profile's vignette and bars.</summary>
    public void ShowOver(IntPtr game, GameFilterProfile profile)
    {
        if (profile.Vignette != _lastVignette)
        {
            _lastVignette = profile.Vignette;
            var a = (byte)Math.Clamp(profile.Vignette * 2.4, 0, 240);
            _vignette.Fill = new RadialGradientBrush
            {
                RadiusX = 0.75, RadiusY = 0.75,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45),
                    new GradientStop(Color.FromArgb((byte)(a * 0.45), 0, 0, 0), 0.78),
                    new GradientStop(Color.FromArgb(a, 0, 0, 0), 1.0),
                },
            };
            _vignette.Visibility = profile.Vignette > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_letterbox != profile.Letterbox)
        {
            _letterbox = profile.Letterbox;
            LayoutBars();
        }

        if (!IsVisible) Show();

        // Over the game's picture (its client area), in screen pixels.
        if (!GetClientRect(game, out var client)) return;
        var origin = new POINT();
        ClientToScreen(game, ref origin);
        var rect = new RECT { Left = origin.X, Top = origin.Y, Right = origin.X + client.Right, Bottom = origin.Y + client.Bottom };
        if (rect.Equals(_lastRect)) return;
        _lastRect = rect;
        SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public new void Hide()
    {
        if (!IsVisible) return;
        base.Hide();
        _lastRect = default;
    }

    private void LayoutBars()
    {
        double w = ActualWidth, h = ActualHeight;
        var ratio = _letterbox switch { "2.39" => 2.39, "1.85" => 1.85, _ => 0 };
        var bar = ratio > 0 && w > 0 ? Math.Max(0, (h - w / ratio) / 2) : 0;
        _top.Height = _bottom.Height = bar;
        _top.Visibility = _bottom.Visibility = bar > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Win32 ----------------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
