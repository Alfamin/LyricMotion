using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LyricMotion;

/// <summary>
/// The motion of one word cell on a lyrics surface. It reads the word's sweep progress
/// (the value Noctis already updates every frame for its colour sweep) and turns it
/// into a calm, one-way glide: the word floats up as it is sung and stays there until
/// its line is over, and its letters grow a touch as the fill reaches them (more, and
/// with a glow, on a held word). Nothing bounces and nothing comes back down mid-line.
/// The moving word is redrawn by a <see cref="LetterLayer"/>. Everything is applied
/// as an override on top of Noctis' own styling and removed again on Dispose.
/// </summary>
internal sealed class WordRig : IDisposable
{
    private readonly object _word;
    private readonly WordAccess _access;
    private readonly Panel _cell;
    private readonly Panel? _inner;
    private readonly TextBlock? _base;
    private readonly TextBlock? _sweep;
    private readonly TextBlock? _glow;
    private readonly bool _isLast;
    private readonly bool _isBackground;
    private readonly double _durationMs;
    // How long the words before and after this one are sung (below zero: there is none).
    private readonly double _beforeMs, _afterMs;
    private readonly bool _afterIsLast;

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _move = new();
    private IDisposable? _transformOverride;
    private IDisposable? _transitionsOverride;
    private SmoothText? _smoothText;
    private IDisposable? _cacheOverride;
    private readonly bool _nativeGlow;
    private readonly ItemsControl? _row;

    // A right-to-left word fills in from the right.
    private readonly bool _still;
    private readonly bool _fillFromRight;
    private IDisposable? _fillOverride;
    private readonly FillReader _fill = new();

    // The motion itself is a pure function of how far the word has been sung, so it is
    // exactly in step with the highlight. These followers add no delay in normal play;
    // they only glide across a sudden change (a seek), critically damped, so they
    // never overshoot.
    private Follower _lift = new(9, 0.004);
    private Follower _grow = new(9, 0.00005);

    private bool _wasActive = true;
    private double _sinceRelease;
    private double _clock = double.PositiveInfinity;
    private bool _seen;

    private LetterLayer? _letters;
    private int _letterAttempts;
    // True for a redrawn word of a finished line that only waits to be handed back.
    private bool _parked;

    /// <summary>How long a word is sung, in milliseconds (a word without a time of its own counts as an ordinary one).</summary>
    public static double DurationOf(object word, WordAccess access)
    {
        var held = access.HeldMs?.Invoke(word) ?? 0;
        return held > 1 ? held : 320;
    }

    public WordRig(object word, WordAccess access, Panel cell, int index, int count, bool isBackground, Tuning tuning, bool still,
        double beforeMs, double afterMs)
    {
        _word = word;
        _access = access;
        _cell = cell;
        _isLast = index == count - 1;
        _beforeMs = beforeMs;
        _afterMs = afterMs;
        _afterIsLast = index == count - 2;
        _isBackground = isBackground;
        _still = still;
        _fillFromRight = tuning.RightToLeft && Script.Of(access.Text?.Invoke(word)) == TextDirection.RightToLeft;

        // Noctis' cell: a Viewbox holding a Panel with the glow, the dimmed base and
        // the bright sweep overlay. Parts that are not found are simply not used.
        _inner = (cell.Children.Count > 0 ? cell.Children[0] as Viewbox : null)?.Child as Panel;
        if (_inner is not null)
        {
            foreach (var child in _inner.Children)
            {
                if (child is TextBlock text)
                {
                    if (text.Classes.Contains("word-base")) _base = text;
                    else _sweep = text;
                }
                else if (child is Canvas canvas)
                {
                    foreach (var inCanvas in canvas.Children)
                        if (inCanvas is TextBlock glowText) _glow = glowText;
                }
            }
        }

        _durationMs = DurationOf(word, access);
        if (_still) return;   // no motion on this list: only the direction of the fill is looked after

        // Replaces Noctis' own transform on the cell, including the one-step "pop" it
        // gives held words: the glide and the letter wave take its place.
        // Noctis animates that pop with a 0.8 s transition on the cell. A running
        // transition writes its own values over ours and lets go of them when it ends,
        // which showed as a second pop under the word and a one-frame drop 0.8 s in.
        // So the cell's transitions are parked for as long as the word is ours.
        _transitionsOverride = cell.SetValue(Animatable.TransitionsProperty, null, BindingPriority.Animation);
        var transform = new TransformGroup { Children = { _scale, _move } };
        _transformOverride = cell.SetValue(Visual.RenderTransformProperty, transform, BindingPriority.Animation);
        _nativeGlow = access.IsEmphasis?.Invoke(word) ?? false;

        // The row of words this one sits in: Noctis fades it when the line starts and ends.
        foreach (var ancestor in cell.GetVisualAncestors())
        {
            if (ancestor is ItemsControl row && row.Classes.Contains("word-layer")) { _row = row; break; }
        }

        // Words are redrawn one at a time, each when its turn is near (Noctis starts a
        // word's progress shortly before it is sung), never a whole line at once.
        var progress = access.Progress(word);
        if (progress > -1.5 && progress < 1) TryBuildLetters(tuning);
    }

    private enum Drawn
    {
        /// <summary>Noctis' own text, untouched.</summary>
        Plain,
        /// <summary>Live text, freed from the pixel grid.</summary>
        Smooth,
        /// <summary>One image of the word, moved and scaled by the graphics card.</summary>
        Image,
    }

    /// <summary>
    /// How the word as a whole is drawn while it is not redrawn by a
    /// <see cref="LetterLayer"/>: when it rests (not sung yet, or handed back), and as
    /// the way out should a word ever not be redrawable.
    ///
    /// A word that is not moving is always Noctis' own text, exactly as Noctis draws it
    /// (only shifted by whole pixels once it has been sung).
    ///
    /// A moving word is drawn once into an image, which the graphics card moves and
    /// scales perfectly evenly; text redrawn at every new size comes out in uneven steps
    /// (the letters are fitted to the pixel grid each time). An image is only right
    /// while the line is at full strength, though: Noctis draws a word as two layers
    /// (dimmed text and the bright sweep on top), and while the line fades each layer
    /// fades on its own; one flat image of both comes out darker. So during a fade,
    /// and for the words Noctis itself glows (the image is only as big as the word and
    /// would cut the glow off), a moving word is drawn live instead, with the pixel
    /// fitting switched off.
    /// </summary>
    private void Draw(Drawn how)
    {
        if (how == Drawn.Image && _nativeGlow) how = Drawn.Smooth;
        if (how == Drawn.Image)
        {
            if (_cacheOverride is not null) return;
            _smoothText?.Dispose();
            _smoothText = null;
            // On the cell itself: the image is moved and scaled by the transform of the
            // element that owns it. (Put on a child, it is redrawn under the parent's
            // transform instead, pixel steps and all.)
            _cacheOverride = _cell.SetValue(Visual.CacheModeProperty, Images.New(), BindingPriority.Animation);
            return;
        }

        _cacheOverride?.Dispose();
        _cacheOverride = null;
        if (how == Drawn.Smooth) _smoothText ??= new SmoothText(_cell);
        else
        {
            _smoothText?.Dispose();
            _smoothText = null;
        }
    }

    /// <summary>
    /// Every word that moves is redrawn by a <see cref="LetterLayer"/>: in letters when
    /// its letters grow (each as the fill reaches it), in one piece when it only floats
    /// up or cannot be cut into letters. Either way it is drawn the same way from the
    /// moment before it starts until it is handed back, so there is no point at which
    /// it changes from one kind of drawing to another while it is looked at.
    /// </summary>
    private void TryBuildLetters(Tuning tuning)
    {
        if (_letters is not null || _letterAttempts > 40) return;
        if (_inner is null || _base is null || _sweep is null) return;
        _letterAttempts++;
        var held = _durationMs >= tuning.HeldMs;
        var split = tuning.LetterWave && (tuning.Flow || tuning.Grow(_durationMs, _isLast) >= Tuning.LetterGrowFloor);
        try
        {
            _letters = LetterLayer.TryCreate(_inner, _base, _sweep, _glow, tuning.Glow && held, split);
        }
        catch
        {
            // This word cannot be redrawn (a font or a layout this does not know): it is
            // not tried again and is moved as a whole instead (see Step).
            _letters = null;
            _letterAttempts = int.MaxValue / 2;
        }
        // The layer is drawn outside the word's own box and shows the fill itself: the
        // whole-word image and the turned fill are not needed next to it.
        if (_letters is not null)
        {
            Draw(Drawn.Plain);
            _fillOverride?.Dispose();
            _fillOverride = null;
        }
    }

    /// <summary>Builds the word's redrawn layer and runs it once, unseen (see <see cref="Surface"/>'s rehearsal).</summary>
    public void Rehearse(Tuning tuning)
    {
        if (_still) return;
        TryBuildLetters(tuning);
        _letters?.Rehearse();
    }

    /// <summary>The value the surface watches to notice a seek while the animation loop is idle.</summary>
    public double Progress => _access.Progress(_word);

    /// <summary>The size the word's text is set in.</summary>
    public double FontSize => _base?.FontSize ?? 46;

    /// <summary>Advances the word by one frame. Returns true while anything is still moving.</summary>
    /// <param name="lineBlurred">True when Noctis shows the word's line blurred (a line that is over).</param>
    /// <param name="lineSpeed">How fast the line is travelling across the window, in text heights per second.</param>
    public bool Step(double dt, bool lineActive, bool lineBlurred, double lineSpeed, double pixelScale, Tuning tuning)
    {
        if (_fillFromRight && _letters is null) FillFromRight();
        if (_still) return false;

        var progress = _access.Progress(_word);
        var fontSize = _base?.FontSize ?? 46;

        if (_letters is not null && (Math.Abs(_letters.FontSize - fontSize) > 0.01 || _letters.IsStale))
        {
            // The window was resized or the lyrics changed colour: what was drawn is stale.
            _letters.Dispose();
            _letters = null;
            _parked = false;
            if (_letterAttempts < 1000) _letterAttempts = 0;
        }
        if (_letters is null && lineActive && progress > -1.5 && progress < 1) TryBuildLetters(tuning);

        // The height a sung word rests at is a whole number of screen pixels, the same
        // for every word of the line: sung text sits on one clean, sharp baseline.
        var reach = Math.Max(1, Math.Round(tuning.LiftEm * fontSize * pixelScale)) / pixelScale;
        if (_isBackground) reach = Math.Max(1, Math.Round(reach * 0.6 * pixelScale)) / pixelScale;

        // The word's own clock, in seconds since it began. While it is sung this is
        // exactly the highlight's position; afterwards it simply runs on, so that what
        // comes down can take its time and need not be finished when the voice moves on.
        var durationSec = _durationMs / 1000;
        if (progress >= 1)
        {
            if (_seen) _clock += dt;
            else _clock = double.PositiveInfinity;   // already sung when we got here: nothing to play out
        }
        else if (progress > 0)
        {
            _seen = true;
            _clock = progress * durationSec;
        }
        else
        {
            _seen = false;
            _clock = 0;
        }

        // The line being let go: 1 while it is current, down to 0 as Noctis dims it.
        double lineKeep = 1;
        if (lineActive) _wasActive = true;
        else
        {
            if (_wasActive)
            {
                _wasActive = false;
                _sinceRelease = 0;
            }
            _sinceRelease += dt;
            lineKeep = 1 - Curves.SmootherStep(_sinceRelease / Tuning.LineReleaseSec);
        }

        // A word relaxing after it was sung: 0 while it is sung and for a breath after,
        // 1 when it is back to its own size.
        var held = _durationMs >= tuning.HeldMs;
        var amount = tuning.Grow(_durationMs, _isLast);
        var amountBefore = _beforeMs < 0 || !tuning.Flow ? amount : tuning.Grow(_beforeMs, false);
        var amountAfter = _afterMs < 0 || !tuning.Flow ? amount : tuning.Grow(_afterMs, _afterIsLast);
        // The first letter starts to relax at settleStart, the last one a little later.
        var settleStart = Tuning.SettleStartSec(durationSec);
        var settleSec = Tuning.SettleSec(durationSec);
        var settleSpread = _letters is not null && tuning.Flow ? Tuning.SettleSpreadSec(durationSec) : 0;
        var settle = Curves.Clamp01((_clock - settleStart - settleSpread) / settleSec);

        // How far the word has come up: with the fill, but never faster than a quick
        // word can be followed by the eye (it then finishes just after its note).
        var riseSec = tuning.RiseSec(durationSec);
        var arrive = progress > 0 ? Curves.Clamp01(_clock / riseSec) : 0;

        if (_letters is not null)
        {
            // The word is moved by its redrawn letters (or its one redrawn piece) alone;
            // the cell stays put, so nothing is redrawn while it moves.
            SetCell(0, 1);
            if (lineActive) _parked = false;
            if (!_parked)
            {
                var busy = _letters.Step(dt, progress, reach, amount, amountBefore, amountAfter,
                    held ? tuning.HeldGlow(_durationMs, _isLast) : 0,
                    _clock, settleStart, settleSpread, settleSec, tuning.Tail, tuning.LetterRise, lineKeep, arrive, durationSec, lineActive);
                // Still coming up or playing out after the note, or fading with the line that was let go.
                var grows = amount > 0 || amountBefore > 0 || amountAfter > 0;
                var playing = _seen && progress >= 1 && (arrive < 1 || (grows && settle < 1 && lineKeep > 0));
                if (grows && !lineActive && _sinceRelease < Tuning.LineReleaseSec + 0.05 && !_letters.IsQuiet) playing = true;
                busy |= playing;

                // A line that is over is dimmed and blurred by Noctis from its very first
                // frame. Under that blur a redrawn word cannot be told from Noctis' own
                // text, and a redrawn word that is kept in such a line costs a little on
                // every frame. So once the word has nothing left to play out it goes back
                // to Noctis' text, still raised: the cell carries the two pixels from here on.
                if (lineActive || !lineBlurred || playing || !_letters.IsQuiet) return busy;

                // But only at a moment when the change cannot be seen: as the line ends,
                // or while it travels (it has come back down by then, see LineReleaseSec;
                // failing that, the next time a line starts). Nothing about a word that
                // waits for that changes, so once it has settled it is not looked at again.
                if (_sinceRelease > Tuning.HandBackSec && lineSpeed < Tuning.HandBackSpeed)
                {
                    _parked = !busy && _sinceRelease > Tuning.LineReleaseSec + 0.15;
                    return busy;
                }
            }
            else if (lineSpeed < Tuning.HandBackSpeed) return false;

            _parked = false;
            _letters.Dispose();
            _letters = null;
            _lift = new Follower(9, 0.004);
            _grow = new Follower(9, 0.00005);
        }

        // What follows is only for a word that is not redrawn: one that is waiting for
        // its turn or was already sung when we got here (it simply rests, on the line
        // or at its full height), and, should Noctis' layout ever stop being what the
        // redrawing expects, the plain way of moving the cell itself.
        // Once sung a word stays up, also after its line has ended: sliding every word
        // back down while the line is already scrolling away and blurring made the end
        // of a line restless. The two pixels are given back when the word is handed
        // back to Noctis, lines later.
        double target = 0, growTarget = 0;
        if (progress >= 1 && arrive >= 1) target = -reach;
        else if (progress > 0)
        {
            target = -reach * Curves.SmootherStep(arrive);
            if (!held && lineActive && progress < 1) growTarget = tuning.Swell * Tuning.SwellWeight(_durationMs) * Curves.Hill(progress);
        }

        // A held word that is not redrawn grows as a whole and relaxes the same way.
        if (held)
        {
            growTarget = tuning.HeldGrow(_durationMs, _isLast) * Tuning.WholeWordShare
                         * Curves.SmootherStep(arrive) * (1 - Curves.SmootherStep(settle)) * lineKeep;
        }

        var lift = _lift.Step(target, dt, 4.7 * reach * dt / riseSec + 0.02);
        var gliding = !_lift.IsSettled;
        var grow = _grow.Step(growTarget, dt, 0.6 * dt + 0.0003);
        var growing = !_grow.IsSettled;
        var moving = gliding || growing;

        // Still playing out after the note, or fading with the line that was let go.
        if (_seen && progress >= 1 && (arrive < 1 || (held && settle < 1 && lineKeep > 0))) moving = true;
        if (held && !lineActive && _sinceRelease < Tuning.LineReleaseSec + 0.05) moving = true;

        // At rest: on the line or at its full height, at its own size.
        var atRest = !gliding && !growing && grow == 0 && (target == 0 || target == -reach);
        var steady = lineActive && (_row is null || _row.Opacity >= 0.995);
        Draw(atRest ? Drawn.Plain : steady ? Drawn.Image : Drawn.Smooth);
        SetCell(lift, 1 + grow);

        return moving;
    }

    /// <summary>
    /// Noctis fills every word in from its left edge. For a right-to-left word the same
    /// fill is shown from the right: Noctis' own brush, read from underneath ours and
    /// turned round, so its width, softness and timing stay exactly Noctis'.
    /// </summary>
    private void FillFromRight()
    {
        if (_sweep is null) return;
        var fill = _fill.Read(_sweep, fromRight: true, out var changed);
        if (!_fill.IsTurned)
        {
            // One flat colour (not sung yet, or sung): there is nothing to turn.
            _fillOverride?.Dispose();
            _fillOverride = null;
            return;
        }
        if (!changed && _fillOverride is not null) return;

        var before = _fillOverride;
        _fillOverride = _sweep.SetValue(TextBlock.ForegroundProperty, fill, BindingPriority.Animation);
        before?.Dispose();
    }

    private void SetCell(double lift, double scale)
    {
        if (_move.Y != lift) _move.Y = lift;
        if (_scale.ScaleX != scale) { _scale.ScaleX = scale; _scale.ScaleY = scale; }
    }

    public void Dispose()
    {
        _fillOverride?.Dispose();
        _fillOverride = null;
        _letters?.Dispose();
        _letters = null;
        Draw(Drawn.Plain);
        // The transform goes back first, while the transitions are still parked, so
        // handing the word back does not itself start one.
        _transformOverride?.Dispose();
        _transformOverride = null;
        _transitionsOverride?.Dispose();
        _transitionsOverride = null;
    }
}

/// <summary>
/// Lets text move by fractions of a pixel. Normally text is locked to the pixel grid
/// (its baseline snapped to whole pixels, each letter's shape hinted to them), which is
/// right for still text and wrong for moving text: a two-pixel glide becomes two visible
/// jumps and letters shiver as they grow. While a word is animated the lock is lifted
/// for its cell; Dispose puts the cell's own setting back.
/// </summary>
internal sealed class SmoothText : IDisposable
{
    private readonly Visual _target;
    private readonly TextOptions _before;
    private bool _applied;

    public SmoothText(Visual target)
    {
        _target = target;
        _before = TextOptions.GetTextOptions(target);

        TextOptions.SetTextOptions(target, new TextOptions
        {
            TextRenderingMode = _before.TextRenderingMode,
            TextHintingMode = TextHintingMode.None,
            BaselinePixelAlignment = BaselinePixelAlignment.Unaligned,
        });
        _applied = true;
    }

    public void Dispose()
    {
        if (!_applied) return;
        _applied = false;
        TextOptions.SetTextOptions(_target, _before);
    }
}

/// <summary>
/// Reads the fill Noctis is sweeping across a word right now (the brush it gives the
/// word's bright layer, whatever the plugin has put on top of it), turned round for a
/// word that fills in from the right.
/// </summary>
internal sealed class FillReader
{
    private IBrush? _source;
    private double _signature = double.NaN;
    private IBrush? _result;

    /// <summary>True when the last brush read was a sweep that had to be turned round.</summary>
    public bool IsTurned { get; private set; }

    public IBrush? Read(TextBlock sweep, bool fromRight, out bool changed)
    {
        var source = sweep.GetBaseValue(TextBlock.ForegroundProperty) is { HasValue: true } value ? value.Value : null;

        double signature = 0;
        var gradient = source as ILinearGradientBrush;
        if (gradient is not null)
        {
            signature = gradient.GradientStops.Count;
            foreach (var stop in gradient.GradientStops)
                signature = signature * 31 + stop.Offset * 1000 + stop.Color.A;
        }
        else if (source is ISolidColorBrush solid) signature = solid.Color.ToUInt32() + solid.Opacity;

        changed = !ReferenceEquals(source, _source) || signature != _signature;
        if (!changed) return _result;
        _source = source;
        _signature = signature;

        IsTurned = fromRight && gradient is not null;
        if (gradient is null) return _result = source;

        // Always a brush of our own: a new one each time the fill has moved on, so
        // whatever shows it is redrawn the same way in both directions.
        var turned = new LinearGradientBrush
        {
            StartPoint = fromRight ? Turned(gradient.StartPoint) : gradient.StartPoint,
            EndPoint = fromRight ? Turned(gradient.EndPoint) : gradient.EndPoint,
            SpreadMethod = gradient.SpreadMethod,
            Opacity = gradient.Opacity,
        };
        foreach (var stop in gradient.GradientStops)
            turned.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
        return _result = turned;
    }

    private static RelativePoint Turned(RelativePoint point) => point.Unit == RelativeUnit.Relative
        ? new RelativePoint(1 - point.Point.X, point.Point.Y, RelativeUnit.Relative)
        : point;
}

/// <summary>The image a moving word or letter is drawn into.</summary>
internal static class Images
{
    // Smoothed in grey, as the lyrics are on the page.
    public static BitmapCache New() => new() { EnableClearType = false };
}

/// <summary>All word rigs of one lyric line, from the moment it becomes current until it has settled again.</summary>
internal sealed class LineRig : IDisposable
{
    private readonly List<WordRig> _words;
    private readonly List<Control> _above = new();

    public object Line { get; }

    /// <summary>How many lines have started since this one stopped being current.</summary>
    public int LinesSince { get; set; }

    /// <summary>How fast the line is travelling across the window right now, in text heights per second.</summary>
    public double Speed { get; private set; }

    private Point? _at;

    public LineAccess Access { get; }
    public Control Container { get; }

    /// <summary>True for a line with no word timing: tracked only so it is not looked at again every frame.</summary>
    public bool IsEmpty => _words.Count == 0;

    private LineRig(object line, LineAccess access, Control container, List<WordRig> words)
    {
        Line = line;
        Access = access;
        Container = container;
        _words = words;
    }

    /// <summary>
    /// Finds the word cells Noctis created for this line. Returns null when they are
    /// not there yet (the line was only just realised); the caller tries again next frame.
    /// </summary>
    public static LineRig? TryCreate(object line, LineAccess access, Control container, Tuning tuning, bool still)
    {
        var words = new List<WordRig>();
        var cells = new List<(object Word, WordAccess Access, Panel Cell, int Index, int Count, bool Background)>();
        var sawLayer = false;

        foreach (var visual in container.GetVisualDescendants())
        {
            if (visual is not ItemsControl layer || !layer.Classes.Contains("word-layer")) continue;
            sawLayer = true;
            if (!layer.IsVisible) continue;

            var background = layer.Classes.Contains("bg-vocals") || layer.Classes.Contains("romanization");
            var count = layer.ItemCount;
            for (var i = 0; i < count; i++)
            {
                var presenter = layer.ContainerFromIndex(i);
                if (presenter is null) return null;

                Panel? cell = null;
                foreach (var inside in presenter.GetVisualDescendants())
                {
                    if (inside is Panel panel && panel.Classes.Contains("word-cell")) { cell = panel; break; }
                }
                if (cell is null) return null;

                var word = cell.DataContext;
                if (word is null) return null;
                var wordAccess = WordAccess.For(word);
                if (wordAccess is null) continue;
                cells.Add((word, wordAccess, cell, i, count, background));
            }
        }

        if (!sawLayer && (access.HasAnyWords?.Invoke(line) ?? false)) return null;

        for (var i = 0; i < cells.Count; i++)
        {
            // The words sung before and after it, in the same row of words.
            var c = cells[i];
            double beforeMs = -1, afterMs = -1;
            if (i > 0 && cells[i - 1].Index == c.Index - 1) beforeMs = WordRig.DurationOf(cells[i - 1].Word, cells[i - 1].Access);
            if (i + 1 < cells.Count && cells[i + 1].Index == c.Index + 1) afterMs = WordRig.DurationOf(cells[i + 1].Word, cells[i + 1].Access);
            words.Add(new WordRig(c.Word, c.Access, c.Cell, c.Index, c.Count, c.Background, tuning, still, beforeMs, afterMs));
        }
        var rig = new LineRig(line, access, container, words);

        // What Noctis may blur the line through: everything from the words up to the line itself.
        if (cells.Count > 0)
        {
            foreach (var ancestor in cells[0].Cell.GetVisualAncestors())
            {
                if (ancestor is Control control) rig._above.Add(control);
                if (ReferenceEquals(ancestor, container)) break;
            }
        }
        return rig;
    }

    /// <summary>Blur from which a redrawn word and Noctis' own text look the same.</summary>
    private const double TellApartBlur = 2;

    private bool IsBlurred()
    {
        foreach (var control in _above)
            if (control.Effect is IBlurEffect { Radius: >= TellApartBlur }) return true;
        return false;
    }

    /// <summary>Advances every word by one frame. Returns true while anything is still moving.</summary>
    public bool Step(double dt, bool active, TopLevel top, Tuning tuning)
    {
        // Screen pixels per layout unit where this line is shown: the display's scaling
        // times whatever zoom Noctis shows its lyrics at (1.1 on the lyrics page). The
        // height a sung word rests at is a whole number of these.
        var pixelScale = top.RenderScaling;
        Speed = 0;
        if (Container.TransformToVisual(top) is { } m && m.M11 > 0.05 && Math.Abs(m.M12) < 1e-4)
        {
            pixelScale *= m.M11;
            var at = new Point(m.M31, m.M32);
            if (_at is { } before && dt > 0 && _words.Count > 0)
            {
                var dx = at.X - before.X;
                var dy = at.Y - before.Y;
                Speed = Math.Sqrt(dx * dx + dy * dy) / (_words[0].FontSize * m.M11 * dt);
            }
            _at = at;
        }

        var blurred = !active && IsBlurred();
        var moving = false;
        foreach (var word in _words)
            moving |= word.Step(dt, active, blurred, Speed, pixelScale, tuning);
        return moving;
    }

    public double Signature()
    {
        double sum = 0;
        foreach (var word in _words) sum += word.Progress;
        return sum;
    }

    /// <summary>Runs the first few words of the line once through what a sung word goes through, unseen.</summary>
    public void Rehearse(Tuning tuning)
    {
        for (var i = 0; i < _words.Count && i < 4; i++) _words[i].Rehearse(tuning);
    }

    public void Dispose()
    {
        foreach (var word in _words) word.Dispose();
        _words.Clear();
    }
}
