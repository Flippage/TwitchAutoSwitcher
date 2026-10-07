using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AutoSwitcher;

public enum DetectionMode { Focus, Launch }

public sealed class AppConfig
{
    public string ClientId { get; set; } = "";
    public bool AutoSwitch { get; set; } = true;
    public DetectionMode Mode { get; set; } = DetectionMode.Focus;
    public int FocusDelaySeconds { get; set; } = 8;
    public bool UpdateTitle { get; set; } = true;
    public string TitleTemplate { get; set; } = "Currently Playing: %customName%";
    public bool Toasts { get; set; } = true;
    public bool SuppressToastsFullscreen { get; set; } = true;
    public bool FallbackEnabled { get; set; }
    public CategoryRef FallbackCategory { get; set; } = new() { Id = "509658", Name = "Just Chatting" };
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

public sealed class ExeMapping
{
    /// <summary>Full path to the executable.</summary>
    public string Path { get; set; } = "";
    /// <summary>%fullGameName% — read from the exe's version info, editable.</summary>
    public string FullName { get; set; } = "";
    /// <summary>%customName% — optional short name; falls back to FullName.</summary>
    public string CustomName { get; set; } = "";

    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string ProcessName => System.IO.Path.GetFileNameWithoutExtension(Path);
    [JsonIgnore] public string EffectiveCustom => string.IsNullOrWhiteSpace(CustomName) ? FullName : CustomName.Trim();

    public ExeMapping Clone() => new() { Path = Path, FullName = FullName, CustomName = CustomName };
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
