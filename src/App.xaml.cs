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
    public static bool IsExiting { get; private set; }

    private static TrayIcon? _tray;
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private DispatcherTimer? _validateTimer;

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
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;   // never crash the tray app over a UI glitch
        };

        Config = ConfigStore.Load();
        Twitch = new TwitchService(Config.ClientId);
        Switcher = new Switcher(Config, Twitch);
        Watcher = new GameWatcher { FocusDelay = TimeSpan.FromSeconds(Config.FocusDelaySeconds) };
        Watcher.UpdateMappings(Config.Categories);
        Watcher.Activated += Switcher.OnActivated;
        Watcher.Exited += Switcher.OnExited;

        _tray = new TrayIcon();
        Switcher.Toast += (title, text) => _tray?.ShowToast(title, text);

        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) window.Show();

        ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.InvokeAsync(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await Twitch.InitAsync();
            await Switcher.RefreshLiveAsync();
        }
        catch { /* offline at boot is fine */ }

        Watcher.Start(Config.Mode);

        _validateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromHours(1) };
        _validateTimer.Tick += async (_, _) => await Twitch.ValidateAsync();
        _validateTimer.Start();
    }

    public static void SaveConfig()
    {
        try { ConfigStore.Save(Config); } catch { }
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
