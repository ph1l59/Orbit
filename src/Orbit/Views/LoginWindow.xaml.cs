using System.Diagnostics;
using System.IO;
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

    public LoginWindow()
    {
        InitializeComponent();
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 12, 16);
        Loaded += OnLoaded;
        _poll.Tick += async (_, _) => await CheckForLoginAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // A fresh profile per login, so different accounts never share a session.
            var environment = await CoreWebView2Environment.CreateAsync(null, _profileDir);
            await Web.EnsureCoreWebView2Async(environment);

            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckForLoginAsync();
            Web.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                // Keep popups (e.g. "forgot password") inside this window.
                args.Handled = true;
                Web.CoreWebView2.Navigate(args.Uri);
            };

            Web.CoreWebView2.Navigate("https://www.roblox.com/login");
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "Für die Anmeldung wird die Microsoft Edge WebView2 Runtime benötigt.\n\n" +
                "Sie ist auf Windows 10/11 normalerweise schon installiert. Falls nicht, öffnet sich gleich die Download-Seite.",
                "Orbit", MessageBoxButton.OK, MessageBoxImage.Warning);
            Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Der Login konnte nicht geöffnet werden: " + ex.Message, "Orbit",
                MessageBoxButton.OK, MessageBoxImage.Error);
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

            StatusText.Text = "Angemeldet – Account wird überprüft…";
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
