namespace LyricMotion;

/// <summary>
/// A damped spring solved in closed form, so a long or uneven frame never makes it
/// explode. It is what glides a sudden change out (see <see cref="Follower"/>).
/// </summary>
internal struct Spring
{
    public double Value;
    public double Velocity;

    private readonly double _omega;
    private readonly double _zeta;

    /// <param name="frequency">Undamped frequency in Hz (how quickly it answers).</param>
    /// <param name="damping">1 = no overshoot; below 1 it overshoots a little and settles.</param>
    public Spring(double frequency, double damping, double value = 0)
    {
        _omega = 2 * Math.PI * frequency;
        _zeta = Math.Clamp(damping, 0.05, 1.0);
        Value = value;
        Velocity = 0;
    }

    public double Step(double target, double dt)
    {
        if (dt <= 0) return Value;
        var x = Value - target;
        var v = Velocity;

        if (_zeta >= 0.999)
        {
            var e = Math.Exp(-_omega * dt);
            var k = v + _omega * x;
            Value = target + e * (x + k * dt);
            Velocity = e * (v - _omega * k * dt);
        }
        else
        {
            var wd = _omega * Math.Sqrt(1 - _zeta * _zeta);
            var e = Math.Exp(-_zeta * _omega * dt);
            var c = Math.Cos(wd * dt);
            var s = Math.Sin(wd * dt);
            var b = (v + _zeta * _omega * x) / wd;
            Value = target + e * (x * c + b * s);
            Velocity = e * (v * c - (x * wd + _zeta * _omega * b) * s);
        }
        return Value;
    }

    public readonly bool IsAsleep(double target, double epsilon) =>
        Math.Abs(Value - target) < epsilon && Math.Abs(Velocity) < epsilon * 8;

    public void Snap(double value)
    {
        Value = value;
        Velocity = 0;
    }
}

/// <summary>
/// Follows a value that is itself already smooth (everything here is driven by Noctis'
/// sweep, which advances evenly on every frame) without any delay: in normal play the
/// result IS the target, so the motion sits exactly on the voice. Only a sudden change
/// (a seek, a word picked up half-way) is glided across, critically damped, so it never
/// overshoots. A plain spring chasing the target would trail it by a few hundredths of
/// a second all the time, which on a quick word is half the word.
/// </summary>
internal struct Follower
{
    private Spring _gap;
    private readonly double _rest;
    private double _target;
    private bool _started;

    /// <param name="frequency">How quickly a sudden change is made up, in Hz.</param>
    /// <param name="rest">Below this the remaining gap is dropped.</param>
    public Follower(double frequency, double rest)
    {
        _gap = new Spring(frequency, 1.0);
        _rest = rest;
        _target = 0;
        _started = false;
    }

    /// <summary>True when nothing is left to make up: the value is the target.</summary>
    public readonly bool IsSettled => _gap.Value == 0 && _gap.Velocity == 0;

    /// <param name="step">The largest change one frame of normal play can bring; anything bigger is a jump.</param>
    public double Step(double target, double dt, double step)
    {
        if (!_started)
        {
            _started = true;
            _target = target;
            return target;
        }

        var change = target - _target;
        _target = target;
        // A jump: stay where we were and make the difference up from here.
        if (Math.Abs(change) > step) _gap.Value -= change;

        if (!IsSettled)
        {
            _gap.Step(0, dt);
            if (_gap.IsAsleep(0, _rest)) _gap.Snap(0);
        }
        return target + _gap.Value;
    }
}

/// <summary>Easing curves and envelopes shared by the word and letter animations.</summary>
internal static class Curves
{
    public static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>0 below <paramref name="lo"/>, 1 above <paramref name="hi"/>, smooth in between.</summary>
    public static double SmoothStep(double lo, double hi, double x)
    {
        var t = Clamp01((x - lo) / (hi - lo));
        return t * t * (3 - 2 * t);
    }

    /// <summary>0 to 1 with no sudden start and no sudden stop (zero speed and zero acceleration at both ends).</summary>
    public static double SmootherStep(double x)
    {
        var t = Clamp01(x);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// <summary>
    /// The size of an ordinary word while it is sung: a single even swell, 0 at the
    /// start, largest in the middle, 0 again at the end, the way up mirroring the way down.
    /// </summary>
    public static double Hill(double u)
    {
        u = Clamp01(u);
        var s = Math.Sin(Math.PI * u);
        return s * s;
    }

    /// <summary>
    /// How far a held word that grows as a whole has come up, from the seconds since it
    /// began: unhurried (a quarter to half a second) and eased at both ends.
    /// </summary>
    public static double HeldAttack(double seconds, double durationSec)
    {
        if (seconds <= 0) return 0;
        return SmootherStep(seconds / Math.Clamp(0.4 * durationSec, 0.26, 0.48));
    }
}
