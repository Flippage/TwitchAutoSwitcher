using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AutoSwitcher;

public partial class MainWindow : Window
{
    private readonly MappingsPage _mappings = new();
    private readonly TitlesPage _titles = new();
    private readonly ManualPage _manual = new();
    private readonly BehaviourPage _behaviour = new();
    private readonly AccountPage _settings = new();

    private readonly DispatcherTimer _countdown = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
    private GameHit? _detected;
    private bool _detectedIsPending;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.StyleTitleBar(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;

        App.Twitch.AuthChanged += () => Dispatcher.InvokeAsync(UpdateAccountChip);
        App.Switcher.Changed += () => Dispatcher.InvokeAsync(() => { UpdateLiveBadge(); UpdateNowPlaying(); });
        App.Watcher.PendingChanged += () => Dispatcher.InvokeAsync(UpdateNowPlaying);
        App.Updater.Changed += () => Dispatcher.InvokeAsync(UpdateUpdateBadge);
        App.Updater.Notice += downloaded => Dispatcher.InvokeAsync(() => QueueUpdateToast(downloaded));
        _countdown.Tick += (_, _) => UpdateCountdown();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) ShowQueuedToast(); };
        SizeChanged += (_, _) => UpdateArtVisibility();

        UpdateAccountChip();
        UpdateLiveBadge();
        UpdateNowPlaying();
        UpdateUpdateBadge();

        NavMappings.IsChecked = true;
        if (!App.Twitch.IsSignedIn) NavSettings.IsChecked = true;
    }

    // ------------------------------------------------------------------ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        Host.Content = sender == NavTitles ? _titles
                     : sender == NavManual ? _manual
                     : sender == NavBehaviour ? _behaviour
                     : sender == NavSettings ? _settings
                     : _mappings;
    }

    public void OpenEditor(CategoryMapping? category)
    {
        NavMappings.IsChecked = true;
        Host.Content = new EditCategoryPage(category);
    }

    public void CloseEditor()
    {
        _mappings.Refresh();
        Host.Content = _mappings;
    }

    public void GoToSettings() => NavSettings.IsChecked = true;
    public void GoToTitles() => NavTitles.IsChecked = true;

    // ------------------------------------------------------------------ account + live

    private void UpdateAccountChip()
    {
        var t = App.Twitch.Tokens;
        if (t == null)
        {
            AccountName.Text = "Not connected";
            AccountStatus.Text = "Open Settings to connect";
            AccountStatus.Foreground = (Brush)FindResource("SubBrush");
            AvatarLetter.Text = "?";
            Art.SetUrl(Avatar, null);
            Avatar.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
            return;
        }
        string name = string.IsNullOrEmpty(t.DisplayName) ? t.Login : t.DisplayName;
        AccountName.Text = string.IsNullOrEmpty(name) ? "Connected" : name;
        AccountStatus.Text = "Connected";
        AccountStatus.Foreground = (Brush)FindResource("GoodBrush");
        AvatarLetter.Text = string.IsNullOrEmpty(name) ? "" : name.Substring(0, 1).ToUpperInvariant();
        if (!string.IsNullOrEmpty(t.ProfileImageUrl))
        {
            AvatarLetter.Text = "";
            Art.SetUrl(Avatar, t.ProfileImageUrl);
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
    private static readonly Brush OnAirBrush = Frozen(0x4A, 0xDE, 0x80);
    private static readonly Brush OffAirBrush = Frozen(0xEF, 0x44, 0x44);
    private static readonly Brush DetAmberBg = Frozen(0x1F, 0x1A, 0x10);
    private static readonly Brush DetCyanBg = Frozen(0x0F, 0x22, 0x26);

    private void UpdateLiveBadge()
    {
        bool? live = App.Switcher.IsLive;
        if (live == null) { LiveBadge.Visibility = Visibility.Collapsed; return; }
        LiveBadge.Visibility = Visibility.Visible;
        var brush = live == true ? OnAirBrush : OffAirBrush;
        LiveDot.Fill = brush;
        LiveGlow.Fill = brush;
        LiveText.Text = live == true ? "Live" : "Offline";
        LiveText.Foreground = brush;
        ViewerText.Text = live == true ? $"{App.Switcher.Viewers:N0} viewers" : "";
    }

    // ------------------------------------------------------------------ On Stream panel

    private bool _syncingSide;

    /// <summary>
    /// Big card = what's on Twitch right now. A second box appears only when a detected mapped game
    /// differs from it: "Switching in Ns" (auto-switch, focus delay running) or "Mapped game detected".
    /// </summary>
    private void UpdateNowPlaying()
    {
        var cfg = App.Config;
        var hit = App.Switcher.Current;
        var pending = App.Watcher.Pending;
        var live = App.Switcher.Live;
        bool hasLive = live != null && !string.IsNullOrEmpty(live.GameName);

        _syncingSide = true;
        SideAutoToggle.IsChecked = cfg.AutoSwitch;
        SideTitleToggle.IsChecked = cfg.UpdateTitle;
        _syncingSide = false;
        AutoOffTag.Visibility = cfg.AutoSwitch ? Visibility.Collapsed : Visibility.Visible;

        _detected = null;
        _detectedIsPending = false;

        if (hasLive)
        {
            NowLabel.Text = "ON STREAM";
            NowGame.Text = live!.GameName;
            NowGame.Foreground = (Brush)FindResource("TextBrush");
            _ = ShowLiveArtAsync(live.GameId);

            bool hitMatches = hit != null && hit.Category.Id == live.GameId;
            if (pending != null && pending.Category.Id != live.GameId) { _detected = pending; _detectedIsPending = cfg.AutoSwitch; }
            else if (hit != null && !hitMatches) _detected = hit;

            if (hitMatches && _detected == null)
            {
                NowExe.Text = "✓ " + hit!.Exe.FullName + " · detected";
                NowExe.Foreground = OnAirBrush;
                NowCard.ToolTip = $"{hit.Exe.FullName} ({hit.Exe.FileName}) → {hit.Category.Name}";
            }
            else
            {
                NowExe.Text = _detected == null ? "No mapped game detected" : "Set on Twitch";
                NowExe.Foreground = (Brush)FindResource("SubBrush");
                NowCard.ToolTip = "Your current Twitch category";
            }
            NowExe.Visibility = Visibility.Visible;
        }
        else if (hit != null)
        {
            // Not connected (or no category on Twitch yet): show the detected game.
            _liveArtId = null;
            NowLabel.Text = "DETECTED";
            NowGame.Text = hit.Category.Name;
            NowGame.Foreground = (Brush)FindResource("TextBrush");
            NowExe.Text = hit.Exe.FullName;
            NowExe.Foreground = (Brush)FindResource("SubBrush");
            NowExe.Visibility = Visibility.Visible;
            Art.SetUrl(NowArt, hit.Category.BoxArtUrl);
            NowArtIdle.Visibility = Visibility.Collapsed;
            NowCard.ToolTip = $"{hit.Exe.FullName} ({hit.Exe.FileName}) → {hit.Category.Name}";
        }
        else
        {
            _liveArtId = null;
            NowLabel.Text = "ON STREAM";
            NowGame.Text = !App.Twitch.IsSignedIn ? "Not connected"
                         : cfg.Categories.Count == 0 ? "No games mapped yet" : "Waiting for a game…";
            NowGame.Foreground = (Brush)FindResource("Text2Brush");
            NowExe.Text = !App.Twitch.IsSignedIn ? "Connect Twitch in Settings"
                        : cfg.Categories.Count == 0 ? "Add one on Mappings" : "Launch or focus a mapped game";
            NowExe.Foreground = (Brush)FindResource("SubBrush");
            NowExe.Visibility = Visibility.Visible;
            Art.SetUrl(NowArt, null);
            NowArt.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            NowArtIdle.Visibility = Visibility.Visible;
            NowCard.ToolTip = null;
        }

        // Detected box
        if (_detected != null)
        {
            DetBox.Visibility = Visibility.Visible;
            DetName.Text = _detected.Category.Name;
            DetExe.Text = _detected.Exe.FullName;
            Art.SetUrl(DetArt, _detected.Category.BoxArtUrl);
            if (_detectedIsPending)
            {
                DetBox.Background = DetCyanBg;
                DetBox.BorderBrush = (Brush)FindResource("AccentLineBrush");
                DetLabel.Foreground = (Brush)FindResource("AccentBrush");
                DetBtn.Foreground = (Brush)FindResource("AccentBrush");
                DetBtn.BorderBrush = (Brush)FindResource("AccentLineBrush");
                DetBarTrack.Visibility = Visibility.Visible;
                UpdateCountdown();
                _countdown.Start();
            }
            else
            {
                DetBox.Background = DetAmberBg;
                DetBox.BorderBrush = (Brush)FindResource("WarnLineBrush");
                DetLabel.Foreground = (Brush)FindResource("WarnBrush");
                DetLabel.Text = "MAPPED GAME DETECTED";
                DetBtn.Foreground = (Brush)FindResource("WarnTextBrush");
                DetBtn.BorderBrush = (Brush)FindResource("WarnLineBrush");
                DetBarTrack.Visibility = Visibility.Collapsed;
                _countdown.Stop();
            }
        }
        else
        {
            DetBox.Visibility = Visibility.Collapsed;
            _countdown.Stop();
        }

        string? err = App.Switcher.LastError;
        NowError.Text = err ?? "";
        NowError.Visibility = string.IsNullOrEmpty(err) ? Visibility.Collapsed : Visibility.Visible;
        UpdateArtVisibility();
    }

    /// <summary>Only ticks while a switch is pending (100 ms), so it costs nothing otherwise.</summary>
    private void UpdateCountdown()
    {
        if (!_detectedIsPending || App.Watcher.Pending == null) { _countdown.Stop(); return; }
        double total = Math.Max(0.1, App.Watcher.FocusDelay.TotalSeconds);
        double elapsed = (DateTime.UtcNow - App.Watcher.PendingSinceUtc).TotalSeconds;
        double remaining = Math.Max(0, total - elapsed);
        DetLabel.Text = $"SWITCHING IN {Math.Ceiling(remaining):0}s";
        DetBarScale.ScaleX = Math.Clamp(elapsed / total, 0, 1);
    }

    /// <summary>The box art hides on windows too short to fit everything (more room needed with the detected box).</summary>
    private void UpdateArtVisibility()
    {
        double needed = DetBox.Visibility == Visibility.Visible ? 890 : 790;
        NowArtBox.Visibility = ActualHeight > 0 && ActualHeight < needed ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void SwitchNow_Click(object sender, RoutedEventArgs e)
    {
        var target = _detected;
        if (target == null) return;
        if (App.Watcher.Pending != null && ReferenceEquals(App.Watcher.Pending, target))
        {
            App.Watcher.ActivatePendingNow();          // auto-switch on: the normal switch path applies it
            if (App.Config.AutoSwitch) return;
        }
        DetBtn.IsEnabled = false;
        await App.Switcher.SwitchNowAsync(target);    // auto-switch off: apply this game anyway
        DetBtn.IsEnabled = true;
    }

    private string? _liveArtId;
    private readonly System.Collections.Generic.Dictionary<string, string> _artById = new();

    /// <summary>Box art for the channel's current category: from a mapping if we have it, else one Helix call (cached).</summary>
    private async System.Threading.Tasks.Task ShowLiveArtAsync(string gameId)
    {
        if (string.IsNullOrEmpty(gameId) || _liveArtId == gameId) return;
        _liveArtId = gameId;
        if (!_artById.TryGetValue(gameId, out var url))
        {
            url = App.Config.Categories.Find(c => c.Id == gameId)?.BoxArtUrl ?? "";
            if (url.Length == 0)
            {
                try { url = (await App.Twitch.GetGameAsync(gameId))?.BoxArtUrl ?? ""; } catch { url = ""; }
            }
            _artById[gameId] = url;
        }
        if (_liveArtId != gameId) return;
        if (url.Length > 0) Art.SetUrl(NowArt, url);
        else
        {
            Art.SetUrl(NowArt, null);
            NowArt.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        }
        NowArtIdle.Visibility = url.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SideAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingSide) App.SetAutoSwitch(SideAutoToggle.IsChecked == true);
    }

    private void SideTitle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSide) return;
        App.Config.UpdateTitle = SideTitleToggle.IsChecked == true;
        App.SaveConfig();
        App.Switcher.RaiseChanged();   // keep the Stream Titles toggle in sync
    }

    // ------------------------------------------------------------------ updates

    private void UpdateUpdateBadge()
        => UpdateBadge.Visibility = App.Updater.HasUpdate ? Visibility.Visible : Visibility.Collapsed;

    private bool? _queuedToast;          // null = none; true = downloaded; false = available
    private AnimationClock? _toastClock;

    private void QueueUpdateToast(bool downloaded)
    {
        _queuedToast = downloaded;
        if (IsVisible && WindowState != WindowState.Minimized) ShowQueuedToast();
    }

    private void ShowQueuedToast()
    {
        if (_queuedToast is not bool downloaded) return;
        _queuedToast = null;
        string tag = App.Updater.LatestTag ?? "A new version";
        ToastText.Text = downloaded
            ? $"AutoSwitcher {tag} is downloaded and ready to install."
            : $"AutoSwitcher {tag} is available to download and install.";

        UpdateToast.Visibility = Visibility.Visible;
        UpdateToast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

        var drain = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(10));
        _toastClock = drain.CreateClock();
        _toastClock.Completed += (_, _) => HideToast();
        ToastBarScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _toastClock);
    }

    private void HideToast()
    {
        if (UpdateToast.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            UpdateToast.Visibility = Visibility.Collapsed;
            UpdateToast.BeginAnimation(OpacityProperty, null);
        };
        UpdateToast.BeginAnimation(OpacityProperty, fade);
        _toastClock?.Controller?.Stop();
        _toastClock = null;
    }

    private void Toast_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _toastClock?.Controller?.Pause();
    private void Toast_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _toastClock?.Controller?.Resume();
    private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();
    private void ToastNotes_Click(object sender, RoutedEventArgs e) => App.Updater.OpenReleaseNotes();

    private void ToastUpdate_Click(object sender, RoutedEventArgs e)
    {
        HideToast();
        GoToSettings();                     // show progress there
        _ = App.Updater.UpdateNowAsync();
    }

    // ------------------------------------------------------------------ window

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.IsExiting) return;
        if (App.Config.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            Native.TrimWorkingSet();
        }
        else
        {
            e.Cancel = true;
            Dispatcher.InvokeAsync(App.Current.ExitApp);   // don't shut down from inside Closing
        }
    }
}
