using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoSwitcher;

public sealed class CategoryRow
{
    public required CategoryMapping Category { get; init; }
    public required List<ExeChip> Chips { get; init; }
}

public sealed class ExeChip
{
    public required string Name { get; init; }
    public required string File { get; init; }
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

    private bool _loading;
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
                File = e.FileName,
                Path = e.Path,
                Background = ReferenceEquals(e, active) ? ActiveBg : ChipBg,
                Foreground = ReferenceEquals(e, active) ? ActiveFg : ChipFg,
            }).ToList(),
        }).ToList();
        MapList.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshState()
    {
        var cfg = App.Config;
        var hit = App.Switcher.Current;

        _loading = true;
        AutoToggle.IsChecked = cfg.AutoSwitch;
        _loading = false;

        string mode = cfg.Mode == DetectionMode.Focus ? "FOCUSED WINDOW" : "LAUNCHED GAME";
        if (!cfg.AutoSwitch)
        {
            NowLabel.Text = "AUTO-SWITCH PAUSED · MANUAL MODE";
            StatusDot.Fill = (Brush)FindResource("WarnBrush");
        }
        else
        {
            NowLabel.Text = "NOW PLAYING · " + mode;
            StatusDot.Fill = (Brush)FindResource(hit != null ? "GoodBrush" : "SubBrush");
        }

        NowText.Inlines.Clear();
        if (hit != null)
        {
            NowText.Inlines.Add(new System.Windows.Documents.Run(hit.Exe.FullName));
            NowText.Inlines.Add(new System.Windows.Documents.Run($"  ({hit.Exe.FileName}) → {hit.Category.Name}")
            { Foreground = (Brush)FindResource("SubBrush"), FontWeight = FontWeights.Normal });
        }
        else
        {
            NowText.Inlines.Add(new System.Windows.Documents.Run(App.Config.Categories.Count == 0
                ? "Add a category to get started"
                : "Waiting for a mapped game…") { Foreground = (Brush)FindResource("Text2Brush"), FontWeight = FontWeights.Normal });
        }

        string? err = App.Switcher.LastError;
        ErrorText.Text = err ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(err) ? Visibility.Collapsed : Visibility.Visible;

        TitleState.Text = cfg.UpdateTitle ? "auto" : "off";
        TitleState.Foreground = (Brush)FindResource(cfg.UpdateTitle ? "AccentBrush" : "SubBrush");

        if (!ReferenceEquals(_renderedActive, hit?.Exe)) BuildRows();
    }

    private void AutoToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.SetAutoSwitch(AutoToggle.IsChecked == true);
    }

    private void TitleLink_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.GoToTitles();

    private void Add_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OpenEditor(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: CategoryMapping c })
            (Window.GetWindow(this) as MainWindow)?.OpenEditor(c);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: CategoryMapping c }) return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"Remove \"{c.Name}\" and its {c.Executables.Count} executable(s)?", "Remove category",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        App.Config.Categories.Remove(c);
        App.SaveConfig();
        App.Watcher.UpdateMappings(App.Config.Categories);
        Refresh();
    }
}
