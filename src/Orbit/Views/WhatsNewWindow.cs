using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>"What's new" – shown once after Orbit was updated (and from the About page).</summary>
public sealed class WhatsNewWindow : Window
{
    private sealed record Change(string Glyph, string Title, string Text);

    /// <summary>The changes of this version. Edit this list for every release.</summary>
    private static readonly Change[] Changes =
    {
        new("", "Disconnect notification",
            "When a Roblox window loses its game (disconnected, kicked, server closed), Orbit tells you which account it was and why."),
        new("\uECA5", "Arrange windows",
            "One click puts all open Roblox windows side by side on your screen – and another click puts them back. Accounts page → Roblox windows."),
        new("", "Resource saver",
            "Roblox windows in the background get less CPU, the one you're playing in stays fast. Turn it on under Accounts → Roblox windows."),
        new("", "Launch presets",
            "Save your selected accounts and game as a preset with a name, and start everything again with one click."),
        new("", "Robux in the account picker",
            "The account picker on the Play page shows the Robux balance of every account."),
        new("", "Login check",
            "At start Orbit checks every saved login and tells you which accounts need to log in again."),
        new("", "Playtime",
            "The Play page shows how long you played Roblox today, this week and in total."),
    };

    private static T R<T>(string key) => (T)Application.Current.FindResource(key);

    /// <summary>
    /// Shows the window once per version, but only when Orbit was updated
    /// (not on the very first start). Returns true if it was shown.
    /// </summary>
    public static bool ShowIfUpdated(Window owner, bool startedAfterUpdate)
    {
        var s = AppState.Settings;
        var current = Updater.DisplayVersion;
        if (s.LastSeenVersion == current) return false;

        var updated = startedAfterUpdate || s.LastSeenVersion.Length > 0;
        s.LastSeenVersion = current;
        AppState.SaveSettings();
        if (!updated) return false; // fresh install: nothing "new" yet

        new WhatsNewWindow(owner).ShowDialog();
        return true;
    }

    public WhatsNewWindow(Window? owner)
    {
        Title = "What's new in Orbit";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = R<FontFamily>("UiFont");
        Foreground = R<Brush>("TextBrush");
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // ---- Header: logo + version ----
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")),
            Width = 58, Height = 58,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xA8, 0x55, 0xF7), BlurRadius = 30, ShadowDepth = 0, Opacity = 0.8 },
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);

        var heading = new StackPanel { Margin = new Thickness(16, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = "WHAT'S NEW", Style = R<Style>("Crumb"), Foreground = R<Brush>("SignalLightBrush") });
        var title = new TextBlock { FontFamily = R<FontFamily>("DisplayFont"), FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0) };
        title.Inlines.Add(new System.Windows.Documents.Run("Orbit "));
        title.Inlines.Add(new System.Windows.Documents.Run("v" + Updater.DisplayVersion) { Foreground = R<Brush>("AccentTextBrush") });
        heading.Children.Add(title);

        var header = new DockPanel();
        header.Children.Add(logo);
        header.Children.Add(heading);

        // ---- The changes ----
        var list = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        var index = 0;
        foreach (var change in Changes)
            list.Children.Add(Row(change, index++));

        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 430, Padding = new Thickness(0, 0, 6, 0) };

        var ok = new Button { Content = "Let's go", IsDefault = true, IsCancel = true, MinWidth = 120, Height = 38, Style = R<Style>("SolidButton") };
        ok.Click += (_, _) => Close();
        var releases = new Button { Content = "All releases", MinWidth = 110, Height = 38, Margin = new Thickness(0, 0, 8, 0) };
        releases.Click += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/ph1l59/Orbit/releases") { UseShellExecute = true });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(releases);
        buttons.Children.Add(ok);

        var content = new StackPanel { Margin = new Thickness(28, 26, 28, 24) };
        content.Children.Add(header);
        content.Children.Add(scroll);
        content.Children.Add(buttons);

        // ---- Card with purple glow at the top ----
        var glow = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x55, 0x7C, 0x3A, 0xED), 0),
                new GradientStop(Colors.Transparent, 1),
            })
            {
                Center = new Point(0.2, 0), GradientOrigin = new Point(0.2, 0), RadiusX = 0.9, RadiusY = 0.6,
            },
            IsHitTestVisible = false,
        };
        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = R<Brush>("PanelBrush"),
            BorderBrush = R<Brush>("LineStrongBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(20),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 30, ShadowDepth = 6, Opacity = 0.6 },
            Child = new Grid { Children = { glow, content } },
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var scale = new ScaleTransform(0.94, 0.94);
        card.RenderTransform = scale;
        Content = card;

        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* ignore */ } };

        // Pop in: grow slightly and fade in.
        Opacity = 0;
        Loaded += (_, _) =>
        {
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            var duration = TimeSpan.FromMilliseconds(380);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        };
    }

    /// <summary>One change: round icon, title, description. Slides in a little after the one above it.</summary>
    private static FrameworkElement Row(Change change, int index)
    {
        var icon = new Grid { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Top };
        icon.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = R<Brush>("SignalTintBrush"), BorderBrush = R<Brush>("SignalBorderBrush"), BorderThickness = new Thickness(1) });
        icon.Children.Add(new TextBlock
        {
            Text = change.Glyph, Style = R<Style>("Icon"), FontSize = 17, Foreground = R<Brush>("SignalLightBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = change.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = change.Text, Style = R<Style>("RowDesc"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16), Opacity = 0 };
        row.Children.Add(icon);
        row.Children.Add(text);

        var shift = new TranslateTransform(0, 10);
        row.RenderTransform = shift;
        row.Loaded += (_, _) =>
        {
            var begin = TimeSpan.FromMilliseconds(120 + index * 60);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(360)) { BeginTime = begin, EasingFunction = ease });
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = begin });
        };
        return row;
    }
}
