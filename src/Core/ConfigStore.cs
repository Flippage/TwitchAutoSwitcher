using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoSwitcher;

/// <summary>Config lives in %APPDATA%\AutoSwitcher. Tokens are encrypted with DPAPI (current Windows user only).</summary>
public static class ConfigStore
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoSwitcher");

    private static string ConfigPath => Path.Combine(Dir, "config.json");
    private static string TokenPath => Path.Combine(Dir, "tokens.dat");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Opts) ?? new AppConfig();
        }
        catch
        {
            // Keep a copy of a broken file rather than silently losing the user's mappings.
            try { File.Copy(ConfigPath, ConfigPath + ".broken", overwrite: true); } catch { }
        }
        return new AppConfig();
    }

    /// <summary>Screenshot (demo) mode: never write the user's settings or tokens.</summary>
    public static bool ReadOnly { get; set; }

    public static void Save(AppConfig config)
    {
        if (ReadOnly) return;
        Directory.CreateDirectory(Dir);
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Opts));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    public static TokenSet? LoadTokens()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            byte[] plain = Native.Unprotect(File.ReadAllBytes(TokenPath));
            return JsonSerializer.Deserialize<TokenSet>(plain);
        }
        catch { return null; }
    }

    public static void SaveTokens(TokenSet? tokens)
    {
        if (ReadOnly) return;
        try
        {
            if (tokens == null)
            {
                if (File.Exists(TokenPath)) File.Delete(TokenPath);
                return;
            }
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(TokenPath, Native.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens)));
        }
        catch { /* non-fatal: user just has to log in again next launch */ }
    }
}
