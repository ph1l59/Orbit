using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Orbit.Models;

public sealed class Account : INotifyPropertyChanged
{
    public long UserId { get; set; }

    private string _username = "";
    public string Username
    {
        get => _username;
        set { _username = value; OnChanged(); OnChanged(nameof(Subtitle)); OnChanged(nameof(PickerSubtitle)); }
    }

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnChanged(); OnChanged(nameof(Title)); OnChanged(nameof(Initial)); }
    }

    /// <summary>.ROBLOSECURITY cookie. Only ever stored encrypted (see AccountStore).</summary>
    public string Cookie { get; set; } = "";

    private string _alias = "";
    public string Alias
    {
        get => _alias;
        set { _alias = value ?? ""; OnChanged(); OnChanged(nameof(Title)); }
    }

    public long BrowserTrackerId { get; set; }

    private DateTime? _lastUsed;
    public DateTime? LastUsed
    {
        get => _lastUsed;
        set { _lastUsed = value; OnChanged(); OnChanged(nameof(LastUsedText)); }
    }

    // ---------- UI-only state ----------

    private string? _avatarUrl;
    [JsonIgnore]
    public string? AvatarUrl
    {
        get => _avatarUrl;
        set { _avatarUrl = value; OnChanged(); }
    }

    private bool _isSelected;
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnChanged(); }
    }

    private string _status = "";
    [JsonIgnore]
    public string Status
    {
        get => _status;
        set { _status = value; OnChanged(); }
    }

    private int _presence;
    /// <summary>0 offline, 1 online, 2 in game, 3 in Studio (Roblox presence API).</summary>
    [JsonIgnore]
    public int Presence
    {
        get => _presence;
        set { _presence = value; OnChanged(); OnChanged(nameof(IsOnline)); OnChanged(nameof(PresenceText)); }
    }

    private string _presenceLocation = "";
    [JsonIgnore]
    public string PresenceLocation
    {
        get => _presenceLocation;
        set { _presenceLocation = value ?? ""; OnChanged(); OnChanged(nameof(PresenceText)); }
    }

    private long? _robux;
    /// <summary>Robux balance (null = not loaded yet).</summary>
    [JsonIgnore]
    public long? Robux
    {
        get => _robux;
        set { _robux = value; OnChanged(); OnChanged(nameof(RobuxText)); OnChanged(nameof(HasRobux)); }
    }

    private bool _loginExpired;
    /// <summary>Roblox no longer accepts the saved login (found by the check at start).</summary>
    [JsonIgnore]
    public bool LoginExpired
    {
        get => _loginExpired;
        set { _loginExpired = value; OnChanged(); OnChanged(nameof(PickerSubtitle)); OnChanged(nameof(HasRobux)); }
    }

    /// <summary>Second line in the Play page's account picker: @name · R$ balance.</summary>
    [JsonIgnore]
    public string PickerSubtitle => LoginExpired ? "Login expired – log in again" : Subtitle;

    /// <summary>Robux balance as text, e.g. "1,234".</summary>
    [JsonIgnore]
    public string RobuxText => Robux?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    [JsonIgnore]
    public bool HasRobux => Robux != null && !LoginExpired;

    [JsonIgnore]
    public bool IsOnline => Presence > 0;

    [JsonIgnore]
    public string PresenceText => Presence switch
    {
        1 => "Online",
        2 => string.IsNullOrEmpty(PresenceLocation) ? "In game" : "In game · " + PresenceLocation,
        3 => "In Studio",
        _ => "Offline",
    };

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias;

    [JsonIgnore]
    public string Subtitle => "@" + Username;

    [JsonIgnore]
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();

    [JsonIgnore]
    public string LastUsedText => LastUsed is null
        ? "Never launched"
        : "Last launched " + LastUsed.Value.ToLocalTime().ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
