using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Orbit.Services;

namespace Orbit.Views;

public partial class ModsView : UserControl, IPage
{
    private bool _loading = true;

    public ModsView()
    {
        InitializeComponent();
        Load();
    }

    public void OnShown() => Load();

    private void Load()
    {
        _loading = true;
        var s = AppState.Settings;
        DpiToggle.IsChecked = s.CompatHighDpi;
        FsoToggle.IsChecked = s.CompatDisableFullscreenOptimizations;
        CursorToggle.IsChecked = s.CustomCursorEnabled && File.Exists(ModService.CustomCursorFile);
        CursorToggle.IsEnabled = File.Exists(ModService.CustomCursorFile);
        RemoveFontButton.IsEnabled = s.CustomFontEnabled;
        FontText.Text = s.CustomFontEnabled
            ? "A custom font is applied. Choose another one or remove it."
            : "Replace every font in Roblox with a .ttf or .otf file of your choice.";
        _loading = false;
    }

    private Window? Owner => Window.GetWindow(this);

    private void Changed(object sender, EventArgs e)
    {
        if (_loading) return;
        var s = AppState.Settings;
        s.CompatHighDpi = DpiToggle.IsChecked == true;
        s.CompatDisableFullscreenOptimizations = FsoToggle.IsChecked == true;
        s.CustomCursorEnabled = CursorToggle.IsChecked == true;
        AppState.SaveSettings();
        SetStatus("Saved – applied the next time Roblox launches through Orbit.", false);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.ModsDir}\"") { UseShellExecute = true });

    private void Help_Click(object sender, RoutedEventArgs e)
        => Dialog.Info(Owner, "How mods work",
            "Everything inside the Mods folder is copied over Roblox's own files every time Orbit launches it. " +
            "The folder mirrors Roblox's installation, for example:\n\n" +
            "content\\sounds\\oof.ogg  →  replaces the death sound\n" +
            "content\\textures\\…  →  replaces textures\n\n" +
            "Orbit keeps a backup of every file it replaces. Delete a file from the Mods folder and the original comes back on the next launch.");

    private void Compat_Click(object sender, RoutedEventArgs e)
        => CompatPanel.Visibility = CompatPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ChooseCursor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG image|*.png", Title = "Choose a cursor image" };
        if (dialog.ShowDialog(Owner) != true) return;

        File.Copy(dialog.FileName, ModService.CustomCursorFile, true);
        AppState.Settings.CustomCursorEnabled = true;
        AppState.SaveSettings();
        Load();
        SetStatus("Cursor set – applied on the next launch.", false);
    }

    private void ChooseFont_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Font files|*.ttf;*.otf", Title = "Choose a font" };
        if (dialog.ShowDialog(Owner) != true) return;

        File.Copy(dialog.FileName, ModService.CustomFontFile, true);
        AppState.Settings.CustomFontEnabled = true;
        AppState.SaveSettings();
        Load();
        SetStatus($"Font \"{Path.GetFileNameWithoutExtension(dialog.FileName)}\" set – applied on the next launch.", false);
    }

    private void RemoveFont_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.CustomFontEnabled = false;
        AppState.SaveSettings();
        try { File.Delete(ModService.CustomFontFile); } catch { /* ignore */ }
        Load();
        SetStatus("Custom font removed – the original fonts come back on the next launch.", false);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        var dir = s.UseManagedInstall
            ? Bootstrapper.InstalledVersionDir
            : RobloxPaths.FindOfficialPlayerExe() is { } exe ? Path.GetDirectoryName(exe) : null;

        if (dir == null)
        {
            SetStatus("Roblox isn't installed yet – mods will be applied on the first launch.", true);
            return;
        }

        try
        {
            var count = await Task.Run(() => ModService.Apply(s, dir));
            SetStatus($"Applied ✓ {count} modded file(s).", false);
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't apply mods: " + ex.Message, true);
        }
    }

    private void SetStatus(string text, bool warn)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "OkBrush");
    }
}
