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
    private readonly System.Collections.Generic.List<GameHit> _stack = new();
    private int _stackIndex;
    private GameHit? _lastPendingSeen;
    private bool _fading;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.StyleTitleBar(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;

        App.Twitch.AuthChanged += () => Dispatcher.InvokeAsync(UpdateAccountChip);
        App.Switcher.Changed += () => Dispatcher.InvokeAsync(() => { UpdateLiveBadge(); UpdateNowPlaying(); });
        App.Watcher.PendingChanged += () => Dispatcher.InvokeAsync(UpdateNowPlaying);
        App.Watcher.RunningChanged += () => Dispatcher.InvokeAsync(UpdateNowPlaying);
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
    private static readonly Brush DetAmberBack1 = Frozen(0x19, 0x15, 0x0D);
    private static readonly Brush DetAmberBack2 = Frozen(0x14, 0x11, 0x0B);
    private static readonly Brush DetAmberBackLine = Frozen(0x3D, 0x2F, 0x12);
    private static readonly Brush DetCyanBack1 = Frozen(0x0D, 0x1C, 0x1F);
    private static readonly Brush DetCyanBack2 = Frozen(0x0B, 0x17, 0x19);
    private static readonly Brush DetCyanBackLine = Frozen(0x18, 0x30, 0x2F);

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

    private static bool Same(GameHit? a, GameHit? b) =>
        a != null && b != null && a.Pid == b.Pid && ReferenceEquals(a.Exe, b.Exe);

    /// <summary>
    /// Big card = what's on Twitch right now. Underneath, a stack of running mapped games that aren't on stream:
    /// the pending switch first (cyan, "Switching in Ns"), then the current game, then the rest (newest first).
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

        // ---- big card
        if (hasLive)
        {
            NowLabel.Text = "ON STREAM";
            FitText(NowGame, live!.GameName, 160, new[] { 14.0, 13.0, 12.0 }, 2);
            NowGame.Foreground = (Brush)FindResource("TextBrush");
            _ = ShowLiveArtAsync(live.GameId);
            bool hitMatches = hit != null && hit.Category.Id == live.GameId;
            if (hitMatches)
            {
                NowExe.Text = "✓ " + hit!.Exe.FullName + " · detected";
                NowExe.Foreground = OnAirBrush;
            }
            else
            {
                NowExe.Text = App.Watcher.Running.Count == 0 ? "No mapped game detected" : "Set on Twitch";
                NowExe.Foreground = (Brush)FindResource("SubBrush");
            }
            NowCard.ToolTip = live.GameName;
        }
        else if (hit != null)
        {
            // Not connected (or no category on Twitch yet): show the detected game.
            _liveArtId = null;
            NowLabel.Text = "DETECTED";
            FitText(NowGame, hit.Category.Name, 160, new[] { 14.0, 13.0, 12.0 }, 2);
            NowGame.Foreground = (Brush)FindResource("TextBrush");
            NowExe.Text = hit.Exe.FullName;
            NowExe.Foreground = (Brush)FindResource("SubBrush");
            Art.SetUrl(NowArt, hit.Category.BoxArtUrl);
            NowArtIdle.Visibility = Visibility.Collapsed;
            NowCard.ToolTip = hit.Category.Name;
        }
        else
        {
            _liveArtId = null;
            NowLabel.Text = "ON STREAM";
            string t = !App.Twitch.IsSignedIn ? "Not connected"
                     : cfg.Categories.Count == 0 ? "No games mapped yet" : "Waiting for a game…";
            FitText(NowGame, t, 160, new[] { 14.0 }, 2);
            NowGame.Foreground = (Brush)FindResource("Text2Brush");
            NowExe.Text = !App.Twitch.IsSignedIn ? "Connect Twitch in Settings"
                        : cfg.Categories.Count == 0 ? "Add one on Mappings" : "Launch or focus a mapped game";
            NowExe.Foreground = (Brush)FindResource("SubBrush");
            Art.SetUrl(NowArt, null);
            NowArt.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            NowArtIdle.Visibility = Visibility.Visible;
            NowCard.ToolTip = null;
        }
        NowExe.Visibility = Visibility.Visible;

        // ---- stack of running games not on stream
        bool OnStream(GameHit g) => hasLive ? g.Category.Id == live!.GameId : Same(g, hit);
        var previous = _stack.Count > 0 && _stackIndex < _stack.Count ? _stack[_stackIndex] : null;
        _stack.Clear();
        if (pending != null && !OnStream(pending)) _stack.Add(pending);
        if (hit != null && !OnStream(hit) && !_stack.Exists(g => Same(g, hit))) _stack.Add(hit);
        foreach (var r in App.Watcher.Running)
            if (!OnStream(r) && !_stack.Exists(g => Same(g, r))) _stack.Add(r);

        // Keep the user's place; jump to a new pending switch when one starts.
        bool newPending = pending != null && !Same(pending, _lastPendingSeen);
        _lastPendingSeen = pending;
        int keep = newPending ? 0 : _stack.FindIndex(g => Same(g, previous));
        _stackIndex = keep >= 0 ? keep : 0;

        ShowStackCard();
        string? err = App.Switcher.LastError;
        NowError.Text = err ?? "";
        NowError.Visibility = string.IsNullOrEmpty(err) ? Visibility.Collapsed : Visibility.Visible;
        UpdateArtVisibility();
    }

    private void ShowStackCard()
    {
        int n = _stack.Count;
        if (n == 0)
        {
            DetStack.Visibility = Visibility.Collapsed;
            _countdown.Stop();
            return;
        }
        DetStack.Visibility = Visibility.Visible;
        var g = _stack[Math.Clamp(_stackIndex, 0, n - 1)];
        bool isPending = App.Config.AutoSwitch && Same(g, App.Watcher.Pending);

        FitText(DetName, g.Category.Name, 107, new[] { 11.5, 10.5, 9.5 }, 2);
        DetName.ToolTip = g.Category.Name;
        DetExe.Text = g.Exe.FullName;
        Art.SetUrl(DetArt, g.Category.BoxArtUrl);

        bool multi = n > 1;
        DetCounter.Text = $"{_stackIndex + 1}/{n}";
        DetCounter.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        DetPrev.Visibility = DetNext.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        DetBack1.Visibility = n >= 2 ? Visibility.Visible : Visibility.Collapsed;
        DetBack2.Visibility = n >= 3 ? Visibility.Visible : Visibility.Collapsed;

        Brush line = (Brush)FindResource(isPending ? "AccentLineBrush" : "WarnLineBrush");
        Brush fg = (Brush)FindResource(isPending ? "AccentBrush" : "WarnBrush");
        Brush btnFg = (Brush)FindResource(isPending ? "AccentBrush" : "WarnTextBrush");
        DetBox.Background = isPending ? DetCyanBg : DetAmberBg;
        DetBox.BorderBrush = line;
        DetBack1.Background = isPending ? DetCyanBack1 : DetAmberBack1;
        DetBack2.Background = isPending ? DetCyanBack2 : DetAmberBack2;
        DetBack1.BorderBrush = DetBack2.BorderBrush = isPending ? DetCyanBackLine : DetAmberBackLine;
        DetLabel.Foreground = fg;
        foreach (var b in new[] { DetBtn, DetPrev, DetNext }) { b.Foreground = btnFg; b.BorderBrush = line; }

        if (isPending)
        {
            DetBarTrack.Visibility = Visibility.Visible;
            UpdateCountdown();
            _countdown.Start();
        }
        else
        {
            DetLabel.Text = "MAPPED GAME DETECTED";
            DetBarTrack.Visibility = Visibility.Collapsed;
            _countdown.Stop();
        }
    }

    /// <summary>‹ › : fade out (150 ms) → next game → fade in (150 ms).</summary>
    private void FlipStack(int delta)
    {
        if (_fading || _stack.Count < 2) return;
        _fading = true;
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() };
        fadeOut.Completed += (_, _) =>
        {
            _stackIndex = (_stackIndex + delta + _stack.Count) % _stack.Count;
            ShowStackCard();
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() };
            fadeIn.Completed += (_, _) => _fading = false;
            DetContent.BeginAnimation(OpacityProperty, fadeIn);
        };
        DetContent.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void DetPrev_Click(object sender, RoutedEventArgs e) => FlipStack(-1);
    private void DetNext_Click(object sender, RoutedEventArgs e) => FlipStack(+1);

    /// <summary>
    /// Largest font size (from <paramref name="sizes"/>, biggest first) at which the text fits in
    /// <paramref name="maxLines"/> lines; at the smallest size anything left over is trimmed with "…".
    /// </summary>
    private static void FitText(System.Windows.Controls.TextBlock tb, string text, double width, double[] sizes, int maxLines)
    {
        tb.Text = text;
        double w = tb.ActualWidth > 20 ? tb.ActualWidth : width;
        double dpi = 1.0;
        try { dpi = VisualTreeHelper.GetDpi(tb).PixelsPerDip; } catch { }
        var face = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        double chosen = sizes[^1], lineH = 0;
        foreach (double size in sizes)
        {
            var one = new FormattedText("Ag", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.White, dpi);
            var full = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.White, dpi)
            { MaxTextWidth = Math.Max(10, w) };
            lineH = one.Height;
            chosen = size;
            if (full.Height <= lineH * maxLines + 0.5) break;
        }
        tb.FontSize = chosen;
        tb.MaxHeight = lineH * maxLines + 1;
    }

    /// <summary>Only ticks while a switch is pending (100 ms), so it costs nothing otherwise.</summary>
    private void UpdateCountdown()
    {
        var pending = App.Watcher.Pending;
        bool showing = _stack.Count > 0 && _stackIndex < _stack.Count && Same(_stack[_stackIndex], pending);
        if (!showing || !App.Config.AutoSwitch) { _countdown.Stop(); return; }
        double total = Math.Max(0.1, App.Watcher.FocusDelay.TotalSeconds);
        double elapsed = (DateTime.UtcNow - App.Watcher.PendingSinceUtc).TotalSeconds;
        double remaining = Math.Max(0, total - elapsed);
        DetLabel.Text = $"SWITCHING IN {Math.Ceiling(remaining):0}s";
        DetBarScale.ScaleX = Math.Clamp(elapsed / total, 0, 1);
    }

    /// <summary>The box art hides on windows too short to fit everything (more room needed with the stack).</summary>
    private void UpdateArtVisibility()
    {
        double needed = DetStack.Visibility == Visibility.Visible ? 890 : 790;
        NowArtBox.Visibility = ActualHeight > 0 && ActualHeight < needed ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void SwitchNow_Click(object sender, RoutedEventArgs e)
    {
        if (_stack.Count == 0) return;
        var target = _stack[Math.Clamp(_stackIndex, 0, _stack.Count - 1)];
        Log.Info("ui", $"Switch now: {target.Exe.FileName} → {target.Category.Name}");
        if (Same(App.Watcher.Pending, target))
        {
            App.Watcher.ActivatePendingNow();          // auto-switch on: the normal switch path applies it
            if (App.Config.AutoSwitch) return;
        }
        DetBtn.IsEnabled = false;
        await App.Switcher.SwitchNowAsync(target);    // anything else: apply this game now
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

    /// <summary>Used by --selftest: open every page and the transient UI so their construction/layout runs.</summary>
    public void SelfTestVisitPages()
    {
        foreach (var nav in new[] { NavTitles, NavManual, NavBehaviour, NavSettings, NavMappings })
        {
            nav.IsChecked = true;
            UpdateLayout();
        }
        OpenEditor(null);
        UpdateLayout();
        CloseEditor();
        QueueUpdateToast(true);
        UpdateLayout();
        HideToast();
        UpdateNowPlaying();
        UpdateLayout();
    }

    /// <summary>Used by --selftest: flip through the detected-games stack.</summary>
    public async System.Threading.Tasks.Task SelfTestFlipStackAsync()
    {
        UpdateNowPlaying();
        if (_stack.Count < 2) throw new InvalidOperationException($"Self-test expected 2+ running games in the stack, found {_stack.Count}.");
        FlipStack(+1);
        await System.Threading.Tasks.Task.Delay(500);
        FlipStack(-1);
        await System.Threading.Tasks.Task.Delay(500);
        UpdateLayout();
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
