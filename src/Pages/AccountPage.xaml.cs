using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) UpdateView(); };
        UpdateView();
    }

    private void UpdateView()
    {
        bool hasClient = !string.IsNullOrWhiteSpace(App.Config.ClientId);
        bool signedIn = App.Twitch.IsSignedIn;

        SetupCard.Visibility = hasClient ? Visibility.Collapsed : Visibility.Visible;
        SignedOutCard.Visibility = hasClient && !signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedInCard.Visibility = hasClient && signedIn ? Visibility.Visible : Visibility.Collapsed;

        if (signedIn)
        {
            var t = App.Twitch.Tokens!;
            ProfileName.Text = string.IsNullOrEmpty(t.DisplayName) ? t.Login : t.DisplayName;
            Art.SetUrl(ProfileArt, string.IsNullOrEmpty(t.ProfileImageUrl) ? null : t.ProfileImageUrl);
            StopDeviceFlow();
        }

        _loading = true;
        StartupToggle.IsChecked = App.Config.StartWithWindows;
        TrayToggle.IsChecked = App.Config.CloseToTray;
        if (!ClientIdBox.IsKeyboardFocusWithin) ClientIdBox.Text = App.Config.ClientId;
        _loading = false;
    }

    // ------------------------------------------------------------ client id

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e) => OpenUrl("https://dev.twitch.tv/console/apps/create");

    private void SaveClientId_Click(object sender, RoutedEventArgs e) => _ = SetClientIdAsync(ClientIdSetupBox.Text);

    private void SaveClientIdAdvanced_Click(object sender, RoutedEventArgs e) => _ = SetClientIdAsync(ClientIdBox.Text);

    private async Task SetClientIdAsync(string raw)
    {
        string id = raw.Trim();
        if (id == App.Config.ClientId) return;
        if (App.Twitch.IsSignedIn) await App.Twitch.SignOutAsync();
        App.Config.ClientId = id;
        App.Twitch.ClientId = id;
        App.SaveConfig();
        UpdateView();
        App.Switcher.RaiseChanged();
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
                           (ex.Message.Contains("client", StringComparison.OrdinalIgnoreCase)
                               ? " — check the Client ID and that the app's Client Type is Public."
                               : ""));
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
