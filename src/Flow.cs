using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LyricMotion;

internal enum TextDirection
{
    /// <summary>No letters at all (numbers, punctuation): goes with whatever is around it.</summary>
    Neutral,
    LeftToRight,
    RightToLeft,
}

/// <summary>Which way a piece of lyric text is read.</summary>
internal static class Script
{
    public static void Count(string? text, ref int rightToLeft, ref int leftToRight)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            if (IsRightToLeft(rune.Value)) rightToLeft++;
            else leftToRight++;
        }
    }

    /// <summary>The direction of one word: that of most of its letters.</summary>
    public static TextDirection Of(string? text)
    {
        int rtl = 0, ltr = 0;
        Count(text, ref rtl, ref ltr);
        if (rtl == 0 && ltr == 0) return TextDirection.Neutral;
        return rtl >= ltr ? TextDirection.RightToLeft : TextDirection.LeftToRight;
    }

    /// <summary>
    /// Whether a line is laid out from the right. In a right-to-left song every line with
    /// a right-to-left word in it is (a Persian line stays Persian with an English word
    /// or two in it); elsewhere only a line that is mostly right-to-left.
    /// </summary>
    public static bool FlowsRightToLeft(string? text, bool songRightToLeft)
    {
        int rtl = 0, ltr = 0;
        Count(text, ref rtl, ref ltr);
        return FlowsRightToLeft(rtl, ltr, songRightToLeft);
    }

    public static bool FlowsRightToLeft(int rtl, int ltr, bool songRightToLeft) =>
        rtl > 0 && (songRightToLeft || rtl > ltr);

    // Hebrew, Arabic (with Persian, Urdu, Pashto, Kurdish…), Syriac, Thaana, N'Ko and
    // the older right-to-left scripts, with their presentation forms.
    private static bool IsRightToLeft(int v) =>
        (v >= 0x0590 && v <= 0x08FF) || (v >= 0xFB1D && v <= 0xFDFF) || (v >= 0xFE70 && v <= 0xFEFC)
        || (v >= 0x10800 && v <= 0x10FFF) || (v >= 0x1E800 && v <= 0x1EFFF);
}

/// <summary>
/// Right-to-left layout for one lyric line. Noctis lays every line out from the left:
/// a Persian or Arabic line then has its words in reverse order and sits against the
/// wrong edge. This turns the line round where it should be: the word rows run from the
/// right, the text reads from the right, and in a right-to-left song the lines sit
/// against the right edge of the lyrics column. Everything is an override that Dispose
/// takes back; the lyrics themselves are not touched.
/// </summary>
internal sealed class LineFlow : IDisposable
{
    private readonly Control _container;
    private readonly bool _songRightToLeft;
    private readonly List<IDisposable> _overrides = new();
    private readonly List<LayerFlow> _layers = new();
    private bool _built;

    public object Line { get; }

    /// <summary>Set by the owner on every pass; a flow that was not visited belongs to a line that is gone.</summary>
    public int Stamp { get; set; }

    public LineFlow(object line, Control container, bool songRightToLeft)
    {
        Line = line;
        _container = container;
        _songRightToLeft = songRightToLeft;
        Update();
    }

    /// <summary>Cheap when there is nothing to do; called after every layout pass.</summary>
    public void Update()
    {
        if (!_built) _built = Build();
        foreach (var layer in _layers) layer.Update();
    }

    private bool Build()
    {
        // The line's own controls are created on its first layout: until its word rows
        // exist there is nothing to turn round yet.
        var found = new List<Control>();
        var ready = false;
        Collect(_container, found, ref ready);
        if (!ready) return false;

        foreach (var control in found)
        {
            if (control is ItemsControl layer && layer.Classes.Contains("word-layer"))
            {
                AlignRight(control);
                _layers.Add(new LayerFlow(layer, _songRightToLeft));
                continue;
            }
            AlignRight(control);
            if (control is TextBlock text) Turn(text);
        }
        return true;
    }

    private static void Collect(Visual parent, List<Control> found, ref bool ready)
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (child is not Control control) continue;
            found.Add(control);
            if (control is ItemsControl layer && layer.Classes.Contains("word-layer"))
            {
                // What is inside a word row is turned round by the row itself.
                ready = true;
                continue;
            }
            Collect(control, found, ref ready);
        }
    }

    /// <summary>In a right-to-left song, what Noctis put against the left edge goes against the right one.</summary>
    private void AlignRight(Control control)
    {
        if (!_songRightToLeft || control.HorizontalAlignment != HorizontalAlignment.Left) return;
        Keep(control.SetValue(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Right, BindingPriority.Animation));

        var margin = control.Margin;
        if (margin.Left != margin.Right)
            Keep(control.SetValue(Layoutable.MarginProperty, new Thickness(margin.Right, margin.Top, margin.Left, margin.Bottom), BindingPriority.Animation));

        // The line shrinks a little when it is not current: towards the edge it sits against.
        var origin = control.RenderTransformOrigin;
        if (origin.Unit == RelativeUnit.Relative && origin.Point.X != 0.5)
            Keep(control.SetValue(Visual.RenderTransformOriginProperty,
                new RelativePoint(1 - origin.Point.X, origin.Point.Y, RelativeUnit.Relative), BindingPriority.Animation));
        else if (origin.Unit == RelativeUnit.Absolute && origin.Point.X <= 1 && origin.Point.Y <= 1)
            // Noctis' lines shrink towards their top left corner (a point given in pixels).
            Keep(control.SetValue(Visual.RenderTransformOriginProperty,
                new RelativePoint(1, 0, RelativeUnit.Relative), BindingPriority.Animation));
    }

    /// <summary>A line shown as plain text (no word timing), a translation, a romanization.</summary>
    private void Turn(TextBlock text)
    {
        var rightToLeft = Script.FlowsRightToLeft(text.Text, _songRightToLeft);
        if (rightToLeft)
            Keep(text.SetValue(Visual.FlowDirectionProperty, FlowDirection.RightToLeft, BindingPriority.Animation));
        if ((rightToLeft || _songRightToLeft) && text.TextAlignment == TextAlignment.Left)
            Keep(text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right, BindingPriority.Animation));
    }

    private void Keep(IDisposable? handle)
    {
        if (handle is not null) _overrides.Add(handle);
    }

    public void Dispose()
    {
        foreach (var layer in _layers) layer.Dispose();
        _layers.Clear();
        foreach (var handle in _overrides) handle.Dispose();
        _overrides.Clear();
        _built = true;
    }
}

/// <summary>
/// One row of word cells. A right-to-left row is mirrored as a whole (first word on the
/// right, wrapping included); words of the other direction inside it (an English phrase
/// in a Persian line, or the reverse) keep their own direction and, when there are
/// several in a row, their own order.
///
/// The cells themselves are never left mirrored: each is turned back, and a
/// right-to-left word is arranged inside its cell through its text blocks instead. A
/// mirrored cell would be drawn back to front and flipped on screen, and the image a
/// moving word is drawn into then has its letter edges smoothed for the wrong side:
/// coloured, hazy edges.
/// </summary>
internal sealed class LayerFlow : IDisposable
{
    private sealed class RunCell
    {
        public required Control Slot;
        public required Panel Cell;
        public required TextBlock Sweep;
        public required TranslateTransform Move;
        public int Run;
    }

    /// <summary>Noctis' own glow behind a word: a box around the text, placed from the left.</summary>
    private sealed class GlowBox
    {
        public required TextBlock Glow;
        public required Panel Inner;
        public double Left = double.NaN;
        public double Applied = double.NaN;
        public IDisposable? Override;
    }

    private List<GlowBox>? _glows;

    private readonly ItemsControl _layer;
    private readonly bool _songRightToLeft;
    private readonly List<IDisposable> _overrides = new();
    private List<(int From, int To)>? _runs;
    private List<RunCell>? _cells;
    private bool _decided;
    private bool _done;
    private double _signature = double.NaN;

    public LayerFlow(ItemsControl layer, bool songRightToLeft)
    {
        _layer = layer;
        _songRightToLeft = songRightToLeft;
    }

    public void Update()
    {
        if (!_done) _done = Apply();
        if (_cells is not null) Place();
        if (_glows is not null) PlaceGlows();
    }

    private bool Apply()
    {
        var count = _layer.ItemCount;
        if (count == 0) return false;   // an empty row (no backing vocals on this line): may fill later

        if (!_decided)
        {
            var kinds = new TextDirection[count];
            int rtl = 0, ltr = 0;
            for (var i = 0; i < count; i++)
            {
                var word = _layer.Items[i];
                var text = word is null ? null : WordAccess.For(word)?.Text?.Invoke(word);
                Script.Count(text, ref rtl, ref ltr);
                kinds[i] = Script.Of(text);
            }

            var rightToLeft = _rightToLeft = Script.FlowsRightToLeft(rtl, ltr, _songRightToLeft);
            if (rightToLeft)
                Keep(_layer.SetValue(Visual.FlowDirectionProperty, FlowDirection.RightToLeft, BindingPriority.Animation));

            // Stretches of words that read the other way: from the first such word to
            // the last, numbers and punctuation in between included.
            var along = rightToLeft ? TextDirection.RightToLeft : TextDirection.LeftToRight;
            var against = rightToLeft ? TextDirection.LeftToRight : TextDirection.RightToLeft;
            for (var i = 0; i < count; i++)
            {
                if (kinds[i] != against) continue;
                var last = i;
                for (var j = i + 1; j < count && kinds[j] != along; j++)
                    if (kinds[j] == against) last = j;
                (_runs ??= new()).Add((i, last));
                i = last;
            }
            _decided = true;
        }

        if (!_rightToLeft && _runs is null && !_songRightToLeft) return true;

        // The word cells are created on the row's first layout.
        var all = new List<(Control Slot, Panel Cell, Panel Inner, TextBlock Sweep, int Run)>();
        for (var i = 0; i < count; i++)
        {
            if (_layer.ContainerFromIndex(i) is not { } slot) return false;
            Panel? cell = null;
            foreach (var inside in slot.GetVisualDescendants())
                if (inside is Panel panel && panel.Classes.Contains("word-cell")) { cell = panel; break; }
            var inner = (cell is { Children.Count: > 0 } ? cell.Children[0] as Viewbox : null)?.Child as Panel;
            TextBlock? sweep = null;
            if (inner is not null)
                foreach (var child in inner.Children)
                    if (child is TextBlock text && !text.Classes.Contains("word-base")) sweep = text;
            if (cell is null || inner is null || sweep is null) return false;

            var run = -1;
            if (_runs is not null)
                for (var r = 0; r < _runs.Count; r++)
                    if (i >= _runs[r].From && i <= _runs[r].To) run = r;
            all.Add((slot, cell, inner, sweep, run));
        }

        List<RunCell>? cells = null;
        foreach (var c in all)
        {
            // Turned back: the inside of every cell is drawn the right way round.
            if (_rightToLeft)
                Keep(c.Cell.SetValue(Visual.FlowDirectionProperty, FlowDirection.LeftToRight, BindingPriority.Animation));

            if (c.Run >= 0)
            {
                var runCell = new RunCell { Slot = c.Slot, Cell = c.Cell, Sweep = c.Sweep, Move = new TranslateTransform(), Run = c.Run };
                Keep(c.Slot.SetValue(Visual.RenderTransformProperty, runCell.Move, BindingPriority.Animation));
                (cells ??= new()).Add(runCell);
            }

            // A word that reads from the right: in a right-to-left row every word that
            // is not part of a stretch the other way; in a left-to-right row those that are.
            if (_rightToLeft != (c.Run < 0))
            {
                // A left-to-right line in a right-to-left song sits against the right
                // edge: the room Noctis leaves after its last word would push it in.
                if (!_rightToLeft && _songRightToLeft && c.Run < 0) Even(c.Inner);
                continue;
            }

            // It grows towards the words still to come, as a left-to-right word does.
            var origin = c.Cell.RenderTransformOrigin;
            if (origin.Unit == RelativeUnit.Absolute && origin.Point.X <= 1 && origin.Point.Y <= 1)
                Keep(c.Cell.SetValue(Visual.RenderTransformOriginProperty,
                    new RelativePoint(1, 0, RelativeUnit.Relative), BindingPriority.Animation));
            else if (origin.Unit == RelativeUnit.Relative && origin.Point.X != 0.5)
                Keep(c.Cell.SetValue(Visual.RenderTransformOriginProperty,
                    new RelativePoint(1 - origin.Point.X, origin.Point.Y, RelativeUnit.Relative), BindingPriority.Animation));

            foreach (var visual in c.Inner.GetVisualDescendants())
            {
                if (visual is not TextBlock text) continue;
                // The text itself reads from the right: the space after the word is on its left.
                Keep(text.SetValue(Visual.FlowDirectionProperty, FlowDirection.RightToLeft, BindingPriority.Animation));

                // Noctis leaves room after a line's last word, as padding on the right
                // of its text. Here "after" is on the left; and where that is the edge
                // the lines sit against (a right-to-left line in a left-to-right song),
                // the room is dropped so the line is not pushed in.
                var padding = text.Padding;
                if (padding.Left != padding.Right)
                {
                    var away = _rightToLeft && !_songRightToLeft && c.Run < 0 ? Math.Min(padding.Left, padding.Right) : padding.Right;
                    var near = _rightToLeft && !_songRightToLeft && c.Run < 0 ? away : padding.Left;
                    Keep(text.SetValue(TextBlock.PaddingProperty,
                        new Thickness(away, padding.Top, near, padding.Bottom), BindingPriority.Animation));
                }

                if (ReferenceEquals(text.GetVisualParent(), c.Inner))
                {
                    // The bright fill lies over the word: against the right edge, like the word.
                    if (text.HorizontalAlignment == HorizontalAlignment.Left)
                        Keep(text.SetValue(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Right, BindingPriority.Animation));
                }
                else (_glows ??= new()).Add(new GlowBox { Glow = text, Inner = c.Inner });
            }
        }
        _cells = cells;
        return true;
    }

    private void Even(Panel inner)
    {
        foreach (var visual in inner.GetVisualDescendants())
        {
            if (visual is not TextBlock text) continue;
            var padding = text.Padding;
            if (padding.Left == padding.Right) continue;
            var side = Math.Min(padding.Left, padding.Right);
            Keep(text.SetValue(TextBlock.PaddingProperty,
                new Thickness(side, padding.Top, side, padding.Bottom), BindingPriority.Animation));
        }
    }

    /// <summary>
    /// Noctis' glow is a box placed a little to the left of the word's left edge. For a
    /// word that sits against the right edge of its cell the box goes the same distance
    /// past the right edge instead. Only shown glows have a size to work from.
    /// </summary>
    private void PlaceGlows()
    {
        foreach (var g in _glows!)
        {
            if (!g.Glow.IsVisible) continue;
            var width = g.Glow.Bounds.Width;
            var room = g.Inner.Bounds.Width;
            if (width <= 0 || room <= 0) continue;
            if (double.IsNaN(g.Left)) g.Left = g.Glow.Bounds.X;   // where Noctis put it

            var canvasLeft = room - width - g.Left - g.Glow.Margin.Left;
            if (Math.Abs(canvasLeft - g.Applied) < 0.01) continue;
            g.Applied = canvasLeft;
            var before = g.Override;
            g.Override = g.Glow.SetValue(Canvas.LeftProperty, canvasLeft, BindingPriority.Animation);
            before?.Dispose();
        }
    }

    private bool _rightToLeft;

    /// <summary>
    /// Puts the words of each stretch in their own reading order within the room the
    /// stretch takes up. Every cell is a word followed by its space; turned round, the
    /// space after the last word of the stretch is the one that is left over at the far
    /// end, where it separates the stretch from what follows.
    /// </summary>
    private void Place()
    {
        double signature = 0;
        foreach (var c in _cells!)
        {
            var b = c.Slot.Bounds;
            if (b.Width <= 0 || c.Sweep.Bounds.Width <= 0) return;   // not laid out yet
            signature = signature * 31 + b.X * 3 + b.Y * 7 + b.Width + c.Sweep.Bounds.Width * 0.5;
        }
        if (signature == _signature) return;
        _signature = signature;

        var index = 0;
        while (index < _cells.Count)
        {
            // One stretch, or the part of it on one row when it wraps.
            var end = index;
            while (end + 1 < _cells.Count && _cells[end + 1].Run == _cells[index].Run
                   && Math.Abs(_cells[end + 1].Slot.Bounds.Y - _cells[index].Slot.Bounds.Y) < 0.5) end++;

            var anchor = _cells[index].Slot.Bounds.X;
            for (var k = end; k >= index; k--)
            {
                var slot = _cells[k].Slot.Bounds;
                var x = anchor + Ink(_cells[k]) - slot.Width;
                var dx = x - slot.X;
                if (_cells[k].Move.X != dx) _cells[k].Move.X = dx;
                anchor += Ink(_cells[k]);
                if (k > index) anchor += Math.Max(0, _cells[k - 1].Slot.Bounds.Width - Ink(_cells[k - 1]));
            }
            index = end + 1;
        }
    }

    /// <summary>Width of the word itself, without the space that follows it in its cell.</summary>
    private static double Ink(RunCell c)
    {
        var width = c.Sweep.Bounds.Width;
        // The cell's content sits in a Viewbox; normally at its own size.
        if (c.Sweep.GetVisualParent() is Visual inner && inner.Bounds.Width > 0 && c.Cell.Bounds.Width > 0)
            width *= c.Cell.Bounds.Width / inner.Bounds.Width;
        return Math.Min(width, c.Slot.Bounds.Width);
    }

    private void Keep(IDisposable? handle)
    {
        if (handle is not null) _overrides.Add(handle);
    }

    public void Dispose()
    {
        foreach (var handle in _overrides) handle.Dispose();
        _overrides.Clear();
        if (_glows is not null)
            foreach (var g in _glows) g.Override?.Dispose();
        _glows = null;
        _cells = null;
        _runs = null;
        _done = true;
        _decided = true;
    }
}
