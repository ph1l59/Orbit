using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orbit.Services;

namespace Orbit.Views;

public partial class GlobalSettingsView : UserControl, IPage
{
    public GlobalSettingsView()
    {
        InitializeComponent();
        FontCombo.ItemsSource = new List<Option>
        {
            new("Default", 1),
            new("Large", 2),
            new("Larger", 3),
            new("Largest", 4),
        };
        Load();
    }

    public void OnShown() => Load();

    private void Load()
    {
        RobloxGlobalSettings? data = null;
        try { data = GlobalSettingsService.Load(); }
        catch (Exception ex) { SetStatus("Couldn't read Roblox's settings: " + ex.Message, true); }

        var found = data != null;
        MissingBanner.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
        Form.IsEnabled = found;
        SaveButton.IsEnabled = found;
        if (data == null) return;

        ReadOnlyToggle.IsChecked = GlobalSettingsService.IsReadOnly;
        QualitySlider.Value = data.GraphicsQuality;
        FpsBox.Text = data.FramerateCap.ToString();
        TransparencySlider.Value = Math.Round(data.Transparency, 1);
        MotionToggle.IsChecked = data.ReducedMotion;
        Option.Select(FontCombo, data.FontSize);
        SensitivitySlider.Value = Math.Round(data.MouseSensitivity, 2);
        VrToggle.IsChecked = data.VrEnabled;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var fps = Numeric.Parse(FpsBox.Text, -1);
        if (fps < 1 || fps > 10000)
        {
            SetStatus("Enter a framerate limit between 1 and 10000.", true);
            return;
        }

        try
        {
            GlobalSettingsService.Save(new RobloxGlobalSettings
            {
                GraphicsQuality = QualitySlider.IntValue,
                FramerateCap = fps,
                Transparency = TransparencySlider.Value,
                ReducedMotion = MotionToggle.IsChecked == true,
                FontSize = Option.Get(FontCombo, 1),
                MouseSensitivity = SensitivitySlider.Value,
                VrEnabled = VrToggle.IsChecked == true,
            });
            GlobalSettingsService.IsReadOnly = ReadOnlyToggle.IsChecked == true;

            SetStatus(RobloxPaths.IsRobloxRunning()
                ? "Saved ✓ Roblox is running – restart it to see the changes (and close it before turning read-only off)."
                : "Saved ✓ Applies the next time Roblox starts.", false);
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't save: " + ex.Message, true);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        Load();
        SetStatus("Reloaded from Roblox.", false);
    }

    private void SetStatus(string text, bool warn)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "OkBrush");
    }
}
