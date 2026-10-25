using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>
/// Lets the user enter a birthday and gender once and a list of Username:Password lines.
/// The accounts themselves are created one after another on Roblox's real sign-up page
/// (see <see cref="LoginWindow"/>), where the user clicks Sign Up and solves any check.
/// </summary>
public partial class CreateAccountsWindow : Window
{
    public List<SignupEntry> Entries { get; } = new();
    public SignupOptions? Options { get; private set; }

    private sealed class LineRow
    {
        public string Name { get; init; } = "";
        public string Message { get; init; } = "";
        public string Icon { get; init; } = "";
        public Brush Brush { get; init; } = Brushes.Gray;
    }

    private sealed record ParsedLine(int LineNo, string Raw, SignupEntry? Entry, string? Error);

    /// <summary>Results of "Check names", by lower-case username.</summary>
    private readonly Dictionary<string, (bool Valid, string Message)> _remote = new();
    private DateTime? _remoteBirthday;
    private bool _checking;

    private static readonly Regex UsernameChars = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    public CreateAccountsWindow()
    {
        InitializeComponent();

        for (var d = 1; d <= 31; d++) DayBox.Items.Add(d.ToString());
        var months = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12);
        foreach (var m in months) MonthBox.Items.Add(m);
        for (var y = DateTime.Today.Year; y >= 1900; y--) YearBox.Items.Add(y.ToString());

        var s = AppState.Settings;
        if (s.SignupBirthday is { } b)
        {
            DayBox.SelectedIndex = b.Day - 1;
            MonthBox.SelectedIndex = b.Month - 1;
            YearBox.SelectedItem = b.Year.ToString();
        }

        (s.SignupGender switch
        {
            "male" => GenderMale,
            "female" => GenderFemale,
            _ => GenderNone,
        }).IsChecked = true;

        Loaded += (_, _) => AccountsBox.Focus();
        Refresh();
    }

    // ---------- Input ----------

    private DateTime? Birthday
    {
        get
        {
            if (DayBox.SelectedIndex < 0 || MonthBox.SelectedIndex < 0 || YearBox.SelectedItem is not string y) return null;
            var year = int.Parse(y);
            var month = MonthBox.SelectedIndex + 1;
            var day = DayBox.SelectedIndex + 1;
            if (day > DateTime.DaysInMonth(year, month)) return null;
            return new DateTime(year, month, day);
        }
    }

    private string Gender =>
        GenderMale.IsChecked == true ? "male" :
        GenderFemale.IsChecked == true ? "female" : "none";

    private List<ParsedLine> Parse()
    {
        var result = new List<ParsedLine>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = AccountsBox.Text.Replace("\r", "").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0 || raw.StartsWith('#')) continue;

            var colon = raw.IndexOf(':');
            if (colon < 0)
            {
                result.Add(new(i + 1, raw, null, "Missing \":\" – use Username:Password"));
                continue;
            }

            var user = raw[..colon].Trim();
            var pass = raw[(colon + 1)..]; // passwords may contain ':' themselves
            var entry = new SignupEntry(user, pass);

            var error = CheckUsername(user) ?? CheckPassword(user, pass);
            if (error == null && !seen.Add(user)) error = "Listed twice";
            if (error == null && AppState.Accounts.Any(a => a.Username.Equals(user, StringComparison.OrdinalIgnoreCase)))
                error = "Already in Orbit";

            result.Add(new(i + 1, raw, error == null ? entry : null, error));
        }
        return result;
    }

    private static string? CheckUsername(string name)
    {
        if (name.Length is < 3 or > 20) return "Username needs 3–20 characters";
        if (!UsernameChars.IsMatch(name)) return "Only letters, numbers and _ allowed";
        if (name.StartsWith('_') || name.EndsWith('_')) return "Can't start or end with _";
        if (name.Count(c => c == '_') > 1) return "Only one _ allowed";
        return null;
    }

    private static string? CheckPassword(string user, string pass)
    {
        if (pass.Length < 8) return "Password needs at least 8 characters";
        if (pass.Length > 200) return "Password is too long";
        if (pass.Contains(user, StringComparison.OrdinalIgnoreCase)) return "Password can't contain the username";
        return null;
    }

    // ---------- UI ----------

    private void List_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Details_Changed(object sender, SelectionChangedEventArgs e)
    {
        // The name check depends on the birthday (Roblox is stricter for younger users).
        if (Birthday != _remoteBirthday) _remote.Clear();
        Refresh();
    }

    private void Refresh()
    {
        if (!IsInitialized || RowsList == null) return;

        var parsed = Parse();
        var ok = Res("OkBrush");
        var bad = Res("DangerBrush");
        var dim = Res("DimBrush");

        var rows = new List<LineRow>();
        foreach (var p in parsed)
        {
            if (p.Entry == null)
            {
                rows.Add(new LineRow { Name = $"Line {p.LineNo}", Message = p.Error ?? "", Icon = "", Brush = bad });
                continue;
            }

            if (_remote.TryGetValue(p.Entry.Username.ToLowerInvariant(), out var r))
                rows.Add(new LineRow
                {
                    Name = p.Entry.Username,
                    Message = r.Valid ? "Name is free" : (string.IsNullOrWhiteSpace(r.Message) ? "Name can't be used" : r.Message),
                    Icon = r.Valid ? "" : "",
                    Brush = r.Valid ? ok : bad,
                });
            else
                rows.Add(new LineRow { Name = p.Entry.Username, Message = "Looks fine – not checked yet", Icon = "", Brush = dim });
        }

        RowsList.ItemsSource = rows;

        var valid = parsed.Count(p => p.Entry != null);
        CountText.Text = parsed.Count == 0 ? "" : $"{valid} of {parsed.Count} ok";
        StartText.Text = valid switch
        {
            0 => "Start",
            1 => "Create 1 account",
            _ => $"Create {valid} accounts",
        };
        FooterText.Text = Birthday == null ? "Pick a birthday first." : "";
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    // ---------- Buttons ----------

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_checking) return;
        if (Birthday is not { } birthday)
        {
            FooterText.Text = "Pick a birthday first.";
            return;
        }

        var entries = Parse().Where(p => p.Entry != null).Select(p => p.Entry!).ToList();
        if (entries.Count == 0)
        {
            FooterText.Text = "Add at least one Username:Password line.";
            return;
        }

        _checking = true;
        CheckButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        if (_remoteBirthday != birthday) _remote.Clear();
        _remoteBirthday = birthday;

        var unreachable = 0;
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                FooterText.Text = $"Checking {i + 1} of {entries.Count}…";
                var key = entries[i].Username.ToLowerInvariant();
                if (_remote.ContainsKey(key)) continue;

                var result = await RobloxApi.ValidateUsernameAsync(entries[i].Username, birthday);
                if (result is { } r) _remote[key] = r;
                else unreachable++;

                Refresh();
                await Task.Delay(250); // be gentle with Roblox's API
            }
        }
        finally
        {
            _checking = false;
            CheckButton.IsEnabled = true;
            StartButton.IsEnabled = true;
            Refresh();
            FooterText.Text = unreachable > 0
                ? $"{unreachable} name(s) couldn't be checked – Roblox will tell you on the sign-up page."
                : "Names checked.";
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_checking) return;
        if (Birthday is not { } birthday)
        {
            FooterText.Text = "Pick a birthday first.";
            return;
        }

        var parsed = Parse();
        var broken = parsed.Where(p => p.Entry == null).ToList();
        if (broken.Count > 0)
        {
            FooterText.Text = broken.Count == 1
                ? $"Fix line {broken[0].LineNo} first: {broken[0].Error}"
                : $"Fix the {broken.Count} red lines first.";
            return;
        }

        var entries = parsed.Select(p => p.Entry!).ToList();
        if (entries.Count == 0)
        {
            FooterText.Text = "Add at least one Username:Password line.";
            return;
        }

        var taken = entries.Where(en => _remote.TryGetValue(en.Username.ToLowerInvariant(), out var r) && !r.Valid).ToList();
        if (taken.Count > 0)
        {
            FooterText.Text = taken.Count == 1
                ? $"\"{taken[0].Username}\" can't be used – change or remove it."
                : $"{taken.Count} names can't be used – change or remove them.";
            return;
        }

        var s = AppState.Settings;
        s.SignupBirthday = birthday;
        s.SignupGender = Gender;
        AppState.SaveSettings();

        Entries.AddRange(entries);
        Options = new SignupOptions(birthday, Gender);
        DialogResult = true;
    }
}
