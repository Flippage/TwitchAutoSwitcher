using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace AutoSwitcher;

public partial class MainWindow : Window
{
    private readonly MappingsPage _mappings = new();
    private readonly TitlesPage _titles = new();
    private readonly ManualPage _manual = new();
    private readonly AccountPage _account = new();
    private readonly BehaviourPage _behaviour = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.StyleTitleBar(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;

        App.Twitch.AuthChanged += () => Dispatcher.InvokeAsync(UpdateAccountChip);
        App.Switcher.Changed += () => Dispatcher.InvokeAsync(() => { UpdateLiveBadge(); UpdateNowPlaying(); });
        UpdateAccountChip();
        UpdateLiveBadge();
        UpdateNowPlaying();
        // Short windows: drop the box art so the sidebar never overlaps the nav.
        SizeChanged += (_, _) => NowArtBox.Visibility = ActualHeight < 790 ? Visibility.Collapsed : Visibility.Visible;

        NavMappings.IsChecked = true;
        if (!App.Twitch.IsSignedIn) NavAccount.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        Host.Content = sender == NavTitles ? _titles
                     : sender == NavManual ? _manual
                     : sender == NavAccount ? _account
                     : sender == NavBehaviour ? _behaviour
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

    public void GoToAccount() => NavAccount.IsChecked = true;
    public void GoToTitles() => NavTitles.IsChecked = true;

    private void UpdateAccountChip()
    {
        var t = App.Twitch.Tokens;
        if (t == null)
        {
            AccountName.Text = "Not connected";
            AccountStatus.Text = "Settings → Account";
            AccountStatus.Foreground = (System.Windows.Media.Brush)FindResource("SubBrush");
            AvatarLetter.Text = "?";
            Art.SetUrl(Avatar, null);
            Avatar.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A));
            return;
        }
        string name = string.IsNullOrEmpty(t.DisplayName) ? t.Login : t.DisplayName;
        AccountName.Text = string.IsNullOrEmpty(name) ? "Connected" : name;
        AccountStatus.Text = "Connected";
        AccountStatus.Foreground = (System.Windows.Media.Brush)FindResource("GoodBrush");
        AvatarLetter.Text = string.IsNullOrEmpty(name) ? "" : name.Substring(0, 1).ToUpperInvariant();
        if (!string.IsNullOrEmpty(t.ProfileImageUrl))
        {
            AvatarLetter.Text = "";
            Art.SetUrl(Avatar, t.ProfileImageUrl);
        }
    }

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
    private static readonly System.Windows.Media.Brush OnAirBrush = Frozen(0x4A, 0xDE, 0x80);
    private static readonly System.Windows.Media.Brush OffAirBrush = Frozen(0xEF, 0x44, 0x44);

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

    private bool _syncingSide;

    private void UpdateNowPlaying()
    {
        var cfg = App.Config;
        var hit = App.Switcher.Current;

        _syncingSide = true;
        SideAutoToggle.IsChecked = cfg.AutoSwitch;
        SideTitleToggle.IsChecked = cfg.UpdateTitle;
        _syncingSide = false;

        NowLabel.Text = !cfg.AutoSwitch ? "PAUSED · MANUAL MODE"
                      : cfg.Mode == DetectionMode.Focus ? "NOW PLAYING · FOCUS" : "NOW PLAYING · LAUNCH";
        NowLabel.Foreground = (System.Windows.Media.Brush)FindResource(cfg.AutoSwitch ? "SubBrush" : "WarnBrush");

        if (hit != null)
        {
            _liveArtId = null;
            NowGame.Text = hit.Category.Name;
            NowExe.Text = hit.Exe.FullName;
            NowExe.Visibility = string.Equals(hit.Exe.FullName, hit.Category.Name, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed : Visibility.Visible;
            Art.SetUrl(NowArt, hit.Category.BoxArtUrl);
            NowArtIdle.Visibility = Visibility.Collapsed;
            NowCard.ToolTip = $"{hit.Exe.FullName} ({hit.Exe.FileName}) → {hit.Category.Name}";
        }
        else if (App.Switcher.Live is { } live && !string.IsNullOrEmpty(live.GameName))
        {
            // Nothing mapped is running: show what's actually set on the channel.
            NowLabel.Text = cfg.AutoSwitch ? "ON TWITCH NOW" : "PAUSED · ON TWITCH NOW";
            NowGame.Text = live.GameName;
            NowExe.Text = "No mapped game detected";
            NowExe.Visibility = Visibility.Visible;
            NowArtIdle.Visibility = Visibility.Collapsed;
            NowCard.ToolTip = "Current Twitch category. Launch or focus a mapped game to switch.";
            _ = ShowLiveArtAsync(live.GameId);
        }
        else
        {
            _liveArtId = null;
            NowGame.Text = cfg.Categories.Count == 0 ? "No games mapped yet" : "Waiting for a game…";
            NowExe.Text = cfg.Categories.Count == 0 ? "Add one on Mappings" : "Launch or focus a mapped game";
            NowExe.Visibility = Visibility.Visible;
            Art.SetUrl(NowArt, null);
            NowArt.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x1F));
            NowArtIdle.Visibility = Visibility.Visible;
            NowCard.ToolTip = null;
        }
        NowGame.Foreground = (System.Windows.Media.Brush)FindResource(
            hit != null || !string.IsNullOrEmpty(App.Switcher.Live?.GameName) ? "TextBrush" : "Text2Brush");

        string? err = App.Switcher.LastError;
        NowError.Text = err ?? "";
        NowError.Visibility = string.IsNullOrEmpty(err) ? Visibility.Collapsed : Visibility.Visible;
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
        if (_liveArtId == gameId && App.Switcher.Current == null)
        {
            if (url.Length > 0) Art.SetUrl(NowArt, url);
            NowArtIdle.Visibility = url.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
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
