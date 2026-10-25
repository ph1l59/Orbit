using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Orbit.Views;

/// <summary>Dialogs in Orbit's own style (instead of the grey Windows message box).</summary>
public static class Dialog
{
    private static T R<T>(string key) => (T)Application.Current.FindResource(key);

    private static Window Build(Window? owner, string title, UIElement body, StackPanel buttons, out Window window)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = "ORBIT // " + title.ToUpperInvariant(), Style = R<Style>("Crumb"), Foreground = R<Brush>("SignalBrush") });
        panel.Children.Add(new TextBlock
        {
            Text = title, FontFamily = R<FontFamily>("DisplayFont"), FontSize = 20, FontWeight = FontWeights.Light,
            Foreground = R<Brush>("TextBrush"), Margin = new Thickness(0, 6, 0, 10),
        });
        panel.Children.Add(body);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Orientation = Orientation.Horizontal;
        buttons.Margin = new Thickness(0, 22, 0, 0);
        panel.Children.Add(buttons);

        window = new Window
        {
            Title = title,
            Content = new Border
            {
                Child = panel,
                BorderBrush = R<Brush>("LineStrongBrush"),
                BorderThickness = new Thickness(1),
            },
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = R<Brush>("PanelBrush"),
            Foreground = R<Brush>("TextBrush"),
            FontFamily = R<FontFamily>("UiFont"),
            FontSize = 13,
            Topmost = owner == null,
        };
        if (owner != null && owner.IsVisible) window.Owner = owner;
        var w = window;
        window.MouseLeftButtonDown += (_, _) => { try { w.DragMove(); } catch { /* ignore */ } };
        return window;
    }

    private static TextBlock Message(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = R<Brush>("DimBrush"), FontSize = 13, LineHeight = 19,
    };

    public static bool Confirm(Window? owner, string title, string message, string okText = "OK", bool danger = false)
    {
        var buttons = new StackPanel();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };
        var ok = new Button
        {
            Content = okText, IsDefault = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0),
            Style = R<Style>(danger ? "DangerButton" : "PrimaryButton"),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Build(owner, title, Message(message), buttons, out var window);
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message)
    {
        var buttons = new StackPanel();
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 96, Style = R<Style>("PrimaryButton") };
        buttons.Children.Add(ok);
        Build(owner, title, Message(message), buttons, out var window);
        ok.Click += (_, _) => window.DialogResult = true;
        window.ShowDialog();
    }

    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var body = new StackPanel();
        body.Children.Add(Message(prompt));
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(box);

        var buttons = new StackPanel();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), Style = R<Style>("PrimaryButton") };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Build(owner, title, body, buttons, out var window);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }
}

/// <summary>A small notification in the bottom-right corner of the screen.</summary>
public static class Toast
{
    private static T R<T>(string key) => (T)Application.Current.FindResource(key);

    public static void Show(string title, string message)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "ORBIT // " + title.ToUpperInvariant(), Style = R<Style>("Crumb"), Foreground = R<Brush>("SignalBrush") });
        panel.Children.Add(new TextBlock { Text = message, Foreground = R<Brush>("TextBrush"), FontSize = 14, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });

        var window = new Window
        {
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = R<Brush>("PanelBrush"),
            FontFamily = R<FontFamily>("UiFont"),
            Content = new Border
            {
                BorderBrush = R<Brush>("LineStrongBrush"),
                BorderThickness = new Thickness(1, 1, 1, 1),
                Child = new Border
                {
                    BorderBrush = R<Brush>("SignalBrush"),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(16, 12, 16, 14),
                    Child = panel,
                },
            },
            Opacity = 0,
        };

        window.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            window.Left = area.Right - window.ActualWidth - 16;
            window.Top = area.Bottom - window.ActualHeight - 16;
            window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        };
        window.MouseLeftButtonUp += (_, _) => window.Close();
        window.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => window.Close();
            window.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        timer.Start();
    }
}
