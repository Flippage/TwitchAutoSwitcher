using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoSwitcher;

/// <summary>
/// Single-line title editor where variables appear as pills ("Game Name", "Custom Name"…) instead of %tokens%.
/// Stored/serialized as the plain template string, e.g. "Currently Playing: %customName% | !discord".
/// A pill behaves like one character: the caret steps over it and Backspace removes it whole.
/// </summary>
public partial class TemplateEditor : UserControl
{
    public static readonly IReadOnlyList<(string Token, string Label)> Tokens = new[]
    {
        ("%gameName%", "Game Name"),
        ("%fullGameName%", "Game Executable Name"),
        ("%customName%", "Custom Name"),
    };

    private static readonly Regex TokenRx = new("(%gameName%|%fullGameName%|%customName%)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private bool _updating;

    /// <summary>Raised (debounce-free) whenever the template text changes.</summary>
    public event Action? TemplateChanged;

    public string PlaceholderText
    {
        get => Placeholder.Text;
        set => Placeholder.Text = value;
    }

    public TemplateEditor()
    {
        InitializeComponent();
        DataObject.AddPastingHandler(Box, OnPaste);
        DataObject.AddCopyingHandler(Box, OnCopy);
    }

    public static string LabelFor(string token)
    {
        foreach (var (t, l) in Tokens)
            if (string.Equals(t, token, StringComparison.OrdinalIgnoreCase)) return l;
        return token;
    }

    /// <summary>Builds a pill visual. Shared with the Live title preview.</summary>
    public static Border MakePill(string text, bool valueStyle = false)
    {
        var app = Application.Current;
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)app.FindResource("AccentDimBrush"),
            BorderBrush = (Brush)app.FindResource("AccentLineBrush"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                LineHeight = double.NaN,                                  // Block.LineHeight is inherited from the document; reset it
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = valueStyle ? FontWeights.Normal : FontWeights.SemiBold,
                Foreground = (Brush)app.FindResource("AccentBrush"),
            },
        };
    }

    // ------------------------------------------------------------ text <-> document

    public string Template
    {
        get => Serialize();
        set
        {
            if (Serialize() == value) return;
            _updating = true;
            Box.Document.Blocks.Clear();
            Box.Document.Blocks.Add(BuildParagraph(value ?? ""));
            Box.CaretPosition = Box.Document.ContentEnd;
            _updating = false;
            UpdatePlaceholder();
        }
    }

    private static Paragraph BuildParagraph(string template)
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        foreach (string part in TokenRx.Split(template))
        {
            if (part.Length == 0) continue;
            if (TokenRx.IsMatch(part) && TokenRx.Match(part).Length == part.Length)
                p.Inlines.Add(MakeContainer(NormalizeToken(part)));
            else
                p.Inlines.Add(new Run(part));
        }
        return p;
    }

    private static string NormalizeToken(string raw)
    {
        foreach (var (t, _) in Tokens)
            if (string.Equals(t, raw, StringComparison.OrdinalIgnoreCase)) return t;
        return raw;
    }

    private static InlineUIContainer MakeContainer(string token) =>
        new(MakePill(LabelFor(token))) { Tag = token, BaselineAlignment = BaselineAlignment.Center };

    private string Serialize()
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var block in Box.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            if (!first) sb.Append(' ');
            first = false;
            Walk(p.Inlines, sb);
        }
        return sb.ToString();
    }

    private static void Walk(InlineCollection inlines, StringBuilder sb)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run r: sb.Append(r.Text); break;
                case InlineUIContainer { Tag: string token }: sb.Append(token); break;
                case Span s: Walk(s.Inlines, sb); break;
                case LineBreak: sb.Append(' '); break;
            }
        }
    }

    // ------------------------------------------------------------ editing

    public void InsertToken(string token)
    {
        Box.Focus();
        if (!Box.Selection.IsEmpty) Box.Selection.Text = "";
        var pos = Box.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
        var container = new InlineUIContainer(MakePill(LabelFor(token)), pos)
        {
            Tag = token,
            BaselineAlignment = BaselineAlignment.Center,
        };
        Box.CaretPosition = container.ElementEnd;
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        // A %token% typed or pasted as text becomes a pill.
        string current = Serialize();
        if (HasRawToken())
        {
            _updating = true;
            Box.Document.Blocks.Clear();
            Box.Document.Blocks.Add(BuildParagraph(current));
            Box.CaretPosition = Box.Document.ContentEnd;
            _updating = false;
        }
        UpdatePlaceholder();
        TemplateChanged?.Invoke();
    }

    private bool HasRawToken()
    {
        foreach (var block in Box.Document.Blocks)
            if (block is Paragraph p)
                foreach (var inline in p.Inlines)
                    if (inline is Run r && TokenRx.IsMatch(r.Text)) return true;
        return false;
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) e.Handled = true;   // titles are one line
    }

    private void Box_FocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        Frame.BorderBrush = (Brush)FindResource(Box.IsKeyboardFocusWithin ? "AccentBrush" : "LineBrush");
    }

    private void UpdatePlaceholder() =>
        Placeholder.Visibility = Serialize().Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    // Paste as plain text only (no foreign formatting); tokens in it turn into pills via TextChanged.
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        string? text = e.DataObject.GetData(DataFormats.UnicodeText) as string
                       ?? e.DataObject.GetData(DataFormats.Text) as string;
        if (text == null) { e.CancelCommand(); return; }
        text = text.Replace("\r", " ").Replace("\n", " ");
        e.DataObject = new DataObject(DataFormats.UnicodeText, text);
    }

    // Copy pills as their %token% so pasting elsewhere (or back here) keeps them.
    private void OnCopy(object sender, DataObjectCopyingEventArgs e)
    {
        var sel = Box.Selection;
        if (sel.IsEmpty) return;
        var sb = new StringBuilder();
        var nav = sel.Start;
        while (nav != null && nav.CompareTo(sel.End) < 0)
        {
            var ctx = nav.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                string run = nav.GetTextInRun(LogicalDirection.Forward);
                int max = nav.GetOffsetToPosition(sel.End);
                sb.Append(run.Length > max ? run[..max] : run);
            }
            else if (ctx == TextPointerContext.EmbeddedElement && nav.Parent is InlineUIContainer { Tag: string token })
            {
                sb.Append(token);
            }
            nav = nav.GetNextContextPosition(LogicalDirection.Forward);
        }
        e.DataObject.SetData(DataFormats.UnicodeText, sb.ToString());
    }
}
