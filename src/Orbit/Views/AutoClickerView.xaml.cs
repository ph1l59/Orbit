using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Orbit.Services;

namespace Orbit.Views;

public partial class AutoClickerView : UserControl, IPage
{
    private bool _loading = true;
    private bool _capturing;
    private bool _countingDown;

    public AutoClickerView()
    {
        InitializeComponent();

        ButtonCombo.ItemsSource = new List<Option> { new("Left", "left"), new("Right", "right"), new("Middle", "middle") };
        ModeCombo.ItemsSource = new List<Option> { new("Toggle", "toggle"), new("Hold", "hold") };
        TargetCombo.ItemsSource = new List<Option>
        {
            new("Where the mouse is", "cursor"),
            new("Fixed position on screen", "fixed"),
            new("Roblox in the background (experimental)", "background"),
        };

        AutoClicker.Instance.RunningChanged += _ => Dispatcher.InvokeAsync(UpdateState);
        Load();
        UpdateState();
    }

    public void OnShown() => Load();

    private void Load()
    {
        _loading = true;
        var s = AppState.Settings;
        CpsSlider.Value = Math.Clamp(s.ClickerCps, 1, 1000);
        Option.Select(ButtonCombo, s.ClickerButton);
        DoubleToggle.IsChecked = s.ClickerDouble;
        LimitBox.Text = s.ClickerLimit.ToString();
        Option.Select(ModeCombo, s.ClickerMode);
        DelaySlider.Value = Math.Clamp(s.ClickerStartDelay, 0, 10);
        Option.Select(TargetCombo, s.ClickerTarget);
        RobloxOnlyToggle.IsChecked = s.ClickerRobloxOnly;
        UpdateHotkeyText();
        UpdateTargetUi();
        _loading = false;
    }

    private void Changed(object sender, EventArgs e)
    {
        if (_loading) return;
        var s = AppState.Settings;
        s.ClickerCps = CpsSlider.IntValue;
        s.ClickerButton = Option.Get(ButtonCombo, "left");
        s.ClickerDouble = DoubleToggle.IsChecked == true;
        s.ClickerLimit = Math.Max(0, Numeric.Parse(LimitBox.Text, 0));
        s.ClickerMode = Option.Get(ModeCombo, "toggle");
        s.ClickerStartDelay = DelaySlider.IntValue;
        s.ClickerTarget = Option.Get(TargetCombo, "cursor");
        s.ClickerRobloxOnly = RobloxOnlyToggle.IsChecked == true;
        AppState.SaveSettings();

        UpdateHotkeyText();
        UpdateTargetUi();

        // Restart so changed settings are used right away.
        if (AutoClicker.Instance.IsRunning && s.ClickerMode == "toggle")
        {
            AutoClicker.Instance.Stop();
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(60);
                AutoClicker.Instance.Start(AppState.Settings);
            });
        }
    }

    // ---------------- State ----------------

    private void UpdateState()
    {
        var running = AutoClicker.Instance.IsRunning;
        StateText.Text = running ? "Active" : "Inactive";
        StateText.Foreground = (Brush)FindResource(running ? "OkBrush" : "TextBrush");
        StateDot.Fill = (Brush)FindResource(running ? "OkBrush" : "FaintBrush");
        StateRing.Stroke = (Brush)FindResource(running ? "OkBrush" : "LineStrongBrush");
        StateGlow.Opacity = running ? 0.15 : 0;
        if (!_countingDown) StartText.Text = running ? "Stop" : "Start";
    }

    private void UpdateHotkeyText()
    {
        var s = AppState.Settings;
        var key = ClickerHotkey.Describe(s.ClickerHotkeyVk, s.ClickerHotkeyMods);
        if (!_capturing) HotkeyButton.Content = key;
        HotkeyHint.Text = s.ClickerMode == "hold" ? $"Hold {key} to click" : $"Press {key} to start / stop";
        StartButton.IsEnabled = s.ClickerMode != "hold";
    }

    private void UpdateTargetUi()
    {
        var s = AppState.Settings;
        TargetDesc.Text = s.ClickerTarget switch
        {
            "fixed" => "Always clicks the same spot on your screen. The mouse jumps there for every click.",
            "background" => "Sends clicks straight into every open Roblox window – also when it's minimized or another window is in front. " +
                            "Doesn't move your mouse. Experimental: some games ignore clicks they get this way.",
            _ => "Clicks wherever your mouse currently is.",
        };
        PositionPanel.Visibility = s.ClickerTarget == "cursor" ? Visibility.Collapsed : Visibility.Visible;
        RobloxOnlyRow.Visibility = s.ClickerTarget == "background" ? Visibility.Collapsed : Visibility.Visible;

        var hasPos = s.ClickerX != 0 || s.ClickerY != 0;
        PositionText.Text = hasPos
            ? $"POSITION · X {s.ClickerX} · Y {s.ClickerY}"
            : s.ClickerTarget == "background" ? "NO POSITION · CLICKS THE CENTER OF THE ROBLOX WINDOW" : "NO POSITION PICKED YET";
        ClearPosButton.IsEnabled = hasPos;
    }

    // ---------------- Start / stop ----------------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AutoClicker.Instance.IsRunning)
        {
            AutoClicker.Instance.Stop();
            return;
        }
        if (_countingDown) return;

        _countingDown = true;
        for (var i = AppState.Settings.ClickerStartDelay; i > 0; i--)
        {
            StartText.Text = $"Starting in {i}…";
            await Task.Delay(1000);
        }
        _countingDown = false;
        AutoClicker.Instance.Start(AppState.Settings);
        UpdateState();
    }

    // ---------------- Hotkey capture ----------------

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            EndCapture();
            return;
        }
        AutoClicker.Instance.Stop();
        _capturing = true;
        ClickerHotkey.Suspended = true;
        HotkeyButton.Content = "Press a key…";
        HotkeyButton.Focus();
    }

    private void EndCapture()
    {
        _capturing = false;
        ClickerHotkey.Suspended = false;
        UpdateHotkeyText();
    }

    private void Assign(int vk, int mods)
    {
        var s = AppState.Settings;
        s.ClickerHotkeyVk = vk;
        s.ClickerHotkeyMods = mods;
        AppState.SaveSettings();
        EndCapture();
        HotkeyDesc.Text = $"Hotkey set to {ClickerHotkey.Describe(vk, mods)}. Click the button to change it again.";
    }

    private void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            EndCapture();
            return;
        }

        // Wait for the actual key when only a modifier is pressed.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin) return;

        var mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= ClickerHotkey.ModCtrl;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= ClickerHotkey.ModAlt;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= ClickerHotkey.ModShift;

        Assign(KeyInterop.VirtualKeyFromKey(key), mods);
    }

    private void Hotkey_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing) return;
        var vk = e.ChangedButton switch
        {
            MouseButton.XButton1 => 0x05,
            MouseButton.XButton2 => 0x06,
            MouseButton.Middle => 0x04,
            _ => 0,
        };
        if (vk == 0) return; // left/right click: let the button handle it (cancels)
        e.Handled = true;
        Assign(vk, 0);
    }

    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturing) EndCapture();
    }

    // ---------------- Position ----------------

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        PickButton.IsEnabled = false;
        for (var i = 3; i > 0; i--)
        {
            PositionText.Text = $"MOVE YOUR MOUSE TO THE SPOT… {i}";
            await Task.Delay(1000);
        }
        AutoClicker.Native.GetCursorPos(out var point);
        var s = AppState.Settings;
        s.ClickerX = point.X;
        s.ClickerY = point.Y;
        AppState.SaveSettings();
        PickButton.IsEnabled = true;
        UpdateTargetUi();
    }

    private void ClearPos_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        s.ClickerX = 0;
        s.ClickerY = 0;
        AppState.SaveSettings();
        UpdateTargetUi();
    }
}
