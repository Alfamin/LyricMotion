using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace LyricMotion;

/// <summary>
/// A word redrawn so it can move: laid exactly over Noctis' own text, as separate letters
/// (each grows, rises and, on a held word, glows on its own as the fill reaches it) or,
/// for a word that only floats up or cannot be cut into letters, in one piece. Noctis'
/// three text layers for the word stay in place (they keep the layout) but paint nothing
/// while this layer is up; disposing it hands the word back untouched.
///
/// A word that cannot be cut into letters (Persian, Arabic and other joined scripts,
/// where a letter's shape depends on its neighbours; very long words) grows, rises and
/// glows as a whole, and is filled in by Noctis' own sweep, from the right for a
/// right-to-left word.
///
/// Until its turn comes a letter is ordinary live text, drawn exactly where Noctis'
/// own letter was: taking a word over changes nothing that can be seen. From the moment
/// the fill reaches it, the letter is a picture: it is moved and scaled as a picture
/// (even, sub-pixel motion; text drawn afresh at every new size comes out in uneven
/// steps) and it lights up by fading from the dimmed level to full as the fill crosses
/// it. (A picture of text is never quite the same as live text, a few percent bolder
/// with stems a fraction of a pixel off, so the change is made at the one moment the
/// letter is changing anyway.)
///
/// The pictures of all the letters of a word are drawn once, side by side, on one
/// sheet, at the size the screen shows them at, and the whole word is painted by one
/// element that puts the pieces where they belong on every frame. (One element and one
/// sheet per word, not two cached layers per letter: a line's worth of those was enough
/// to make frames late.) A sung word at rest sits on whole screen pixels (see
/// <see cref="Align"/>) and is as sharp as its neighbours. A letter's glow is a blurred
/// copy of it behind it, which only ever changes opacity.
/// </summary>
internal sealed class LetterLayer : IDisposable
{
    private sealed class Letter
    {
        /// <summary>The letter alone (or the whole word), laid out the way Noctis lays its own text out.</summary>
        public required TextLayout Text;
        public TextBlock? Halo;
        public ScaleTransform? HaloScale;
        public TranslateTransform? HaloMove;
        /// <summary>Where the letter starts inside the word's text, and how wide it is.</summary>
        public double X;
        public double Width;
        /// <summary>Where the letter's text starts inside Noctis' word panel.</summary>
        public double Left, Top;
        /// <summary>How far ahead of the letter the fill's edge starts to light it, and how far past it finishes.</summary>
        public double Ahead, Behind;
        /// <summary>Where the letter stands in its word: 0 at the start, 1 at the end.</summary>
        public double At;
        /// <summary>How much of its growth the letter takes from the word before and the word after its own.</summary>
        public double FromBefore, FromAfter;
        /// <summary>How far the letter has come up: with the fill, but never faster than the eye can follow.</summary>
        public double Motion;
        public bool MotionKnown;
        public double Light;
        /// <summary>How much of its glow the letter still has: 1 until it starts to relax.</summary>
        public double Keep;
        public double Rise;
        public double Grow;
        /// <summary>False while the letter has not moved yet and is still drawn as live text.</summary>
        public bool Imaged;

        // Its picture: where it is on the sheet (in pixels), and the box on the word it
        // is painted into (the letter with some room around it).
        public Rect Source;
        public double BoxLeft, BoxWidth;
        /// <summary>From the left edge of its picture to where the letter's text starts, in pixels.</summary>
        public double Inside;

        // What is painted right now.
        public double Scale = 1, MoveX, MoveY, Shown;

        // Exactly on the fill in normal play; only a sudden change (a seek) is glided
        // across, critically damped, so it never overshoots.
        public Follower Arrival = new(6, 0.001);
    }

    /// <summary>The one element that paints the word.</summary>
    private sealed class Painter : Control
    {
        public LetterLayer? Owner;

        public override void Render(DrawingContext context) => Owner?.Paint(context);
    }

    /// <summary>Where a letter grows from, as a share of its height: just above the baseline.</summary>
    private const double OriginY = 0.62;

    /// <summary>
    /// The band a letter arrives in: how far ahead of the fill's edge it opens, and how far
    /// behind it closes. At the two ends of the word the band stops at the word itself, so
    /// the first letter starts exactly when the word does and the last is done exactly
    /// when the word is.
    /// </summary>
    private const double BandAheadEm = 0.3, BandBehindEm = 0.35;

    /// <summary>
    /// Where one word hands over to the next, the letters lean towards how much the
    /// neighbour grows: at the very edge by this share, fading out over this distance
    /// into the word (and never past its middle). A held word next to a quick one then
    /// rises out of the line as a soft hill, not as a block.
    /// </summary>
    private const double EdgeShare = 0.35, EdgeEm = 0.9;

    /// <summary>How quickly a picture closes the distance to its pixel row (see Place), in Hz.</summary>
    private const double RowHz = 5;

    /// <summary>Share of the settling during which the glow still holds (see Step).</summary>
    private const double GlowHold = 0.06;

    /// <summary>Room in a letter's picture on either side of the letter, in em, for strokes that reach past it.</summary>
    private const double SlackEm = 0.25;

    private readonly Panel _host;
    private readonly Canvas _canvas;
    private readonly Painter _painter;
    private readonly TextBlock _base;
    private readonly TextBlock _sweep;
    private readonly TextBlock? _colorSource;
    private readonly List<Letter> _letters;
    private readonly bool _whole;
    private readonly bool _fromRight;
    private readonly FillReader _fill = new();
    private readonly List<IDisposable> _hidden = new();
    private readonly double _width;
    private readonly double _first;
    private readonly double _height;
    private readonly double _slack;
    private readonly double _baseline;
    private readonly double _top;
    private readonly Color _foreground;

    // The sheet with the letters' pictures, and the pixel grid it was made for: the
    // zoom, and the fraction of a pixel the word starts at sideways. (Up and down needs
    // none: see Place.)
    private RenderTargetBitmap? _sheet;
    private double _alignedX = double.NaN, _alignedScale = 1;
    /// <summary>How far below the top of its box the text sits, and how high the box is.</summary>
    private double _insideY, _boxHeight;
    /// <summary>Where the top of the boxes is right now (it follows the line, see Place).</summary>
    private double _boxTop;
    // Where the word was on the window when its boxes were last put, and for how many
    // frames it has been there since.
    private Matrix _placed;
    private double _placedDpi;
    private bool _hasPlaced;
    private int _steady;
    // The glide to the pixel row (see Place): how far from the true height the pictures
    // are drawn right now, in pixels.
    private Spring _toRow = new(RowHz, 1.0);
    private bool _rowKnown, _onRow;

    // What is painted right now, besides the letters' own values.
    private double _dim;
    private IBrush? _fillBrush;
    private bool _repaint;

    /// <summary>Font size the letter positions were measured at; the owner rebuilds the layer when it changes.</summary>
    public double FontSize { get; }

    /// <summary>True when, after the last step, nothing about the word was grown, glowing or on its way.</summary>
    public bool IsQuiet { get; private set; }

    /// <summary>True when the lyrics colour has changed since the word was drawn: the owner rebuilds the layer.</summary>
    public bool IsStale { get; private set; }

    private LetterLayer(Panel host, Canvas canvas, Painter painter, TextBlock @base, TextBlock sweep, TextBlock? colorSource,
        List<Letter> letters, double width, double fontSize, double top, Color foreground, bool whole, bool fromRight)
    {
        _host = host;
        _canvas = canvas;
        _painter = painter;
        _base = @base;
        _sweep = sweep;
        _colorSource = colorSource;
        _letters = letters;
        _whole = whole;
        _fromRight = fromRight;
        _width = width;
        _first = double.MaxValue;
        foreach (var letter in letters) _first = Math.Min(_first, letter.X);
        _height = @base.Bounds.Height;
        _slack = SlackEm * fontSize;
        _baseline = @base.TextLayout.Baseline;
        _top = top;
        _foreground = foreground;
        _dim = Math.Clamp(@base.Opacity, 0, 1);
        FontSize = fontSize;
    }

    /// <summary>
    /// Builds the layer, or returns null when the word is not laid out yet. Joined or
    /// right-to-left scripts (Arabic, Persian, Hebrew, Indic) shape letters by their
    /// neighbours, so cutting them apart would change how the word is written: those
    /// words, and very long ones, are kept in one piece.
    /// </summary>
    /// <param name="split">False: keep the word in one piece whatever its script (it only floats up, or letters are switched off).</param>
    public static LetterLayer? TryCreate(Panel host, TextBlock @base, TextBlock sweep, TextBlock? glow, bool withGlow, bool split)
    {
        var text = (@base.Text ?? string.Empty).TrimEnd();
        if (text.Length == 0) return null;
        if (@base.Bounds.Width <= 0 || @base.Bounds.Height <= 0) return null;

        var pieces = new List<(int Index, int Length, string Text)>();
        var whole = !split || !IsSplittable(text) || @base.FlowDirection == FlowDirection.RightToLeft;
        if (!whole)
        {
            var each = StringInfo.GetTextElementEnumerator(text);
            while (each.MoveNext())
            {
                var element = each.GetTextElement();
                if (!string.IsNullOrWhiteSpace(element)) pieces.Add((each.ElementIndex, element.Length, element));
            }
            if (pieces.Count > Tuning.MaxLetters) whole = true;
        }
        if (whole)
        {
            pieces.Clear();
            pieces.Add((0, text.Length, text));
        }
        var fromRight = Script.Of(text) == TextDirection.RightToLeft;

        var fontSize = @base.FontSize;
        var height = @base.Bounds.Height;
        var layout = @base.TextLayout;
        var padding = @base.Padding;
        var foreground = SolidColor(glow?.Foreground) ?? SolidColor(@base.Foreground) ?? Colors.White;
        var brush = new ImmutableSolidColorBrush(foreground);
        var typeface = new Typeface(@base.FontFamily, @base.FontStyle, @base.FontWeight, @base.FontStretch);

        // Room around the glow's letter for the blur to fade out in, so it is never cut
        // off at the edge of its own box.
        var blur = 0.16 * fontSize;
        var room = Math.Ceiling(blur * 2.5);

        var letters = new List<Letter>();
        var canvas = new Canvas
        {
            Width = 0,
            Height = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            ClipToBounds = false,
            IsHitTestVisible = false,
            UseLayoutRounding = false,
        };

        double right = 0;
        foreach (var piece in pieces)
        {
            var element = piece.Text;

            // Read the way Noctis' own text is: in a word that reads from the right, a
            // bracket, a comma or a number stands on the other side of the letters than
            // in one that reads from the left (and a bracket is turned round).
            var own = new TextLayout(element, typeface, fontSize: fontSize, foreground: brush,
                flowDirection: @base.FlowDirection,
                letterSpacing: @base.LetterSpacing, fontFeatures: @base.FontFeatures);

            double x = double.MaxValue, end = double.MinValue;
            if (whole)
            {
                // The word without the space after it, as wide as it is really drawn (the
                // bounds of Noctis' layers are fitted to whole screen pixels, which is too
                // coarse here), against the edge its text starts from.
                var ink = own.WidthIncludingTrailingWhitespace;
                var content = @base.Bounds.Width - padding.Left - padding.Right;
                if (ink <= 0 || content <= 0) return null;
                x = @base.FlowDirection == FlowDirection.RightToLeft ? Math.Max(0, content - ink) : 0;
                end = x + ink;
            }
            else foreach (var rect in layout.HitTestTextRange(piece.Index, piece.Length))
            {
                x = Math.Min(x, rect.Left);
                end = Math.Max(end, rect.Right);
            }
            if (x == double.MaxValue || end - x < 0.5) return null;
            var width = end - x;

            TextBlock? halo = null;
            ScaleTransform? haloScale = null;
            TranslateTransform? haloMove = null;
            if (withGlow)
            {
                // Same letter, blurred, moving with the letter (same point of growth),
                // invisible until the letter's turn in the wave.
                haloScale = new ScaleTransform(1, 1);
                haloMove = new TranslateTransform();
                halo = new TextBlock
                {
                    Text = element,
                    FontWeight = @base.FontWeight,
                    FontStyle = @base.FontStyle,
                    FlowDirection = @base.FlowDirection,
                    Foreground = brush,
                    Padding = new Thickness(room),
                    Opacity = 0,
                    IsHitTestVisible = false,
                    ClipToBounds = false,
                    Effect = new BlurEffect { Radius = blur },
                    RenderTransformOrigin = new RelativePoint(room + width / 2, room + height * OriginY, RelativeUnit.Absolute),
                    RenderTransform = new TransformGroup { Children = { haloScale, haloMove } },
                    CacheMode = new BitmapCache(),
                    Tag = "halo",
                };
                Canvas.SetLeft(halo, padding.Left + x - room);
                Canvas.SetTop(halo, padding.Top - room);
            }

            letters.Add(new Letter
            {
                Text = own,
                Halo = halo, HaloScale = haloScale, HaloMove = haloMove,
                X = x, Width = width, Left = padding.Left + x, Top = padding.Top,
            });
            right = Math.Max(right, end);
        }

        if (letters.Count == 0 || right <= 0) return null;

        var first = double.MaxValue;
        foreach (var letter in letters) first = Math.Min(first, letter.X);
        var edge = Math.Min(EdgeEm * fontSize, 0.45 * (right - first));
        foreach (var letter in letters)
        {
            letter.Ahead = Math.Clamp(letter.X - first, 0, BandAheadEm * fontSize);
            letter.Behind = Math.Clamp(right - (letter.X + letter.Width), 0, BandBehindEm * fontSize);
            letter.At = 0.5;
            if (whole || edge <= 0) continue;
            var centre = letter.X + letter.Width / 2;
            letter.At = Curves.Clamp01((centre - first) / (right - first));
            letter.FromBefore = EdgeShare * (1 - Curves.SmoothStep(0, 1, (centre - first) / edge));
            letter.FromAfter = EdgeShare * (1 - Curves.SmoothStep(0, 1, (right - centre) / edge));
        }

        // Glows behind, the word in front.
        foreach (var letter in letters)
            if (letter.Halo is not null) canvas.Children.Add(letter.Halo);
        var painter = new Painter
        {
            Width = @base.Bounds.Width,
            Height = height,
            IsHitTestVisible = false,
            ClipToBounds = false,
            UseLayoutRounding = false,
            Tag = whole ? "word" : "letters",
        };
        canvas.Children.Add(painter);

        var layer = new LetterLayer(host, canvas, painter, @base, sweep, glow, letters, right, fontSize, padding.Top, foreground, whole, fromRight);
        painter.Owner = layer;
        try
        {
            host.Children.Add(canvas);
            layer.Align();

            // Noctis' layers keep their size (so the line does not re-wrap) and keep their
            // own bindings; they only stop painting while the letters stand in for them.
            layer.Hide(@base.SetValue(TextBlock.ForegroundProperty, Brushes.Transparent, BindingPriority.Animation));
            layer.Hide(sweep.SetValue(TextBlock.ForegroundProperty, Brushes.Transparent, BindingPriority.Animation));
            if (glow is not null)
                layer.Hide(glow.SetValue(Visual.OpacityProperty, 0.0, BindingPriority.Animation));
            return layer;
        }
        catch
        {
            // Nothing half-built is left on the word: it is Noctis' own again.
            layer.Dispose();
            throw;
        }
    }

    private void Hide(IDisposable? handle)
    {
        if (handle is not null) _hidden.Add(handle);
    }

    /// <summary>
    /// Draws the sheet: every letter's picture at the size the screen really shows it
    /// at, made so that, painted on a whole screen pixel, its text lands exactly where
    /// Noctis draws its own, pixel for pixel.
    ///
    /// Two things make that necessary. Noctis shows its lyrics panel zoomed (1.1 times
    /// on the lyrics page): a picture made at normal size and then enlarged is soft next
    /// to text drawn at the zoomed size. And the lyrics sit at fractions of a pixel:
    /// live text copes with that letter by letter, but a picture shown between pixels is
    /// smeared over its neighbours. So each picture starts on a whole pixel and its
    /// text is drawn into it at the remaining fraction.
    ///
    /// The pictures are made for where the word will rest. A line that has only just
    /// become current is still growing to its full size and gliding to its place; its
    /// first word is sung meanwhile. So the size and the sideways place are taken from
    /// where the line is heading (see <see cref="Resting"/>), and nothing has to be put
    /// right, visibly, once it gets there. Up and down is looked after every frame by
    /// <see cref="Place"/>.
    /// </summary>
    private void Align()
    {
        var top = TopLevel.GetTopLevel(_host);
        var dpi = top?.RenderScaling ?? 1;
        double ox = 0, zoom = 1;
        var known = false;
        if (top is not null && Straight(_host.TransformToVisual(top), out var now))
        {
            var rest = Resting(_host, top, now);
            zoom = rest.M11;
            ox = rest.M31 * dpi;
            known = true;
        }

        // Screen pixels per unit of the word's own layout.
        var k = zoom * dpi;
        _alignedX = known ? ox - Math.Floor(ox) : double.NaN;
        _alignedScale = k;

        // Up and down, live text is not drawn at its exact height: its baseline is put on
        // the nearest whole pixel row (which keeps the flat tops and bottoms of letters
        // crisp). The text sits in its picture so that its baseline is a whole number of
        // pixels below the top; Place then brings the picture onto the pixel grid.
        var rows = _baseline * k;
        _insideY = (Math.Ceiling(rows - 0.002) - rows) / k;
        var pixelHeight = (int)Math.Ceiling(_height * k + 2);
        _boxHeight = pixelHeight / k;

        // Sideways, a picture starts on the whole pixel at or before where the letter
        // (less its slack) begins; the text makes up the difference inside it.
        var at = 0;
        foreach (var letter in _letters)
        {
            var wantX = ox + (letter.Left - _slack) * k;
            var boxX = Math.Floor(wantX + 0.002);
            var pixelWidth = (int)Math.Ceiling((letter.Width + 2 * _slack) * k + 2);
            letter.Inside = wantX - boxX + _slack * k;
            letter.BoxLeft = letter.Left - letter.Inside / k;
            letter.BoxWidth = pixelWidth / k;
            letter.Source = new Rect(at, 0, pixelWidth, pixelHeight);
            at += pixelWidth + 1;
        }

        var sheet = new RenderTargetBitmap(new PixelSize(Math.Max(1, at), Math.Max(1, pixelHeight)), new Vector(96, 96));
        using (var context = sheet.CreateDrawingContext())
        {
            // Smoothed in grey, as the lyrics are on the page (coloured sub-pixel
            // smoothing needs to know what is behind the text, and a picture does not).
            var options = TextOptions.GetTextOptions(_host);
            using (context.PushTextOptions(new TextOptions
            {
                TextRenderingMode = TextRenderingMode.Antialias,
                TextHintingMode = options.TextHintingMode,
                BaselinePixelAlignment = options.BaselinePixelAlignment,
            }))
            {
                foreach (var letter in _letters)
                {
                    using (context.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(letter.Source.X + letter.Inside, _insideY * k)))
                        letter.Text.Draw(context, default);
                }
            }
        }
        _sheet?.Dispose();
        _sheet = sheet;

        _hasPlaced = false;
        Place(0, lineActive: false);
        _repaint = true;
    }

    /// <summary>
    /// Keeps the pictures on the pixel grid, up and down, without ever making them hop.
    ///
    /// Live text is not drawn at its exact height: its baseline is put on the nearest
    /// whole pixel row at every frame. While a line glides to its place or grows to its
    /// full size, that row changes, and live text makes the change in one hop of a
    /// pixel. A picture has to end up on that same row (it is only sharp there, and it
    /// has to share a baseline with the words next to it), but it need not hop: the
    /// distance to the row is closed in a glide, critically damped, so it never
    /// overshoots. While the line moves fast the row changes at every frame and the
    /// picture simply keeps its own even course; a fifth of a second after the line has
    /// come to rest it sits exactly on the row.
    ///
    /// Should a word come to rest somewhere its pictures were not made for (the window
    /// was zoomed, say), they are made again there, once, while its line is current.
    /// </summary>
    /// <returns>True while the pictures are still on their way to their row.</returns>
    private bool Place(double dt, bool lineActive)
    {
        var top = TopLevel.GetTopLevel(_host);
        if (top is null || !Straight(_host.TransformToVisual(top), out var m)) return false;

        var dpi = top.RenderScaling;
        var settled = _onRow && _toRow.Velocity == 0;
        if (_hasPlaced && m == _placed && dpi == _placedDpi)
        {
            if (_steady < 2 && ++_steady == 2 && lineActive)
            {
                var x = m.M31 * dpi;
                if (!Near(x - Math.Floor(x), _alignedX) || Math.Abs(m.M11 * dpi - _alignedScale) > 0.0005)
                {
                    Align();
                    return false;
                }
            }
            if (settled) return false;
        }
        else
        {
            _placed = m;
            _placedDpi = dpi;
            _hasPlaced = true;
            _steady = 0;
        }

        // How far the row live text puts this baseline on is from where the baseline
        // really is, in pixels (half a pixel at most).
        var k = m.M11 * dpi;
        var exact = m.M32 * dpi + (_top + _baseline) * k;
        var toRow = Math.Round(exact) - exact;
        if (!_rowKnown)
        {
            _rowKnown = true;
            _toRow.Snap(toRow);
        }
        else if (dt > 0)
        {
            _toRow.Step(toRow, dt);
            if (_toRow.IsAsleep(toRow, 0.004)) _toRow.Snap(toRow);
        }
        _onRow = _toRow.Value == toRow;

        var boxTop = _top - _insideY + _toRow.Value / k;
        if (Math.Abs(boxTop - _boxTop) >= 1e-6)
        {
            _boxTop = boxTop;
            _repaint = true;
        }
        return !_onRow;
    }

    /// <summary>True for a place on the window that is only moved and evenly scaled (not turned, mirrored or stretched).</summary>
    private static bool Straight(Matrix? found, out Matrix m)
    {
        m = found ?? Matrix.Identity;
        return found is not null && Math.Abs(m.M12) < 1e-4 && Math.Abs(m.M21) < 1e-4 && m.M11 > 0.05 && Math.Abs(m.M11 - m.M22) < 1e-4;
    }

    /// <summary>
    /// Where the word will be on the window once the transforms above its line that are
    /// still on their way have arrived: Noctis grows a line to full size over half a
    /// second as it becomes current. The way up to the window is walked twice, once as
    /// it is now and once with every such transform at the value it is heading for (the
    /// value under its running transition). If the first walk does not give the place
    /// the word really has (a layout this does not know), no looking ahead is done.
    /// </summary>
    private static Matrix Resting(Visual host, TopLevel top, Matrix now)
    {
        var current = Matrix.Identity;
        var resting = Matrix.Identity;
        var aboveRow = false;
        for (Visual? v = host; v is not null && !ReferenceEquals(v, top); v = v.GetVisualParent())
        {
            // An element that reads the other way round from its parent is shown mirrored
            // (a right-to-left row of words; each word in it is turned back).
            if (v is Control control && Mirrored(control))
            {
                var mirror = new Matrix(-1, 0, 0, 1, v.Bounds.Width, 0);
                current *= mirror;
                resting *= mirror;
            }
            if (v.RenderTransform is { } transform)
            {
                var origin = v.RenderTransformOrigin.ToPixels(v.Bounds.Size);
                var to = Matrix.CreateTranslation(-origin.X, -origin.Y);
                var back = Matrix.CreateTranslation(origin.X, origin.Y);
                var value = transform.Value;
                // Only Noctis' own transitions above the row of words: on the words
                // themselves the value underneath is the one the plugin has replaced.
                var heading = value;
                if (aboveRow && v.GetBaseValue(Visual.RenderTransformProperty) is { HasValue: true } under)
                    heading = under.Value?.Value ?? Matrix.Identity;
                current *= to * value * back;
                resting *= to * heading * back;
            }
            var place = Matrix.CreateTranslation(v.Bounds.X, v.Bounds.Y);
            current *= place;
            resting *= place;
            if (v is ItemsControl row && row.Classes.Contains("word-layer")) aboveRow = true;
        }

        return Same(current, now) && Straight(resting, out var rest) ? rest : now;
    }

    private static bool Mirrored(Control control)
    {
        var parent = control.GetVisualParent() as Control ?? control.Parent as Control;
        var parentRightToLeft = parent is not null && parent.FlowDirection == FlowDirection.RightToLeft;
        return (control.FlowDirection == FlowDirection.RightToLeft) != parentRightToLeft;
    }

    private static bool Same(Matrix a, Matrix b) =>
        Math.Abs(a.M11 - b.M11) < 1e-6 && Math.Abs(a.M22 - b.M22) < 1e-6 && Math.Abs(a.M12 - b.M12) < 1e-6
        && Math.Abs(a.M21 - b.M21) < 1e-6 && Math.Abs(a.M31 - b.M31) < 1e-3 && Math.Abs(a.M32 - b.M32) < 1e-3;

    private static bool Near(double a, double b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, 1 - d) < 0.01;
    }

    /// <summary>Advances the letters by one frame.</summary>
    /// <param name="progress">Noctis' sweep progress for the word (its own sentinels far before / after).</param>
    /// <param name="reach">How far a sung word floats up, in pixels.</param>
    /// <param name="grow">How much a letter grows when the fill has reached it (0.05 = 5%).</param>
    /// <param name="growBefore">The same for the word before this one.</param>
    /// <param name="growAfter">The same for the word after this one.</param>
    /// <param name="glowPeak">Opacity of a letter's glow at its fullest.</param>
    /// <param name="clock">Seconds since the word began to be sung; it runs on after the word.</param>
    /// <param name="settleStart">When, on that clock, the word's first letter starts to relax.</param>
    /// <param name="settleSpread">How much later its last letter does.</param>
    /// <param name="settleSec">How long a letter takes to relax.</param>
    /// <param name="tail">How much of a letter's band is left standing past the end of its word (0 to 1, see <see cref="Tuning.Tail"/>).</param>
    /// <param name="riseFloor">The shortest time a letter takes to come up, in seconds; 0 = exactly with the fill.</param>
    /// <param name="lineKeep">1 while the line is current; runs to 0 as the line is let go.</param>
    /// <param name="arrive">For a word in one piece: 0 to 1 as it is sung (its rise, growth and glow follow it).</param>
    /// <param name="durationSec">How long the word is sung.</param>
    /// <param name="lineActive">True while the word's line is the current one.</param>
    /// <returns>True while a letter is still moving.</returns>
    public bool Step(double dt, double progress, double reach, double grow, double growBefore, double growAfter, double glowPeak,
        double clock, double settleStart, double settleSpread, double settleSec, double tail, double riseFloor,
        double lineKeep, double arrive, double durationSec, bool lineActive)
    {
        var placing = Place(dt, lineActive);

        // The lyrics colour can change while a word is up (new artwork, another theme).
        // Noctis' glow layer still carries it; the word is then drawn anew by its owner.
        var foreground = (_colorSource is not null ? SolidColor(_colorSource.Foreground) : null) ?? _foreground;
        if (foreground != _foreground) IsStale = true;

        // Noctis' base layer still runs its own fade between full brightness (line not
        // current) and the dimmed level (line current), even though it paints nothing
        // right now: unsung letters follow it, so they match the words around them.
        var dim = Math.Clamp(_base.Opacity, 0, 1);
        if (Math.Abs(dim - _dim) > 0.002)
        {
            _dim = dim;
            _repaint = true;
        }

        var before = progress <= -1.5;
        var after = progress >= 2.5;
        var edge = before ? double.NegativeInfinity : after ? double.PositiveInfinity : _first + progress * (_width - _first);

        // What one frame of normal play moves the fill's edge by, as a share of the word.
        var pace = durationSec > 0 ? dt / durationSec : 1;

        // The fill's edge for the motion: the same edge, but it runs on past the end of
        // the word at the pace the word was sung at, so the last letters finish coming
        // up while the next word has already begun. (The light does not: a word is fully
        // lit exactly when it is over.)
        var runOn = _first + (durationSec > 0 ? clock / durationSec : 1) * (_width - _first);
        var most = riseFloor > 0 ? dt / riseFloor : double.PositiveInfinity;

        var moving = false;
        var glowing = false;
        double total = 0;
        foreach (var letter in _letters)
        {
            // A letter "arrives" while a soft band passes over it: the white fills in
            // the grey from left to right as it does on every word, centred on the
            // voice. The band is a little wider than Noctis' own (0.25em either side),
            // so the arrival is unhurried and neighbouring letters overlap a lot.
            // A word in one piece arrives as a whole, as far as it has been sung.
            double lit, step;
            if (_whole)
            {
                lit = before ? 0 : after ? 1 : arrive;
                step = 2.5 * dt / Math.Max(durationSec, Tuning.MinRiseSec) + 0.02;
            }
            else
            {
                var span = letter.Width + letter.Ahead + letter.Behind;
                lit = Curves.Clamp01((edge + letter.Ahead - letter.X) / span);
                step = 2.5 * pace * (_width - _first) / span + 0.02;
            }
            var arrived = Curves.Clamp01(letter.Arrival.Step(lit, dt, step));
            if (!letter.Arrival.IsSettled) moving = true;

            // Its turn has come: from here on the letter is a picture.
            if (!letter.Imaged && arrived > 0)
            {
                letter.Imaged = true;
                _repaint = true;
            }

            // The light is exactly the fill. The rise and the size start with it, on the
            // note. In the crisp look they are the light: every word is finished when its
            // note is. Otherwise they follow the same band, only never faster than the
            // eye can follow and with the band left standing (in part or in full) where
            // the word ends: the last letters of a word finish a moment into the next one.
            letter.Light = Curves.SmoothStep(0, 1, arrived);
            if (_whole) letter.Rise = Curves.SmootherStep(arrived);
            else if (tail <= 0 && riseFloor <= 0) letter.Rise = letter.Light;
            else
            {
                var behind = letter.Behind + Curves.Clamp01(tail) * (BandBehindEm * FontSize - letter.Behind);
                var want = before ? 0 : Curves.Clamp01((runOn + letter.Ahead - letter.X) / (letter.Width + letter.Ahead + behind));
                if (!letter.MotionKnown)
                {
                    letter.MotionKnown = true;
                    letter.Motion = want;
                }
                else if (letter.Motion != want)
                {
                    letter.Motion += Math.Clamp(want - letter.Motion, -most, most);
                    moving = true;
                }
                letter.Rise = Curves.SmoothStep(0, 1, letter.Motion);
            }

            // What comes down again (the growth and the glow) does so letter after letter
            // in the order they were sung, unhurried, each on the same curve: the release
            // passes through the word and on into the next one. It is faster if the line
            // itself is let go first. The glow is held a moment longer than the size: a
            // change of light is noticed sooner than a change of a pixel or two, and to
            // the eye this is what makes the two start together.
            var settle = Curves.Clamp01((clock - settleStart - (_whole ? 0 : letter.At) * settleSpread) / settleSec);
            var keepSize = (1 - Curves.SmootherStep(settle)) * lineKeep;
            letter.Keep = (1 - Curves.SmootherStep((settle - GlowHold) / (1 - GlowHold))) * lineKeep;

            var amount = grow + letter.FromBefore * (growBefore - grow) + letter.FromAfter * (growAfter - grow);
            letter.Grow = amount * letter.Rise * keepSize * (_whole ? Tuning.WholeWordShare : 1);
            total += letter.Grow * letter.Width;
        }

        if (_whole)
        {
            // The white fills in the grey exactly as Noctis sweeps it (its brush still
            // runs underneath), from the right for a right-to-left word.
            var fill = _fill.Read(_sweep, _fromRight, out var changed);
            if (changed)
            {
                _fillBrush = fill;
                _repaint = true;
            }
        }

        double passed = 0;
        foreach (var letter in _letters)
        {
            if (!_whole && Math.Abs(letter.Shown - letter.Light) > 0.002)
            {
                letter.Shown = letter.Light;
                _repaint = true;
            }

            // Grown letters make room for themselves: each is pushed by what the ones
            // before it have grown, the word as a whole staying centred where it was.
            var s = 1 + letter.Grow;
            var x = passed + letter.Grow * letter.Width / 2 - total / 2;
            passed += letter.Grow * letter.Width;
            var y = -reach * letter.Rise - Tuning.LiftPerGrow * FontSize * letter.Grow;
            if (Math.Abs(letter.Scale - s) > 0.00005 || (letter.Grow == 0 && letter.Scale != 1))
            {
                letter.Scale = letter.Grow == 0 ? 1 : s;
                _repaint = true;
                if (letter.HaloScale is not null) letter.HaloScale.ScaleX = letter.HaloScale.ScaleY = letter.Scale;
            }
            if (Math.Abs(letter.MoveY - y) > 0.005 || (y != letter.MoveY && (letter.Rise == 0 || letter.Rise == 1) && letter.Grow == 0))
            {
                letter.MoveY = y;
                _repaint = true;
                if (letter.HaloMove is not null) letter.HaloMove.Y = y;
            }
            if (Math.Abs(letter.MoveX - x) > 0.005 || (x == 0 && letter.MoveX != 0))
            {
                letter.MoveX = x;
                _repaint = true;
                if (letter.HaloMove is not null) letter.HaloMove.X = x;
            }

            // A letter that has to move is a picture from that moment on, also one the
            // fill has not reached yet: the letters before it make room for themselves as
            // they grow and push it along. (Live text is only ever drawn where Noctis
            // drew it: left as live text, the letter stayed behind and hopped into its
            // place when its turn came, further the longer the word and the more it grows.)
            if (!letter.Imaged && (letter.MoveX != 0 || letter.MoveY != 0 || letter.Scale != 1))
            {
                letter.Imaged = true;
                _repaint = true;
            }

            if (letter.Halo is not null)
            {
                var glow = Math.Clamp(glowPeak * letter.Light * letter.Keep, 0, 1);
                if (Math.Abs(letter.Halo.Opacity - glow) > 0.002) letter.Halo.Opacity = glow;
                if (glow > 0.002) glowing = true;
            }
        }

        if (_repaint)
        {
            _repaint = false;
            _painter.InvalidateVisual();
        }

        IsQuiet = !moving && !glowing && total == 0;
        return moving || placing;
    }

    /// <summary>
    /// Takes the layer once through being sung, settled and painted, into a scrap
    /// picture nobody sees (see <see cref="Surface"/>'s rehearsal). The layer is thrown
    /// away afterwards.
    /// </summary>
    public void Rehearse()
    {
        Step(1.0 / 240, 0.5, 1.5, 0.02, 0.03, 0.01, 0.2, 0.15, 0.6, 0.2, 1, 0.5, 0.12, 1, 0.5, 0.3, lineActive: false);
        Step(1.0 / 240, 3, 1.5, 0.02, 0.03, 0.01, 0.2, 1.1, 0.6, 0.2, 1, 0.5, 0.12, 0.5, 1, 0.3, lineActive: false);
        using var scrap = new RenderTargetBitmap(new PixelSize(8, 8), new Vector(96, 96));
        using var context = scrap.CreateDrawingContext();
        Paint(context);
    }

    /// <summary>
    /// Paints the word: a letter that has not had its turn as live text at the dimmed
    /// level; one that has, as its picture, twice. Noctis draws a word as two layers,
    /// dimmed text and the bright sweep on top, and so is each letter here: it then
    /// looks the same as the words around it in every state, also while its line fades
    /// in or out (a single layer fades darker than two stacked ones).
    /// </summary>
    private void Paint(DrawingContext context)
    {
        var sheet = _sheet;
        using var smooth = context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality });
        foreach (var letter in _letters)
        {
            if (!letter.Imaged || sheet is null)
            {
                if (_dim > 0.002)
                {
                    using (context.PushOpacity(_dim))
                        letter.Text.Draw(context, new Point(letter.Left, letter.Top));
                }
                continue;
            }

            var box = new Rect(letter.BoxLeft, _boxTop, letter.BoxWidth, _boxHeight);
            var moved = letter.Scale != 1 || letter.MoveX != 0 || letter.MoveY != 0;
            DrawingContext.PushedState transform = default;
            if (moved)
            {
                // Grown from a point just above its baseline, then moved.
                var cx = letter.Left + letter.Width / 2;
                var cy = _boxTop + _insideY + _height * OriginY;
                transform = context.PushTransform(Matrix.CreateTranslation(-cx, -cy)
                    * Matrix.CreateScale(letter.Scale, letter.Scale)
                    * Matrix.CreateTranslation(cx + letter.MoveX, cy + letter.MoveY));
            }

            if (_dim > 0.002)
            {
                using (context.PushOpacity(_dim))
                    context.DrawImage(sheet, letter.Source, box);
            }

            if (!_whole)
            {
                if (letter.Shown > 0.002)
                {
                    using (context.PushOpacity(letter.Shown))
                        context.DrawImage(sheet, letter.Source, box);
                }
            }
            else if (_fillBrush is ISolidColorBrush solid)
            {
                var alpha = solid.Color.A / 255.0 * Math.Clamp(solid.Opacity, 0, 1);
                if (alpha > 0.002)
                {
                    using (context.PushOpacity(alpha))
                        context.DrawImage(sheet, letter.Source, box);
                }
            }
            else if (_fillBrush is not null)
            {
                // Noctis' own sweep, laid over the word's picture the way Noctis lays
                // it over the word's text.
                using (context.PushOpacityMask(_fillBrush, new Rect(letter.Left, _boxTop + _insideY, letter.Width, _height)))
                    context.DrawImage(sheet, letter.Source, box);
            }

            if (moved) transform.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var handle in _hidden) handle.Dispose();
        _hidden.Clear();
        _painter.Owner = null;
        _host.Children.Remove(_canvas);
        _sheet?.Dispose();
        _sheet = null;
    }

    internal static Color? SolidColor(IBrush? brush) => brush is ISolidColorBrush solid
        ? Color.FromArgb((byte)Math.Round(solid.Color.A * Math.Clamp(solid.Opacity, 0, 1)), solid.Color.R, solid.Color.G, solid.Color.B)
        : null;

    private static bool IsSplittable(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v < 0x0250) continue;                       // Latin, incl. accents
            if (v >= 0x0300 && v <= 0x036F) continue;       // combining accents
            if (v >= 0x0370 && v <= 0x052F) continue;       // Greek, Cyrillic
            if (v >= 0x1E00 && v <= 0x1FFF) continue;       // Latin additional, Greek extended
            if (v >= 0x2000 && v <= 0x206F) continue;       // general punctuation
            if (v >= 0x3000 && v <= 0x30FF) continue;       // CJK punctuation, kana
            if (v >= 0x3400 && v <= 0x4DBF) continue;       // CJK extension A
            if (v >= 0x4E00 && v <= 0x9FFF) continue;       // CJK
            if (v >= 0xAC00 && v <= 0xD7AF) continue;       // Hangul syllables
            if (v >= 0xFF00 && v <= 0xFFEF) continue;       // full-width forms
            return false;
        }
        return true;
    }
}
