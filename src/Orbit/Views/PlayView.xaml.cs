using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media.Media3D;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Views;

/// <summary>
/// Start page: pick one of your accounts and open the Roblox app on its home page,
/// like "Launch Roblox" in Fishstrap.
/// </summary>
public partial class PlayView : UserControl, IPage
{
    private Account? _account;
    private bool _launching;
    private int _avatarVersion;
    private static readonly Dictionary<long, string> FullBodyCache = new();

    public PlayView()
    {
        InitializeComponent();
        PickerList.ItemsSource = AppState.Accounts;
        AppState.Accounts.CollectionChanged += (_, _) => Dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    public void OnShown() => Refresh();

    private Window Owner => Window.GetWindow(this)!;

    // ---------- Account ----------

    private void Refresh()
    {
        var accounts = AppState.Accounts;
        var hasAny = accounts.Count > 0;
        EmptyState.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
        Stage.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        PickerButton.IsEnabled = hasAny;

        var wanted = accounts.FirstOrDefault(a => a.UserId == AppState.Settings.PlayAccountId)
                     ?? accounts.OrderByDescending(a => a.LastUsed ?? DateTime.MinValue).FirstOrDefault();
        Select(wanted, save: false);
    }

    private void Select(Account? account, bool save)
    {
        var changed = account?.UserId != _account?.UserId;
        if (changed) Dispatcher.InvokeAsync(() => AccountChanged?.Invoke(account));
        if (_account != null) _account.PropertyChanged -= OnAccountChanged;
        _account = account;
        if (account != null) account.PropertyChanged += OnAccountChanged;

        if (account == null)
        {
            PickerName.Text = "No account";
            PickerUser.Text = "Add one under Accounts";
            PickerAvatar.ImageSource = null;
            NameTag.Text = "—";
            AvatarImage.Source = null;
            return;
        }

        PickerName.Text = account.Title;
        PickerUser.Text = account.Subtitle;
        PickerAvatar.ImageSource = Load(account.AvatarUrl);
        NameTag.Text = account.Title;

        if (save)
        {
            AppState.Settings.PlayAccountId = account.UserId;
            AppState.SaveSettings();
        }

        if (changed || AvatarImage.Source == null) _ = LoadFullBodyAsync(account);

    }

    /// <summary>The profile picture often arrives a moment after the page is built (right after Orbit starts).</summary>
    private void OnAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Account account || account != _account) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (e.PropertyName == nameof(Account.AvatarUrl)) PickerAvatar.ImageSource = Load(account.AvatarUrl);
            if (e.PropertyName is nameof(Account.Alias) or nameof(Account.DisplayName) or nameof(Account.Title))
            {
                PickerName.Text = account.Title;
                NameTag.Text = account.Title;
            }
        });
    }

    private async Task LoadFullBodyAsync(Account account)
    {
        var version = ++_avatarVersion;
        ShowModel(null);
        AvatarImage.Source = null;
        AvatarImage.Visibility = Visibility.Collapsed;
        AvatarHint.Visibility = Visibility.Collapsed;

        // The flat picture is only a fallback if the 3D model can't be loaded.
        var pictureTask = FullBodyCache.TryGetValue(account.UserId, out var cachedUrl)
            ? Task.FromResult<string?>(cachedUrl)
            : RobloxApi.GetFullBodyAvatarAsync(account.UserId);
        var model = await Avatar3D.LoadAsync(account.UserId, account.Cookie);
        if (version != _avatarVersion) return; // another account was picked meanwhile

        if (model != null)
        {
            ShowModel(model);
            return;
        }

        var url = await pictureTask ?? "";
        if (url.Length > 0) FullBodyCache[account.UserId] = url;
        if (version != _avatarVersion) return;
        AvatarImage.Source = Load(url);
        if (AvatarImage.Source != null)
        {
            AvatarImage.Visibility = Visibility.Visible;
            AvatarImage.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
        }
        else
        {
            AvatarHint.Text = "Avatar couldn't be loaded";
            AvatarHint.Visibility = Visibility.Visible;
        }
    }

    // ---------- 3D avatar ----------

    private AvatarModel? _model;
    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _pitch = new(new Vector3D(1, 0, 0), 0);
    private double _yawAngle, _pitchAngle, _velocity;
    private Point? _dragLast;
    private DateTime _dragTime;
    private bool _spinning;

    private void ShowModel(AvatarModel? model)
    {
        _model = model;
        if (model == null)
        {
            StopIdle();
            AvatarVisual.Content = null;
            AvatarView.Visibility = Visibility.Collapsed;
            DragSurface.Visibility = Visibility.Collapsed;
            return;
        }

        var b = model.Bounds;
        var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        var front = model.Front;
        var up = new Vector3D(0, 1, 0);
        var side = Vector3D.CrossProduct(up, front);
        side.Normalize();

        // Turn around the model's own middle: left/right around "up", a little tilt around the side axis.
        _yaw.Axis = up;
        _pitch.Axis = side;
        var feet = new Point3D(center.X, b.Y, center.Z);
        _idleScale.CenterX = feet.X; _idleScale.CenterY = feet.Y; _idleScale.CenterZ = feet.Z;
        _idleYaw.Axis = up;
        _bobUnit = b.SizeY;

        var transform = new Transform3DGroup();
        transform.Children.Add(_idleScale);                              // breathing + pop-in, from the feet
        transform.Children.Add(new RotateTransform3D(_idleYaw, center)); // gentle sway
        transform.Children.Add(new RotateTransform3D(_yaw, center));     // dragged with the mouse
        transform.Children.Add(new RotateTransform3D(_pitch, center));
        transform.Children.Add(_bob);                                    // small float up and down
        AvatarVisual.Content = model.Model;
        AvatarVisual.Transform = transform;
        ResetRotation();
        StartIdle();

        // Camera in front of the avatar, far enough away to fit it (plus some room for turning).
        var size = Math.Max(b.SizeY, Math.Max(b.SizeX, b.SizeZ) * 1.15);
        var distance = size / 2 / Math.Tan(AvatarCamera.FieldOfView / 2 * Math.PI / 180) * 1.12;
        // Slightly from above, like the avatar editor.
        const double elevation = 16 * Math.PI / 180;
        AvatarCamera.Position = center + front * (distance * Math.Cos(elevation)) + up * (distance * Math.Sin(elevation));
        AvatarCamera.LookDirection = center - AvatarCamera.Position;
        AvatarCamera.NearPlaneDistance = 0.1;
        AvatarCamera.FarPlaneDistance = distance * 10;

        // Main light from the front-top-left, a purple rim light from behind.
        KeyLight.Direction = -front + new Vector3D(0, -0.55, 0) + side * 0.35;
        RimLight.Direction = front + new Vector3D(0, -0.2, 0);

        AvatarImage.Visibility = Visibility.Collapsed;
        AvatarView.Visibility = Visibility.Visible;
        DragSurface.Visibility = Visibility.Visible;
    }

    /// <summary>Start view: turned a little to the side, so the avatar looks three-dimensional right away.</summary>
    private const double DefaultYaw = 10;

    // ---------- Idle animation: pop in, breathe, sway, float ----------

    private readonly ScaleTransform3D _idleScale = new(1, 1, 1);
    private readonly AxisAngleRotation3D _idleYaw = new(new Vector3D(0, 1, 0), 0);
    private readonly TranslateTransform3D _bob = new();
    private double _bobUnit = 5;
    private TimeSpan _idleStart = TimeSpan.MinValue;
    private double _swayWeight = 1;      // fades to 0 while the user turns the avatar
    private DateTime _lastTouch = DateTime.MinValue;
    private bool _idleRunning;

    private void StartIdle()
    {
        _idleStart = TimeSpan.MinValue;
        _swayWeight = 1;
        if (_idleRunning) return;
        _idleRunning = true;
        System.Windows.Media.CompositionTarget.Rendering += Idle;
    }

    private void StopIdle()
    {
        if (!_idleRunning) return;
        _idleRunning = false;
        System.Windows.Media.CompositionTarget.Rendering -= Idle;
    }

    private void Idle(object? sender, EventArgs e)
    {
        if (_model == null || !IsVisible) return;
        var now = ((System.Windows.Media.RenderingEventArgs)e).RenderingTime;
        if (_idleStart == TimeSpan.MinValue) _idleStart = now;
        var t = (now - _idleStart).TotalSeconds;

        // Pop in: grows from 85 % with a small overshoot, fades in.
        var appear = Math.Min(1, t / 0.7);
        var back = 1 + 2.2 * Math.Pow(appear - 1, 3) + 1.2 * Math.Pow(appear - 1, 2); // ease-out-back
        var pop = 0.85 + 0.15 * back;
        AvatarView.Opacity = Math.Min(1, t / 0.35);

        // Breathing (very slight) and floating.
        var breath = 1 + 0.012 * Math.Sin(t * 2 * Math.PI / 3.2);
        _idleScale.ScaleX = pop;
        _idleScale.ScaleZ = pop;
        _idleScale.ScaleY = pop * breath;
        _bob.OffsetY = _bobUnit * 0.012 * Math.Sin(t * 2 * Math.PI / 3.2 + 1.2);

        // Gentle sway – paused while the user turns the avatar, comes back slowly afterwards.
        var touching = _dragLast != null || _spinning || (DateTime.UtcNow - _lastTouch).TotalSeconds < 1.5;
        var wanted = touching ? 0 : 1;
        _swayWeight += (wanted - _swayWeight) * Math.Min(1, 0.04);
        _idleYaw.Angle = _swayWeight * 5 * Math.Sin(t * 2 * Math.PI / 7);
    }

    private void ResetRotation()
    {
        _yawAngle = DefaultYaw;
        _pitchAngle = _velocity = 0;
        _yaw.Angle = DefaultYaw;
        _pitch.Angle = 0;
    }

    private void Avatar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _lastTouch = DateTime.UtcNow;
        if (_model == null) return;
        if (e.ClickCount == 2) { ResetRotation(); return; }
        _dragLast = e.GetPosition(DragSurface);
        _dragTime = DateTime.UtcNow;
        _velocity = 0;
        DragSurface.CaptureMouse();
    }

    private void Avatar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragLast is not { } last) return;
        _lastTouch = DateTime.UtcNow;
        var now = e.GetPosition(DragSurface);
        var dx = now.X - last.X;
        var dy = now.Y - last.Y;

        _yawAngle += dx * 0.6;
        _pitchAngle = Math.Clamp(_pitchAngle + dy * 0.25, -18, 18);
        _yaw.Angle = _yawAngle;
        _pitch.Angle = _pitchAngle;

        var seconds = Math.Max(0.001, (DateTime.UtcNow - _dragTime).TotalSeconds);
        _velocity = dx * 0.6 / seconds; // degrees per second, for the spin after letting go
        _dragTime = DateTime.UtcNow;
        _dragLast = now;
    }

    private void Avatar_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragSurface.ReleaseMouseCapture();

    private void Avatar_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragLast == null) return;
        _dragLast = null;
        // A quick flick keeps turning and slows down smoothly.
        if ((DateTime.UtcNow - _dragTime).TotalMilliseconds > 80) _velocity = 0;
        _velocity = Math.Clamp(_velocity, -900, 900);
        if (Math.Abs(_velocity) > 20 && !_spinning)
        {
            _spinning = true;
            _lastFrame = DateTime.UtcNow;
            System.Windows.Media.CompositionTarget.Rendering += Spin;
        }
    }

    private DateTime _lastFrame;

    private void Spin(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (_dragLast != null || Math.Abs(_velocity) < 5 || _model == null)
        {
            _spinning = false;
            System.Windows.Media.CompositionTarget.Rendering -= Spin;
            return;
        }
        _yawAngle += _velocity * dt;
        _yaw.Angle = _yawAngle;
        _velocity *= Math.Pow(0.08, dt); // friction
    }

    private static BitmapImage? Load(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void Picker_Click(object sender, RoutedEventArgs e) => PickerPopup.IsOpen = !PickerPopup.IsOpen;

    private void PickAccount_Click(object sender, RoutedEventArgs e)
    {
        PickerPopup.IsOpen = false;
        if ((sender as FrameworkElement)?.Tag is Account account) Select(account, save: true);
    }

    private void GoToAccounts_Click(object sender, RoutedEventArgs e) => (Owner as MainWindow)?.Navigate("accounts");

    // ---------- Launch ----------

    /// <summary>"Launch Roblox" from the tray menu.</summary>
    public void LaunchFromOutside() => Launch_Click(this, new RoutedEventArgs());

    private void Launch_Click(object sender, RoutedEventArgs e) =>
        _ = RunLaunchAsync((account, exe) => RobloxLauncher.LaunchAppAsync(account, exe), "Starting Roblox…");

    /// <summary>Shared launch flow: update Roblox, then start it the given way, with the launch window on top.</summary>
    private async Task RunLaunchAsync(Func<Account, string, Task> start, string startingText)
    {
        if (_launching || _account is not { } account) return;
        var s = AppState.Settings;

        if (!MultiInstance.IsActive && s.ConfirmLaunchWhenRunning && RobloxPaths.IsRobloxRunning())
        {
            if (!Dialog.Confirm(Owner, "Roblox is already running",
                    "Launching will close the Roblox window that is open right now. Turn on Multi-Roblox to keep several open.",
                    "Launch anyway"))
                return;
        }

        _launching = true;
        LaunchButton.IsEnabled = false;
        StatusText.Text = "";
        var window = new LaunchWindow(Owner, account.Title);
        window.Show();

        try
        {
            window.SetStatus("Checking for Roblox updates…");
            var progress = new Progress<BootstrapProgress>(p =>
            {
                window.SetStatus(p.Message);
                window.SetProgress(p.Percent);
            });
            var exe = await RobloxLauncher.PrepareAsync(s, progress);
            if (window.CancelRequested) { window.CloseSoftly(); return; }

            window.SetProgress(null);
            window.SetStatus(startingText);
            window.HideCancel();
            var before = RobloxPaths.RunningCount();

            await start(account, exe);
            Integrations.StartCustomIntegrations();
            account.LastUsed = DateTime.UtcNow;
            AppState.SaveAccounts();

            // Stay up until Roblox has really started (at most ~15 s).
            for (var i = 0; i < 30 && RobloxPaths.RunningCount() <= before; i++)
                await Task.Delay(500);
            await Task.Delay(600);
            window.CloseSoftly();
        }
        catch (Exception ex)
        {
            window.Close();
            StatusText.Text = "Couldn't start Roblox: " + ex.Message;
        }
        finally
        {
            _launching = false;
            LaunchButton.IsEnabled = true;
        }
    }

    // ---------- Friends (the drawer itself lives in the main window) ----------

    /// <summary>The account picked on this page changed.</summary>
    public event Action<Account?>? AccountChanged;
    /// <summary>The friends button was clicked.</summary>
    public event Action? FriendsRequested;

    public Account? CurrentAccount => _account;

    private void FriendsToggle_Click(object sender, RoutedEventArgs e) => FriendsRequested?.Invoke();

    /// <summary>Number of open friend requests, shown on the friends button.</summary>
    public void SetRequestCount(int count)
    {
        RequestBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RequestBadgeText.Text = count > 99 ? "99+" : count.ToString();
    }

    /// <summary>Joins a friend's server with the account picked on this page.</summary>
    public void Join(PersonItem friend) =>
        _ = RunLaunchAsync((account, exe) => RobloxLauncher.LaunchAccountAsync(account, JoinTarget.Follow(friend.Id), exe),
            $"Joining {friend.Title}…");
}
