using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AutoSwitcher;

public enum DetectionMode { Focus, Launch }

public sealed class AppConfig
{
    public bool AutoSwitch { get; set; } = true;
    public DetectionMode Mode { get; set; } = DetectionMode.Focus;
    public int FocusDelaySeconds { get; set; } = 8;
    public bool UpdateTitle { get; set; } = true;
    public string TitleTemplate { get; set; } = "Currently Playing: %customName%";
    public bool Toasts { get; set; } = true;
    public bool SuppressToastsFullscreen { get; set; } = true;
    public bool FallbackEnabled { get; set; }
    public CategoryRef FallbackCategory { get; set; } = new() { Id = "509658", Name = "Just Chatting", BoxArtUrl = "https://static-cdn.jtvnw.net/ttv-boxart/509658-{width}x{height}.jpg" };
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public List<string> RecentTitles { get; set; } = new();
    public List<CategoryMapping> Categories { get; set; } = new();
}

/// <summary>A Twitch category (game) as returned by Helix.</summary>
public sealed class CategoryRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BoxArtUrl { get; set; } = "";
}

public sealed class CategoryMapping
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BoxArtUrl { get; set; } = "";
    public List<ExeMapping> Executables { get; set; } = new();
}

public sealed class ExeMapping : System.ComponentModel.INotifyPropertyChanged
{
    private bool _matchTitle;
    private string _titlePattern = "";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Full path to the executable.</summary>
    public string Path { get; set; } = "";
    /// <summary>%fullGameName% — read from the exe's version info, editable.</summary>
    public string FullName { get; set; } = "";
    /// <summary>%customName% — optional short name; falls back to FullName.</summary>
    public string CustomName { get; set; } = "";

    /// <summary>Only match when the window title contains <see cref="TitlePattern"/> (emulators: one exe, many games).</summary>
    public bool MatchTitle
    {
        get => _matchTitle;
        set { _matchTitle = value; PropertyChanged?.Invoke(this, new(nameof(MatchTitle))); }
    }

    /// <summary>Case-insensitive "contains" text; * matches any run of characters (e.g. "Donkey Kong 64*USA").</summary>
    public string TitlePattern
    {
        get => _titlePattern;
        set { _titlePattern = value ?? ""; PropertyChanged?.Invoke(this, new(nameof(TitlePattern))); }
    }

    /// <summary>True when this mapping actually filters on the window title.</summary>
    [JsonIgnore] public bool UsesTitle => MatchTitle && !string.IsNullOrWhiteSpace(TitlePattern);

    /// <summary>Two mappings conflict only if they're the same exe AND the same title rule.</summary>
    [JsonIgnore] public string Key => Path.ToLowerInvariant() + "|" + (UsesTitle ? TitlePattern.Trim().ToLowerInvariant() : "");

    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string ProcessName => System.IO.Path.GetFileNameWithoutExtension(Path);
    [JsonIgnore] public string EffectiveCustom => string.IsNullOrWhiteSpace(CustomName) ? FullName : CustomName.Trim();

    public ExeMapping Clone() => new() { Path = Path, FullName = FullName, CustomName = CustomName, MatchTitle = MatchTitle, TitlePattern = TitlePattern };
}

public sealed class TokenSet
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public System.DateTime ExpiresAtUtc { get; set; }
    public string UserId { get; set; } = "";
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProfileImageUrl { get; set; } = "";
}

public sealed class ChannelInfo
{
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>A running process that matched a mapping.</summary>
public sealed record GameHit(CategoryMapping Category, ExeMapping Exe, int Pid);

public sealed record DeviceCode(string Code, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

public sealed record RunningApp(string Path, string ProcessName, string WindowTitle);
