using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>A browser window that is already logged in as one account (like "Open Browser" in Roblox Account Manager).</summary>
public sealed class BrowserWindow : Window
{
    private readonly Account _account;
    private readonly string _startUrl;
    private readonly WebView2 _web = new();
    private readonly TextBlock _address;

    private static T R<T>(string key) => (T)Application.Current.FindResource(key);

    /// <param name="startUrl">Page to open first (default: roblox.com home).</param>
    public BrowserWindow(Account account, string? startUrl = null)
    {
        _account = account;
        _startUrl = startUrl ?? "https://www.roblox.com/home";
        Title = $"Orbit – {account.Title} (@{account.Username})";
        Width = 1180;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = R<Brush>("BgBrush");
        Foreground = R<Brush>("TextBrush");
        FontFamily = R<FontFamily>("UiFont");
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/orbit.ico"));

        var back = new Button { Content = "", FontFamily = R<FontFamily>("IconFont"), Width = 34, Padding = new Thickness(0), ToolTip = "Back" };
        var forward = new Button { Content = "", FontFamily = R<FontFamily>("IconFont"), Width = 34, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Forward" };
        var reload = new Button { Content = "", FontFamily = R<FontFamily>("IconFont"), Width = 34, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Reload" };
        var home = new Button { Content = "", FontFamily = R<FontFamily>("IconFont"), Width = 34, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Home" };
        back.Click += (_, _) => { if (_web.CanGoBack) _web.GoBack(); };
        forward.Click += (_, _) => { if (_web.CanGoForward) _web.GoForward(); };
        reload.Click += (_, _) => _web.Reload();
        home.Click += (_, _) => _web.CoreWebView2?.Navigate("https://www.roblox.com/home");

        _address = new TextBlock
        {
            Style = R<Style>("Crumb"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var who = new TextBlock
        {
            Text = $"LOGGED IN AS @{account.Username.ToUpperInvariant()}",
            Style = R<Style>("Crumb"), Foreground = R<Brush>("SignalLightBrush"), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Don't log out in this window – that would also log this account out of Orbit.",
        };

        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        DockPanel.SetDock(who, Dock.Right);
        bar.Children.Add(who);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(back);
        buttons.Children.Add(forward);
        buttons.Children.Add(reload);
        buttons.Children.Add(home);
        DockPanel.SetDock(buttons, Dock.Left);
        bar.Children.Add(buttons);
        bar.Children.Add(_address);

        var top = new Border
        {
            Background = R<Brush>("SidebarBrush"), BorderBrush = R<Brush>("LineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1), Child = bar,
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_web);
        Content = root;

        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(10, 9, 13);
        Loaded += OnLoaded;
        Closed += (_, _) => _web.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // One browser profile per account, so accounts never mix.
            var profile = Path.Combine(AppPaths.LocalAppData, "Orbit", "Browser", _account.UserId.ToString());
            var env = await CoreWebView2Environment.CreateAsync(null, profile);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            var cookie = core.CookieManager.CreateCookie(".ROBLOSECURITY", _account.Cookie, ".roblox.com", "/");
            cookie.IsSecure = true;
            cookie.IsHttpOnly = true;
            cookie.Expires = DateTime.Now.AddYears(1);
            core.CookieManager.AddOrUpdateCookie(cookie);

            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                core.Navigate(args.Uri);
            };
            core.SourceChanged += (_, _) => _address.Text = core.Source;

            core.Navigate(_startUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Dialog.Info(this, "WebView2 needed", "The browser needs the Microsoft Edge WebView2 Runtime, which is part of Windows 10/11 normally.");
            Close();
        }
        catch (Exception ex)
        {
            Dialog.Info(this, "Couldn't open the browser", ex.Message);
            Close();
        }
    }
}
