using System.Windows;
using System.Windows.Media;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>
/// Animated star background for the Play page: twinkling, slowly drifting stars with a little parallax,
/// stars near the mouse join up with thin purple lines like a constellation, and a shooting star on launch.
/// Everything is drawn directly (no elements per star), and it pauses whenever it can't be seen.
/// </summary>
public sealed class Starfield : FrameworkElement
{
    private sealed class Star
    {
        public double X, Y;          // 0..1 across the area
        public double Depth;         // 0.3 (far) .. 1 (near)
        public double Size;
        public double Phase, Speed;  // twinkle
        public double Drift;         // sideways drift per second (in area widths)
        public int Color;            // 0 white, 1 purple, 2 pink
    }

    private sealed class Shooter
    {
        public Point From, To;
        public double Start;
    }

    private const int StarCount = 110;
    private const double LinkRadius = 170;   // stars this close to the mouse join up
    private const double LinkDistance = 95;  // ...with other stars at most this far away

    private readonly List<Star> _stars = new();
    private readonly Random _random = new(7);
    private Shooter? _shooter;

    private Point? _mouse;               // in this element's coordinates
    private Point _lastMouse = new(-9999, -9999); // where the lines fade out after the mouse left
    private double _mouseStrength;       // fades in/out
    private Vector _parallax;            // smoothed
    private double _time;
    private TimeSpan _lastFrame = TimeSpan.Zero;
    private bool _running;

    // Brushes for 16 brightness steps per color, made once.
    private static readonly Color[] Colors =
    {
        Color.FromRgb(0xF4, 0xF2, 0xF8), Color.FromRgb(0xC0, 0x84, 0xFC), Color.FromRgb(0xE8, 0x79, 0xF9),
    };
    private static readonly Brush[,] Brushes = MakeBrushes();
    private static readonly Color LineColor = Color.FromRgb(0xC0, 0x84, 0xFC);

    public Starfield()
    {
        IsHitTestVisible = false;
        for (var i = 0; i < StarCount; i++)
        {
            var depth = 0.3 + Math.Pow(_random.NextDouble(), 1.6) * 0.7;
            _stars.Add(new Star
            {
                X = _random.NextDouble(),
                Y = _random.NextDouble(),
                Depth = depth,
                Size = 0.6 + depth * 1.5 + (_random.NextDouble() < 0.06 ? 1.0 : 0),
                Phase = _random.NextDouble() * Math.PI * 2,
                Speed = 0.6 + _random.NextDouble() * 1.6,
                Drift = (0.002 + _random.NextDouble() * 0.004) * depth * (_random.NextDouble() < 0.5 ? -1 : 1),
                Color = _random.NextDouble() switch { < 0.62 => 0, < 0.88 => 1, _ => 2 },
            });
        }

        IsVisibleChanged += (_, _) => UpdateRunning();
        Loaded += (_, _) => UpdateRunning();
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Mouse position relative to this element, or null when the mouse left.</summary>
    public void SetMouse(Point? position)
    {
        _mouse = position;
        if (position is { } p) _lastMouse = p;
    }

    /// <summary>A shooting star across the sky (used when Launch is pressed).</summary>
    public void ShootingStar()
    {
        if (!_running || ActualWidth < 10) return;
        var w = ActualWidth;
        var h = ActualHeight;
        var from = new Point(w * (0.55 + _random.NextDouble() * 0.35), h * (0.05 + _random.NextDouble() * 0.15));
        var to = new Point(from.X - w * 0.55, from.Y + h * 0.45);
        _shooter = new Shooter { From = from, To = to, Start = _time };
    }

    public void UpdateRunning()
    {
        var want = IsVisible && IsLoaded && AppState.Settings.AnimatedBackground;
        if (want && !_running)
        {
            _running = true;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }
        else if (!want) Stop();
        InvalidateVisual();
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // Nothing to draw while the window is minimised or hidden in the tray.
        if (Window.GetWindow(this) is { } window && (window.WindowState == WindowState.Minimized || !window.IsVisible)) return;

        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        _time += dt;

        foreach (var s in _stars)
        {
            s.X += s.Drift * dt;
            if (s.X < -0.02) s.X += 1.04;
            else if (s.X > 1.02) s.X -= 1.04;
        }

        // Mouse influence and parallax ease in and out.
        var target = _mouse != null ? 1.0 : 0.0;
        _mouseStrength += (target - _mouseStrength) * (1 - Math.Exp(-6 * dt));
        var wantParallax = _mouse is { } m && ActualWidth > 0
            ? new Vector((m.X / ActualWidth - 0.5) * -18, (m.Y / ActualHeight - 0.5) * -12)
            : new Vector();
        _parallax += (wantParallax - _parallax) * (1 - Math.Exp(-3 * dt));

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (!AppState.Settings.AnimatedBackground) return;

        // Positions on screen (with parallax: near stars move more).
        var points = new Point[_stars.Count];
        for (var i = 0; i < _stars.Count; i++)
        {
            var s = _stars[i];
            points[i] = new Point(s.X * w + _parallax.X * s.Depth, s.Y * h + _parallax.Y * s.Depth);
        }

        // Constellation lines around the mouse.
        var center = _lastMouse;
        if (_mouseStrength > 0.01)
        {
            var near = new List<int>();
            for (var i = 0; i < points.Length; i++)
                if ((points[i] - center).Length < LinkRadius) near.Add(i);

            for (var a = 0; a < near.Count; a++)
            {
                var pa = points[near[a]];
                var fadeA = 1 - (pa - center).Length / LinkRadius;
                for (var b = a + 1; b < near.Count; b++)
                {
                    var pb = points[near[b]];
                    var d = (pa - pb).Length;
                    if (d > LinkDistance) continue;
                    var fadeB = 1 - (pb - center).Length / LinkRadius;
                    var alpha = Math.Min(fadeA, fadeB) * (1 - d / LinkDistance) * _mouseStrength;
                    if (alpha < 0.02) continue;
                    dc.DrawLine(Pen(alpha * 0.75, 0.8), pa, pb);
                }
            }
        }

        // Stars.
        for (var i = 0; i < _stars.Count; i++)
        {
            var s = _stars[i];
            var twinkle = 0.55 + 0.45 * Math.Sin(_time * s.Speed + s.Phase);
            var glow = 0.0;
            if (_mouseStrength > 0.01)
            {
                var d = (points[i] - center).Length;
                if (d < LinkRadius) glow = (1 - d / LinkRadius) * _mouseStrength;
            }
            var brightness = Math.Clamp((0.25 + 0.55 * s.Depth) * twinkle + glow * 0.5, 0, 1);
            var brush = Brushes[s.Color, (int)Math.Round(brightness * 15)];
            var r = s.Size * (1 + glow * 0.5) / 2;
            dc.DrawEllipse(brush, null, points[i], r, r);
        }

        // Shooting star.
        if (_shooter is { } sh)
        {
            var t = (_time - sh.Start) / 0.9;
            if (t >= 1) _shooter = null;
            else
            {
                var eased = 1 - Math.Pow(1 - t, 2);
                var head = sh.From + (sh.To - sh.From) * eased;
                var dir = sh.To - sh.From;
                dir.Normalize();
                var tail = head - dir * 150 * Math.Min(1, t * 3);
                var fade = t < 0.75 ? 1 : (1 - t) / 0.25;

                var trail = new LinearGradientBrush(
                    Color.FromArgb(0, 0xC0, 0x84, 0xFC), Color.FromArgb((byte)(255 * fade), 0xF4, 0xF2, 0xF8),
                    tail, head) { MappingMode = BrushMappingMode.Absolute };
                dc.DrawLine(new Pen(trail, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, tail, head);
                dc.DrawEllipse(Brushes[0, (int)Math.Round(15 * fade)], null, head, 2.2, 2.2);
            }
        }
    }

    private static readonly Dictionary<int, Pen> Pens = new();

    private static Pen Pen(double alpha, double thickness)
    {
        var key = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 20);
        if (Pens.TryGetValue(key, out var pen)) return pen;
        var brush = new SolidColorBrush(Color.FromArgb((byte)(key / 20.0 * 255), LineColor.R, LineColor.G, LineColor.B));
        brush.Freeze();
        pen = new Pen(brush, thickness);
        pen.Freeze();
        Pens[key] = pen;
        return pen;
    }

    private static Brush[,] MakeBrushes()
    {
        var result = new Brush[Colors.Length, 16];
        for (var c = 0; c < Colors.Length; c++)
            for (var i = 0; i < 16; i++)
            {
                var b = new SolidColorBrush(Color.FromArgb((byte)(i / 15.0 * 255), Colors[c].R, Colors[c].G, Colors[c].B));
                b.Freeze();
                result[c, i] = b;
            }
        return result;
    }
}
