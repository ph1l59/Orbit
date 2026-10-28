using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>One row in the DNS list.</summary>
public sealed class DnsRowItem
{
    public required DnsProvider Provider { get; init; }
    public int Rank { get; init; }
    public double? Ms { get; init; }
    public bool IsFastest { get; init; }
    public bool IsCurrent { get; init; }
    public string Name => Provider.Name;
    public string Servers => Provider.Servers;
    public string Time => Ms is { } ms ? ms.ToString("0", CultureInfo.InvariantCulture) + " ms" : "No answer";
    public Brush DotBrush => (Brush)Application.Current.FindResource(Ms switch
    {
        null => "DangerBrush",
        < 40 => "OkBrush",
        < 100 => "WarnBrush",
        _ => "DangerBrush",
    });
}

/// <summary>PC tweaks: RAM cleaner and DNS optimizer.</summary>
public partial class TweaksView : UserControl, IPage
{
    private readonly DispatcherTimer _ramTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _loadingSettings;
    private bool _cleaning;
    private bool _testing;
    private bool _testedOnce;

    public TweaksView()
    {
        InitializeComponent();

        _loadingSettings = true;
        var s = AppState.Settings;
        AutoToggle.IsChecked = s.RamAutoClean;
        RobloxToggle.IsChecked = s.RamIncludeRoblox;
        (s.RamAutoMode switch { "30" => Mode30, "60" => Mode60, _ => ModeHigh }).IsChecked = true;
        _loadingSettings = false;

        RefreshAllowList();
        _ramTimer.Tick += (_, _) => UpdateRam();
        RamCleaner.Cleaned += () => Dispatcher.InvokeAsync(UpdateRam);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && RamPage.Visibility == Visibility.Visible) { UpdateRam(); _ramTimer.Start(); }
            else _ramTimer.Stop();
        };
        UpdateRam();
    }

    public void OnShown()
    {
        UpdateRam();
        if (DnsPage.Visibility == Visibility.Visible && !_testedOnce) _ = TestAsync();
    }

    private Window Owner => Window.GetWindow(this)!;

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (RamPage == null || DnsPage == null) return; // during InitializeComponent
        var dns = DnsTab.IsChecked == true;
        RamPage.Visibility = dns ? Visibility.Collapsed : Visibility.Visible;
        DnsPage.Visibility = dns ? Visibility.Visible : Visibility.Collapsed;
        if (dns)
        {
            _ramTimer.Stop();
            if (!_testedOnce) _ = TestAsync();
        }
        else
        {
            UpdateRam();
            _ramTimer.Start();
        }
    }

    // =================== RAM cleaner ===================

    private static string Gb(double bytes) => (bytes / 1024 / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    private void UpdateRam()
    {
        var ram = RamCleaner.Read();
        if (ram.Total == 0) return;

        UsedText.Text = Gb(ram.Used);
        FreeText.Text = Gb(ram.Free);
        TotalText.Text = Gb(ram.Total) + " total";

        // Bar glides to the new width.
        var width = Math.Max(0, ((FrameworkElement)UsedBar.Parent).ActualWidth - 2) * ram.UsedPercent / 100;
        if (width > 0)
            UsedBar.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        var (text, brush) = ram.UsedPercent switch
        {
            >= RamCleaner.NeedsCleaningPercent => ("Needs cleaning", "WarnBrush"),
            >= RamCleaner.HighPercent => ($"{ram.UsedPercent:0} % used", "SignalLightBrush"),
            _ => ("Healthy", "OkBrush"),
        };
        var color = (Brush)FindResource(brush);
        StatusText.Text = text;
        StatusText.Foreground = color;
        StatusDot.Fill = color;
        StatusPill.BorderBrush = color;
        StatusPill.Background = new SolidColorBrush(((SolidColorBrush)color).Color) { Opacity = 0.1 };

        var last = AppState.Settings.RamLastClean;
        LastCleanText.Text = last is { } l ? "LAST CLEANUP " + l.ToString("dd.MM.yy · HH:mm", CultureInfo.InvariantCulture) : "NOT CLEANED YET";
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_cleaning) return;
        _cleaning = true;
        CleanButton.IsEnabled = false;
        CleanText.Text = "Cleaning…";
        try
        {
            var result = await RamCleaner.CleanAsync();
            ResultText.Text = result.FreedBytes > 50L * 1024 * 1024
                ? $"✓ {Gb(result.FreedBytes)} freed from {result.Programs} apps."
                : $"✓ Done – {result.Programs} apps cleaned. Not much to free right now, your RAM is in good shape.";
            ResultText.Foreground = (Brush)FindResource("OkBrush");
            ResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ResultText.Text = "Cleaning failed: " + ex.Message;
            ResultText.Foreground = (Brush)FindResource("WarnBrush");
            ResultText.Visibility = Visibility.Visible;
        }
        finally
        {
            _cleaning = false;
            CleanButton.IsEnabled = true;
            CleanText.Text = "Clean";
            UpdateRam();
        }
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.RamAutoClean = AutoToggle.IsChecked == true;
        AppState.SaveSettings();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || sender is not RadioButton { Tag: string mode }) return;
        AppState.Settings.RamAutoMode = mode;
        AppState.SaveSettings();
    }

    private void Roblox_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.RamIncludeRoblox = RobloxToggle.IsChecked == true;
        AppState.SaveSettings();
    }

    private void RefreshAllowList()
    {
        var list = AppState.Settings.RamAllowList;
        AllowList.ItemsSource = list.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        AllowEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an app that should never be cleaned",
            Filter = "Programs (*.exe)|*.exe",
        };
        if (dialog.ShowDialog(Owner) != true) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        if (!AppState.Settings.RamAllowList.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            AppState.Settings.RamAllowList.Add(name);
            AppState.SaveSettings();
        }
        RefreshAllowList();
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        AppState.Settings.RamAllowList.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        AppState.SaveSettings();
        RefreshAllowList();
    }

    // =================== DNS optimizer ===================

    private void Test_Click(object sender, RoutedEventArgs e) => _ = TestAsync();

    private async Task TestAsync()
    {
        if (_testing) return;
        _testing = true;
        _testedOnce = true;
        TestButton.IsEnabled = false;
        TestText.Text = "Testing…";
        DnsList.Visibility = Visibility.Collapsed;
        DnsLoading.Visibility = Visibility.Visible;
        SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });

        try
        {
            var (current, results) = await DnsOptimizer.TestAllAsync();
            var inUse = DnsOptimizer.CurrentProvider();

            CurrentName.Text = inUse?.Name ?? "Windows";
            CurrentServers.Text = current.Provider.Servers;
            CurrentTime.Text = current.AverageMs is { } ms ? ms.ToString("0", CultureInfo.InvariantCulture) + " ms" : "–";
            CurrentDot.Fill = (Brush)FindResource(current.AverageMs switch { null => "FaintBrush", < 40 => "OkBrush", < 100 => "WarnBrush", _ => "DangerBrush" });

            var fastest = results.FirstOrDefault(r => r.AverageMs != null);
            DnsList.ItemsSource = results.Select((r, i) => new DnsRowItem
            {
                Provider = r.Provider,
                Rank = i + 1,
                Ms = r.AverageMs,
                IsFastest = r == fastest && r.Provider != inUse,
                IsCurrent = r.Provider == inUse,
            }).ToList();
        }
        catch (Exception ex)
        {
            ShowDnsStatus("Testing failed: " + ex.Message, true);
        }
        finally
        {
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            DnsLoading.Visibility = Visibility.Collapsed;
            DnsList.Visibility = Visibility.Visible;
            TestButton.IsEnabled = true;
            TestText.Text = "Test again";
            _testing = false;
        }
    }

    private async void DnsChoose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DnsRowItem row) return;
        ShowDnsStatus($"Switching to {row.Name}… Windows will ask for permission.", false);
        var error = await DnsOptimizer.ApplyAsync(row.Provider);
        if (error != null) { ShowDnsStatus(error, true); return; }
        ShowDnsStatus($"✓ Now using {row.Name} ({row.Servers}).", false, ok: true);
        await TestAsync();
    }

    private async void ResetDns_Click(object sender, RoutedEventArgs e)
    {
        ShowDnsStatus("Switching back to automatic DNS… Windows will ask for permission.", false);
        var error = await DnsOptimizer.ResetAsync();
        if (error != null) { ShowDnsStatus(error, true); return; }
        ShowDnsStatus("✓ Back to the DNS from your router.", false, ok: true);
        await TestAsync();
    }

    private void ShowDnsStatus(string text, bool warning, bool ok = false)
    {
        DnsStatus.Text = text;
        DnsStatus.Foreground = (Brush)FindResource(warning ? "WarnBrush" : ok ? "OkBrush" : "DimBrush");
        DnsStatus.Visibility = Visibility.Visible;
    }
}
