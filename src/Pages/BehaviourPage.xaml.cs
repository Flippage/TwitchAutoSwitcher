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
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        DelayCard.Visibility = App.Config.Mode == DetectionMode.Focus ? Visibility.Visible : Visibility.Collapsed;
        FallbackPicker.Visibility = App.Config.FallbackEnabled ? Visibility.Visible : Visibility.Collapsed;
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
