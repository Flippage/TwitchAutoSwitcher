using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AutoSwitcher;

/// <summary>
/// Builds the "Recent Titles" list shared by the Stream Titles and Manual pages:
/// current Twitch title first (CURRENT badge, not deletable), then titles set in the app (max 8),
/// each as a full-width, wrapping row with name pills shown as pills and a delete button on the right.
/// </summary>
public static class RecentTitleRows
{
    private static readonly Regex TokenSplit = new("(%gameName%|%fullGameName%|%customName%)", RegexOptions.IgnoreCase);

    /// <param name="res">Element used to look up theme resources.</param>
    /// <param name="exclude">Text currently in the editor (not repeated in the list).</param>
    /// <param name="pick">Called with the stored title when a row is clicked.</param>
    /// <param name="delete">Called with the stored title when its delete button is clicked.</param>
    public static List<UIElement> Build(FrameworkElement res, string? exclude, Action<string> pick, Action<string> delete)
    {
        var rows = new List<UIElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(exclude)) seen.Add(exclude);

        string? live = App.Switcher.Live?.Title;
        if (!string.IsNullOrWhiteSpace(live) && seen.Add(live))
            rows.Add(WithDelete(res, MakeRow(res, live, current: true, pick), null, delete));

        foreach (string t in App.Config.RecentTitles)
            if (!string.IsNullOrWhiteSpace(t) && seen.Add(t) && rows.Count < 8)
                rows.Add(WithDelete(res, MakeRow(res, t, current: false, pick), t, delete));
        return rows;
    }

    private static UIElement WithDelete(FrameworkElement res, Button row, string? deletable, Action<string> delete)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(row);

        var del = new Button
        {
            Style = (Style)res.FindResource("IconButton"),
            Tag = res.FindResource("IconTrash"),
            Width = 36,
            Height = 36,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove from recent titles",
            Visibility = deletable == null ? Visibility.Hidden : Visibility.Visible,   // keeps rows aligned
        };
        if (deletable != null) del.Click += (_, _) => delete(deletable);
        Grid.SetColumn(del, 1);
        grid.Children.Add(del);
        return grid;
    }

    private static Button MakeRow(FrameworkElement res, string title, bool current, Action<string> pick)
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
                Background = (Brush)res.FindResource("AccentDimBrush"),
                Child = new TextBlock
                {
                    Text = "CURRENT", FontSize = 9.5, FontWeight = FontWeights.Bold,
                    Foreground = (Brush)res.FindResource("AccentBrush"),
                    LineHeight = double.NaN,                       // don't inherit the row's 24px line height
                    LineStackingStrategy = LineStackingStrategy.MaxHeight,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            text.Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
        }
        foreach (string part in TokenSplit.Split(title))
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
            Style = (Style)res.FindResource("PillButton"),
            Height = double.NaN,
            MinHeight = 34,
            Padding = new Thickness(14, 5, 14, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = current ? "Your current Twitch title. Click to use it." : "Click to use this title",
            Content = text,
        };
        button.Click += (_, _) => pick(title);
        return button;
    }
}
