using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoSwitcher;

/// <summary>
/// Paints a translucent red "highlighter" over every character past <see cref="Limit"/> in a TextBox,
/// without stopping the user typing. Lives in the TextBox's adorner layer, so it's clipped and
/// scrolled with the page. Redraws only when text, size or scroll change.
/// </summary>
public sealed class OverflowHighlighter : Adorner
{
    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0xEF, 0x44, 0x44)));
    private readonly TextBox _box;
    private bool _queued;

    public int Limit { get; }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private OverflowHighlighter(TextBox box, int limit) : base(box)
    {
        _box = box;
        Limit = limit;
        IsHitTestVisible = false;
        box.TextChanged += (_, _) => Queue();
        box.SizeChanged += (_, _) => Queue();
        box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Queue()));
    }

    /// <summary>Attach once the TextBox is loaded (needs an adorner layer).</summary>
    public static OverflowHighlighter? Attach(TextBox box, int limit)
    {
        var layer = AdornerLayer.GetAdornerLayer(box);
        if (layer == null) return null;
        var adorner = new OverflowHighlighter(box, limit);
        layer.Add(adorner);
        adorner.Queue();
        return adorner;
    }

    // Character rectangles are only valid after the TextBox lays out the new text,
    // so redraw at Loaded priority (runs after layout/render of the current frame).
    private void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.InvokeAsync(() => { _queued = false; InvalidateVisual(); }, DispatcherPriority.Loaded);
    }

    protected override void OnRender(DrawingContext dc)
    {
        string text = _box.Text;
        if (text.Length <= Limit) return;

        Rect run = Rect.Empty;
        for (int i = Limit; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n') continue;
            Rect lead = _box.GetRectFromCharacterIndex(i);
            Rect trail = _box.GetRectFromCharacterIndex(i, trailingEdge: true);
            if (lead.IsEmpty || trail.IsEmpty) continue;

            double right = trail.X >= lead.X ? trail.X : lead.X + 6;   // trailing edge wrapped: give it a sliver
            var r = new Rect(lead.X, lead.Top, Math.Max(1, right - lead.X), lead.Height);

            if (!run.IsEmpty && Math.Abs(run.Top - r.Top) < 0.5 && r.Left <= run.Right + 1)
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
