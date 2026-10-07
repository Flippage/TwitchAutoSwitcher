using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoSwitcher;

/// <summary>Stream Titles page: template editor with name pills, live preview, Apply now, recent titles.</summary>
public partial class TitlesPage : UserControl
{
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _loading;

    public TitlesPage()
    {
        InitializeComponent();
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); App.SaveConfig(); };
        LoadFromConfig();
        TemplateBox.TemplateChanged += OnTemplateChanged;
        App.Switcher.Changed += () => Dispatcher.InvokeAsync(() => { if (IsVisible) UpdatePreview(); });
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) LoadFromConfig(); };
    }

    private void LoadFromConfig()
    {
        _loading = true;
        TemplateBox.Template = App.Config.TitleTemplate;
        UpdateTitleToggle.IsChecked = App.Config.UpdateTitle;
        _loading = false;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var v = FillValues();
        string game = v.Game, full = v.Full, custom = v.Custom;
        string template = TemplateBox.Template;
        TemplateBox.SetTokenValues(game, full, custom);

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

    /// <summary>Current Twitch title first, then titles set in the app. Full text; click = use as template.</summary>
    private void UpdateRecent()
    {
        var rows = new List<UIElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { TemplateBox.Template };
        string? live = App.Switcher.Live?.Title;
        if (!string.IsNullOrWhiteSpace(live) && seen.Add(live))
            rows.Add(WithDelete(MakeRecentRow(live, current: true), null));      // live title: nothing to delete
        foreach (string t in App.Config.RecentTitles)
            if (!string.IsNullOrWhiteSpace(t) && seen.Add(t) && rows.Count < 8)
                rows.Add(WithDelete(MakeRecentRow(t, current: false), t));
        RecentTitles.ItemsSource = rows;
        RecentRow.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Row = the title button (left) + a delete button pinned to the right edge.</summary>
    private UIElement WithDelete(Button row, string? deletable)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Margin = new Thickness(0);
        grid.Children.Add(row);

        var del = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Tag = FindResource("IconTrash"),
            Width = 36,
            Height = 36,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            CommandParameter = deletable,
            ToolTip = "Remove from recent titles",
            Visibility = deletable == null ? Visibility.Hidden : Visibility.Visible,   // keeps rows aligned
        };
        del.Click += DeleteRecent_Click;
        Grid.SetColumn(del, 1);
        grid.Children.Add(del);
        return grid;
    }

    private void DeleteRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string t }) return;
        App.Config.RecentTitles.Remove(t);
        App.SaveConfig();
        UpdateRecent();
    }

    private Button MakeRecentRow(string template, bool current)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 24, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        if (current)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)FindResource("AccentDimBrush"),
                Child = new TextBlock
                {
                    Text = "CURRENT", FontSize = 9.5, FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("AccentBrush"),
                    LineHeight = double.NaN,                       // don't inherit the row's 24px line height
                    LineStackingStrategy = LineStackingStrategy.MaxHeight,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            text.Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
        }
        foreach (string part in TokenSplit.Split(template))
        {
            if (part.Length == 0) continue;
            if (TokenSplit.IsMatch(part) && TokenSplit.Match(part).Length == part.Length)
                text.Inlines.Add(new InlineUIContainer(TemplateEditor.MakePill(TemplateEditor.LabelFor(part)))
                    { BaselineAlignment = BaselineAlignment.Center });
            else
                text.Inlines.Add(new Run(part));
        }

        var button = new Button
        {
            Style = (Style)FindResource("PillButton"),
            Height = double.NaN,
            MinHeight = 34,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CommandParameter = template,
            ToolTip = current ? "Your current Twitch title. Click to use it, then add a name pill." : "Click to use this title",
            Content = text,
        };
        button.Click += Recent_Click;
        return button;
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

}
