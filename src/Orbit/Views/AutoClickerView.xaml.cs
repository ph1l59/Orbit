using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Orbit.Services;

namespace Orbit.Views;

public partial class AutoClickerView : UserControl, IPage
{
    private bool _loading = true;
    private bool _countingDown;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public AutoClickerView()
    {
        InitializeComponent();

        ButtonCombo.ItemsSource = new List<Option> { new("Left", "left"), new("Right", "right"), new("Middle", "middle") };
        ModeCombo.ItemsSource = new List<Option> { new("Toggle", "toggle"), new("Hold", "hold") };
        HotkeyCombo.ItemsSource = ClickerHotkey.Keys.Select(k => new Option(k, k)).ToList();

        _timer.Tick += (_, _) => UpdateState();
        _timer.Start();
        AutoClicker.Instance.RunningChanged += _ => Dispatcher.InvokeAsync(UpdateState);

        Load();
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
        Option.Select(HotkeyCombo, s.ClickerHotkey);
        Option.Select(ModeCombo, s.ClickerMode);
        RobloxOnlyToggle.IsChecked = s.ClickerRobloxOnly;
        FixedToggle.IsChecked = s.ClickerFixedPosition;
        UpdatePositionText();
        UpdateHint();
        _loading = false;
    }

    private void Changed(object sender, EventArgs e)
    {
        if (_loading) return;
        var s = AppState.Settings;
        var hotkeyBefore = (s.ClickerHotkey, s.ClickerMode);

        s.ClickerCps = CpsSlider.IntValue;
        s.ClickerButton = Option.Get(ButtonCombo, "left");
        s.ClickerDouble = DoubleToggle.IsChecked == true;
        s.ClickerLimit = Math.Max(0, Numeric.Parse(LimitBox.Text, 0));
        s.ClickerHotkey = Option.Get(HotkeyCombo, "F6");
        s.ClickerMode = Option.Get(ModeCombo, "toggle");
        s.ClickerRobloxOnly = RobloxOnlyToggle.IsChecked == true;
        s.ClickerFixedPosition = FixedToggle.IsChecked == true;
        AppState.SaveSettings();

        if (hotkeyBefore != (s.ClickerHotkey, s.ClickerMode))
        {
            AutoClicker.Instance.Stop();
            HotkeyDesc.Text = ClickerHotkey.Apply()
                ? "Works everywhere, also while Roblox is focused."
                : $"{s.ClickerHotkey} is already used by another program – pick a different key.";
        }
        UpdateHint();

        // Settings take effect on the next start; restart if running so the change is visible right away.
        if (AutoClicker.Instance.IsRunning && s.ClickerMode == "toggle")
        {
            AutoClicker.Instance.Stop();
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(50);
                AutoClicker.Instance.Start(AppState.Settings);
            });
        }
    }

    private void UpdateHint()
    {
        var s = AppState.Settings;
        HotkeyHint.Text = s.ClickerMode == "hold"
            ? $"Hold {s.ClickerHotkey} to click"
            : $"Press {s.ClickerHotkey} to start / stop";
        StartButton.IsEnabled = s.ClickerMode != "hold";
    }

    private void UpdateState()
    {
        var running = AutoClicker.Instance.IsRunning;
        var clicks = AutoClicker.Instance.Clicks;
        StateText.Text = running ? "RUNNING" : "STOPPED";
        StateText.Foreground = (Brush)FindResource(running ? "OkBrush" : "FaintBrush");
        StateDot.Fill = (Brush)FindResource(running ? "OkBrush" : "FaintBrush");
        StateRing.Stroke = (Brush)FindResource(running ? "SignalBrush" : "LineStrongBrush");
        ClicksText.Text = clicks == 1 ? "1 click" : $"{clicks:N0} clicks";
        if (!_countingDown) StartText.Text = running ? "Stop" : "Start";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AutoClicker.Instance.IsRunning)
        {
            AutoClicker.Instance.Stop();
            return;
        }

        // Give the user a moment to switch to the window they want to click in.
        if (_countingDown) return;
        _countingDown = true;
        for (var i = 3; i > 0; i--)
        {
            StartText.Text = $"Starting in {i}…";
            await Task.Delay(1000);
        }
        _countingDown = false;
        AutoClicker.Instance.Start(AppState.Settings);
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        PickButton.IsEnabled = false;
        for (var i = 3; i > 0; i--)
        {
            PositionText.Text = $"Move your mouse to the spot… {i}";
            await Task.Delay(1000);
        }
        AutoClicker.Native.GetCursorPos(out var point);
        var s = AppState.Settings;
        s.ClickerX = point.X;
        s.ClickerY = point.Y;
        s.ClickerFixedPosition = true;
        AppState.SaveSettings();
        PickButton.IsEnabled = true;
        Load();
    }

    private void UpdatePositionText()
    {
        var s = AppState.Settings;
        PositionText.Text = s.ClickerX == 0 && s.ClickerY == 0
            ? "Always click at the same spot instead of where the mouse is."
            : $"Clicks at X {s.ClickerX}, Y {s.ClickerY} when turned on.";
    }
}
