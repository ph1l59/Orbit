using System.Windows;
using System.Windows.Controls;

namespace Orbit.Views;

/// <summary>Minimal dark text-input dialog, built in code.</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button
        {
            Content = "Speichern", IsDefault = true, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
        };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 100 };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.FindResource("Sub"),
        });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Owner = owner,
            Content = panel,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"),
        };

        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return window.ShowDialog() == true ? box.Text : null;
    }
}
