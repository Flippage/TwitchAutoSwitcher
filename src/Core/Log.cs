using System;
using System.IO;
using System.Text;

namespace AutoSwitcher;

/// <summary>
/// Small rolling diagnostic log: %APPDATA%\AutoSwitcher\logs\autoswitcher.log (+ .1 when it passes 1 MB).
/// Records detection decisions, switches, updates and errors so problems can be traced afterwards.
/// Privacy: windows that aren't mapped games are logged by exe file name only — never by window title.
/// </summary>
public static class Log
{
    public static readonly string Dir = Path.Combine(ConfigStore.Dir, "logs");
    public static string FilePath => Path.Combine(Dir, "autoswitcher.log");
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();
    private static bool _started;

    public static void Info(string area, string message) => Write("INFO", area, message);
    public static void Warn(string area, string message) => Write("WARN", area, message);
    public static void Error(string area, string message, Exception? ex = null) =>
        Write("ERROR", area, ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string area, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    string old = FilePath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                var sb = new StringBuilder();
                if (!_started)
                {
                    _started = true;
                    sb.Append($"\n===== AutoSwitcher {Updater.CurrentTag} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n");
                }
                sb.Append($"{DateTime.Now:HH:mm:ss.fff} {level,-5} [{area}] {message}\n");
                File.AppendAllText(FilePath, sb.ToString());
            }
        }
        catch { /* logging must never break the app */ }
    }
}
