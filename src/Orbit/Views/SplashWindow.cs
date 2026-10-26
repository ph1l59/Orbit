using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>
/// Small loading window shown while Orbit starts. The percentage follows the real startup steps
/// (settings, accounts, Roblox setup, interface, avatars) – nothing is slowed down on purpose.
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly TextBlock _step;
    private readonly TextBlock _percent;
    private readonly Border _fill;
    private readonly Border _track;
    private double _shown;

    private static T R<T>(string key) => (T)Application.Current.FindResource(key);

    public SplashWindow()
    {
        Title = "Orbit";
        Width = 380;
        Height = 250;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        FontFamily = R<FontFamily>("UiFont");
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/orbit.ico"));

        // Logo with an orbiting dot.
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")), Width = 64, Height = 64 };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        logo.Effect = new DropShadowEffect { Color = Color.FromRgb(0xA8, 0x55, 0xF7), BlurRadius = 30, ShadowDepth = 0, Opacity = 0.8 };

        var dot = new Ellipse { Width = 7, Height = 7, Fill = R<Brush>("SignalLightBrush") };
        dot.Effect = new DropShadowEffect { Color = Color.FromRgb(0xC0, 0x84, 0xFC), BlurRadius = 10, ShadowDepth = 0, Opacity = 1 };
        Canvas.SetLeft(dot, 46);
        Canvas.SetTop(dot, 0);
        var spin = new RotateTransform(0, 50, 50);
        var orbit = new Canvas { Width = 100, Height = 100, RenderTransform = spin, IsHitTestVisible = false };
        orbit.Children.Add(dot);
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });

        var logoBox = new Grid { Width = 100, Height = 100, HorizontalAlignment = HorizontalAlignment.Center };
        logoBox.Children.Add(orbit);
        logoBox.Children.Add(logo);

        var title = new TextBlock
        {
            FontFamily = R<FontFamily>("DisplayFont"), FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = R<Brush>("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
        };
        title.Inlines.Add(new System.Windows.Documents.Run("Orbit"));
        title.Inlines.Add(new System.Windows.Documents.Run(".") { Foreground = R<Brush>("SignalBrush") });

        _step = new TextBlock { Text = "Starting…", Style = R<Style>("RowDesc"), Margin = new Thickness(0) };
        _percent = new TextBlock { Text = "0%", Style = R<Style>("Crumb"), Foreground = R<Brush>("SignalLightBrush"), VerticalAlignment = VerticalAlignment.Center };
        var stepRow = new DockPanel { Margin = new Thickness(30, 14, 30, 8) };
        DockPanel.SetDock(_percent, Dock.Right);
        stepRow.Children.Add(_percent);
        stepRow.Children.Add(_step);

        _fill = new Border
        {
            Width = 0, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
            Background = R<Brush>("AccentTextBrush"),
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xA8, 0x55, 0xF7), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 },
        };
        _track = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), Background = R<Brush>("LineBrush"),
            Margin = new Thickness(30, 0, 30, 0), Child = _fill,
        };

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(logoBox);
        content.Children.Add(title);
        content.Children.Add(stepRow);
        content.Children.Add(_track);

        var glow = new Border { CornerRadius = new CornerRadius(16), Background = R<Brush>("GlowCenter"), IsHitTestVisible = false };
        var inner = new Grid();
        inner.Children.Add(glow);
        inner.Children.Add(content);

        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = R<Brush>("BgBrush"),
            BorderBrush = R<Brush>("LineStrongBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 26, ShadowDepth = 4, Opacity = 0.6 },
            Child = inner,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var scale = new ScaleTransform(0.96, 0.96);
        card.RenderTransform = scale;
        Content = card;
        Width += 28;
        Height += 28;

        Opacity = 0;
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        };
    }

    /// <summary>Moves the bar to <paramref name="percent"/> with a short smooth animation and lets it render.</summary>
    public async Task StepAsync(string text, double percent)
    {
        _step.Text = text;
        var from = _shown;
        _shown = Math.Clamp(percent, 0, 100);
        var width = Math.Max(0, _track.ActualWidth);
        _fill.BeginAnimation(WidthProperty, new DoubleAnimation(from / 100 * width, _shown / 100 * width, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        // Count the number up in a few frames so it feels alive.
        for (var i = 1; i <= 6; i++)
        {
            _percent.Text = $"{Math.Round(from + (_shown - from) * i / 6):0}%";
            await Task.Delay(16);
        }
        await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
    }

    public void CloseSoftly()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
