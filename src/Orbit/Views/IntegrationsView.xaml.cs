using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

public partial class IntegrationsView : UserControl, IPage
{
    private bool _loading = true;
    private readonly ObservableCollection<CustomIntegration> _custom = new();

    public IntegrationsView()
    {
        InitializeComponent();
        StatusCombo.ItemsSource = new List<Option>
        {
            new("Name (Playing Roblox)", "name"),
            new("Game (Playing <game>)", "details"),
            new("Creator", "state"),
        };
        CustomList.ItemsSource = _custom;
        Load();
    }

    public void OnShown() => Load();

    private void Load()
    {
        _loading = true;
        var s = AppState.Settings;
        TrackingToggle.IsChecked = s.ActivityTracking;
        LocationToggle.IsChecked = s.ShowServerLocation;
        DiscordToggle.IsChecked = s.DiscordRichPresence;
        AppIdBox.Text = s.DiscordAppId;
        Option.Select(StatusCombo, s.DiscordStatusDisplay);
        JoinToggle.IsChecked = s.DiscordAllowJoin;
        AccountToggle.IsChecked = s.DiscordShowAccount;

        _custom.Clear();
        foreach (var c in s.CustomIntegrations) _custom.Add(c);

        UpdateEnabled();
        _loading = false;
    }

    private void UpdateEnabled()
    {
        var tracking = TrackingToggle.IsChecked == true;
        LocationToggle.IsEnabled = tracking;
        DiscordToggle.IsEnabled = tracking;
        var discord = tracking && DiscordToggle.IsChecked == true;
        AppIdBox.IsEnabled = discord;
        StatusCombo.IsEnabled = discord;
        JoinToggle.IsEnabled = discord;
        AccountToggle.IsEnabled = discord;
    }

    private void Changed(object sender, EventArgs e)
    {
        if (_loading) return;
        UpdateEnabled();

        var s = AppState.Settings;
        s.ActivityTracking = TrackingToggle.IsChecked == true;
        s.ShowServerLocation = LocationToggle.IsChecked == true;
        s.DiscordRichPresence = DiscordToggle.IsChecked == true;
        s.DiscordStatusDisplay = Option.Get(StatusCombo, "name");
        s.DiscordAllowJoin = JoinToggle.IsChecked == true;
        s.DiscordShowAccount = AccountToggle.IsChecked == true;
        AppState.SaveSettings();
        Integrations.Refresh();

    }

    private void AppId_LostFocus(object sender, EventArgs e)
    {
        var id = new string(AppIdBox.Text.Where(char.IsDigit).ToArray());
        AppIdBox.Text = id;
        if (AppState.Settings.DiscordAppId == id) return;
        AppState.Settings.DiscordAppId = id;
        AppState.SaveSettings();
        Integrations.Refresh();
    }

    private void DevPortal_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true });

    // ---------------- Custom integrations ----------------

    private void SaveCustom()
    {
        AppState.Settings.CustomIntegrations = _custom.ToList();
        AppState.SaveSettings();
    }

    private void CustomChanged(object sender, EventArgs e)
    {
        if (!_loading) SaveCustom();
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*", Title = "Choose a program" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        _custom.Add(new CustomIntegration
        {
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            Path = dialog.FileName,
            CloseWithRoblox = true,
        });
        SaveCustom();
    }

    private void RemoveCustom_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CustomIntegration item) return;
        _custom.Remove(item);
        SaveCustom();
    }
}
