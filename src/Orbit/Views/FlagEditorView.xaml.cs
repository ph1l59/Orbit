using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orbit.Services;

namespace Orbit.Views;

public sealed class FlagRow
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public partial class FlagEditorView : UserControl, IPage
{
    private readonly ObservableCollection<FlagRow> _rows = new();

    public FlagEditorView()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => UpdateCount();
        Load();
    }

    public void OnShown() => Load();

    private void Load()
    {
        _rows.Clear();
        foreach (var (name, value) in AppState.Settings.UserFlags.OrderBy(kv => kv.Key))
            _rows.Add(new FlagRow { Name = name, Value = value });
        UpdateCount();
    }

    private void UpdateCount()
    {
        CountText.Text = _rows.Count == 1 ? "1 FLAG" : $"{_rows.Count} FLAGS";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => _rows.Add(new FlagRow());

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FlagRow row) _rows.Remove(row);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var json = Dialog.Ask(Window.GetWindow(this), "Import JSON",
            "Paste a JSON object, e.g. { \"FIntDebugForceMSAASamples\": \"4\" }. Existing flags with the same name are replaced.");
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            foreach (var (name, value) in FlagService.ParseJson(json))
            {
                var existing = _rows.FirstOrDefault(r => r.Name == name);
                if (existing != null) _rows.Remove(existing);
                _rows.Add(new FlagRow { Name = name, Value = value });
            }
            SetStatus("Imported – don't forget to save.", false);
        }
        catch (Exception ex)
        {
            SetStatus("That's not valid JSON: " + ex.Message, true);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dict = _rows.Where(r => r.Name.Trim().Length > 0).ToDictionary(r => r.Name.Trim(), r => r.Value);
        Clipboard.SetText(JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus("Copied to the clipboard.", false);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var flags = new Dictionary<string, string>();
        foreach (var row in _rows)
        {
            var name = row.Name.Trim();
            if (name.Length == 0) continue;
            if (name.Contains(' '))
            {
                SetStatus($"\"{name}\" contains a space – flag names can't have spaces.", true);
                return;
            }
            flags[name] = row.Value.Trim();
        }

        AppState.Settings.UserFlags = flags;
        AppState.SaveSettings();
        var written = FlagService.ApplyEverywhere(AppState.Settings);
        SetStatus(written > 0 ? $"Saved ✓ Applied to {written} Roblox install(s)." : "Saved ✓ Applied on the next launch.", false);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.Navigate("fastflags");

    private void SetStatus(string text, bool warn)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "OkBrush");
    }
}
