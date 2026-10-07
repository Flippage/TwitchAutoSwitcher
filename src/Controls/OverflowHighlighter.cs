using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoSwitcher;

/// <summary>
/// Paints a translucent red "highlighter" over everything past the character limit, without blocking typing.
/// Lives in the control's adorner layer, so it's clipped and scrolled with the page.
/// Works for a plain TextBox (Manual page) and for the pill TemplateEditor's RichTextBox, where each pill
/// counts as the length of the name it will be filled with.
/// </summary>
public sealed class OverflowHighlighter : Adorner
{
    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0xEF, 0x44, 0x44)));
    private readonly Func<IEnumerable<Rect>> _segments;
    private bool _queued;

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private OverflowHighlighter(UIElement adorned, Func<IEnumerable<Rect>> segments) : base(adorned)
    {
        _segments = segments;
        IsHitTestVisible = false;
    }

    // ------------------------------------------------------------ attach helpers

    public static OverflowHighlighter? Attach(TextBox box, int limit)
    {
        var a = Create(box, () => TextBoxSegments(box, limit));
        if (a == null) return null;
        box.TextChanged += (_, _) => a.Queue();
        box.SizeChanged += (_, _) => a.Queue();
        box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => a.Queue()));
        return a;
    }

    /// <param name="valueLength">Filled-in length of a pill's token (e.g. %gameName% → "Neon White".Length).</param>
    public static OverflowHighlighter? Attach(RichTextBox box, int limit, Func<string, int> valueLength)
    {
        var a = Create(box, () => RichSegments(box, limit, valueLength));
        if (a == null) return null;
        box.TextChanged += (_, _) => a.Queue();
        box.SizeChanged += (_, _) => a.Queue();
        return a;
    }

    private static OverflowHighlighter? Create(UIElement el, Func<IEnumerable<Rect>> segments)
    {
        var layer = AdornerLayer.GetAdornerLayer(el);
        if (layer == null) return null;
        var a = new OverflowHighlighter(el, segments);
        layer.Add(a);
        a.Queue();
        return a;
    }

    /// <summary>Character rectangles are only valid after layout, so redraw at Loaded priority.</summary>
    public void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.InvokeAsync(() => { _queued = false; InvalidateVisual(); }, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------ geometry

    private static IEnumerable<Rect> TextBoxSegments(TextBox box, int limit)
    {
        string text = box.Text;
        for (int i = limit; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n') continue;
            Rect lead = box.GetRectFromCharacterIndex(i);
            Rect trail = box.GetRectFromCharacterIndex(i, trailingEdge: true);
            if (lead.IsEmpty || trail.IsEmpty) continue;
            yield return CharRect(lead, trail);
        }
    }

    private static IEnumerable<Rect> RichSegments(RichTextBox box, int limit, Func<string, int> valueLength)
    {
        int count = 0;
        foreach (var block in box.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            foreach (var inline in p.Inlines)
            {
                if (inline is Run run)
                {
                    string t = run.Text;
                    for (int i = 0; i < t.Length; i++, count++)
                    {
                        if (count < limit) continue;
                        var at = run.ContentStart.GetPositionAtOffset(i);
                        var next = at?.GetPositionAtOffset(1);
                        if (at == null || next == null) continue;
                        Rect lead = at.GetCharacterRect(LogicalDirection.Forward);
                        Rect trail = next.GetCharacterRect(LogicalDirection.Backward);
                        if (lead.IsEmpty || trail.IsEmpty) continue;
                        yield return CharRect(lead, trail);
                    }
                }
                else if (inline is InlineUIContainer { Tag: string token, Child: FrameworkElement pill })
                {
                    int len = valueLength(token);
                    bool over = count + len > limit;
                    count += len;
                    if (!over || !pill.IsVisible) continue;
                    Rect r;
                    try { r = pill.TransformToAncestor(box).TransformBounds(new Rect(pill.RenderSize)); }
                    catch (InvalidOperationException) { continue; }
                    yield return r;
                }
            }
        }
    }

    private static Rect CharRect(Rect lead, Rect trail)
    {
        // Same line: span lead→trail. Wrapped to the next line: give it a sliver.
        bool sameLine = Math.Abs(trail.Top - lead.Top) < 1 && trail.X >= lead.X;
        double width = sameLine ? Math.Max(1, trail.X - lead.X) : 6;
        return new Rect(lead.X, lead.Top, width, lead.Height);
    }

    // ------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        Rect run = Rect.Empty;
        foreach (var r in _segments())
        {
            if (!run.IsEmpty && Math.Abs(run.Top - r.Top) < 2 && r.Left <= run.Right + 2)
                run.Union(r);
            else
            {
                Draw(dc, run);
                run = r;
            }
        }
        Draw(dc, run);
    }

    private static void Draw(DrawingContext dc, Rect r)
    {
        if (r.IsEmpty) return;
        r.Inflate(1, 0);
        dc.DrawRoundedRectangle(Fill, null, r, 3, 3);
    }
}
