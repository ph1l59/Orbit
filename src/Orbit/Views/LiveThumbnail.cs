using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Orbit.Views;

/// <summary>
/// A live picture of another window, drawn by Windows itself (the same thing the taskbar preview uses):
/// the graphics card shows the window's content inside a rectangle of Orbit's window, nothing is copied.
/// It's drawn on top of Orbit's content, so the owner keeps it inside the visible area.
/// </summary>
public sealed class LiveThumbnail : IDisposable
{
    private IntPtr _thumb;
    private readonly IntPtr _source;
    private RECT _lastDest;
    private RECT _lastSource;
    private bool _lastVisible;

    public IntPtr Source => _source;

    public LiveThumbnail(Window owner, IntPtr source)
    {
        _source = source;
        var dest = new WindowInteropHelper(owner).Handle;
        if (dest != IntPtr.Zero && DwmRegisterThumbnail(dest, source, out var thumb) == 0) _thumb = thumb;
    }

    public bool IsValid => _thumb != IntPtr.Zero;

    /// <summary>
    /// Shows the window's picture fitted into <paramref name="area"/> (an element of the owner window),
    /// cut to <paramref name="clip"/> (the visible part of the page, in the same window's coordinates).
    /// </summary>
    public void Update(FrameworkElement area, Window owner, Rect clip, bool visible)
    {
        if (_thumb == IntPtr.Zero) return;
        if (!visible || !area.IsVisible || area.ActualWidth < 4)
        {
            Hide();
            return;
        }

        // The window's content size (without its title bar and borders).
        if (!GetClientRect(_source, out var client) || client.Right <= 0 || client.Bottom <= 0)
        {
            Hide();
            return;
        }
        double sw = client.Right, sh = client.Bottom;

        // Area in owner-window coordinates (device independent), then fit the picture inside (letterbox).
        Rect box;
        try { box = area.TransformToAncestor(owner).TransformBounds(new Rect(0, 0, area.ActualWidth, area.ActualHeight)); }
        catch { Hide(); return; }
        var scale = Math.Min(box.Width / sw, box.Height / sh);
        var fit = new Rect(box.X + (box.Width - sw * scale) / 2, box.Y + (box.Height - sh * scale) / 2, sw * scale, sh * scale);

        var shown = Rect.Intersect(fit, clip);
        if (shown.IsEmpty || shown.Width < 1 || shown.Height < 1)
        {
            Hide();
            return;
        }

        // Matching part of the source (when the picture is cut off at the edge of the page).
        var src = new RECT
        {
            Left = (int)Math.Round((shown.X - fit.X) / scale),
            Top = (int)Math.Round((shown.Y - fit.Y) / scale),
            Right = (int)Math.Round((shown.Right - fit.X) / scale),
            Bottom = (int)Math.Round((shown.Bottom - fit.Y) / scale),
        };

        // Device pixels.
        var m = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var dest = new RECT
        {
            Left = (int)Math.Round(shown.X * m.M11),
            Top = (int)Math.Round(shown.Y * m.M22),
            Right = (int)Math.Round(shown.Right * m.M11),
            Bottom = (int)Math.Round(shown.Bottom * m.M22),
        };

        if (_lastVisible && dest.Equals(_lastDest) && src.Equals(_lastSource)) return;
        _lastDest = dest;
        _lastSource = src;
        _lastVisible = true;

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY | DWM_TNP_OPACITY,
            rcDestination = dest,
            rcSource = src,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true,
        };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    public void Hide()
    {
        if (_thumb == IntPtr.Zero || !_lastVisible) return;
        _lastVisible = false;
        var props = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_VISIBLE, fVisible = false };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    public void Dispose()
    {
        if (_thumb == IntPtr.Zero) return;
        DwmUnregisterThumbnail(_thumb);
        _thumb = IntPtr.Zero;
    }

    // ---------------- Win32 ----------------

    private const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4,
        DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
