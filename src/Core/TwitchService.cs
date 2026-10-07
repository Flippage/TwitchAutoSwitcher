using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AutoSwitcher;

/// <summary>
/// Twitch auth (OAuth Device Code Grant, public client — no secret in the EXE) and the few Helix calls we need.
/// Scope: channel:manage:broadcast (change category + title).
/// </summary>
public sealed class TwitchService
{
    public const string Scopes = "channel:manage:broadcast";
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string Helix = "https://api.twitch.tv/helix/";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public string ClientId { get; set; }
    public TokenSet? Tokens { get; private set; }
    public bool IsSignedIn => Tokens != null;
    public event Action? AuthChanged;

    public TwitchService(string clientId)
    {
        ClientId = clientId;
        Tokens = ConfigStore.LoadTokens();
    }

    // ------------------------------------------------------------------ auth

    /// <summary>On startup: refresh (rolls the 30-day public-client refresh token) and load the profile.</summary>
    public async Task InitAsync()
    {
        if (Tokens == null || string.IsNullOrEmpty(ClientId)) return;
        await RefreshAsync(force: true);
        if (Tokens != null) await LoadUserAsync();
        AuthChanged?.Invoke();
    }

    public async Task<DeviceCode> StartDeviceFlowAsync()
    {
        using var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/device",
            Form(("client_id", ClientId), ("scopes", Scopes)));
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(ErrorMessage(body));
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return new DeviceCode(
            r.GetProperty("device_code").GetString()!,
            r.GetProperty("user_code").GetString()!,
            r.GetProperty("verification_uri").GetString()!,
            r.GetProperty("expires_in").GetInt32(),
            r.TryGetProperty("interval", out var i) ? i.GetInt32() : 5);
    }

    /// <summary>Polls until the user approves on twitch.tv/activate. Returns false if the code expired.</summary>
    public async Task<bool> CompleteDeviceFlowAsync(DeviceCode dc, CancellationToken ct)
    {
        int interval = Math.Max(1, dc.Interval);
        DateTime deadline = DateTime.UtcNow.AddSeconds(dc.ExpiresIn);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            using var resp = await Http.PostAsync(TokenUrl, Form(
                ("client_id", ClientId),
                ("scopes", Scopes),
                ("device_code", dc.Code),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code")), ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                SetTokens(ParseToken(body, null));
                await LoadUserAsync();
                AuthChanged?.Invoke();
                return true;
            }
            string msg = ErrorMessage(body);
            if (msg.Contains("pending", StringComparison.OrdinalIgnoreCase)) continue;
            if (msg.Contains("slow_down", StringComparison.OrdinalIgnoreCase)) { interval += 5; continue; }
            if (msg.Contains("invalid device code", StringComparison.OrdinalIgnoreCase)) return false;
            throw new InvalidOperationException(msg);
        }
        return false;
    }

    public async Task SignOutAsync()
    {
        var t = Tokens;
        ClearTokens();
        AuthChanged?.Invoke();
        if (t == null) return;
        try
        {
            using var _ = await Http.PostAsync("https://id.twitch.tv/oauth2/revoke",
                Form(("client_id", ClientId), ("token", t.AccessToken)));
        }
        catch { /* revocation is best effort */ }
    }

    /// <summary>Twitch asks apps to validate tokens hourly.</summary>
    public async Task ValidateAsync()
    {
        var t = Tokens;
        if (t == null) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
            req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", t.AccessToken);
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) await RefreshAsync(force: true);
        }
        catch { /* offline: try again next hour */ }
    }

    private async Task<bool> RefreshAsync(bool force = false)
    {
        var before = Tokens;
        if (before == null) return false;
        await _refreshLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(before, Tokens)) return Tokens != null;          // refreshed by someone else
            if (!force && Tokens!.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2)) return true;

            using var resp = await Http.PostAsync(TokenUrl, Form(
                ("client_id", ClientId),
                ("grant_type", "refresh_token"),
                ("refresh_token", before.RefreshToken)));
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                // 400/401 = refresh token expired (30 days unused) or revoked → need to log in again.
                if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                {
                    ClearTokens();
                    AuthChanged?.Invoke();
                }
                return false;
            }
            SetTokens(ParseToken(body, before));
            return true;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
        finally { _refreshLock.Release(); }
    }

    private async Task LoadUserAsync()
    {
        try
        {
            using var resp = await ApiAsync(HttpMethod.Get, "users");
            if (resp == null || !resp.IsSuccessStatusCode || Tokens == null) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var u = doc.RootElement.GetProperty("data")[0];
            Tokens.UserId = u.GetProperty("id").GetString() ?? "";
            Tokens.Login = u.GetProperty("login").GetString() ?? "";
            Tokens.DisplayName = u.GetProperty("display_name").GetString() ?? Tokens.Login;
            Tokens.ProfileImageUrl = u.GetProperty("profile_image_url").GetString() ?? "";
            ConfigStore.SaveTokens(Tokens);
        }
        catch { }
    }

    private void SetTokens(TokenSet t)
    {
        Tokens = t;
        ConfigStore.SaveTokens(t);
    }

    private void ClearTokens()
    {
        Tokens = null;
        ConfigStore.SaveTokens(null);
    }

    private static TokenSet ParseToken(string body, TokenSet? previous)
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return new TokenSet
        {
            AccessToken = r.GetProperty("access_token").GetString()!,
            RefreshToken = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : previous?.RefreshToken ?? "",
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(r.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600),
            UserId = previous?.UserId ?? "",
            Login = previous?.Login ?? "",
            DisplayName = previous?.DisplayName ?? "",
            ProfileImageUrl = previous?.ProfileImageUrl ?? "",
        };
    }

    // ------------------------------------------------------------------ helix

    public async Task<List<CategoryRef>> SearchCategoriesAsync(string query, CancellationToken ct)
    {
        var list = new List<CategoryRef>();
        using var resp = await ApiAsync(HttpMethod.Get, "search/categories?first=20&query=" + Uri.EscapeDataString(query), null, ct);
        if (resp == null || !resp.IsSuccessStatusCode) return list;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var c in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            list.Add(new CategoryRef
            {
                Id = c.GetProperty("id").GetString() ?? "",
                Name = c.GetProperty("name").GetString() ?? "",
                BoxArtUrl = c.GetProperty("box_art_url").GetString() ?? "",
            });
        }
        return list;
    }

    public async Task<CategoryRef?> GetGameAsync(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        using var resp = await ApiAsync(HttpMethod.Get, "games?id=" + Uri.EscapeDataString(id));
        if (resp == null || !resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) return null;
        var g = data[0];
        return new CategoryRef
        {
            Id = g.GetProperty("id").GetString() ?? "",
            Name = g.GetProperty("name").GetString() ?? "",
            BoxArtUrl = g.GetProperty("box_art_url").GetString() ?? "",
        };
    }

    public async Task<ChannelInfo?> GetChannelAsync()
    {
        if (Tokens == null || string.IsNullOrEmpty(Tokens.UserId)) return null;
        using var resp = await ApiAsync(HttpMethod.Get, "channels?broadcaster_id=" + Tokens.UserId);
        if (resp == null || !resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var c = doc.RootElement.GetProperty("data")[0];
        return new ChannelInfo
        {
            GameId = c.GetProperty("game_id").GetString() ?? "",
            GameName = c.GetProperty("game_name").GetString() ?? "",
            Title = c.GetProperty("title").GetString() ?? "",
        };
    }

    /// <summary>PATCH /channels with only the fields that changed. Returns true on 204.</summary>
    public async Task<bool> UpdateChannelAsync(string? gameId, string? title)
    {
        if (Tokens == null || string.IsNullOrEmpty(Tokens.UserId)) return false;
        var body = new JsonObject();
        if (gameId != null) body["game_id"] = gameId;
        if (title != null) body["title"] = title;
        using var resp = await ApiAsync(HttpMethod.Patch, "channels?broadcaster_id=" + Tokens.UserId, body.ToJsonString());
        return resp != null && resp.IsSuccessStatusCode;
    }

    /// <summary>Sends a Helix request; on 401 refreshes once and retries.</summary>
    private async Task<HttpResponseMessage?> ApiAsync(HttpMethod method, string path, string? json = null, CancellationToken ct = default)
    {
        if (Tokens == null) return null;
        if (Tokens.ExpiresAtUtc < DateTime.UtcNow.AddMinutes(1)) await RefreshAsync();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var t = Tokens;
            if (t == null) return null;
            using var req = new HttpRequestMessage(method, Helix + path);
            req.Headers.Add("Client-Id", ClientId);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.AccessToken);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                resp.Dispose();
                if (!await RefreshAsync(force: true)) return null;
                continue;
            }
            return resp;
        }
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs)
    {
        var list = new List<KeyValuePair<string, string>>(pairs.Length);
        foreach (var (k, v) in pairs) list.Add(new KeyValuePair<string, string>(k, v));
        return new FormUrlEncodedContent(list);
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString() ?? body;
        }
        catch { }
        return string.IsNullOrWhiteSpace(body) ? "Unknown Twitch error" : body;
    }
}
