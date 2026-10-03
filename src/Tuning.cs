using Noctis.Plugins;

namespace LyricMotion;

/// <summary>
/// Every number that shapes the motion, resolved once from the plugin's settings.
/// Lengths are in em (multiples of the lyric font size) so the motion scales with the
/// window. The reference values are the ones Apple Music-like Lyrics (AMLL) uses.
/// </summary>
internal sealed class Tuning
{
    /// <summary>Overall strength: 0.7 subtle, 1 balanced, 1.4 expressive.</summary>
    public double Intensity { get; private init; } = 1.0;

    public bool LetterWave { get; private init; } = true;
    public bool Glow { get; private init; } = true;
    public bool SidePanel { get; private init; } = true;

    /// <summary>Lay right-to-left lyrics (Persian, Arabic, Hebrew…) out from the right.</summary>
    public bool RightToLeft { get; private init; } = true;

    /// <summary>A word sung at least this long gets the letter-by-letter wave.</summary>
    public double HeldMs { get; private init; } = 600;

    /// <summary>How far a word floats up as it is sung (AMLL: 0.05em). It stays there until its line ends.</summary>
    public double LiftEm => 0.05 * Intensity;

    /// <summary>How much the word being sung grows: a touch, not a pop.</summary>
    public double Swell => 0.02 * Intensity;

    /// <summary>
    /// Quick words do not grow at all (a rap verse runs at 150-300 ms a word, and a size
    /// change on every one of them would read as flicker); from about 0.4 s it is full.
    /// </summary>
    public static double SwellWeight(double ms) => Curves.SmoothStep(140, 420, ms);

    // ── ordinary words, letter by letter ──

    /// <summary>
    /// How much the letters of an ordinary word grow as the fill reaches them. Less than
    /// a held word gets, and nothing at all on quick words: a rap verse runs at 70-250 ms
    /// a word, and a size change on every one of them would read as restlessness.
    /// </summary>
    public double WordGrow(double ms) => 0.018 * Intensity * Curves.SmoothStep(160, 420, ms);

    /// <summary>How much the letters of a word grow: by how long it is sung.</summary>
    public double Grow(double ms, bool lastWordOfLine) =>
        ms >= HeldMs ? HeldGrow(ms, lastWordOfLine) : WordGrow(ms);

    /// <summary>Growth below this cannot be seen; such a word is moved in one piece.</summary>
    public const double LetterGrowFloor = 0.004;

    /// <summary>
    /// The shortest time a word takes to float up. A word sung in less (a quick syllable
    /// in a rap line) still starts exactly on its note, but finishes its two pixels a
    /// moment after it: a run of such words then rolls instead of ticking.
    /// </summary>
    public const double MinRiseSec = 0.16;

    // ── held words ──

    /// <summary>
    /// How much a held word grows, from how long it is held. It starts just above what
    /// an ordinary word gets, so a word on the edge of "held" does not jump out. The
    /// last word of a line gets a little more, because that is where singers hold.
    /// </summary>
    public double HeldGrow(double ms, bool lastWordOfLine)
    {
        var grow = Curves.Lerp(0.03, 0.06, Curves.SmoothStep(HeldMs, HeldMs + 1800, ms)) * Intensity;
        if (lastWordOfLine) grow *= 1.1;
        return Math.Min(grow, 0.10);
    }

    /// <summary>Opacity of a held word's glow at its fullest.</summary>
    public double HeldGlow(double ms, bool lastWordOfLine)
    {
        var glow = Curves.Lerp(0.2, 0.5, Curves.SmoothStep(HeldMs, HeldMs + 2000, ms)) * Intensity;
        if (lastWordOfLine) glow *= 1.1;
        return Math.Min(glow, 0.8);
    }

    /// <summary>A held word that cannot be shown in letters grows as a whole, a little less.</summary>
    public const double WholeWordShare = 0.7;

    /// <summary>How far a letter rises on top of the word's float, in em per unit of growth.</summary>
    public const double LiftPerGrow = 0.5;

    /// <summary>Longest word (in letters) that is split; longer ones stay whole.</summary>
    public const int MaxLetters = 14;

    /// <summary>
    /// When a held word starts to relax, in seconds from its start: once the highlight
    /// has fully left it and a breath has passed.
    /// </summary>
    public static double SettleStartSec(double durationSec) => durationSec + Math.Min(0.2 * durationSec, 0.3) + 0.25;

    /// <summary>How long a held word takes to relax: unhurried, longer for longer notes.</summary>
    public static double SettleSec(double durationSec) => Math.Clamp(0.8 + 0.3 * durationSec, 1.0, 1.5);

    /// <summary>
    /// How long a line's words take to come back down once the line is over. Noctis
    /// dims the line over 0.5 s and glides it over 0.65 s; this sits between the two so
    /// the words settle while the light goes.
    /// </summary>
    public const double LineReleaseSec = 0.6;

    public static Tuning From(IPluginSettings? settings)
    {
        if (settings is null) return new Tuning();

        var style = settings.GetString("style") ?? "Balanced";
        var intensity = style.Trim().ToLowerInvariant() switch
        {
            "subtle" => 0.7,
            "expressive" => 1.4,
            _ => 1.0,
        };

        return new Tuning
        {
            Intensity = intensity,
            LetterWave = settings.GetBool("letterWave", true),
            Glow = settings.GetBool("glow", true),
            SidePanel = settings.GetBool("sidePanel", true),
            RightToLeft = settings.GetBool("rtl", true),
            HeldMs = Math.Clamp(settings.GetNumber("heldMs", 600), 300, 2000),
        };
    }
}
