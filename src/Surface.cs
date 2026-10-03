using Avalonia.Controls;

namespace LyricMotion;

/// <summary>
/// One list of lyric lines on screen (the lyrics page, the side panel or the mini
/// player). It watches which lines are current, rigs their words, and runs a frame
/// loop only while something is moving: with line-only lyrics, a paused player or a
/// hidden page it costs nothing per frame. It also turns right-to-left lines round
/// (<see cref="LineFlow"/>), which is done once per line, when the line is laid out.
/// </summary>
internal sealed class Surface : IDisposable
{
    private readonly Director _owner;
    private readonly ItemsControl _lines;
    private readonly Dictionary<object, LineRig> _rigs = new(ReferenceEqualityComparer.Instance);
    private readonly List<LineRig> _scratch = new();
    private readonly Func<bool> _motion;

    // Right-to-left layout, one per line that is on screen.
    private readonly Dictionary<Control, LineFlow> _flows = new();
    private readonly List<Control> _gone = new();
    private int _stamp;
    private int _songCount = -1;
    private object? _songFirst, _songLast;
    private bool _songRightToLeft;
    private bool _anyRightToLeft;
    private const int HandBackAfterLines = 2;

    // Once per session (see Rehearse).
    private static bool _rehearsed;

    private bool _looping;
    private bool _wordSynced;
    private int _loop;
    private bool _disposed;
    private TimeSpan? _lastFrame;
    private double _idleSignature = double.NaN;

    public Surface(Director owner, ItemsControl lines, Func<bool> motion)
    {
        _owner = owner;
        _lines = lines;
        _motion = motion;
        // Lines are turned round right after the layout pass that created them, which
        // is still before they are drawn: no line is ever seen the wrong way round.
        _lines.LayoutUpdated += OnLayoutUpdated;
        SyncFlows();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed) _owner.Guard(SyncFlows);
    }

    /// <summary>
    /// Gives every line on screen its direction. Costs next to nothing for a song with
    /// no right-to-left text in it (one comparison per pass).
    /// </summary>
    private void SyncFlows()
    {
        if (!_owner.Tuning.RightToLeft)
        {
            DropFlows();
            return;
        }

        var count = _lines.ItemCount;
        var first = count > 0 ? _lines.Items[0] : null;
        var last = count > 0 ? _lines.Items[count - 1] : null;
        if (count != _songCount || !ReferenceEquals(first, _songFirst) || !ReferenceEquals(last, _songLast))
        {
            // Another song: which way does it read as a whole?
            _songCount = count;
            _songFirst = first;
            _songLast = last;
            int rtl = 0, ltr = 0;
            for (var i = 0; i < count; i++)
            {
                var line = _lines.Items[i];
                if (line is not null) Script.Count(LineAccess.For(line)?.Text?.Invoke(line), ref rtl, ref ltr);
            }
            var songRightToLeft = rtl > ltr;
            if (songRightToLeft != _songRightToLeft) DropFlows();
            _songRightToLeft = songRightToLeft;
            _anyRightToLeft = rtl > 0;
        }

        if (!_anyRightToLeft)
        {
            DropFlows();
            return;
        }

        _stamp++;
        var visited = 0;
        for (var i = 0; i < count; i++)
        {
            var line = _lines.Items[i];
            if (line is null || _lines.ContainerFromIndex(i) is not { } container) continue;
            visited++;
            if (_flows.TryGetValue(container, out var flow))
            {
                if (ReferenceEquals(flow.Line, line))
                {
                    flow.Stamp = _stamp;
                    flow.Update();
                    continue;
                }
                flow.Dispose();
            }
            _flows[container] = new LineFlow(line, container, _songRightToLeft) { Stamp = _stamp };
        }

        if (_flows.Count == visited) return;
        _gone.Clear();
        foreach (var (container, flow) in _flows)
            if (flow.Stamp != _stamp) _gone.Add(container);
        foreach (var container in _gone)
        {
            if (_flows.Remove(container, out var flow)) flow.Dispose();
        }
    }

    private void DropFlows()
    {
        if (_flows.Count == 0) return;
        foreach (var flow in _flows.Values) flow.Dispose();
        _flows.Clear();
    }

    public ItemsControl Lines => _lines;

    /// <summary>Called a few times a second while idle: starts the frame loop when there is something to animate.</summary>
    public void Scout()
    {
        if (_disposed || _looping) return;
        if (TopLevel.GetTopLevel(_lines) is not { } top) return;

        Rehearse();
        Sync();
        if (!HasWork() && !Awaiting()) return;

        var signature = Signature();
        if (!_owner.IsPlaying && signature == _idleSignature) return;

        _looping = true;
        _lastFrame = null;
        var loop = ++_loop;
        top.RequestAnimationFrame(now => OnFrame(now, loop));
    }

    /// <summary>
    /// The first time anything runs, it is slow: the program code behind it is made
    /// ready only then, which takes a few hundredths of a second in all. Paid on the
    /// first sung word of a session, that is a visible hitch at the worst moment. So
    /// once, while no line is being sung (a song's intro, the lyrics page just opened),
    /// one line is taken through everything a sung word goes through and handed
    /// straight back, all inside one step, so nothing of it is ever drawn.
    /// </summary>
    private void Rehearse()
    {
        if (_rehearsed || !_motion()) return;
        try
        {
            var count = _lines.ItemCount;
            for (var i = 0; i < count; i++)
            {
                var line = _lines.Items[i];
                if (line is null) continue;
                var access = LineAccess.For(line);
                if (access is null) return;
                // A line with words is being sung: not now. (The placeholder Noctis
                // shows during an intro is a line too, and current, but has no words.)
                if (access.IsActive(line) && (access.HasAnyWords?.Invoke(line) ?? true)) return;
            }

            for (var i = 0; i < count; i++)
            {
                var line = _lines.Items[i];
                if (line is null || _lines.ContainerFromIndex(i) is not { } container) continue;
                var access = LineAccess.For(line);
                if (access is null || !(access.HasAnyWords?.Invoke(line) ?? false)) continue;

                var rig = LineRig.TryCreate(line, access, container, _owner.Tuning, still: false);
                if (rig is null) continue;
                try
                {
                    if (rig.IsEmpty) continue;
                    _rehearsed = true;
                    rig.Rehearse(_owner.Tuning);
                }
                finally
                {
                    rig.Dispose();
                }
                return;
            }
        }
        catch
        {
            // Only a rehearsal: whatever went wrong will show, and be dealt with, in the real run.
            _rehearsed = true;
        }
    }

    private bool HasWork()
    {
        foreach (var rig in _rigs.Values)
            if (!rig.IsEmpty) return true;
        return false;
    }

    /// <summary>
    /// True while a word-synced song is playing on a visible list with no line current
    /// (an intro, a break): the loop keeps turning so the next line is picked up on its
    /// very first frame, and its first word is not half over before it starts to move.
    /// </summary>
    private bool Awaiting() => _wordSynced && _owner.IsPlaying && _lines.IsEffectivelyVisible;

    private double Signature()
    {
        // Besides where the song is: how big the list is shown (a resized window, another
        // monitor), so that redrawn words are put right again while nothing is playing.
        double sum = _rigs.Count + _lines.Bounds.Width * 3 + _lines.Bounds.Height * 7
                     + (TopLevel.GetTopLevel(_lines)?.RenderScaling ?? 1) * 1000;
        foreach (var rig in _rigs.Values)
            sum += rig.Signature() + (rig.Access.IsActive(rig.Line) ? 1000 : 0);
        return sum;
    }

    /// <summary>Rigs lines that became current; forgets wordless lines that no longer are.</summary>
    private void Sync()
    {
        SyncFlows();

        var count = _lines.ItemCount;
        var tuning = _owner.Tuning;
        // A list whose motion is switched off (the side panel) still needs its words
        // looked after when they are right-to-left: they fill in from the right.
        var still = !_motion();
        _wordSynced = false;
        for (var i = 0; i < count; i++)
        {
            var line = _lines.Items[i];
            if (line is null) continue;
            var access = LineAccess.For(line);
            if (access is null) return;
            if (!_wordSynced && (access.HasAnyWords?.Invoke(line) ?? false)) _wordSynced = true;
            if (!access.IsActive(line) || _rigs.ContainsKey(line)) continue;
            if (still && !_anyRightToLeft) continue;

            if (_lines.ContainerFromIndex(i) is not { } container) continue;
            var rig = LineRig.TryCreate(line, access, container, tuning, still);
            if (rig is null) continue;

            // A line has started: every line that is no longer current is one further back.
            foreach (var earlier in _rigs.Values)
                if (!earlier.Access.IsActive(earlier.Line)) earlier.LinesSince++;
            _rigs[line] = rig;
        }

        _scratch.Clear();
        foreach (var rig in _rigs.Values)
            if (rig.IsEmpty && !rig.Access.IsActive(rig.Line)) _scratch.Add(rig);
        foreach (var rig in _scratch) _rigs.Remove(rig.Line);
    }

    private void OnFrame(TimeSpan now, int loop)
    {
        // A loop that was released (settings changed, surface gone) must not carry on
        // next to the one started after it.
        if (_disposed || loop != _loop) return;
        _owner.Guard(() =>
        {
            // A stalled frame (window dragged, app in the background) must not arrive
            // as one huge step: springs would lurch. Cap it, as AMLL does.
            var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 1.0 / 60;
            _lastFrame = now;

            var top = TopLevel.GetTopLevel(_lines);
            if (top is null)
            {
                Release();
                return;
            }

            Rehearse();
            Sync();

            var tuning = _owner.Tuning;
            var moving = false;
            _scratch.Clear();
            foreach (var rig in _rigs.Values)
            {
                if (rig.IsEmpty) continue;
                if (TopLevel.GetTopLevel(rig.Container) is null)
                {
                    _scratch.Add(rig);
                    continue;
                }

                var active = rig.Access.IsActive(rig.Line);
                moving |= rig.Step(dt, active, top, tuning);
                if (active) rig.LinesSince = 0;
                // A finished line keeps its words where they are (sung words stay up) and
                // is handed back to Noctis only two lines later: by then it is blurred and
                // on the move, and the two pixels it gives back cannot be seen.
                else if (rig.LinesSince >= HandBackAfterLines) _scratch.Add(rig);
            }
            foreach (var rig in _scratch)
            {
                rig.Dispose();
                _rigs.Remove(rig.Line);
            }

            if ((HasWork() && (moving || _owner.IsPlaying)) || Awaiting())
            {
                top.RequestAnimationFrame(next => OnFrame(next, loop));
            }
            else
            {
                _looping = false;
                _idleSignature = Signature();
            }
        }, onFailure: () => _looping = false);
    }

    /// <summary>Hands every word back to Noctis exactly as it was.</summary>
    public void Release()
    {
        foreach (var rig in _rigs.Values) rig.Dispose();
        _rigs.Clear();
        DropFlows();
        _songCount = -1;
        _loop++;
        _looping = false;
        _lastFrame = null;
        _idleSignature = double.NaN;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lines.LayoutUpdated -= OnLayoutUpdated;
        Release();
    }
}
