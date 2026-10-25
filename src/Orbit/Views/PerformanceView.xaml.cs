using System.Windows;
using System.Windows.Controls;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>An entry in a settings dropdown.</summary>
public sealed record Option(string Label, object Value)
{
    public override string ToString() => Label;
}

public partial class PerformanceView : UserControl
{
    private const int CustomFps = -2;

    public PerformanceView()
    {
        InitializeComponent();

        FpsCombo.ItemsSource = new List<Option>
        {
            new("Nicht ändern", 0),
            new("30 FPS", 30),
            new("60 FPS", 60),
            new("75 FPS", 75),
            new("90 FPS", 90),
            new("120 FPS", 120),
            new("144 FPS", 144),
            new("165 FPS", 165),
            new("240 FPS", 240),
            new("Unbegrenzt", 9999),
            new("Eigener Wert…", CustomFps),
        };

        var quality = new List<Option> { new("Automatisch", 0) };
        for (var i = 1; i <= 21; i++)
            quality.Add(new(i switch { 1 => "1 – Minimum", 21 => "21 – Maximum", _ => i.ToString() }, i));
        QualityCombo.ItemsSource = quality;

        TextureCombo.ItemsSource = new List<Option>
        {
            new("Automatisch", -1),
            new("Sehr niedrig", 0),
            new("Niedrig", 1),
            new("Mittel", 2),
            new("Hoch", 3),
        };

        MsaaCombo.ItemsSource = new List<Option>
        {
            new("Automatisch", -1),
            new("Aus (1x)", 1),
            new("2x", 2),
            new("4x", 4),
            new("8x", 8),
        };

        RendererCombo.ItemsSource = new List<Option>
        {
            new("Automatisch", "auto"),
            new("Direct3D 11", "d3d11"),
            new("Vulkan", "vulkan"),
            new("OpenGL", "opengl"),
        };

        LoadFrom(AppState.Settings);
    }

    private static void Select(ComboBox combo, object value)
    {
        var items = (IEnumerable<Option>)combo.ItemsSource;
        combo.SelectedItem = items.FirstOrDefault(o => Equals(o.Value, value)) ?? items.First();
    }

    private static T Selected<T>(ComboBox combo, T fallback)
        => combo.SelectedItem is Option { Value: T value } ? value : fallback;

    private void LoadFrom(AppSettings s)
    {
        var presets = ((IEnumerable<Option>)FpsCombo.ItemsSource).Select(o => (int)o.Value).ToList();
        if (s.FpsCap > 0 && !presets.Contains(s.FpsCap))
        {
            Select(FpsCombo, CustomFps);
            CustomFpsBox.Text = s.FpsCap.ToString();
        }
        else
        {
            Select(FpsCombo, s.FpsCap);
            CustomFpsBox.Text = "";
        }

        Select(QualityCombo, s.QualityLevel);
        Select(TextureCombo, s.TextureQuality);
        Select(MsaaCombo, s.MsaaSamples);
        Select(RendererCombo, s.Renderer);

        DpiToggle.IsChecked = s.DisableDpiScale;
        FullscreenToggle.IsChecked = s.ExclusiveFullscreen;
        GrassToggle.IsChecked = s.DisableGrass;
        SkyToggle.IsChecked = s.GraySky;
        CustomFlagsBox.Text = s.CustomFlags;
    }

    private void FpsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var custom = Selected(FpsCombo, 0) == CustomFps;
        CustomFpsBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom && IsLoaded) CustomFpsBox.Focus();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;

        var fps = Selected(FpsCombo, 0);
        if (fps == CustomFps)
        {
            if (!int.TryParse(CustomFpsBox.Text.Trim(), out fps) || fps < 5 || fps > 1000)
            {
                ShowStatus("Bitte einen eigenen FPS-Wert zwischen 5 und 1000 eingeben.", true);
                return;
            }
        }

        FlagService.ParseCustomFlags(CustomFlagsBox.Text, out var flagError);
        if (flagError != null)
        {
            ShowStatus(flagError, true);
            return;
        }

        s.FpsCap = fps;
        s.QualityLevel = Selected(QualityCombo, 0);
        s.TextureQuality = Selected(TextureCombo, -1);
        s.MsaaSamples = Selected(MsaaCombo, -1);
        s.Renderer = Selected(RendererCombo, "auto");
        s.DisableDpiScale = DpiToggle.IsChecked == true;
        s.ExclusiveFullscreen = FullscreenToggle.IsChecked == true;
        s.DisableGrass = GrassToggle.IsChecked == true;
        s.GraySky = SkyToggle.IsChecked == true;
        s.CustomFlags = CustomFlagsBox.Text.Trim();
        AppState.SaveSettings();

        var result = FlagService.ApplyAll(s);
        ShowStatus(result.Summary, !result.FpsOk);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            "Alle Performance-Einstellungen auf Standard zurücksetzen?\n(Das FPS-Limit in Roblox selbst bleibt beim zuletzt gesetzten Wert.)",
            "Orbit", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var defaults = new AppSettings();
        var s = AppState.Settings;
        s.FpsCap = defaults.FpsCap;
        s.QualityLevel = defaults.QualityLevel;
        s.TextureQuality = defaults.TextureQuality;
        s.MsaaSamples = defaults.MsaaSamples;
        s.Renderer = defaults.Renderer;
        s.DisableDpiScale = defaults.DisableDpiScale;
        s.ExclusiveFullscreen = defaults.ExclusiveFullscreen;
        s.DisableGrass = defaults.DisableGrass;
        s.GraySky = defaults.GraySky;
        s.CustomFlags = defaults.CustomFlags;
        AppState.SaveSettings();

        LoadFrom(s);
        var result = FlagService.ApplyAll(s);
        ShowStatus("Zurückgesetzt. " + result.Summary, false);
    }

    private void ShowStatus(string text, bool isWarning)
    {
        ApplyStatus.Text = text;
        ApplyStatus.Foreground = (System.Windows.Media.Brush)FindResource(isWarning ? "WarningBrush" : "SuccessBrush");
    }
}
