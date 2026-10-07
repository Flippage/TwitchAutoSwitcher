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

    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _loading;
    private ExeMapping? _renderedActive;

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public MappingsPage()
    {
        InitializeComponent();
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); App.SaveConfig(); };

        _loading = true;
        TemplateBox.Template = App.Config.TitleTemplate;
        UpdateTitleToggle.IsChecked = App.Config.UpdateTitle;
        _loading = false;

        App.Switcher.Changed += () => Dispatcher.InvokeAsync(RefreshState);
        TemplateBox.TemplateChanged += OnTemplateChanged;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) LoadFromConfig(); };
        Refresh();
    }

    private void LoadFromConfig()
    {
        _loading = true;
        TemplateBox.Template = App.Config.TitleTemplate;
        UpdateTitleToggle.IsChecked = App.Config.UpdateTitle;
        _loading = false;
        RefreshState();
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

        if (!ReferenceEquals(_renderedActive, hit?.Exe)) BuildRows();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var v = FillValues();
        string game = v.Game, full = v.Full, custom = v.Custom;
        string template = TemplateBox.Template;

        // Live title: plain text with the filled-in values shown as pills.
        Preview.Inlines.Clear();
        foreach (string part in TokenSplit.Split(template))
        {
            if (part.Length == 0) continue;
            string? value = part.ToLowerInvariant() switch
            {
                "%gamename%" => game,
                "%fullgamename%" => full,
                "%customname%" => custom,
                _ => null,
            };
            if (value == null) Preview.Inlines.Add(new Run(part));
            else Preview.Inlines.Add(new InlineUIContainer(TemplateEditor.MakePill(value, valueStyle: true))
                 { BaselineAlignment = BaselineAlignment.Center });
        }

        string text = Switcher.Render(template, game, full, custom);
        int len = template.Replace("%gameName%", game, StringComparison.OrdinalIgnoreCase)
                          .Replace("%fullGameName%", full, StringComparison.OrdinalIgnoreCase)
                          .Replace("%customName%", custom, StringComparison.OrdinalIgnoreCase).Trim().Length;
        Preview.ToolTip = text;
        Counter.Text = $"{len} / {Switcher.MaxTitle}";
        Counter.Foreground = (Brush)FindResource(len > Switcher.MaxTitle ? "DangerBrush" : "SubBrush");
        ApplyNowBtn.IsEnabled = v.Real && App.Twitch.IsSignedIn && template.Trim().Length > 0;
        ApplyNowBtn.ToolTip = !App.Twitch.IsSignedIn ? "Connect your Twitch account first (Account page)."
            : !v.Real ? "Nothing to fill in yet: launch a mapped game, or set a category on Twitch."
            : $"Send this title to Twitch now, filled in for {v.Source}.";
        UpdateRecent();
    }

    /// <summary>
    /// What the template variables become right now, in priority order:
    ///  1. the mapped game AutoSwitcher detected,
    ///  2. the category currently set on Twitch (using that category's first mapped exe for the exe/custom names),
    ///  3. placeholder labels (preview only; Apply now stays disabled).
    /// </summary>
    private (string Game, string Full, string Custom, bool Real, string Source) FillValues()
    {
        var hit = App.Switcher.Current;
        if (hit != null)
            return (hit.Category.Name, hit.Exe.FullName, hit.Exe.EffectiveCustom, true, hit.Exe.FullName);

        var live = App.Switcher.Live;
        if (live != null && !string.IsNullOrEmpty(live.GameName))
        {
            var mapped = App.Config.Categories.FirstOrDefault(c => c.Id == live.GameId && c.Executables.Count > 0);
            var exe = mapped?.Executables[0];
            return (live.GameName, exe?.FullName ?? live.GameName, exe?.EffectiveCustom ?? live.GameName, true,
                    "your current Twitch category");
        }
        return ("Game Name", "Game Executable Name", "Custom Name", false, "");
    }

    private static readonly System.Text.RegularExpressions.Regex TokenSplit = new(
        "(%gameName%|%fullGameName%|%customName%)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public sealed record RecentItem(string Text, string Tip, bool IsCurrent)
    {
        public Visibility BadgeVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Current Twitch title first, then titles used on the Manual page. Click = use as template.</summary>
    private void UpdateRecent()
    {
        var items = new List<RecentItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { TemplateBox.Template };
        string? live = App.Switcher.Live?.Title;
        if (!string.IsNullOrWhiteSpace(live) && seen.Add(live))
            items.Add(new RecentItem(live, "Your current Twitch title. Click to use it, then add a name pill.", true));
        foreach (string t in App.Config.RecentTitles)
            if (!string.IsNullOrWhiteSpace(t) && seen.Add(t) && items.Count < 8)
                items.Add(new RecentItem(t, t, false));
        RecentTitles.ItemsSource = items;
        RecentRow.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string t }) return;
        TemplateBox.Template = t;
        OnTemplateChanged();
    }

    private void OnTemplateChanged()
    {
        if (_loading) return;
        App.Config.TitleTemplate = TemplateBox.Template;
        _saveDebounce.Stop();
        _saveDebounce.Start();
        UpdatePreview();
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string token }) TemplateBox.InsertToken(token);
    }

    private void UpdateTitle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Config.UpdateTitle = UpdateTitleToggle.IsChecked == true;
        App.SaveConfig();
    }

    private void AutoToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.SetAutoSwitch(AutoToggle.IsChecked == true);
    }

    /// <summary>Pushes the filled-in template to Twitch right away (title only, plus the category if a game is detected).</summary>
    private async void ApplyNow_Click(object sender, RoutedEventArgs e)
    {
        var v = FillValues();
        if (!v.Real) return;
        App.SaveConfig();
        ApplyNowBtn.IsEnabled = false;
        string title = Switcher.Render(TemplateBox.Template, v.Game, v.Full, v.Custom);
        var hit = App.Switcher.Current;
        if (await App.Switcher.ApplyAsync(hit?.Category.Id, hit?.Category.Name, title, v.Custom))
            App.RememberTitle(TemplateBox.Template);
        UpdatePreview();
    }

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
