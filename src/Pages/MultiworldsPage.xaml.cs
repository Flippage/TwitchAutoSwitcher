using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoSwitcher;

public sealed class MultiworldRow
{
    public required Multiworld World { get; init; }
    public required List<CategoryMapping> Games { get; init; }
    public required bool Active { get; init; }
    public string CountText => Games.Count == 1 ? "1 game" : $"{Games.Count} games";
    public Visibility ActiveVis => Active ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActivateVis => Active ? Visibility.Collapsed : Visibility.Visible;
    public Brush Outline => Active ? (Brush)Application.Current.FindResource("AccentLineBrush") : Brushes.Transparent;
}

public partial class MultiworldsPage : UserControl
{
    public MultiworldsPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Refresh(); };
        Multiworlds.MappingsChanged += () => Dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        var rows = App.Config.Multiworlds.Select(w => new MultiworldRow
        {
            World = w,
            Games = Multiworlds.GamesOf(w),
            Active = Multiworlds.IsActive(w),
        }).ToList();
        WorldList.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AllOnBtn.IsEnabled = App.Config.Categories.Any(c => !c.Enabled);
    }

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private void New_Click(object sender, RoutedEventArgs e) => Main?.OpenMultiworldEditor(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: Multiworld w }) Main?.OpenMultiworldEditor(w);
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: Multiworld w }) { Multiworlds.Activate(w); Refresh(); }
    }

    private void AllOn_Click(object sender, RoutedEventArgs e)
    {
        Multiworlds.AllOn();
        Refresh();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: Multiworld w }) return;
        var copy = new Multiworld { Name = Multiworlds.UniqueName(w.Name + " copy"), CategoryIds = w.CategoryIds.ToList() };
        int i = App.Config.Multiworlds.IndexOf(w);
        App.Config.Multiworlds.Insert(i + 1, copy);
        App.SaveConfig();
        Refresh();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: Multiworld w } || Main is not { } main) return;
        var games = Multiworlds.GamesOf(w);
        bool ok = await main.ConfirmAsync("Delete multiworld?",
            "Your game mappings aren't changed: games stay turned on or off as they are now.",
            "Delete", w.Name, games.Count == 0 ? "No games" : string.Join(", ", games.Select(g => g.Name)),
            games.FirstOrDefault()?.BoxArtUrl);
        if (!ok) return;
        App.Config.Multiworlds.Remove(w);
        App.SaveConfig();
        Refresh();
    }
}
