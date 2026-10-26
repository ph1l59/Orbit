using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Orbit.Views;

/// <summary>
/// Small notification card in the bottom-right corner of the screen. It slides in from the right,
/// a light sweeps over it every couple of seconds, a thin bar shows the time left, and it slides out again.
/// Several notifications stack upwards.
/// </summary>
public static class Toast
{
    private const double CardWidth = 340;
    private const double Gap = 10;
    private static readonly TimeSpan ShowTime = TimeSpan.FromSeconds(5);
    private static readonly List<Window> Open = new();
    private static readonly HashSet<Window> Placed = new();

    private static T R<T>(string key) => (T)Application.Current.FindResource(key);

    public static void Show(string title, string message) => Show(title, message, null, null, null);

    /// <param name="imageUrl">Optional round picture on the left (e.g. a friend's avatar). Default: Orbit logo.</param>
    /// <param name="actionText">Optional button text, e.g. "Join".</param>
    public static void Show(string title, string message, string? imageUrl, string? actionText, Action? action)
    {
        // ---- picture ----
        FrameworkElement picture;
        if (!string.IsNullOrEmpty(imageUrl))
        {
            var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
            try { brush.ImageSource = new BitmapImage(new Uri(imageUrl)); } catch { /* keep empty */ }
            var grid = new Grid { Width = 40, Height = 40 };
            grid.Children.Add(new Ellipse { Fill = R<Brush>("Raised2Brush") });
            grid.Children.Add(new Ellipse { Fill = brush });
            grid.Children.Add(new Ellipse { Stroke = R<Brush>("SignalBorderBrush"), StrokeThickness = 1.5 });
            picture = grid;
        }
        else
        {
            var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")), Width = 34, Height = 34 };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            picture = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(11),
                Background = R<Brush>("SignalTintBrush"), BorderBrush = R<Brush>("SignalBorderBrush"), BorderThickness = new Thickness(1),
                Child = logo,
            };
        }
        picture.VerticalAlignment = VerticalAlignment.Top;

        // ---- text ----
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), Style = R<Style>("Crumb"), Foreground = R<Brush>("SignalLightBrush"),
        });
        text.Children.Add(new TextBlock
        {
            Text = message, Foreground = R<Brush>("TextBrush"), FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        var row = new DockPanel();
        DockPanel.SetDock(picture, Dock.Left);
        row.Children.Add(picture);

        Window? window = null;
        if (actionText != null && action != null)
        {
            var button = new Button
            {
                Content = actionText, Style = R<Style>("SolidButton"), Height = 30, Padding = new Thickness(14, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            };
            button.Click += (_, _) => { action(); if (window != null) Hide(window); };
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
        row.Children.Add(text);

        // ---- time bar + shimmer ----
        var timeBar = new Border
        {
            Height = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Background = R<Brush>("AccentTextBrush"), Opacity = 0.9,
            Width = CardWidth,
        };

        var shimmerMove = new TranslateTransform(-220, 0);
        var shimmer = new Rectangle
        {
            Width = 140, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false,
            RenderTransform = new TransformGroup { Children = { new SkewTransform(-20, 0), shimmerMove } },
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(46, 232, 210, 255), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            },
        };

        var inner = new Grid { ClipToBounds = true };
        inner.Children.Add(new Border { Padding = new Thickness(14, 13, 14, 15), Child = row });
        inner.Children.Add(shimmer);
        inner.Children.Add(timeBar);

        var card = new Border
        {
            Width = CardWidth,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = R<Brush>("LineStrongBrush"),
            Background = new LinearGradientBrush(Color.FromRgb(0x1C, 0x18, 0x26), Color.FromRgb(0x13, 0x11, 0x19), 90),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 4, Opacity = 0.55 },
            Child = inner,
            Margin = new Thickness(12),
        };
        // Round the inner content too (so the shimmer and the bar don't poke out of the corners).
        inner.Loaded += (_, _) => inner.Clip = new RectangleGeometry(new Rect(0, 0, inner.ActualWidth, inner.ActualHeight), 13, 13);

        var slide = new TranslateTransform(CardWidth + 40, 0);
        card.RenderTransform = slide;

        window = new Window
        {
            Width = CardWidth + 24,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            FontFamily = R<FontFamily>("UiFont"),
            Content = card,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Right - (CardWidth + 24) - 4,
            Top = SystemParameters.WorkArea.Bottom - 110,
        };

        var w = window;
        var timer = new DispatcherTimer { Interval = ShowTime };
        timer.Tick += (_, _) => { timer.Stop(); Hide(w); };

        w.Loaded += (_, _) =>
        {
            Open.Add(w);
            Arrange();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(CardWidth + 40, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });

            // A sweep of light that comes back every couple of seconds while the notification is open.
            var sweep = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromMilliseconds(320),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-220, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            sweep.KeyFrames.Add(new EasingDoubleKeyFrame(CardWidth + 60, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(CardWidth + 60, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
            shimmerMove.BeginAnimation(TranslateTransform.XProperty, sweep);

            StartTimeBar(timeBar, ShowTime);
            timer.Start();
        };

        // Hovering keeps it open; a click (outside the button) closes it.
        card.MouseEnter += (_, _) => { timer.Stop(); timeBar.BeginAnimation(FrameworkElement.WidthProperty, null); timeBar.Width = CardWidth; };
        card.MouseLeave += (_, _) => { StartTimeBar(timeBar, ShowTime); timer.Start(); };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            Hide(w);
        };

        window.Show();
    }

    private static void StartTimeBar(Border bar, TimeSpan time) =>
        bar.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(CardWidth, 0, time));

    private static void Hide(Window window)
    {
        if (window.Tag as string == "closing") return;
        window.Tag = "closing";
        if (window.Content is not Border { RenderTransform: TranslateTransform slide } card) { window.Close(); return; }

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(260);
        var away = new DoubleAnimation(0, CardWidth + 40, duration) { EasingFunction = ease };
        away.Completed += (_, _) =>
        {
            window.Close();
            Open.Remove(window);
            Placed.Remove(window);
            Arrange();
        };
        slide.BeginAnimation(TranslateTransform.XProperty, away);
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, duration));
    }

    /// <summary>Newest at the bottom, older ones move up smoothly.</summary>
    private static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - 4;
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var w = Open[i];
            var height = w.ActualHeight;
            var top = bottom - height;
            w.Left = area.Right - w.Width - 4;
            if (!Placed.Add(w) || Math.Abs(w.Top - top) < 1)
            {
                if (Math.Abs(w.Top - top) >= 1)
                    w.BeginAnimation(Window.TopProperty, new DoubleAnimation(w.Top, top, TimeSpan.FromMilliseconds(200))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.Stop,
                    });
            }
            w.Top = top;
            bottom = top - Gap + 24; // the window has a 12 px shadow margin on each side
        }
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
            if (x is T t) return t;
        return null;
    }
}
