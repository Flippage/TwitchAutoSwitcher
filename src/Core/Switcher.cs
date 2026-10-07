using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoSwitcher;

/// <summary>
/// Turns "game X is current" into Twitch updates.
/// Rule: build the target category + filled-in title, compare with what's live, and only PATCH what differs.
/// So switching Ship of Harkinian → Project64 (same category) sends nothing unless the title's text changes
/// (e.g. it uses %fullGameName% or %customName%).
/// </summary>
/// <summary>What a switch notification shows: heading, reason line, optional title line, box art.</summary>
public sealed record ToastInfo(string Heading, string Body, string? Detail, string? BoxArtUrl);

public sealed class Switcher
{
    public const int MaxTitle = 140;

    private readonly AppConfig _cfg;
    private readonly TwitchService _tw;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ChannelInfo? Live { get; private set; }
    /// <summary>True = streaming now, false = offline, null = unknown / not connected.</summary>
    public bool? IsLive { get; private set; }
    public int Viewers { get; private set; }
    public GameHit? Current { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastSwitchUtc { get; private set; }

    /// <summary>Raised on the UI thread whenever state shown in the UI changes.</summary>
    public event Action? Changed;
    public event Action<ToastInfo>? Toast;

    public Switcher(AppConfig cfg, TwitchService tw)
    {
        _cfg = cfg;
        _tw = tw;
    }

    public static string Render(string template, string gameName, string fullName, string customName)
    {
        string s = template
            .Replace("%gameName%", gameName, StringComparison.OrdinalIgnoreCase)
            .Replace("%fullGameName%", fullName, StringComparison.OrdinalIgnoreCase)
            .Replace("%customName%", customName, StringComparison.OrdinalIgnoreCase)
            .Trim();
        return s.Length > MaxTitle ? s[..MaxTitle] : s;
    }

    public string RenderFor(GameHit hit) =>
        Render(_cfg.TitleTemplate, hit.Category.Name, hit.Exe.FullName, hit.Exe.EffectiveCustom);

    public void RaiseChanged() => Changed?.Invoke();

    public async Task RefreshStreamStatusAsync()
    {
        if (!_tw.IsSignedIn) { IsLive = null; Changed?.Invoke(); return; }
        try
        {
            var s = await _tw.GetStreamStatusAsync();
            IsLive = s?.Live;
            Viewers = s?.Viewers ?? 0;
        }
        catch { /* keep last known */ }
        Changed?.Invoke();
    }

    public async Task RefreshLiveAsync()
    {
        if (!_tw.IsSignedIn) { Live = null; Changed?.Invoke(); return; }
        try { Live = await _tw.GetChannelAsync() ?? Live; }
        catch (Exception ex) { LastError = ex.Message; }
        Changed?.Invoke();
    }

    public async void OnActivated(GameHit hit)
    {
        Current = hit;
        Changed?.Invoke();
        if (!_cfg.AutoSwitch) return;
        string? title = _cfg.UpdateTitle ? RenderFor(hit) : null;
        string reason = _cfg.Mode == DetectionMode.Focus ? "Auto-switched · focused window" : "Auto-switched · launched app";
        await ApplyAsync(hit.Category.Id, hit.Category.Name, title, hit.Exe.EffectiveCustom, reason, hit.Category.BoxArtUrl);
    }

    public async void OnExited(GameHit hit)
    {
        Current = null;
        Changed?.Invoke();
        var f = _cfg.FallbackCategory;
        if (!_cfg.AutoSwitch || !_cfg.FallbackEnabled || string.IsNullOrEmpty(f.Id)) return;
        string? title = _cfg.UpdateTitle ? Render(_cfg.TitleTemplate, f.Name, f.Name, f.Name) : null;
        await ApplyAsync(f.Id, f.Name, title, f.Name, "Game closed · fallback category", f.BoxArtUrl);
    }

    /// <summary>"Switch now" from the sidebar: apply the detected game even while auto-switch is off.</summary>
    public async Task SwitchNowAsync(GameHit hit)
    {
        Current = hit;
        string? title = _cfg.UpdateTitle ? RenderFor(hit) : null;
        await ApplyAsync(hit.Category.Id, hit.Category.Name, title, hit.Exe.EffectiveCustom, "Switched from AutoSwitcher", hit.Category.BoxArtUrl);
    }

    /// <summary>Re-apply the current game (used when auto-switch is turned back on or the template changes).</summary>
    public void Reapply()
    {
        if (Current != null) OnActivated(Current);
    }

    public async Task<bool> ApplyAsync(string? gameId, string? gameName, string? title, string? label = null,
                                       string? reason = null, string? boxArtUrl = null)
    {
        if (!_tw.IsSignedIn)
        {
            LastError = "Connect your Twitch account in Settings.";
            Changed?.Invoke();
            return false;
        }

        await _lock.WaitAsync();
        try
        {
            if (Live == null)
            {
                try { Live = await _tw.GetChannelAsync(); } catch { }
            }

            string? newGame = !string.IsNullOrEmpty(gameId) && gameId != Live?.GameId ? gameId : null;
            string? newTitle = title != null && title != Live?.Title ? title : null;
            if (newGame == null && newTitle == null) { LastError = null; return true; }

            bool ok = await _tw.UpdateChannelAsync(newGame, newTitle);
            if (!ok)
            {
                LastError = "Twitch didn't accept the update. Try reconnecting your account.";
                return false;
            }

            Live = new ChannelInfo
            {
                GameId = newGame ?? Live?.GameId ?? "",
                GameName = newGame != null ? gameName ?? "" : Live?.GameName ?? "",
                Title = newTitle ?? Live?.Title ?? "",
            };
            LastError = null;
            LastSwitchUtc = DateTime.UtcNow;

            if (_cfg.Toasts)
            {
                // Windows toasts show up to 3 lines: heading + 2.
                string game = gameName ?? "";
                string how = reason ?? "Updated from AutoSwitcher";
                if (newGame != null && newTitle != null)
                    Toast?.Invoke(new ToastInfo("Stream category & title updated", $"{game} · {how}", newTitle, boxArtUrl));
                else if (newGame != null)
                    Toast?.Invoke(new ToastInfo("Stream category updated", game, how, boxArtUrl));
                else
                    Toast?.Invoke(new ToastInfo("Stream title updated", newTitle!, reason,
                        boxArtUrl ?? Current?.Category.BoxArtUrl));
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
        finally
        {
            _lock.Release();
            Changed?.Invoke();
        }
    }
}
