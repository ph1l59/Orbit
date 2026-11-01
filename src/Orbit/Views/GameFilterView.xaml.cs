using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>Game filter page: profiles, sliders and a preview picture that shows the filter live.</summary>
public partial class GameFilterView : UserControl, IPage
{
    private const int PreviewW = 640, PreviewH = 360;

    private readonly byte[] _scene;
    private readonly byte[] _filtered = new byte[PreviewW * PreviewH * 4];
    private readonly WriteableBitmap _preview = new(PreviewW, PreviewH, 96, 96, PixelFormats.Bgra32, null);
    private readonly DispatcherTimer _saveDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;

    public GameFilterView()
    {
        InitializeComponent();
        _scene = DrawScene();
        PreviewImage.Source = _preview;
        PreviewArea.SizeChanged += (_, _) => UpdateFrame();

        // Closed at first, like NVIDIA's panel: the first two are open.
        foreach (var body in new FrameworkElement[] { DvcBody, DetailsBody, LookBody, FrameBody }) body.Visibility = Visibility.Collapsed;
        if (!NvidiaVibrance.Available)
            DvcNote.Text = "Needs an NVIDIA graphics card – no NVIDIA driver was found on this PC.";

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _saveDelay.Tick += (_, _) => { _saveDelay.Stop(); AppState.SaveSettings(); };
        GameFilter.Changed += () => Dispatcher.InvokeAsync(Load);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _statusTimer.Start(); return; }
            _statusTimer.Stop();
            // The whole-screen preview only lives while you're on this page.
            ScreenPreviewToggle.IsChecked = false;
            GameFilter.PreviewOnScreen = false;
            GameFilter.Refresh();
        };
        Load();
    }

    public void OnShown() => Load();

    // ---------------- Profiles ----------------

    private void Load()
    {
        _loading = true;
        var s = AppState.Settings;
        var p = GameFilter.Current;

        ProfileList.Children.Clear();
        for (var i = 0; i < GameFilter.Profiles.Count; i++)
        {
            var index = i;
            var item = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "profile", Content = GameFilter.Profiles[i].Name,
                IsChecked = i == s.GameFilterIndex, Margin = new Thickness(0, 0, 2, 0),
            };
            item.Checked += (_, _) =>
            {
                if (_loading) return;
                AppState.Settings.GameFilterIndex = index;
                AppState.SaveSettings();
                Load();
                GameFilter.Refresh();
            };
            ProfileList.Children.Add(item);
        }

        EnabledToggle.IsChecked = s.GameFilterEnabled;
        EnhancedToggle.IsChecked = s.GameFilterEnhanced;
        ExposureSlider.Value = p.Exposure;
        BrightnessSlider.Value = p.Brightness;
        ContrastSlider.Value = p.Contrast;
        HighlightsSlider.Value = p.Highlights;
        ShadowsSlider.Value = p.Shadows;
        GammaSlider.Value = p.Gamma;
        TintHueSlider.Value = p.TintHue;
        TintSlider.Value = p.TintStrength;
        TemperatureSlider.Value = p.Temperature;
        SaturationSlider.Value = p.Saturation;
        VibranceSlider.Value = p.Vibrance;
        DvcSlider.Value = p.DigitalVibrance;
        SharpenSlider.Value = p.Sharpen;
        ClaritySlider.Value = p.Clarity;
        HdrSlider.Value = p.HdrToning;
        BloomSlider.Value = p.Bloom;
        Select(LookBox, p.Look);
        LookSlider.Value = p.LookStrength;
        VignetteSlider.Value = p.Vignette;
        Select(LetterboxBox, p.Letterbox);
        _loading = false;

        UpdateStatus();
        UpdatePreview();
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void Changed(object sender, EventArgs e)
    {
        if (_loading) return;
        var p = GameFilter.Current;
        p.Exposure = ExposureSlider.IntValue;
        p.Brightness = BrightnessSlider.IntValue;
        p.Contrast = ContrastSlider.IntValue;
        p.Highlights = HighlightsSlider.IntValue;
        p.Shadows = ShadowsSlider.IntValue;
        p.Gamma = GammaSlider.IntValue;
        p.TintHue = TintHueSlider.IntValue;
        p.TintColor = HueToHex(p.TintHue);
        p.TintStrength = TintSlider.IntValue;
        p.Temperature = TemperatureSlider.IntValue;
        p.Saturation = SaturationSlider.IntValue;
        p.Vibrance = VibranceSlider.IntValue;
        p.DigitalVibrance = DvcSlider.IntValue;
        p.Sharpen = SharpenSlider.IntValue;
        p.Clarity = ClaritySlider.IntValue;
        p.HdrToning = HdrSlider.IntValue;
        p.Bloom = BloomSlider.IntValue;
        p.Look = TagOf(LookBox, "none");
        p.LookStrength = LookSlider.IntValue;
        p.Vignette = VignetteSlider.IntValue;
        p.Letterbox = TagOf(LetterboxBox, "none");

        _saveDelay.Stop();
        _saveDelay.Start(); // sliders change many times a second – save once they rest
        UpdatePreview();
        GameFilter.Refresh();
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.GameFilterEnabled = EnabledToggle.IsChecked == true;
        AppState.SaveSettings();
        UpdateStatus();
        GameFilter.Refresh();
    }

    private void Enhanced_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.GameFilterEnhanced = EnhancedToggle.IsChecked == true;
        AppState.SaveSettings();
        if (AppState.Settings.GameFilterEnhanced) EnhancedFilter.Instance.ResetFailure();
        UpdateStatus();
        UpdatePreview();
        GameFilter.Refresh();
    }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is FrameworkElement body)
            body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Hue 0..360 → a strong color of that hue, as #RRGGBB.</summary>
    private static string HueToHex(int hue)
    {
        double h = (hue % 360 + 360) % 360 / 60.0;
        var x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };
        return $"#{(int)(r * 255):X2}{(int)(g * 255):X2}{(int)(b * 255):X2}";
    }

    private void ScreenPreview_Click(object sender, RoutedEventArgs e)
    {
        GameFilter.PreviewOnScreen = ScreenPreviewToggle.IsChecked == true;
        if (GameFilter.PreviewOnScreen && !AppState.Settings.GameFilterEnabled)
        {
            AppState.Settings.GameFilterEnabled = true;
            AppState.SaveSettings();
            EnabledToggle.IsChecked = true;
            UpdateStatus();
        }
        GameFilter.Refresh();
    }

    private void UpdateStatus()
    {
        var s = AppState.Settings;
        StatusText.Text = s.GameFilterEnabled
            ? $"On · \"{GameFilter.Current.Name}\" – active whenever a Roblox window is in front."
            : "Off. Turn it on, or press Ctrl + Alt + F in game.";

        // Settings that only work in enhanced mode look switched off without it.
        var enhanced = s.GameFilterEnhanced;
        foreach (var panel in new FrameworkElement[] { CurvesPanel, VibrancePanel, DetailsBody }) panel.Opacity = enhanced ? 1 : 0.45;

        var engine = EnhancedFilter.Instance;
        EnhancedStatus.Visibility = enhanced ? Visibility.Visible : Visibility.Collapsed;
        EnhancedStatus.Text = engine.Failed ? engine.Status.ToUpperInvariant()
            : engine.Running ? "● RUNNING OVER ROBLOX"
            : "WAITING FOR A ROBLOX WINDOW IN FRONT";
        EnhancedStatus.Foreground = (Brush)FindResource(engine.Failed ? "WarnBrush" : engine.Running ? "OkBrush" : "DimBrush");
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var name = Dialog.Ask(Window.GetWindow(this), "New profile", "Name of the new profile. It starts with the settings of the selected one.",
            "My filter")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var copy = GameFilter.Current.Clone();
        copy.Name = name;
        GameFilter.Profiles.Add(copy);
        AppState.Settings.GameFilterIndex = GameFilter.Profiles.Count - 1;
        AppState.SaveSettings();
        Load();
        GameFilter.Refresh();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var p = GameFilter.Current;
        var name = Dialog.Ask(Window.GetWindow(this), "Rename profile", "New name for the profile.", p.Name)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        p.Name = name;
        AppState.SaveSettings();
        Load();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var p = GameFilter.Current;
        var fresh = new GameFilterProfile { Name = p.Name };
        GameFilter.Profiles[AppState.Settings.GameFilterIndex] = fresh;
        AppState.SaveSettings();
        Load();
        GameFilter.Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var p = GameFilter.Current;
        if (!Dialog.Confirm(Window.GetWindow(this), "Delete profile", $"Delete the profile \"{p.Name}\"?", "Delete", danger: true)) return;
        GameFilter.Profiles.Remove(p);
        if (GameFilter.Profiles.Count == 0) AppState.Settings.GameFilterProfiles = GameFilterProfile.Defaults();
        AppState.Settings.GameFilterIndex = Math.Max(0, AppState.Settings.GameFilterIndex - 1);
        AppState.SaveSettings();
        Load();
        GameFilter.Refresh();
    }

    // ---------------- Preview ----------------

    /// <summary>
    /// Runs the filter over every pixel of the preview picture: the color matrix Windows uses, and with
    /// enhanced mode on the same steps as Orbit's GPU filter (curves, vibrance, details).
    /// </summary>
    private void UpdatePreview()
    {
        var p = GameFilter.Current;
        var m = GameFilterMath.Build(p);
        var enhanced = AppState.Settings.GameFilterEnhanced;
        float sharpen = p.Sharpen / 100f, clarity = p.Clarity / 100f, hdr = p.HdrToning / 100f, bloom = p.Bloom / 100f;
        float shadows = p.Shadows / 100f, highlights = p.Highlights / 100f, gamma = MathF.Pow(2, -p.Gamma / 100f), vibrance = p.Vibrance / 100f;

        for (int y = 0, i = 0; y < PreviewH; y++)
        for (var x = 0; x < PreviewW; x++, i += 4)
        {
            var (r, g, b) = Pixel(x, y);
            if (enhanced)
            {
                if (sharpen > 0)
                {
                    var (r1, g1, b1) = Pixel(x - 1, y);
                    var (r2, g2, b2) = Pixel(x + 1, y);
                    var (r3, g3, b3) = Pixel(x, y - 1);
                    var (r4, g4, b4) = Pixel(x, y + 1);
                    r += (r - (r1 + r2 + r3 + r4) * 0.25f) * sharpen * 1.6f;
                    g += (g - (g1 + g2 + g3 + g4) * 0.25f) * sharpen * 1.6f;
                    b += (b - (b1 + b2 + b3 + b4) * 0.25f) * sharpen * 1.6f;
                }
                float u = (x + 0.5f) / PreviewW, v = (y + 0.5f) / PreviewH;
                if (clarity > 0)
                {
                    var d = (Luma(r, g, b) - Luma(Blurred(3, u, v))) * clarity * 1.4f;
                    r += d; g += d; b += d;
                }
                if (hdr > 0)
                {
                    var d = (0.5f - Luma(Blurred(5, u, v))) * hdr * 0.45f * (1 - MathF.Abs(Luma(r, g, b) - 0.5f));
                    r += d; g += d; b += d;
                }
                if (bloom > 0)
                {
                    var (gr1, gg1, gb1) = Blurred(4, u, v);
                    var (gr2, gg2, gb2) = Blurred(6, u, v);
                    r += MathF.Max((gr1 + gr2) * 0.5f - 0.55f, 0) * bloom * 1.8f;
                    g += MathF.Max((gg1 + gg2) * 0.5f - 0.55f, 0) * bloom * 1.8f;
                    b += MathF.Max((gb1 + gb2) * 0.5f - 0.55f, 0) * bloom * 1.8f;
                }
                r = Math.Clamp(r, 0, 1); g = Math.Clamp(g, 0, 1); b = Math.Clamp(b, 0, 1);
                var l = Luma(r, g, b);
                var lift = (shadows * (1 - SmoothStep(0, 0.55f, l)) + highlights * SmoothStep(0.45f, 1, l)) * 0.3f;
                r = MathF.Pow(Math.Clamp(r + lift, 0.0001f, 1), gamma);
                g = MathF.Pow(Math.Clamp(g + lift, 0.0001f, 1), gamma);
                b = MathF.Pow(Math.Clamp(b + lift, 0.0001f, 1), gamma);
            }

            var nr = r * m[0] + g * m[5] + b * m[10] + m[20];
            var ng = r * m[1] + g * m[6] + b * m[11] + m[21];
            var nb = r * m[2] + g * m[7] + b * m[12] + m[22];

            if (enhanced && vibrance > 0)
            {
                var sat = Math.Clamp(MathF.Max(nr, MathF.Max(ng, nb)) - MathF.Min(nr, MathF.Min(ng, nb)), 0, 1);
                var k = 1 + vibrance * (1 - sat);
                var lv = Luma(nr, ng, nb);
                nr = lv + (nr - lv) * k; ng = lv + (ng - lv) * k; nb = lv + (nb - lv) * k;
            }

            _filtered[i] = ToByte(nb);
            _filtered[i + 1] = ToByte(ng);
            _filtered[i + 2] = ToByte(nr);
            _filtered[i + 3] = 255;
        }
        _preview.WritePixels(new Int32Rect(0, 0, PreviewW, PreviewH), _filtered, PreviewW * 4, 0);
        UpdateFrame();
    }

    private (float R, float G, float B) Pixel(int x, int y)
    {
        x = Math.Clamp(x, 0, PreviewW - 1);
        y = Math.Clamp(y, 0, PreviewH - 1);
        var i = (y * PreviewW + x) * 4;
        return (_scene[i + 2] / 255f, _scene[i + 1] / 255f, _scene[i] / 255f);
    }

    private static float Luma(float r, float g, float b) => r * 0.2126f + g * 0.7152f + b * 0.0722f;
    private static float Luma((float R, float G, float B) c) => Luma(c.R, c.G, c.B);

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Blurred copies of the preview picture (each half the size of the one before, like GPU mipmaps).</summary>
    private float[][]? _mips;
    private int[]? _mipW, _mipH;

    private (float R, float G, float B) Blurred(int level, float u, float v)
    {
        if (_mips == null)
        {
            _mips = new float[7][];
            _mipW = new int[7];
            _mipH = new int[7];
            _mipW[0] = PreviewW; _mipH[0] = PreviewH;
            _mips[0] = new float[PreviewW * PreviewH * 3];
            for (var i = 0; i < PreviewW * PreviewH; i++)
            {
                _mips[0][i * 3] = _scene[i * 4 + 2] / 255f;
                _mips[0][i * 3 + 1] = _scene[i * 4 + 1] / 255f;
                _mips[0][i * 3 + 2] = _scene[i * 4] / 255f;
            }
            for (var l = 1; l < 7; l++)
            {
                int pw = _mipW[l - 1], ph = _mipH[l - 1], w = Math.Max(1, pw / 2), h = Math.Max(1, ph / 2);
                _mipW[l] = w; _mipH[l] = h;
                var dst = _mips[l] = new float[w * h * 3];
                var src = _mips[l - 1];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++)
                {
                    int x0 = Math.Min(x * 2, pw - 1), x1 = Math.Min(x * 2 + 1, pw - 1), y0 = Math.Min(y * 2, ph - 1), y1 = Math.Min(y * 2 + 1, ph - 1);
                    dst[(y * w + x) * 3 + c] = (src[(y0 * pw + x0) * 3 + c] + src[(y0 * pw + x1) * 3 + c] + src[(y1 * pw + x0) * 3 + c] + src[(y1 * pw + x1) * 3 + c]) * 0.25f;
                }
            }
        }

        // Bilinear sample, like the GPU does.
        int mw = _mipW![level], mh = _mipH![level];
        var data = _mips[level];
        float fx = Math.Clamp(u * mw - 0.5f, 0, mw - 1), fy = Math.Clamp(v * mh - 0.5f, 0, mh - 1);
        int ix = (int)fx, iy = (int)fy, ix1 = Math.Min(ix + 1, mw - 1), iy1 = Math.Min(iy + 1, mh - 1);
        float tx = fx - ix, ty = fy - iy;
        float Get(int xx, int yy, int c) => data[(yy * mw + xx) * 3 + c];
        float Mix(int c) =>
            (Get(ix, iy, c) * (1 - tx) + Get(ix1, iy, c) * tx) * (1 - ty) + (Get(ix, iy1, c) * (1 - tx) + Get(ix1, iy1, c) * tx) * ty;
        return (Mix(0), Mix(1), Mix(2));
    }

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255 + 0.5f);

    /// <summary>Vignette and letterbox over the preview, like the layer over the game.</summary>
    private void UpdateFrame()
    {
        var p = GameFilter.Current;
        var a = (byte)Math.Clamp(p.Vignette * 2.4, 0, 240);
        PreviewVignette.Fill = p.Vignette <= 0 ? null : new RadialGradientBrush
        {
            RadiusX = 0.75, RadiusY = 0.75,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45),
                new GradientStop(Color.FromArgb((byte)(a * 0.45), 0, 0, 0), 0.78),
                new GradientStop(Color.FromArgb(a, 0, 0, 0), 1.0),
            },
        };

        // The picture is 16:9 and fills the width.
        var w = PreviewArea.ActualWidth;
        var h = Math.Min(PreviewArea.ActualHeight, w * 9 / 16);
        var ratio = p.Letterbox switch { "2.39" => 2.39, "1.85" => 1.85, _ => 0 };
        var bar = ratio > 0 && w > 0 ? Math.Max(0, (h - w / ratio) / 2) : 0;
        var gap = Math.Max(0, (PreviewArea.ActualHeight - h) / 2);
        PreviewBarTop.Height = PreviewBarBottom.Height = bar;
        PreviewBarTop.Margin = new Thickness(0, gap, 0, 0);
        PreviewBarBottom.Margin = new Thickness(0, 0, 0, gap);
    }

    /// <summary>A small, bright blocky game scene for the preview (sky, sun, ground, blocks, a figure).</summary>
    private static byte[] DrawScene()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            const double w = PreviewW, h = PreviewH, horizon = h * 0.52;
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x4A, 0x9B, 0xF5), Color.FromRgb(0xCF, 0xE8, 0xFF), 90), null, new Rect(0, 0, w, horizon + 2));

            // Sun with glow.
            var glow = new RadialGradientBrush(Color.FromArgb(0xC0, 0xFF, 0xF4, 0xC2), Color.FromArgb(0, 0xFF, 0xF4, 0xC2));
            dc.DrawEllipse(glow, null, new Point(w * 0.82, h * 0.16), 70, 70);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xB0)), null, new Point(w * 0.82, h * 0.16), 24, 24);

            // Clouds.
            var cloud = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));
            foreach (var (x, y, s) in new[] { (0.12, 0.14, 1.0), (0.42, 0.08, 0.8), (0.6, 0.22, 0.7) })
            {
                dc.DrawRoundedRectangle(cloud, null, new Rect(w * x, h * y, 110 * s, 26 * s), 13 * s, 13 * s);
                dc.DrawRoundedRectangle(cloud, null, new Rect(w * x + 22 * s, h * y - 14 * s, 60 * s, 30 * s), 15 * s, 15 * s);
            }

            // Hills and ground.
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x5F, 0xA8, 0x5A)), null, new Point(w * 0.2, horizon + 10), 210, 60);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x4E, 0x96, 0x4C)), null, new Point(w * 0.75, horizon + 16), 260, 70);
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x5D, 0xB8, 0x4F), Color.FromRgb(0x3A, 0x8A, 0x35), 90), null, new Rect(0, horizon, w, h - horizon));
            var line = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1);
            for (var i = -12; i <= 12; i++)
                dc.DrawLine(line, new Point(w / 2 + i * 14, horizon), new Point(w / 2 + i * 90, h));
            for (var y = horizon + 8; y < h; y += (y - horizon) * 0.35 + 6)
                dc.DrawLine(line, new Point(0, y), new Point(w, y));

            // Blocks (front, top and side face).
            void Block(double x, double y, double size, Color c)
            {
                var front = new SolidColorBrush(c);
                var top = new SolidColorBrush(Color.FromRgb((byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40)));
                var side = new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7)));
                var d = size * 0.35;
                dc.DrawGeometry(top, null, Geometry.Parse(F($"M{x},{y} L{x + d},{y - d} L{x + size + d},{y - d} L{x + size},{y} Z")));
                dc.DrawGeometry(side, null, Geometry.Parse(F($"M{x + size},{y} L{x + size + d},{y - d} L{x + size + d},{y + size - d} L{x + size},{y + size} Z")));
                dc.DrawRectangle(front, null, new Rect(x, y, size, size));
            }
            Block(60, 228, 70, Color.FromRgb(0xE5, 0x3E, 0x3E));
            Block(140, 250, 52, Color.FromRgb(0xF5, 0xC5, 0x1B));
            Block(470, 232, 64, Color.FromRgb(0x2F, 0x7B, 0xE8));
            Block(545, 258, 46, Color.FromRgb(0xF2, 0x8C, 0x28));
            Block(500, 180, 44, Color.FromRgb(0x9B, 0x5C, 0xF6));

            // Tree.
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x7A, 0x4E, 0x2D)), null, new Rect(388, 200, 14, 52));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x3E)), null, new Point(395, 186), 34, 34);

            // A blocky figure.
            double fx = 300, fy = 200;
            var skin = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0xA0));
            dc.DrawRectangle(skin, null, new Rect(fx + 4, fy - 30, 28, 28));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)), null, new Rect(fx, fy, 36, 40));
            dc.DrawRectangle(skin, null, new Rect(fx - 14, fy, 14, 38));
            dc.DrawRectangle(skin, null, new Rect(fx + 36, fy, 14, 38));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x33, 0x37, 0x45)), null, new Rect(fx, fy + 40, 17, 40));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x33, 0x37, 0x45)), null, new Rect(fx + 19, fy + 40, 17, 40));
            dc.DrawRectangle(Brushes.Black, null, new Rect(fx + 11, fy - 21, 4, 5));
            dc.DrawRectangle(Brushes.Black, null, new Rect(fx + 21, fy - 21, 4, 5));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), null, new Point(fx + 18, fy + 82), 30, 6);
        }

        var bitmap = new RenderTargetBitmap(PreviewW, PreviewH, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[PreviewW * PreviewH * 4];
        bitmap.CopyPixels(pixels, PreviewW * 4, 0);
        return pixels;
    }

    private static string F(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Chevron of a section header: points down when closed, up when open.</summary>
public sealed class ChevronAngle : System.Windows.Data.IValueConverter
{
    public static readonly ChevronAngle Instance = new();
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is Visibility.Visible ? 180.0 : 0.0;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
