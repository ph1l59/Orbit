using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>One preset in the list (name + "3 accounts · Blox Fruits").</summary>
public sealed record PresetItem(LaunchPreset Preset, string Name, string Detail);

/// <summary>Accounts page, right column: launch presets and the Roblox windows panel.</summary>
public partial class AccountsView
{
    private readonly DispatcherTimer _windowsTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitTools()
    {
        RefreshPresets();

        SaverToggle.IsChecked = AppState.Settings.ResourceSaver;
        RobloxWindows.SaverChanged += () => Dispatcher.InvokeAsync(UpdateWindowsPanel);
        _windowsTimer.Tick += (_, _) => UpdateWindowsPanel();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateWindowsPanel(); _windowsTimer.Start(); }
            else _windowsTimer.Stop();
        };
        UpdateWindowsPanel();
    }

    // ---------------- Presets ----------------

    private void RefreshPresets()
    {
        var presets = AppState.Settings.Presets;
        PresetList.ItemsSource = presets.Select(p => new PresetItem(p, p.Name, Describe(p))).ToList();
        NoPresets.Visibility = presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PresetList.Visibility = presets.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Describe(LaunchPreset p)
    {
        var found = p.AccountIds.Count(id => AppState.Accounts.Any(a => a.UserId == id));
        var accounts = found == 1 ? "1 ACCOUNT" : $"{found} ACCOUNTS";
        var where = p.Follow ? "FOLLOW @" + p.FollowUser
            : p.GameName.Length > 0 ? p.GameName
            : p.Place;
        if (!p.Follow && p.Server.Length > 0) where += " · SERVER";
        return $"{accounts}  ·  {where}".ToUpperInvariant();
    }

    private static LaunchPreset? PresetFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext is PresetItem item ? item.Preset : null;

    private void PresetSave_Click(object sender, RoutedEventArgs e)
    {
        var selected = AppState.Accounts.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ShowStatus("Select the accounts for the preset on the left first.", true);
            return;
        }

        var follow = FollowMode.IsChecked == true;
        if (follow && FollowBox.Text.Trim().Length == 0)
        {
            ShowStatus("Enter the username of the player to follow first.", true);
            return;
        }
        if (!follow && JoinTarget.Parse(PlaceBox.Text, ServerBox.Text, out var error) == null)
        {
            ShowStatus(error, true);
            return;
        }

        var suggestion = follow ? "Follow " + FollowBox.Text.Trim()
            : _previewGameName.Length > 0 ? _previewGameName
            : "Preset " + (AppState.Settings.Presets.Count + 1);
        var name = Dialog.Ask(Owner, "Save preset",
            $"Give the preset a name. It starts {selected.Count} account{(selected.Count == 1 ? "" : "s")} " +
            (follow ? $"and follows {FollowBox.Text.Trim()}." : "in this game."), suggestion);
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0) name = suggestion;

        var existing = AppState.Settings.Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !Dialog.Confirm(Owner, "Replace preset", $"There's already a preset called \"{existing.Name}\". Replace it?", "Replace"))
            return;

        var preset = existing ?? new LaunchPreset();
        preset.Name = name;
        preset.AccountIds = selected.Select(a => a.UserId).ToList();
        preset.Follow = follow;
        preset.Place = follow ? "" : PlaceBox.Text.Trim();
        preset.Server = follow ? "" : ServerBox.Text.Trim();
        preset.FollowUser = follow ? FollowBox.Text.Trim() : "";
        preset.GameName = follow ? "" : _previewGameName;
        if (existing == null) AppState.Settings.Presets.Add(preset);
        AppState.SaveSettings();
        RefreshPresets();
        ShowStatus($"Preset \"{name}\" saved ✓", false);
    }

    /// <summary>Selects the preset's accounts and fills in the game (or the player to follow).</summary>
    private int ApplyPreset(LaunchPreset preset)
    {
        var count = 0;
        foreach (var account in AppState.Accounts)
        {
            account.IsSelected = preset.AccountIds.Contains(account.UserId);
            if (account.IsSelected) count++;
        }

        if (preset.Follow)
        {
            FollowMode.IsChecked = true;
            FollowBox.Text = preset.FollowUser;
        }
        else
        {
            GameMode.IsChecked = true;
            PlaceBox.Text = preset.Place;
            ServerBox.Text = preset.Server;
        }
        return count;
    }

    private void Preset_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_launching || PresetFrom(sender) is not { } preset) return;
        var count = ApplyPreset(preset);
        ShowStatus(count == 0
            ? "None of this preset's accounts are in Orbit anymore."
            : $"Loaded \"{preset.Name}\" – click Launch to start.", count == 0);
    }

    private void PresetLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_launching || PresetFrom(sender) is not { } preset) return;
        if (ApplyPreset(preset) == 0)
        {
            ShowStatus("None of this preset's accounts are in Orbit anymore.", true);
            return;
        }
        Launch_Click(this, new RoutedEventArgs());
    }

    private void PresetRename_Click(object sender, RoutedEventArgs e)
    {
        if (PresetFrom(sender) is not { } preset) return;
        var name = Dialog.Ask(Owner, "Rename preset", "New name for the preset.", preset.Name)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        preset.Name = name;
        AppState.SaveSettings();
        RefreshPresets();
    }

    private void PresetDelete_Click(object sender, RoutedEventArgs e)
    {
        if (PresetFrom(sender) is not { } preset) return;
        if (!Dialog.Confirm(Owner, "Delete preset", $"Delete the preset \"{preset.Name}\"? Your accounts stay as they are.", "Delete", danger: true))
            return;
        AppState.Settings.Presets.Remove(preset);
        AppState.SaveSettings();
        RefreshPresets();
    }

    // ---------------- Roblox windows ----------------

    private int _screenCount = -1;

    private void UpdateWindowsPanel()
    {
        var count = RobloxWindows.Count;
        WindowCountText.Text = count switch
        {
            0 => "No Roblox window open",
            1 => "1 Roblox window open",
            _ => $"{count} Roblox windows open",
        };
        ArrangeButton.IsEnabled = count > 0;
        RestoreButton.IsEnabled = RobloxWindows.CanRestore;

        // Screen choice only if there's more than one.
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length != _screenCount)
        {
            _screenCount = screens.Length;
            var previous = ScreenBox.SelectedIndex;
            ScreenBox.Items.Clear();
            for (var i = 0; i < screens.Length; i++)
                ScreenBox.Items.Add($"Screen {i + 1}  ·  {screens[i].Bounds.Width}×{screens[i].Bounds.Height}{(screens[i].Primary ? "  (main)" : "")}");
            var primary = Math.Max(0, Array.FindIndex(screens, s => s.Primary));
            ScreenBox.SelectedIndex = previous >= 0 && previous < screens.Length ? previous : primary;
            ScreenBox.Visibility = screens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        var throttled = RobloxWindows.ThrottledCount;
        SaverText.Text = AppState.Settings.ResourceSaver && throttled > 0
            ? $"On · {throttled} window{(throttled == 1 ? "" : "s")} in the background get less CPU. The one you're playing in stays fast."
            : "Roblox windows in the background get less CPU. The one you're playing in stays fast.";
    }

    private void Arrange_Click(object sender, RoutedEventArgs e)
    {
        var count = RobloxWindows.Arrange(ScreenBox.SelectedIndex);
        ShowWindowsStatus(count == 0 ? "No Roblox window is open." : $"{count} window{(count == 1 ? "" : "s")} arranged ✓");
        UpdateWindowsPanel();
    }

    private void RestoreWindows_Click(object sender, RoutedEventArgs e)
    {
        var count = RobloxWindows.Restore();
        ShowWindowsStatus(count == 0 ? "The windows are already closed." : $"{count} window{(count == 1 ? "" : "s")} put back ✓");
        UpdateWindowsPanel();
    }

    private void ShowWindowsStatus(string text)
    {
        WindowsStatus.Text = text;
        WindowsStatus.Visibility = Visibility.Visible;
    }

    private void Saver_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.ResourceSaver = SaverToggle.IsChecked == true;
        AppState.SaveSettings();
        RobloxWindows.RefreshSaver(Dispatcher);
        UpdateWindowsPanel();
    }
}
