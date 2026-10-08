using System.Windows;
using System.Windows.Controls;

namespace AutoSwitcher;

/// <summary>
/// Marks a toggle as "ready" once it's on screen. Before that, the Toggle style snaps straight to its state;
/// after, changes animate. Stops freshly created toggles (e.g. a rebuilt list) from playing an off→on flick.
/// </summary>
public static class ToggleReady
{
    public static readonly DependencyProperty IsReadyProperty = DependencyProperty.RegisterAttached(
        "IsReady", typeof(bool), typeof(ToggleReady), new PropertyMetadata(false));

    public static bool GetIsReady(DependencyObject d) => (bool)d.GetValue(IsReadyProperty);
    public static void SetIsReady(DependencyObject d, bool v) => d.SetValue(IsReadyProperty, v);

    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(CheckBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is CheckBox cb) SetIsReady(cb, true); }));
    }
}
