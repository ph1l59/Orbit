using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>
/// Shows the real roblox.com login page in an isolated WebView2 profile and
/// picks up the .ROBLOSECURITY cookie once the user is logged in.
/// </summary>
public partial class LoginWindow : Window
{
    public Account? Result { get; private set; }

    private readonly string _profileDir =
        Path.Combine(Path.GetTempPath(), "Orbit", "login-" + Guid.NewGuid().ToString("N"));

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _checking;
    private bool _finished;
    private readonly bool _signup;
    private readonly string? _fillScript;
    private bool _filling;

    /// <param name="signup">True = show Roblox's sign-up page to create a new account.</param>
    public LoginWindow(bool signup = false)
    {
        _signup = signup;
        InitializeComponent();
        if (signup)
        {
            Title = "Orbit – Create a Roblox account";
            HeaderCrumb.Text = "ORBIT // CREATE ACCOUNT";
            HeaderTitle.Text = "Create a Roblox account";
            StatusText.Text = "Sign up on the real Roblox page with your own details. When you're done, Orbit adds the new account automatically.";
        }
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(10, 9, 13);
        Loaded += OnLoaded;
        _poll.Tick += async (_, _) =>
        {
            await FillFormAsync();
            await CheckForLoginAsync();
        };
    }

    /// <summary>
    /// Sign-up page with the form filled in for one entry of a "Create accounts" list.
    /// Orbit only fills the fields – the user still clicks Sign Up and solves any check themselves.
    /// </summary>
    public LoginWindow(SignupEntry entry, SignupOptions options, int index, int total) : this(signup: true)
    {
        _fillScript = BuildFillScript(entry, options);
        HeaderCrumb.Text = total > 1 ? $"ORBIT // CREATE ACCOUNT {index} OF {total}" : "ORBIT // CREATE ACCOUNT";
        HeaderTitle.Text = entry.Username;
        StatusText.Text = "Orbit fills in the form for you. Check it, click Sign Up and solve the check if Roblox shows one – " +
                          "the account is added automatically. Close this window to skip it.";
    }

    private static string BuildFillScript(SignupEntry entry, SignupOptions options)
    {
        var b = options.Birthday;
        var monthName = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(b.Month);
        var data = new
        {
            username = entry.Username,
            password = entry.Password,
            gender = options.Gender,
            // Every way Roblox has labelled the dropdown options so far (value or visible text).
            month = new[] { monthName, monthName[..3], b.Month.ToString(), b.Month.ToString("00") },
            day = new[] { b.Day.ToString(), b.Day.ToString("00") },
            year = new[] { b.Year.ToString() },
        };

        // Fills each field once (so later edits by the user are kept) and never submits the form.
        return "(" + FillFunction + ")(" + JsonSerializer.Serialize(data) + ");";
    }

    private const string FillFunction = """
        function (d) {
          const done = window.__orbitFill || (window.__orbitFill = {});
          const q = (s) => document.querySelector(s);
          const setValue = (el, v) => {
            const proto = el.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
            Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, v); // works with React-controlled fields
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            el.dispatchEvent(new Event('blur', { bubbles: true }));
          };
          const pick = (el, wanted) => {
            const w = wanted.map((x) => String(x).toLowerCase());
            for (const o of el.options) {
              if (w.includes(String(o.value).trim().toLowerCase()) || w.includes(String(o.text).trim().toLowerCase())) {
                setValue(el, o.value);
                return true;
              }
            }
            return false;
          };
          const fillSelect = (key, sel, wanted) => {
            if (done[key]) return;
            const el = q(sel);
            if (el && el.options.length > 1) done[key] = pick(el, wanted);
          };
          const fillInput = (key, sel, v) => {
            if (done[key]) return;
            const el = q(sel);
            if (el) { setValue(el, v); done[key] = true; }
          };

          fillSelect('month', '#MonthDropdown, select[name="birthdayMonth"], select[aria-label="Month"]', d.month);
          fillSelect('day', '#DayDropdown, select[name="birthdayDay"], select[aria-label="Day"]', d.day);
          fillSelect('year', '#YearDropdown, select[name="birthdayYear"], select[aria-label="Year"]', d.year);
          fillInput('username', '#signup-username, input[name="signupUsername"]', d.username);
          fillInput('password', '#signup-password, input[name="signupPassword"]', d.password);

          if (!done.gender && d.gender !== 'none') {
            const btn = d.gender === 'male'
              ? q('#MaleButton, button[aria-label="Male"], button[title="Male"]')
              : q('#FemaleButton, button[aria-label="Female"], button[title="Female"]');
            if (btn) { btn.click(); done.gender = true; }
          }
          return JSON.stringify(done);
        }
        """;

    /// <summary>Runs the fill script; safe to call repeatedly (fields that are already filled are skipped).</summary>
    private async Task FillFormAsync()
    {
        if (_fillScript == null || _filling || _finished || Web.CoreWebView2 == null) return;
        _filling = true;
        try { await Web.CoreWebView2.ExecuteScriptAsync(_fillScript); }
        catch { /* page still loading – the timer tries again */ }
        finally { _filling = false; }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // A fresh profile per login, so different accounts never share a session.
            var environment = await CoreWebView2Environment.CreateAsync(null, _profileDir);
            await Web.EnsureCoreWebView2Async(environment);

            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                await FillFormAsync();
                await CheckForLoginAsync();
            };
            Web.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                // Keep popups (e.g. "forgot password") inside this window.
                args.Handled = true;
                Web.CoreWebView2.Navigate(args.Uri);
            };

            Web.CoreWebView2.Navigate(_signup ? "https://www.roblox.com/" : "https://www.roblox.com/login");
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Dialog.Info(this, "WebView2 needed",
                "Logging in needs the Microsoft Edge WebView2 Runtime. It's normally part of Windows 10/11 – " +
                "if it's missing, the download page opens now.");
            Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            Dialog.Info(this, "Login failed", "The login page could not be opened: " + ex.Message);
            Close();
        }
    }

    private async Task CheckForLoginAsync()
    {
        if (_checking || _finished || Web.CoreWebView2 == null) return;
        _checking = true;

        try
        {
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            var cookie = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY")?.Value;
            if (string.IsNullOrEmpty(cookie)) return;

            StatusText.Text = "Logged in – checking the account…";
            var user = await RobloxApi.GetAuthenticatedUserAsync(cookie);
            if (user == null) return;

            _finished = true;
            _poll.Stop();

            Result = new Account
            {
                UserId = user.Id,
                Username = user.Name,
                DisplayName = user.DisplayName,
                Cookie = cookie,
                BrowserTrackerId = Random.Shared.NextInt64(100_000_000_000, 999_999_999_999),
            };

            DialogResult = true; // closes the window
        }
        catch
        {
            // Network hiccup - the timer simply tries again.
        }
        finally
        {
            _checking = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        try { Web.Dispose(); } catch { /* ignore */ }

        // Remove the temporary browser profile once WebView2 has released its files.
        var dir = _profileDir;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(1000);
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    return;
                }
                catch { /* still locked */ }
            }
        });

        base.OnClosed(e);
    }
}
