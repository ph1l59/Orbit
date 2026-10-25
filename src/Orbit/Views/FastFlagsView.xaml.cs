using System.Windows;
using System.Windows.Controls;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

public partial class FastFlagsView : UserControl, IPage
{
    private bool _loading = true;

    public FastFlagsView()
    {
        InitializeComponent();

        MsaaCombo.ItemsSource = new List<Option>
        {
            new("Automatic", -1), new("Off (1x)", 1), new("2x", 2), new("4x", 4), new("8x", 8),
        };
        RendererCombo.ItemsSource = new List<Option>
        {
            new("Automatic", "auto"), new("Direct3D 11", "d3d11"), new("Vulkan", "vulkan"), new("OpenGL", "opengl"),
        };
        TextureCombo.ItemsSource = new List<Option>
        {
            new("Automatic", -1), new("Lowest", 0), new("Low", 1), new("Medium", 2), new("High", 3),
        };

        Load();
    }

    public void OnShown() => Load();

    private void Load()
    {
        _loading = true;
        var s = AppState.Settings;

        ManageToggle.IsChecked = s.ManageFastFlags;
        MeshToggle.IsChecked = s.MeshDetail >= 0;
        MeshSlider.Value = s.MeshDetail >= 0 ? s.MeshDetail : 2;
        Option.Select(MsaaCombo, s.MsaaSamples);
        DpiToggle.IsChecked = s.PreserveRenderingQuality;
        FrmToggle.IsChecked = s.FrmQuality > 0;
        FrmSlider.Value = s.FrmQuality > 0 ? s.FrmQuality : 5;
        Option.Select(RendererCombo, s.RenderingMode);
        Option.Select(TextureCombo, s.TextureQuality);
        FullscreenToggle.IsChecked = s.ExclusiveFullscreen;
        GrassToggle.IsChecked = s.DisableGrass;
        SkyToggle.IsChecked = s.GraySky;
        VoxelToggle.IsChecked = s.PauseVoxelizer;

        UpdateEnabled();
        _loading = false;
    }

    private void UpdateEnabled()
    {
        Presets.IsEnabled = ManageToggle.IsChecked == true;
        MeshPanel.Visibility = MeshToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FrmPanel.Visibility = FrmToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Changed(object sender, EventArgs e)
    {
        if (_loading) return;
        UpdateEnabled();

        var s = AppState.Settings;
        s.ManageFastFlags = ManageToggle.IsChecked == true;
        s.MeshDetail = MeshToggle.IsChecked == true ? MeshSlider.IntValue : -1;
        s.MsaaSamples = Option.Get(MsaaCombo, -1);
        s.PreserveRenderingQuality = DpiToggle.IsChecked == true;
        s.FrmQuality = FrmToggle.IsChecked == true ? FrmSlider.IntValue : 0;
        s.RenderingMode = Option.Get(RendererCombo, "auto");
        s.TextureQuality = Option.Get(TextureCombo, -1);
        s.ExclusiveFullscreen = FullscreenToggle.IsChecked == true;
        s.DisableGrass = GrassToggle.IsChecked == true;
        s.GraySky = SkyToggle.IsChecked == true;
        s.PauseVoxelizer = VoxelToggle.IsChecked == true;

        Save(s);
    }

    private void Save(AppSettings s)
    {
        AppState.SaveSettings();
        var count = FlagService.BuildFlags(s).Count;
        var written = FlagService.ApplyEverywhere(s);
        StatusText.Text = !s.ManageFastFlags
            ? "SAVED · ORBIT IS NOT MANAGING FASTFLAGS"
            : written > 0
                ? $"SAVED · {count} FLAG(S) APPLIED TO {written} ROBLOX INSTALL(S)"
                : $"SAVED · {count} FLAG(S) · APPLIED ON THE NEXT LAUNCH";
    }

    private void Editor_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.Navigate("flageditor");

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialog.Confirm(Window.GetWindow(this), "Reset FastFlags",
                "Turn every preset off and delete all of your own flags?", "Reset", danger: true))
            return;

        var s = AppState.Settings;
        var d = new AppSettings();
        s.MeshDetail = d.MeshDetail;
        s.MsaaSamples = d.MsaaSamples;
        s.PreserveRenderingQuality = d.PreserveRenderingQuality;
        s.FrmQuality = d.FrmQuality;
        s.RenderingMode = d.RenderingMode;
        s.TextureQuality = d.TextureQuality;
        s.ExclusiveFullscreen = d.ExclusiveFullscreen;
        s.DisableGrass = d.DisableGrass;
        s.GraySky = d.GraySky;
        s.PauseVoxelizer = d.PauseVoxelizer;
        s.UserFlags.Clear();
        s.ManageFastFlags = true;

        Load();
        Save(s);
    }
}
