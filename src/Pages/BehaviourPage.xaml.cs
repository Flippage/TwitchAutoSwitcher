using System;
using System.Windows;
using System.Windows.Controls;

namespace AutoSwitcher;

public partial class BehaviourPage : UserControl
{
    private bool _loading;

    public BehaviourPage()
    {
        InitializeComponent();
        FallbackPicker.SelectionChanged += c =>
        {
            if (c == null) return;
            App.Config.FallbackCategory = c;
            App.SaveConfig();
        };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Load(); };
        Load();
    }

    private void Load()
    {
        var cfg = App.Config;
        _loading = true;
        ModeFocus.IsChecked = cfg.Mode == DetectionMode.Focus;
        ModeLaunch.IsChecked = cfg.Mode == DetectionMode.Launch;
        TitleToggle.IsChecked = cfg.UpdateTitle;
        ToastToggle.IsChecked = cfg.Toasts;
        FullscreenToggle.IsChecked = cfg.SuppressToastsFullscreen;
        FallbackToggle.IsChecked = cfg.FallbackEnabled;
        FallbackPicker.SetSelected(string.IsNullOrEmpty(cfg.FallbackCategory.Id) ? null : cfg.FallbackCategory);
        _loading = false;
        UpdateDelay();
        UpdateFallbackDelay();
        UpdateVisibility();
        _ = FillMissingArtAsync();
    }

    private async System.Threading.Tasks.Task FillMissingArtAsync()
    {
        var f = App.Config.FallbackCategory;
        if (string.IsNullOrEmpty(f.Id) || !string.IsNullOrEmpty(f.BoxArtUrl) || !App.Twitch.IsSignedIn) return;
        try
        {
            var game = await App.Twitch.GetGameAsync(f.Id);
            if (game == null || string.IsNullOrEmpty(game.BoxArtUrl)) return;
            f.BoxArtUrl = game.BoxArtUrl;
            App.SaveConfig();
            FallbackPicker.SetSelected(f);
        }
        catch { }
    }

    private void UpdateVisibility()
    {
        DelayCard.Visibility = App.Config.Mode == DetectionMode.Focus ? Visibility.Visible : Visibility.Collapsed;
        FallbackOptions.Visibility = App.Config.FallbackEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDelay()
    {
        DelayText.Text = $"{App.Config.FocusDelaySeconds} s";
        App.Watcher.FocusDelay = TimeSpan.FromSeconds(App.Config.FocusDelaySeconds);
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var mode = ModeFocus.IsChecked == true ? DetectionMode.Focus : DetectionMode.Launch;
        if (mode == App.Config.Mode) return;
        App.Config.Mode = mode;
        App.SaveConfig();
        App.Watcher.Start(mode);
        App.Switcher.RaiseChanged();
        UpdateVisibility();
    }

    private void ChangeDelay(int delta)
    {
        App.Config.FocusDelaySeconds = Math.Clamp(App.Config.FocusDelaySeconds + delta, 0, 120);
        App.SaveConfig();
        UpdateDelay();
    }

    private void UpdateFallbackDelay() => FallbackDelayText.Text = $"{App.Config.FallbackDelaySeconds} s";

    private void ChangeFallbackDelay(int delta)
    {
        App.Config.FallbackDelaySeconds = Math.Clamp(App.Config.FallbackDelaySeconds + delta, 0, 600);
        App.SaveConfig();
        UpdateFallbackDelay();
    }

    private void FallbackDelayDown_Click(object sender, RoutedEventArgs e) => ChangeFallbackDelay(-5);
    private void FallbackDelayUp_Click(object sender, RoutedEventArgs e) => ChangeFallbackDelay(+5);

    private void DelayDown_Click(object sender, RoutedEventArgs e) => ChangeDelay(-1);
    private void DelayUp_Click(object sender, RoutedEventArgs e) => ChangeDelay(+1);

    private void Pref_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var cfg = App.Config;
        cfg.UpdateTitle = TitleToggle.IsChecked == true;
        cfg.Toasts = ToastToggle.IsChecked == true;
        cfg.SuppressToastsFullscreen = FullscreenToggle.IsChecked == true;
        cfg.FallbackEnabled = FallbackToggle.IsChecked == true;
        App.SaveConfig();
        UpdateVisibility();
    }
}
