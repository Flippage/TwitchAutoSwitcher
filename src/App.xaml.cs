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
        Config = ConfigStore.Load();

        // "Update automatically on next launch": swap in the downloaded version before any UI appears.
        if (!_selfTest && Updater.TryInstallOnLaunch(Config))
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
        if (_selfTest || !e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) window.Show();

        ThreadPool.RegisterWaitForSingleObject(_showSignal!,
            (_, _) => Dispatcher.InvokeAsync(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);

        if (_selfTest) { _ = RunSelfTestAsync(window); return; }
        _ = InitAsync();
    }

    // ------------------------------------------------------------------ diagnostics

    private static bool _selfTest;
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
            window.SelfTestVisitPages();
            await window.SelfTestFlipStackAsync();
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
        if (on) Switcher.Reapply();
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
