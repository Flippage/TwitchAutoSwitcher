using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AutoSwitcher;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = null!;
    public static TwitchService Twitch { get; private set; } = null!;
    public static Switcher Switcher { get; private set; } = null!;
    public static GameWatcher Watcher { get; private set; } = null!;
    public static Updater Updater { get; private set; } = null!;
    public static bool IsExiting { get; private set; }

    private static TrayIcon? _tray;
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private DispatcherTimer? _validateTimer;
    private DispatcherTimer? _statusTimer;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: a second launch just brings the first window forward.
        _mutex = new Mutex(true, "AutoSwitcher.SingleInstance.v1", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "AutoSwitcher.Show.v1");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _selfTest = e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);
        if (_selfTest) ConfigStore.ReadOnly = true;   // the self-test adds sample mappings: never save them
        int si = Array.FindIndex(e.Args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        if (si >= 0) _screenshotDir = System.IO.Path.GetFullPath(si + 1 < e.Args.Length ? e.Args[si + 1] : "screenshots");
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;   // never crash the tray app over a UI glitch…
            LogError("UI", args.Exception);
            Log.Error("ui", "Unhandled UI error", args.Exception);
            if (_selfTest) { _selfTestError ??= args.Exception; }
        };

        try
        {
            StartApp(e);
        }
        catch (Exception ex)
        {
            // …but never sit invisible either: say what happened and exit cleanly.
            LogError("Startup", ex);
            if (_selfTest) { Console.Error.WriteLine(ex); SelfTestExit(1); return; }
            MessageBox.Show(
                "AutoSwitcher couldn't start.\n\n" + ex.GetBaseException().Message +
                "\n\nDetails were saved to:\n" + ErrorLogPath +
                "\n\nPlease report this on GitHub (Issues).",
                "AutoSwitcher", MessageBoxButton.OK, MessageBoxImage.Error);
            try { _mutex?.ReleaseMutex(); } catch { }
            Shutdown(1);
        }
    }

    private void StartApp(StartupEventArgs e)
    {
        Toasts.Init();   // AppUserModelID first, so the taskbar and notifications agree on who we are
        Config = _screenshotDir != null ? Screenshots.BuildConfig(_screenshotDir) : ConfigStore.Load();

        // "Update automatically on next launch": swap in the downloaded version before any UI appears.
        if (!_selfTest && _screenshotDir == null && Updater.TryInstallOnLaunch(Config))
        {
            RestartInto(Environment.ProcessPath!, string.Join(" ", e.Args.Select(a => $"\"{a}\"")));
            return;
        }

        Twitch = new TwitchService();
        Switcher = new Switcher(Config, Twitch);
        Watcher = new GameWatcher { FocusDelay = TimeSpan.FromSeconds(Config.FocusDelaySeconds) };
        Watcher.UpdateMappings(Config.Categories);
        Watcher.Activated += Switcher.OnActivated;
        Watcher.Exited += Switcher.OnExited;

        Updater = new Updater(Config);
        _tray = new TrayIcon();
        Switcher.Toast += info => _ = Toasts.ShowAsync(info, fallback: (h, b) => _tray?.ShowToast(h, b));

        var window = new MainWindow();
        MainWindow = window;
        if (_selfTest || _screenshotDir != null || !e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) window.Show();

        ThreadPool.RegisterWaitForSingleObject(_showSignal!,
            (_, _) => Dispatcher.InvokeAsync(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);

        if (_selfTest) { _ = RunSelfTestAsync(window); return; }
        if (_screenshotDir != null)
        {
            _ = Dispatcher.InvokeAsync(async () => { await Screenshots.RunAsync(window, _screenshotDir); SelfTestExit(0); });
            return;
        }
        _ = InitAsync();
    }

    // ------------------------------------------------------------------ diagnostics

    private static bool _selfTest;
    private static string? _screenshotDir;
    private static Exception? _selfTestError;
    public static string ErrorLogPath => System.IO.Path.Combine(ConfigStore.Dir, "error.log");

    public static void LogError(string where, Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConfigStore.Dir);
            System.IO.File.AppendAllText(ErrorLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version} {where}: {ex}\n\n");
        }
        catch { }
    }

    /// <summary>
    /// CI smoke test (--selftest): start for real, show the window, open every page, render, exit 0.
    /// Any exception → exit 1. Catches "starts but shows nothing" bugs before a release goes out.
    /// </summary>
    private async Task RunSelfTestAsync(MainWindow window)
    {
        System.Diagnostics.Process? selfTestChild = null;
        try
        {
            // Start our own short-lived processes and map them, so the detection stack is exercised on any machine.
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
                { CreateNoWindow = true, UseShellExecute = false };
            selfTestChild = System.Diagnostics.Process.Start(psi);
            Config.Categories.Add(new CategoryMapping { Id = "1", Name = "Self-test Game One",
                Executables = { new ExeMapping { Path = @"C:\x\cmd.exe", FullName = "Command" } } });
            Config.Categories.Add(new CategoryMapping { Id = "2", Name = "Self-test Game Two With A Rather Long Category Name",
                Executables = { new ExeMapping { Path = @"C:\x\ping.exe", FullName = "Ping" } } });
            Config.Categories.Add(new CategoryMapping { Id = "3", Name = "Self-test Game Three",
                Executables = { new ExeMapping { Path = @"C:\x\pwsh.exe", FullName = "PowerShell" } } });
            await Task.Delay(500);
            Watcher.UpdateMappings(Config.Categories);
            Watcher.Start(DetectionMode.Launch);
            await Task.Delay(800);
            // RetroArch replies: names can contain commas; CONTENTLESS has no game.
            var ra = RetroArch.Parse("GET_STATUS PLAYING n64,Legend of Zelda, The - Ocarina of Time (USA),crc32=CD16C529");
            if (ra.State != "PLAYING" || ra.System != "n64" || ra.Game != "Legend of Zelda, The - Ocarina of Time (USA)")
                throw new InvalidOperationException($"RetroArch parse failed: '{ra.State}' '{ra.System}' '{ra.Game}'");
            if (RetroArch.Parse("GET_STATUS CONTENTLESS").Game != "") throw new InvalidOperationException("RetroArch CONTENTLESS parse failed.");
            if ((await RetroArch.QueryAsync(55399, TimeSpan.FromMilliseconds(300))).Reachable)
                throw new InvalidOperationException("RetroArch query reported an answer from a closed port.");
            window.SelfTestVisitPages();
            await window.SelfTestFlipStackAsync();
            await window.SelfTestConfirmAsync();
            // Multiworlds: activate turns exactly its games on; deactivate restores; a hand change auto-deactivates.
            Config.Categories[0].Enabled = false;                                  // pretend game 1 was off beforehand
            var mw = new Multiworld { Name = "Self-test world", CategoryIds = { "1", "2" } };
            Config.Multiworlds.Add(mw);
            Multiworlds.Activate(mw);
            if (!Multiworlds.IsActive(mw) || Config.Categories.Any(c => c.Enabled != (c.Id is "1" or "2")))
                throw new InvalidOperationException("Activating a multiworld didn't set the right games.");
            if (Watcher.Running.Any(h => h.Category.Id == "3")) throw new InvalidOperationException("A game outside the multiworld was still detected.");
            window.NavMultiworlds.IsChecked = true;
            window.UpdateLayout();
            Multiworlds.Deactivate();
            if (Multiworlds.IsActive(mw) || Config.Categories[0].Enabled || !Config.Categories[1].Enabled || !Config.Categories[2].Enabled)
                throw new InvalidOperationException("Deactivating didn't restore the games' previous state.");
            Multiworlds.Activate(mw);
            Config.Categories[2].Enabled = true;                                   // turn on a game outside it by hand
            Multiworlds.MappingsEdited();
            if (Multiworlds.Active != null || !Config.Categories[2].Enabled)
                throw new InvalidOperationException("A hand change didn't deactivate the multiworld (or was reverted).");
            Multiworlds.AllOff();
            if (Config.Categories.Any(c => c.Enabled)) throw new InvalidOperationException("All games off failed.");
            Multiworlds.AllOn();
            Multiworlds.Delete(mw);
            // Pausing a mapping must remove it from detection straight away — including a game picked with Switch now.
            if (Watcher.Running.FirstOrDefault(h => h.Category.Id == "3") is { } three) await Switcher.SwitchNowAsync(three);
            Config.Categories[2].Enabled = false;
            Watcher.UpdateMappings(Config.Categories);
            Switcher.ForgetIfInactive(Config.Categories);
            if (Switcher.Current?.Category.Id == "3")
                throw new InvalidOperationException("A paused mapping was still remembered as the current game.");
            if (Watcher.Running.Any(h => h.Category.Id == "3"))
                throw new InvalidOperationException("A paused mapping was still detected.");
            Config.Categories[2].Enabled = true;
            Watcher.UpdateMappings(Config.Categories);
            Watcher.Start(DetectionMode.Focus);
            await Task.Delay(1500);                     // let the 1 s focus check run at least once
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!window.IsVisible) throw new InvalidOperationException("Main window is not visible.");
            _ = Updater.CurrentTag;
            await Task.Delay(500);
        }
        catch (Exception ex) { _selfTestError ??= ex; }
        try { selfTestChild?.Kill(entireProcessTree: true); } catch { }

        if (_selfTestError != null) { LogError("selftest", _selfTestError); SelfTestExit(1); }
        else { Console.WriteLine("SELFTEST OK " + Updater.CurrentTag); SelfTestExit(0); }
    }

    private void SelfTestExit(int code)
    {
        IsExiting = true;
        _tray?.Dispose();
        _tray = null;
        Shutdown(code);
    }

    private async Task InitAsync()
    {
        try
        {
            await Twitch.InitAsync();
            await Switcher.RefreshLiveAsync();
            await Switcher.RefreshStreamStatusAsync();
        }
        catch { /* offline at boot is fine */ }

        Watcher.Start(Config.Mode);
        Updater.Start();

        _validateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromHours(1) };
        _validateTimer.Tick += async (_, _) => await Twitch.ValidateAsync();
        _validateTimer.Start();

        // One tiny GET /streams per minute for the Live / Offline light.
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
        _statusTimer.Tick += async (_, _) =>
        {
            await Switcher.RefreshStreamStatusAsync();
            await Switcher.RefreshLiveAsync();     // picks up category/title changes made on the Twitch dashboard
        };
        _statusTimer.Start();
        Twitch.AuthChanged += () => Dispatcher.InvokeAsync(async () => await Switcher.RefreshStreamStatusAsync());
    }

    public static void SaveConfig()
    {
        try { ConfigStore.Save(Config); } catch { }
    }

    /// <summary>Titles set from the app (Manual page, Apply now). Kept as templates so name pills survive.</summary>
    public static void RememberTitle(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return;
        var list = Config.RecentTitles;
        list.Remove(title);
        list.Insert(0, title);
        if (list.Count > 8) list.RemoveRange(8, list.Count - 8);
        SaveConfig();
    }

    public static void SetAutoSwitch(bool on)
    {
        if (Config.AutoSwitch == on) return;
        Config.AutoSwitch = on;
        SaveConfig();
        _tray?.SetAutoChecked(on);
        Switcher.RaiseChanged();
        if (on) Watcher.Resync();
    }

    public static void SetStartup(bool on)
    {
        Config.StartWithWindows = on;
        SaveConfig();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (on) key.SetValue("AutoSwitcher", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("AutoSwitcher", throwOnMissingValue: false);
        }
        catch { }
    }

    public void ShowMain()
    {
        if (MainWindow is not Window w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;   // nudge to front past focus-stealing rules
        w.Topmost = false;
    }

    /// <summary>Exit and start <paramref name="exePath"/> (used after installing an update).</summary>
    public void RestartInto(string exePath, string args)
    {
        IsExiting = true;
        try { if (Config != null) ConfigStore.Save(Config); } catch { }
        Watcher?.Dispose();
        _tray?.Dispose();
        _tray = null;
        // Free the single-instance lock first, or the new process would just hand off to us and quit.
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, args) { UseShellExecute = false });
        }
        catch { }
        Shutdown();
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        SaveConfig();
        Watcher?.Dispose();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
