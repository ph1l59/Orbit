using System.Windows;
using System.Windows.Media;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>
/// The animated "orbit" background of the Play page: drifting nebula clouds, a star sky in three depths,
/// and glowing tilted orbits around the avatar with little moons that pull light trails behind them.
/// The mouse tilts the orbits, lights up what's close and moves the sky a little (parallax);
/// Launch sends a shock wave through the orbits, speeds the moons up and fires a shooting star.
/// Everything is drawn directly, and it pauses whenever it can't be seen.
/// </summary>
public sealed class Starfield : FrameworkElement
{
    // ---------------- Tuning ----------------
    private const int StarCount = 260;
    private const double DesignWidth = 990;                       // sizes below are for this width and scale with it
    private static readonly double[] RingRadii = { 170, 250, 345, 455 };
    private static readonly double[] RingSpeeds = { 0.55, 0.38, 0.26, 0.17 }; // radians per second
    private static readonly int[] MoonsPerRing = { 2, 3, 2, 3 };
    private const double TrailSpan = 0.9;                         // radians of trail behind each moon
    private const int TrailSteps = 20;

    private static readonly Color[] RingColors =
    {
        Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0xD9, 0x46, 0xEF), Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xC0, 0x84, 0xFC),
    };
    private static readonly Color[] StarColors =
    {
        Color.FromRgb(0xF4, 0xF2, 0xF8), Color.FromRgb(0xC0, 0x84, 0xFC), Color.FromRgb(0xE8, 0x79, 0xF9),
    };
    private static readonly (double X, double Y, double R, Color C, double A)[] Clouds =
    {
        (0.78, 0.18, 0.42, Color.FromRgb(0x7C, 0x3A, 0xED), 0.30),
        (0.18, 0.80, 0.40, Color.FromRgb(0xD9, 0x46, 0xEF), 0.16),
        (0.52, 0.60, 0.30, Color.FromRgb(0x5B, 0x21, 0xB6), 0.30),
        (0.10, 0.20, 0.30, Color.FromRgb(0x43, 0x38, 0xCA), 0.24),
    };

    // ---------------- State ----------------
    private sealed class Star
    {
        public double X, Y, Depth, Size, Phase, Speed, Drift;
        public int Color;
    }

    private sealed class Moon
    {
        public int Ring;
        public double Angle;
    }

    private readonly List<Star> _stars = new();
    private readonly List<Moon> _moons = new();
    private readonly Random _random = new(7);

    private Point? _mouse;
    private Point _lastMouse = new(-9999, -9999);
    private double _mouseStrength;
    private Vector _look;            // smoothed mouse position, -0.5..0.5
    private double _time;
    private double _launchAt = double.NaN;
    private double _shootAt = double.NaN;
    private Point _shootFrom, _shootTo;
    private TimeSpan _lastFrame = TimeSpan.Zero;
    private bool _running;

    /// <summary>Where the orbits are centred (the avatar's feet), in this element's coordinates.</summary>
    public Func<Point?>? CenterProvider { get; set; }

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
                Size = 0.7 + depth * 1.6 + (_random.NextDouble() < 0.07 ? 1.4 : 0),
                Phase = _random.NextDouble() * Math.PI * 2,
                Speed = 0.6 + _random.NextDouble() * 1.6,
                Drift = 0.004 * depth * (i % 2 == 0 ? 1 : -1),
                Color = _random.NextDouble() switch { < 0.65 => 0, < 0.9 => 1, _ => 2 },
            });
        }

        for (var r = 0; r < RingRadii.Length; r++)
        {
            var rnd = new Random(100 + r);
            for (var m = 0; m < MoonsPerRing[r]; m++)
                _moons.Add(new Moon { Ring = r, Angle = rnd.NextDouble() * Math.PI * 2 });
        }

        IsVisibleChanged += (_, _) => UpdateRunning();
        Loaded += (_, _) => UpdateRunning();
        RenderGate.Changed += UpdateRunning;
        Unloaded += (_, _) => Stop();
    }

    // ---------------- Input ----------------

    public void SetMouse(Point? position)
    {
        _mouse = position;
        if (position is { } p) _lastMouse = p;
    }

    /// <summary>Launch: shock wave through the orbits, faster moons, and a shooting star.</summary>
    public void ShootingStar()
    {
        if (!_running || ActualWidth < 10) return;
        _launchAt = _time;
        _shootAt = _time;
        var w = ActualWidth;
        var h = ActualHeight;
        _shootFrom = new Point(w * (0.62 + _random.NextDouble() * 0.3), h * (0.04 + _random.NextDouble() * 0.12));
        _shootTo = new Point(_shootFrom.X - w * 0.6, _shootFrom.Y + h * 0.5);
    }

    // ---------------- Animation loop ----------------

    public void UpdateRunning()
    {
        var want = IsVisible && IsLoaded && RenderGate.WindowShowing && AppState.Settings.AnimatedBackground;
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
        // Nothing to animate while the window is minimised or hidden in the tray.
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

        // Moons speed up right after Launch and settle back.
        var boost = double.IsNaN(_launchAt) ? 1 : 1 + 7 * Math.Exp(-(_time - _launchAt) * 1.6);
        foreach (var m in _moons) m.Angle += RingSpeeds[m.Ring] * boost * dt;

        _mouseStrength += ((_mouse != null ? 1 : 0) - _mouseStrength) * (1 - Math.Exp(-5 * dt));
        var wantLook = _mouse is { } p && ActualWidth > 0 ? new Vector(p.X / ActualWidth - 0.5, p.Y / ActualHeight - 0.5) : new Vector();
        _look += (wantLook - _look) * (1 - Math.Exp(-2.5 * dt));

        InvalidateVisual();
    }

    // ---------------- Drawing ----------------

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (!AppState.Settings.AnimatedBackground) return;

        DrawClouds(dc, w, h);
        DrawStars(dc, w, h);
        DrawOrbits(dc, w, h);
        DrawMouseLight(dc);
        DrawShootingStar(dc);
    }

    private void DrawClouds(DrawingContext dc, double w, double h)
    {
        for (var i = 0; i < Clouds.Length; i++)
        {
            var c = Clouds[i];
            var x = (c.X + 0.04 * Math.Sin(_time * 0.05 + i * 1.7) - _look.X * 0.02) * w;
            var y = (c.Y + 0.03 * Math.Cos(_time * 0.04 + i) - _look.Y * 0.02) * h;
            var r = c.R * w * (1 + 0.04 * Math.Sin(_time * 0.11 + i));
            dc.DrawEllipse(CloudBrush(i, c.C, c.A), null, new Point(x, y), r, r * 0.75);
        }
    }

    private void DrawStars(DrawingContext dc, double w, double h)
    {
        foreach (var s in _stars)
        {
            var p = new Point(s.X * w - _look.X * 32 * s.Depth, s.Y * h - _look.Y * 22 * s.Depth);
            var twinkle = 0.55 + 0.45 * Math.Sin(_time * s.Speed + s.Phase);
            var near = 0.0;
            if (_mouseStrength > 0.01)
            {
                var d = (p - _lastMouse).Length;
                if (d < 200) near = (1 - d / 200) * _mouseStrength;
            }
            var a = Math.Clamp((0.35 + 0.6 * s.Depth) * twinkle + near * 0.6, 0, 1);
            var r = s.Size * (1 + near * 0.7) / 2;
            dc.DrawEllipse(Solid(StarColors[s.Color], a), null, p, r, r);
        }
    }

    private void DrawOrbits(DrawingContext dc, double w, double h)
    {
        var scale = Math.Clamp(w / DesignWidth, 0.6, 1.6);
        var center = CenterProvider?.Invoke() ?? new Point(w * 0.5, h * 0.66);
        var tilt = (-12 + _look.X * 9) * Math.PI / 180;       // the mouse tilts the orbit plane a little
        var flat = 0.26 + _look.Y * 0.07;
        var cos = Math.Cos(tilt);
        var sin = Math.Sin(tilt);
        var launch = double.IsNaN(_launchAt) ? 0 : Math.Max(0, 1 - (_time - _launchAt) / 1.2);

        Point At(double rx, double angle, out double front)
        {
            front = (Math.Sin(angle) + 1) / 2; // 1 = in front of the avatar, 0 = behind it
            var ex = rx * Math.Cos(angle);
            var ey = rx * flat * Math.Sin(angle);
            return new Point(center.X + ex * cos - ey * sin, center.Y + ex * sin + ey * cos);
        }

        // Rings: a wide soft glow and a thin bright line, both brighter at the front.
        for (var r = 0; r < RingRadii.Length; r++)
        {
            var rx = RingRadii[r] * scale;
            var fade = 1 - r * 0.15;
            var color = RingColors[r];
            var light = Lighten(color, 0.35);

            const int segments = 56;
            var prev = At(rx, 0, out var prevFront);
            for (var k = 1; k <= segments; k++)
            {
                var p = At(rx, k * Math.PI * 2 / segments, out var front);
                var f = (front + prevFront) / 2;
                dc.DrawLine(PenFor(color, (0.05 + 0.13 * f + 0.25 * launch) * fade, 9 + 5 * f), prev, p);
                dc.DrawLine(PenFor(light, (0.18 + 0.55 * f + 0.3 * launch) * fade, 0.8 + 0.9 * f), prev, p);
                prev = p;
                prevFront = front;
            }
        }

        // Moons with their light trails.
        foreach (var m in _moons)
        {
            var rx = RingRadii[m.Ring] * scale;
            var span = TrailSpan - m.Ring * 0.12;
            var prev = At(rx, m.Angle - span, out _);
            for (var k = TrailSteps - 1; k >= 0; k--)
            {
                var p = At(rx, m.Angle - span * k / TrailSteps, out var front);
                var f = 1 - (double)k / TrailSteps;
                var width = (0.6 + 3.4 * f) * (0.6 + 0.6 * front);
                var a = Math.Pow(f, 1.8) * (0.35 + 0.65 * front);
                dc.DrawLine(PenFor(Color.FromRgb(0xD9, 0x46, 0xEF), a * 0.22, width * 3.2), prev, p);
                dc.DrawLine(PenFor(Color.FromRgb(0xF0, 0xAB, 0xFC), a, width), prev, p);
                prev = p;
            }

            var head = At(rx, m.Angle, out var headFront);
            var near = 0.0;
            if (_mouseStrength > 0.01)
            {
                var d = (head - _lastMouse).Length;
                if (d < 160) near = (1 - d / 160) * _mouseStrength; // moons light up when the mouse comes close
            }
            var glow = (14 + 16 * headFront) * (1 + near * 0.8);
            dc.DrawEllipse(GlowBrush, null, head, glow, glow);
            var core = (2.2 + 2.6 * headFront) * (1 + near * 0.6);
            dc.DrawEllipse(Solid(Color.FromRgb(0xFF, 0xF5, 0xFF), 0.5 + 0.5 * headFront), null, head, core, core);
        }

        // Shock wave after Launch.
        if (!double.IsNaN(_launchAt))
        {
            var t = _time - _launchAt;
            if (t < 1.4)
            {
                var rx = (120 + t * 520) * scale;
                var a = 0.8 * (1 - t / 1.4);
                const int segments = 64;
                var prev = At(rx, 0, out _);
                for (var k = 1; k <= segments; k++)
                {
                    var p = At(rx, k * Math.PI * 2 / segments, out _);
                    dc.DrawLine(PenFor(Color.FromRgb(0xD9, 0x46, 0xEF), a * 0.3, 10), prev, p);
                    dc.DrawLine(PenFor(Color.FromRgb(0xF0, 0xAB, 0xFC), a, 2.2), prev, p);
                    prev = p;
                }
            }
        }
    }

    private void DrawMouseLight(DrawingContext dc)
    {
        if (_mouseStrength < 0.01) return;
        var light = new RadialGradientBrush(
            Color.FromArgb((byte)(48 * _mouseStrength), 0xC0, 0x84, 0xFC), Color.FromArgb(0, 0xA8, 0x55, 0xF7));
        dc.DrawEllipse(light, null, _lastMouse, 80, 80);
    }

    private void DrawShootingStar(DrawingContext dc)
    {
        if (double.IsNaN(_shootAt)) return;
        var t = (_time - _shootAt) / 0.9;
        if (t >= 1) { _shootAt = double.NaN; return; }

        var eased = 1 - Math.Pow(1 - t, 2);
        var head = _shootFrom + (_shootTo - _shootFrom) * eased;
        var dir = _shootTo - _shootFrom;
        dir.Normalize();
        var tail = head - dir * 180 * Math.Min(1, t * 3);
        var fade = t < 0.75 ? 1 : (1 - t) / 0.25;

        var trail = new LinearGradientBrush(
            Color.FromArgb(0, 0xC0, 0x84, 0xFC), Color.FromArgb((byte)(255 * fade), 0xF4, 0xF2, 0xF8), tail, head)
        { MappingMode = BrushMappingMode.Absolute };
        dc.DrawLine(new Pen(trail, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, tail, head);
        dc.DrawEllipse(GlowBrush, null, head, 12 * fade + 2, 12 * fade + 2);
        dc.DrawEllipse(Solid(StarColors[0], fade), null, head, 2.4, 2.4);
    }

    // ---------------- Cached brushes and pens ----------------

    private static readonly Dictionary<long, Brush> SolidCache = new();
    private static readonly Dictionary<long, Pen> PenCache = new();
    private static readonly Dictionary<int, Brush> CloudCache = new();

    private static readonly Brush GlowBrush = MakeGlow();

    private static Brush MakeGlow()
    {
        var b = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(150, 0xE8, 0x79, 0xF9), 0),
                new GradientStop(Color.FromArgb(55, 0xA8, 0x55, 0xF7), 0.45),
                new GradientStop(Color.FromArgb(0, 0xA8, 0x55, 0xF7), 1),
            },
        };
        b.Freeze();
        return b;
    }

    private static Brush CloudBrush(int index, Color color, double alpha)
    {
        if (CloudCache.TryGetValue(index, out var cached)) return cached;
        var b = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb((byte)(255 * alpha * 0.45), color.R, color.G, color.B), 0.45),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        b.Freeze();
        CloudCache[index] = b;
        return b;
    }

    /// <summary>Solid brush with the alpha rounded to 1/32 steps, cached.</summary>
    private static Brush Solid(Color c, double alpha)
    {
        var a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 32);
        long key = ((long)c.R << 24) | ((long)c.G << 16) | ((long)c.B << 8) | (long)a;
        if (SolidCache.TryGetValue(key, out var b)) return b;
        b = new SolidColorBrush(Color.FromArgb((byte)(a / 32.0 * 255), c.R, c.G, c.B));
        b.Freeze();
        SolidCache[key] = b;
        return b;
    }

    /// <summary>Pen with alpha in 1/32 steps and width in 0.25 px steps, cached.</summary>
    private static Pen PenFor(Color c, double alpha, double width)
    {
        var a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 32);
        var wq = (int)Math.Round(Math.Clamp(width, 0.25, 40) * 4);
        long key = ((long)c.R << 40) | ((long)c.G << 32) | ((long)c.B << 24) | ((long)a << 12) | (long)wq;
        if (PenCache.TryGetValue(key, out var pen)) return pen;
        // Flat ends: round ends would overlap at every joint and show as little dots on see-through lines.
        pen = new Pen(Solid(c, a / 32.0), wq / 4.0) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        PenCache[key] = pen;
        return pen;
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
}
