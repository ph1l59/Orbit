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
        set { _username = value; OnChanged(); OnChanged(nameof(Subtitle)); }
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

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias;

    [JsonIgnore]
    public string Subtitle => "@" + Username;

    [JsonIgnore]
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();

    [JsonIgnore]
    public string LastUsedText => LastUsed is null
        ? "Noch nie gestartet"
        : "Zuletzt: " + LastUsed.Value.ToLocalTime().ToString("dd.MM.yyyy, HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
