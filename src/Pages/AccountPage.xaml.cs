using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoSwitcher;

public partial class AccountPage : UserControl
{
    private DeviceCode? _device;
    private CancellationTokenSource? _loginCts;
    private DateTime _deviceExpiry;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;

    public AccountPage()
    {
        InitializeComponent();
        ConfigPathRun.Text = ConfigStore.Dir;
        _countdown.Tick += (_, _) => UpdateCountdown();
        App.Twitch.AuthChanged += () => Dispatcher.InvokeAsync(UpdateView);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) { UpdateView(); UpdateUpdates(); } };
        App.Updater.Changed += () => Dispatcher.InvokeAsync(UpdateUpdates);
        UpdateView();
        UpdateUpdates();
    }

    // ------------------------------------------------------------ updates

    private void UpdateUpdates()
    {
        var u = App.Updater;
        string tag = u.LatestTag ?? "";
        string cur = Updater.CurrentTag;
        string title, sub, color, bg;
        Geometry icon = (Geometry)FindResource("IconDownload");
        bool showNow = false, showNotes = false, showCheck = false, showBar = false;

        switch (u.State)
        {
            case UpdateState.Available:
                title = $"Update available · {tag}";
                sub = u.WaitingForOffline && App.Config.AutoDownloadUpdates
                    ? "Will download in the background once you're offline, or click Update now."
                    : "Click Update now to download and install. AutoSwitcher restarts by itself.";
                color = "WarnBrush"; bg = "WarnBgBrush"; showNow = showNotes = true;
                break;
            case UpdateState.Ready:
                title = $"{tag} downloaded · ready to install";
                sub = App.Config.AutoDownloadUpdates && App.Config.InstallUpdatesOnLaunch
                    ? "Installs automatically next time AutoSwitcher starts, or click Update now."
                    : "Click Update now to install. AutoSwitcher restarts by itself.";
                color = "WarnBrush"; bg = "WarnBgBrush"; showNow = showNotes = true;
                break;
            case UpdateState.Downloading:
                title = $"Downloading {tag}… {u.Progress * 100:0}%";
                sub = "Verified against GitHub's SHA-256 checksum when finished.";
                color = "AccentBrush"; bg = "AccentDimBrush"; showBar = true;
                break;
            case UpdateState.Installing:
                title = $"Installing {tag}…";
                sub = "AutoSwitcher will close and reopen in a moment.";
                color = "AccentBrush"; bg = "AccentDimBrush"; icon = (Geometry)FindResource("IconRefresh");
                break;
            case UpdateState.Checking:
                title = "Checking for updates…";
                sub = $"You're on {cur}.";
                color = "AccentBrush"; bg = "AccentDimBrush"; icon = (Geometry)FindResource("IconRefresh");
                break;
            case UpdateState.Error:
                title = "Update failed";
                sub = u.Error ?? "Something went wrong.";
                color = "DangerBrush"; bg = "WarnBgBrush"; showCheck = true;
                break;
            default:
                title = $"You're up to date · {cur}";
                sub = "Your mappings, titles and Twitch login are kept across updates.";
                color = "GoodBrush"; bg = "CardBrush"; icon = (Geometry)FindResource("IconCheck"); showCheck = true;
                break;
        }

        UpdTitle.Text = title;
        UpdSub.Text = sub;
        UpdIcon.Data = icon;
        UpdIcon.Stroke = (Brush)FindResource(color);
        UpdIconBox.Background = u.State is UpdateState.UpToDate or UpdateState.Idle
            ? new SolidColorBrush(Color.FromRgb(0x10, 0x29, 0x1A)) : (Brush)FindResource(bg);
        UpdStatusRow.BorderBrush = (Brush)FindResource(color);
        UpdNowBtn.Visibility = showNow ? Visibility.Visible : Visibility.Collapsed;
        UpdNotesBtn.Visibility = showNotes ? Visibility.Visible : Visibility.Collapsed;
        UpdCheckBtn.Visibility = showCheck ? Visibility.Visible : Visibility.Collapsed;
        UpdBarTrack.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        UpdBarScale.ScaleX = Math.Clamp(u.Progress, 0, 1);

        UpdFooter.Text = u.LastChecked is DateTime t
            ? $"Current version {cur} · last checked {t:h:mm tt}"
            : $"Current version {cur}";

        _loading = true;
        AutoDlToggle.IsChecked = App.Config.AutoDownloadUpdates;
        AutoInstallToggle.IsChecked = App.Config.AutoDownloadUpdates && App.Config.InstallUpdatesOnLaunch;
        AutoInstallToggle.IsEnabled = App.Config.AutoDownloadUpdates;
        AutoInstallRow.Opacity = App.Config.AutoDownloadUpdates ? 1 : 0.45;
        AutoInstallSub.Text = App.Config.AutoDownloadUpdates
            ? "Installs a downloaded update the next time AutoSwitcher starts (including Start with Windows)."
            : "Needs \u201CAutomatically download new versions\u201D turned on.";
        _loading = false;
    }

    private void UpdPrefs_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Config.AutoDownloadUpdates = AutoDlToggle.IsChecked == true;
        if (App.Config.AutoDownloadUpdates)
            App.Config.InstallUpdatesOnLaunch = AutoInstallToggle.IsChecked == true;
        App.SaveConfig();
        UpdateUpdates();
        if (App.Config.AutoDownloadUpdates && App.Updater.State == UpdateState.Available) _ = App.Updater.CheckAsync();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Log.Dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private async void UpdNow_Click(object sender, RoutedEventArgs e) => await App.Updater.UpdateNowAsync();
    private async void UpdCheck_Click(object sender, RoutedEventArgs e) => await App.Updater.CheckAsync(manual: true);
    private void UpdNotes_Click(object sender, RoutedEventArgs e) => App.Updater.OpenReleaseNotes();

    private void UpdateView()
    {
        bool signedIn = App.Twitch.IsSignedIn;
        SignedOutCard.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInCard.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        if (signedIn)
        {
            var t = App.Twitch.Tokens!;
            ProfileName.Text = string.IsNullOrEmpty(t.DisplayName) ? t.Login : t.DisplayName;
            bool hasPic = !string.IsNullOrEmpty(t.ProfileImageUrl);
            Art.SetUrl(ProfileArt, hasPic ? t.ProfileImageUrl : null);
            // No profile picture: show the first letter, like the sidebar chip.
            ProfileLetter.Text = hasPic || ProfileName.Text.Length == 0 ? "" : ProfileName.Text.Substring(0, 1).ToUpperInvariant();
            if (!hasPic) ProfileArt.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A));
            StopDeviceFlow();
        }

        _loading = true;
        StartupToggle.IsChecked = App.Config.StartWithWindows;
        TrayToggle.IsChecked = App.Config.CloseToTray;
        _loading = false;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------ device code login

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ShowLoginError(null);
        ConnectBtn.IsEnabled = false;
        try
        {
            _device = await App.Twitch.StartDeviceFlowAsync();
            CodeChars.ItemsSource = Array.ConvertAll(_device.UserCode.ToCharArray(), ch => ch.ToString());
            _deviceExpiry = DateTime.UtcNow.AddSeconds(_device.ExpiresIn);
            DevicePanel.Visibility = Visibility.Visible;
            ConnectBtn.Visibility = Visibility.Collapsed;
            UpdateCountdown();
            _countdown.Start();
            OpenUrl(_device.VerificationUri);   // the URI already carries the code

            _loginCts = new CancellationTokenSource();
            bool ok = await App.Twitch.CompleteDeviceFlowAsync(_device, _loginCts.Token);
            StopDeviceFlow();
            if (ok)
            {
                await App.Switcher.RefreshLiveAsync();
                UpdateView();
            }
            else ShowLoginError("The code expired. Click Connect to get a new one.");
        }
        catch (OperationCanceledException)
        {
            StopDeviceFlow();
        }
        catch (Exception ex)
        {
            StopDeviceFlow();
            ShowLoginError("Couldn't connect: " + ex.Message +
                           " Check your internet connection and try again.");
        }
    }

    private void StopDeviceFlow()
    {
        _countdown.Stop();
        _loginCts?.Cancel();
        _loginCts = null;
        DevicePanel.Visibility = Visibility.Collapsed;
        ConnectBtn.Visibility = Visibility.Visible;
        ConnectBtn.IsEnabled = true;
    }

    private void UpdateCountdown()
    {
        var left = _deviceExpiry - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        WaitText.Text = $"Waiting for approval · code expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void OpenActivate_Click(object sender, RoutedEventArgs e)
    {
        if (_device != null) OpenUrl(_device.VerificationUri);
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_device == null) return;
        try { Clipboard.SetText(_device.UserCode); } catch { }
    }

    private void CancelLogin_Click(object sender, RoutedEventArgs e) => StopDeviceFlow();

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await App.Twitch.SignOutAsync();
        await App.Switcher.RefreshLiveAsync();
        UpdateView();
        App.Switcher.RaiseChanged();
    }

    private void ShowLoginError(string? text)
    {
        LoginError.Text = text ?? "";
        LoginError.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------ app options

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.SetStartup(StartupToggle.IsChecked == true);
    }

    private void Tray_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Config.CloseToTray = TrayToggle.IsChecked == true;
        App.SaveConfig();
    }
}
