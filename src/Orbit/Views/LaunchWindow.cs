using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>
/// Small "Starting Roblox…" window: Orbit logo, status, progress bar and a Cancel button.
/// Used by the Play page and when the Roblox website hands a game to Orbit.
/// </summary>
public sealed class LaunchWindow : Window
{
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly Border _sweep;
    private readonly Button _cancel;

    public bool CancelRequested { get; private set; }

    private static T R<T>(string key) => (T)Application.Current.FindResource(key);

    /// <param name="who">Shown top left, e.g. the account name. Null = "Orbit".</param>
    public LaunchWindow(Window? owner, string? who)
    {
        Title = "Orbit";
        Width = 500;
        Height = 280;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = owner == null;
        Topmost = owner == null;
        FontFamily = R<FontFamily>("UiFont");
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Top line: who is launching, Roblox version.
        var top = new DockPanel { Margin = new Thickness(16, 12, 16, 0) };
        var version = new TextBlock
        {
            Text = Bootstrapper.InstalledVersion is { } v ? "Version: " + v : "",
            Style = R<Style>("Crumb"),
        };
        DockPanel.SetDock(version, Dock.Right);
        top.Children.Add(version);
        top.Children.Add(new TextBlock { Text = who ?? "Orbit", Style = R<Style>("Crumb"), Foreground = R<Brush>("DimBrush") });

        // Logo with a soft purple glow that breathes.
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")),
            Width = 84,
            Height = 84,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xA8, 0x55, 0xF7), BlurRadius = 34, ShadowDepth = 0, Opacity = 0.85 },
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var breathe = new DoubleAnimation(0.45, 0.95, TimeSpan.FromSeconds(1.4))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase(),
        };
        logo.Effect.BeginAnimation(DropShadowEffect.OpacityProperty, breathe);

        _status = new TextBlock
        {
            Text = "Starting Roblox…",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = R<Brush>("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 14),
        };

        // Progress: a real bar while downloading, otherwise a sweeping highlight.
        _bar = new ProgressBar { Maximum = 100, Height = 6, Margin = new Thickness(32, 0, 32, 0) };
        _sweep = new Border
        {
            Width = 140, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
            Background = R<Brush>("AccentTextBrush"), RenderTransform = new TranslateTransform(),
        };
        var track = new Border
        {
            Height = 6, CornerRadius = new CornerRadius(3), Background = R<Brush>("LineBrush"),
            Margin = new Thickness(32, 0, 32, 0), ClipToBounds = true, Child = _sweep,
        };
        var bars = new Grid();
        bars.Children.Add(track);
        bars.Children.Add(_bar);
        _bar.Visibility = Visibility.Collapsed;
        track.Loaded += (_, _) =>
        {
            var slide = new DoubleAnimation(-140, track.ActualWidth, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever };
            _sweep.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        };

        _cancel = new Button { Content = "Cancel", MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        _cancel.Click += (_, _) =>
        {
            CancelRequested = true;
            _cancel.IsEnabled = false;
            SetStatus("Cancelling…");
        };

        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(logo);
        center.Children.Add(_status);
        center.Children.Add(bars);
        center.Children.Add(_cancel);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(top);
        Grid.SetRow(center, 1);
        layout.Children.Add(center);

        var glow = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = R<Brush>("GlowCenter"),
            IsHitTestVisible = false,
        };
        var root = new Grid();
        root.Children.Add(glow);
        root.Children.Add(layout);

        Content = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = R<Brush>("BgBrush"),
            BorderBrush = R<Brush>("LineStrongBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 4, Opacity = 0.6 },
            Child = root,
        };
        Width += 28;
        Height += 28;

        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* ignore */ } };
        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>Shows real progress (0–100), or the sweeping bar when <paramref name="percent"/> is null.</summary>
    public void SetProgress(double? percent)
    {
        if (percent is { } p and > 0 and < 100)
        {
            _bar.Visibility = Visibility.Visible;
            _bar.Value = p;
            _sweep.Visibility = Visibility.Hidden;
        }
        else
        {
            _bar.Visibility = Visibility.Collapsed;
            _sweep.Visibility = Visibility.Visible;
        }
    }

    public void HideCancel() => _cancel.Visibility = Visibility.Hidden;

    /// <summary>Fades out and closes.</summary>
    public void CloseSoftly()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
