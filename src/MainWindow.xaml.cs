using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace AutoSwitcher;

public partial class MainWindow : Window
{
    private readonly MappingsPage _mappings = new();
    private readonly ManualPage _manual = new();
    private readonly AccountPage _account = new();
    private readonly BehaviourPage _behaviour = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.StyleTitleBar(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;

        App.Twitch.AuthChanged += () => Dispatcher.InvokeAsync(UpdateAccountChip);
        App.Switcher.Changed += () => Dispatcher.InvokeAsync(UpdateLiveBadge);
        UpdateAccountChip();
        UpdateLiveBadge();

        NavMappings.IsChecked = true;
        if (!App.Twitch.IsSignedIn) NavAccount.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        Host.Content = sender == NavManual ? _manual
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
