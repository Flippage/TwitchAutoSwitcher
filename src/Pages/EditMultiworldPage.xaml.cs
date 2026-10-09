using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoSwitcher;

public sealed class WorldPick : INotifyPropertyChanged
{
    private bool _selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public required CategoryMapping Cat { get; init; }
    public string Detail => string.Join(" · ", Cat.Executables.Select(e => string.IsNullOrWhiteSpace(e.EffectiveCustom) ? e.FileName : e.EffectiveCustom).Distinct());
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
            PropertyChanged?.Invoke(this, new(nameof(Outline)));
            Toggled?.Invoke();
        }
    }
    public Brush Outline => Selected ? (Brush)Application.Current.FindResource("AccentLineBrush") : Brushes.Transparent;
    public System.Action? Toggled;
}

public partial class EditMultiworldPage : UserControl
{
    private readonly Multiworld? _existing;
    private readonly List<WorldPick> _picks;

    public EditMultiworldPage(Multiworld? existing)
    {
        InitializeComponent();
        _existing = existing;
        Heading.Text = existing == null ? "New multiworld" : "Edit multiworld";
        NameBox.Text = existing?.Name ?? Multiworlds.UniqueName("Multiworld");

        // New multiworld: start from the games that are on right now.
        _picks = App.Config.Categories.Select(c => new WorldPick
        {
            Cat = c,
            Selected = existing != null ? existing.CategoryIds.Contains(c.Id) : c.Enabled,
        }).ToList();
        foreach (var p in _picks) p.Toggled = UpdateCount;
        PickList.ItemsSource = _picks;
        NoGames.Visibility = _picks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void UpdateCount()
    {
        int n = _picks.Count(p => p.Selected);
        GamesHeading.Text = $"GAMES · {n} OF {_picks.Count} SELECTED";
        if (n > 0) ErrorText.Text = "";
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        // Clicking anywhere on the row toggles it (the toggle itself handles its own clicks).
        if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) != null) return;
        if (sender is FrameworkElement { DataContext: WorldPick p }) p.Selected = !p.Selected;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x is T t) return t;
        return null;
    }

    private void All_Click(object sender, RoutedEventArgs e) { foreach (var p in _picks) p.Selected = true; }
    private void None_Click(object sender, RoutedEventArgs e) { foreach (var p in _picks) p.Selected = false; }

    private void Cancel_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.CloseMultiworldEditor();

    private void Save_Click(object sender, RoutedEventArgs e) => Save(activate: false);
    private void SaveActivate_Click(object sender, RoutedEventArgs e) => Save(activate: true);

    private void Save(bool activate)
    {
        var ids = _picks.Where(p => p.Selected).Select(p => p.Cat.Id).ToList();
        if (ids.Count == 0) { ErrorText.Text = "Pick at least one game."; return; }
        var w = _existing ?? new Multiworld();
        w.Name = Multiworlds.UniqueName(NameBox.Text, _existing);
        w.CategoryIds = ids;
        if (_existing == null) App.Config.Multiworlds.Add(w);
        App.SaveConfig();
        Log.Info("multiworld", $"{(_existing == null ? "Created" : "Saved")} \"{w.Name}\" ({ids.Count} games)");
        if (activate) Multiworlds.Activate(w);
        (Window.GetWindow(this) as MainWindow)?.CloseMultiworldEditor();
    }
}
