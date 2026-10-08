using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoSwitcher;

public sealed class CategoryRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Re-read Enabled and everything derived from it, without rebuilding the row.</summary>
    public void NotifyEnabled()
    {
        foreach (var n in new[] { nameof(Enabled), nameof(ContentOpacity), nameof(PausedTag), nameof(ToggleTip) })
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    public required CategoryMapping Category { get; init; }
    public required List<ExeChip> Chips { get; init; }
    public bool Enabled => Category.Enabled;
    public double ContentOpacity => Category.Enabled ? 1 : 0.4;
    public Visibility PausedTag => Category.Enabled ? Visibility.Collapsed : Visibility.Visible;
    public string ToggleTip => Category.Enabled ? "On: AutoSwitcher switches to this game. Click to pause."
                                                : "Paused: AutoSwitcher ignores this game. Click to turn on.";
}

public sealed class ExeChip
{
    public required string Name { get; init; }
    public required string File { get; init; }
    /// <summary>"Zelda OOT  " (with spacing) when a custom name is set, else empty.</summary>
    public string CustomPrefix { get; init; } = "";
    public required string Path { get; init; }
    public required Brush Background { get; init; }
    public required Brush Foreground { get; init; }
}

public partial class MappingsPage : UserControl
{
    private static readonly Brush ChipBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)));
    private static readonly Brush ChipFg = Freeze(new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xCF)));
    private static readonly Brush ActiveBg = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x30)));
    private static readonly Brush ActiveFg = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xFB, 0xFF)));

    private ExeMapping? _renderedActive;

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public MappingsPage()
    {
        InitializeComponent();
        App.Switcher.Changed += () => Dispatcher.InvokeAsync(RefreshState);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) RefreshState(); };
        Refresh();
    }

    public void Refresh()
    {
        BuildRows();
        RefreshState();
    }

    private void BuildRows()
    {
        var active = App.Switcher.Current?.Exe;
        _renderedActive = active;
        var rows = App.Config.Categories.Select(c => new CategoryRow
        {
            Category = c,
            Chips = c.Executables.Select(e => new ExeChip
            {
                Name = string.IsNullOrWhiteSpace(e.FullName) ? e.ProcessName : e.FullName,
                CustomPrefix = string.IsNullOrWhiteSpace(e.CustomName) ? "" : e.CustomName.Trim() + "   ",
                File = e.UsesTitle ? $"{e.FileName}  · title: “{e.TitlePattern}”" : e.FileName,
                Path = e.Path,
                Background = ReferenceEquals(e, active) ? ActiveBg : ChipBg,
                Foreground = ReferenceEquals(e, active) ? ActiveFg : ChipFg,
            }).ToList(),
        }).ToList();
        MapList.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Only the active-exe highlight lives here now; status is in the sidebar.</summary>
    private void RefreshState()
    {
        if (!ReferenceEquals(_renderedActive, App.Switcher.Current?.Exe)) BuildRows();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OpenEditor(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: CategoryMapping c })
            (Window.GetWindow(this) as MainWindow)?.OpenEditor(c);
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: CategoryMapping c } box) return;
        c.Enabled = box.IsChecked == true;
        Log.Info("ui", $"Mapping {(c.Enabled ? "enabled" : "paused")}: {c.Name}");
        App.SaveConfig();
        App.Watcher.UpdateMappings(App.Config.Categories);
        App.Switcher.ForgetIfInactive(App.Config.Categories);
        (box.DataContext as CategoryRow)?.NotifyEnabled();   // update just this row; the others stay untouched
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: CategoryMapping c }) return;
        if (Window.GetWindow(this) is not MainWindow main) return;
        int n = c.Executables.Count;
        string exes = n == 0 ? "No executables"
                    : n <= 3 ? string.Join(", ", c.Executables.Select(x => x.FileName))
                    : $"{n} executables";
        bool ok = await main.ConfirmAsync(
            "Remove category?",
            "AutoSwitcher will stop switching to this category. Your stream's current category and title aren't changed.",
            "Remove", c.Name, exes, c.BoxArtUrl);
        if (!ok) return;
        App.Config.Categories.Remove(c);
        App.SaveConfig();
        App.Watcher.UpdateMappings(App.Config.Categories);
        App.Switcher.ForgetIfInactive(App.Config.Categories);
        Refresh();
    }
}
