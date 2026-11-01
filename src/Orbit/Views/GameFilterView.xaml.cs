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
    private static readonly string[] Tints = { "#A855F7", "#3B82F6", "#06B6D4", "#22C55E", "#F59E0B", "#EF4444", "#EC4899" };

    private readonly byte[] _scene;
    private readonly byte[] _filtered = new byte[PreviewW * PreviewH * 4];
    private readonly WriteableBitmap _preview = new(PreviewW, PreviewH, 96, 96, PixelFormats.Bgra32, null);
    private readonly DispatcherTimer _saveDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loading;

    public GameFilterView()
    {
        InitializeComponent();
        _scene = DrawScene();
        PreviewImage.Source = _preview;
        PreviewArea.SizeChanged += (_, _) => UpdateFrame();

        foreach (var hex in Tints)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new RadioButton
            {
                GroupName = "tint", Tag = hex, Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand,
                Template = SwatchTemplate(color), ToolTip = "Tint color",
            };
            swatch.Checked += (_, _) => Changed(this, EventArgs.Empty);
            TintColors.Children.Add(swatch);
        }

        _saveDelay.Tick += (_, _) => { _saveDelay.Stop(); AppState.SaveSettings(); };
        GameFilter.Changed += () => Dispatcher.InvokeAsync(Load);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
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
        ExposureSlider.Value = p.Exposure;
        BrightnessSlider.Value = p.Brightness;
        ContrastSlider.Value = p.Contrast;
        SaturationSlider.Value = p.Saturation;
        TemperatureSlider.Value = p.Temperature;
        TintSlider.Value = p.TintStrength;
        foreach (var swatch in TintColors.Children.OfType<RadioButton>())
            swatch.IsChecked = string.Equals((string)swatch.Tag, p.TintColor, StringComparison.OrdinalIgnoreCase);
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
        p.Saturation = SaturationSlider.IntValue;
        p.Temperature = TemperatureSlider.IntValue;
        p.TintStrength = TintSlider.IntValue;
        p.TintColor = TintColors.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? p.TintColor;
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
        StatusText.Text = AppState.Settings.GameFilterEnabled
            ? $"On · \"{GameFilter.Current.Name}\" – active whenever a Roblox window is in front."
            : "Off. Turn it on, or press Ctrl + Alt + F in game.";
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

    /// <summary>Runs the same color matrix Windows will use over every pixel of the preview picture.</summary>
    private void UpdatePreview()
    {
        var m = GameFilterMath.Build(GameFilter.Current);
        for (var i = 0; i < _scene.Length; i += 4)
        {
            float b = _scene[i] / 255f, g = _scene[i + 1] / 255f, r = _scene[i + 2] / 255f;
            var nr = r * m[0] + g * m[5] + b * m[10] + m[20];
            var ng = r * m[1] + g * m[6] + b * m[11] + m[21];
            var nb = r * m[2] + g * m[7] + b * m[12] + m[22];
            _filtered[i] = ToByte(nb);
            _filtered[i + 1] = ToByte(ng);
            _filtered[i + 2] = ToByte(nr);
            _filtered[i + 3] = 255;
        }
        _preview.WritePixels(new Int32Rect(0, 0, PreviewW, PreviewH), _filtered, PreviewW * 4, 0);
        UpdateFrame();
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

    private static ControlTemplate SwatchTemplate(Color color)
    {
        var template = new ControlTemplate(typeof(RadioButton));
        var ring = new FrameworkElementFactory(typeof(Border), "ring");
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        ring.SetValue(Border.PaddingProperty, new Thickness(3));
        var dot = new FrameworkElementFactory(typeof(Ellipse));
        dot.SetValue(Shape.FillProperty, new SolidColorBrush(color));
        ring.AppendChild(dot);
        template.VisualTree = ring;
        template.Triggers.Add(new Trigger
        {
            Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true,
            Setters = { new Setter(Border.BorderBrushProperty, Brushes.White, "ring") },
        });
        return template;
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
