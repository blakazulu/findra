using SkiaSharp;

namespace Findra;

/// <summary>
/// Every colour the card paints, worked out from a palette's four values.
///
/// The one idea here is direction. A surface that sits *on* the ground moves away from it -
/// lighter on a dark ground, darker on a light one - so the same arithmetic produces a
/// readable card in either mode. Hardcoding "slightly lighter" is what makes a dark theme
/// unreadable when someone inverts it, and it is the reason this type exists at all.
///
/// Two corrections carry that idea. <c>Lift</c> moves the ground toward black or white and is
/// scaled by <see cref="LightLift"/> on a light ground; <c>Weight</c> moves the ground toward
/// the palette's own ink and is scaled by <see cref="LightInk"/>. They are different numbers
/// because they measure different journeys - see each constant.
/// </summary>
public sealed class Derived
{
    public required Palette Palette { get; init; }

    public required SKColor Accent { get; init; }
    public required SKColor Ink { get; init; }
    public required SKColor Ground { get; init; }

    /// <summary>Secondary ink, opaque: the ground carrying most of the weight of ink (0.62,
    /// up from an earlier 0.55 that was fitted against the wrong background - see below).
    /// Painted only by the resting capsule - its placeholder line (15px) and its progress
    /// caption. The card and the popup reduce their own ink through <see cref="Fade"/> instead,
    /// which is the same arithmetic expressed as an alpha.
    ///
    /// Checked against <see cref="AccentSoft"/>, not <see cref="Ground"/>: the capsule paints
    /// <c>AccentSoft</c> across the whole bar first and draws this text on top of that, so
    /// <c>AccentSoft</c> is the pair actually on screen. 0.55 cleared 4.5 against the ground and
    /// still measured 4.13-4.51 against the real background on three palettes - the same error
    /// class as ink-on-a-derived-surface, just missed for this one property.</summary>
    public required SKColor Dim { get; init; }

    /// <summary>The first step off the ground: a resting surface that is not the page. The
    /// result list paints no fill for an ordinary row, so today this paints the Advanced popup's
    /// ten text inputs and the weakest of its three button states. It is the token the settings
    /// window's list rows will want.</summary>
    public required SKColor Row { get; init; }

    /// <summary>The strongest of the three row states - the current row, the one Enter acts on.
    /// Accent-tinted, never solid accent. Painted by the selected result row, the active filter
    /// chip, a latched pill, the primary action under the stage, the scroll thumb, the popup's
    /// chosen Kind chip and its hovered Apply button.</summary>
    public required SKColor RowSelected { get; init; }

    /// <summary>The middle step: a row (or pill, or action) the pointer is over but that is not
    /// the current selection - the same accent tint as <see cref="RowSelected"/> at half
    /// strength, so hovering reads as "noticed" without competing with what Enter would act on.
    /// The three states are ordered by construction: ground &lt; Row &lt; RowHover &lt;
    /// RowSelected, each about 2 L* or more from the last.</summary>
    public required SKColor RowHover { get; init; }

    /// <summary>A surface above a row - a tile, a thumbnail well, a nested panel. Paints the
    /// settings window's section rail and the first-run screen's three preset tiles. The result
    /// list's kind badge does NOT use it:
    /// that paints a name-hashed gradient pinned dark on every palette, like a file-type tag
    /// that should not shift with theme.</summary>
    public required SKColor Tile { get; init; }

    /// <summary>A small pill or tag's own fill, sitting directly on the ground. Paints an
    /// unchosen pill in either settings surface and the first-run screen's unticked box. The
    /// card's filter chips take
    /// <see cref="RowSelected"/> when active and a stroke otherwise, and the row's kind tag now
    /// reads straight off whatever is under it - the fill it used to draw measured under one L*,
    /// which is below the threshold an eye registers at all.</summary>
    public required SKColor Chip { get; init; }

    /// <summary>Hairline borders and separators. Paints the card's two rules, the popup's
    /// section lines and the capsule's progress track.</summary>
    public required SKColor Edge { get; init; }

    /// <summary>A small opaque plate laid over unknown content, where ink must stay legible
    /// whatever is beneath. Painted by the duration badge on the stage's picture; the preview
    /// panel it is named for draws no fill of its own.</summary>
    public required SKColor Stage { get; init; }

    /// <summary>A drop shadow's own colour - black at a ground-dependent alpha, the one place a
    /// literal colour is correct. Painted by the Advanced popup; the card's own window shadow
    /// belongs to the compositor.</summary>
    public required SKColor Shadow { get; init; }

    /// <summary>Whatever is legible when drawn on top of a solid accent fill. Painted by the `!`
    /// badge on the Advanced pill, and checked by <c>--searchtest</c>.</summary>
    public required SKColor OnAccent { get; init; }

    /// <summary>A wash of accent over the ground, opaque. Painted by the resting capsule's bar;
    /// the field interior it was named for takes the ground itself.</summary>
    public required SKColor AccentSoft { get; init; }

    /// <summary>The halo around a focused or resting control. Painted by the capsule's outer
    /// glow and by the card field's focus ring.</summary>
    public required SKColor AccentGlow { get; init; }

    /// <summary>
    /// How much further a mix toward black or white has to travel on a light ground.
    ///
    /// Lightness (L*) follows a cube-root curve against relative luminance, and that curve is
    /// steep near black and shallow near white: the same mix fraction buys far less perceived
    /// separation on a light ground than on a dark one. This is what makes Paper's rows read as
    /// clearly as Mond's instead of nearly vanishing into the page.
    /// </summary>
    private const float LightLift = 1.4f;

    /// <summary>
    /// The same correction for a mix toward the palette's own ink, which is a shorter journey
    /// than one toward an anchor and so needs a smaller number - copying 1.4 here would overshoot
    /// every secondary line into full ink.
    ///
    /// Fitted, not guessed: 1.17 is the multiplier that minimises the worst error when the three
    /// light palettes' ink-at-130 contrast is matched against the three dark ones (4.29 / 4.57 /
    /// 4.54 : 1). Before it, the whole secondary ramp carried the identical light/dark asymmetry
    /// the lift had - ink at 130 measured 3.15-3.66 : 1 on a light ground - because an alpha
    /// never passed through the correction at all.
    /// </summary>
    private const float LightInk = 1.17f;

    /// <summary>Ink at a reduced weight, corrected for the ground.
    ///
    /// Every secondary line in the card and the popup - paths, counts, captions, placeholders -
    /// asks for ink at some alpha. On a light ground that alpha is scaled by
    /// <see cref="LightInk"/>, so a value chosen while looking at a dark palette lands on the
    /// same perceived weight in either mode. The result saturates at 255 for a raw alpha of 218
    /// or more; nothing painted today asks for more than 215.</summary>
    public SKColor Fade(byte alpha) => Ink.WithAlpha(
        Palette.Light ? (byte)Math.Min(255, (int)Math.Round(alpha * LightInk)) : alpha);

    public static Derived From(Palette p)
    {
        // Away from the ground: toward white on a dark ground, toward black on a light one.
        SKColor Lift(float t) => Mix(p.Ground, p.Light ? Black : White, p.Light ? t * LightLift : t);
        SKColor Tint(float t, float toward) => Mix(Lift(t), p.Accent, toward);
        // Toward the palette's own ink, same correction, its own constant.
        SKColor Weight(float f) => Mix(p.Ground, p.Ink, p.Light ? Math.Min(1f, f * LightInk) : f);

        return new Derived
        {
            Palette = p,
            Accent = p.Accent,
            Ink = p.Ink,
            Ground = p.Ground,

            Dim         = Weight(0.62f),
            Row         = Lift(0.055f),
            // The three row states share one lift and escalate by accent tint alone, so the
            // ladder ground < Row < RowHover < RowSelected holds by construction: hover is
            // exactly half of selected's tint. Giving hover a smaller lift as well - which is
            // what it used to do - put it within 1.4 L* of Row and inverted it on two palettes.
            RowHover    = Tint(0.055f, 0.07f),
            RowSelected = Tint(0.055f, 0.14f),
            Tile        = Lift(0.11f),
            Chip        = Lift(0.075f),
            Edge        = Lift(0.16f),
            Stage       = Lift(0.09f),
            Shadow      = new SKColor(0, 0, 0, p.Light ? (byte)70 : (byte)120),
            OnAccent    = PickOnAccent(p),
            AccentSoft  = Mix(p.Ground, p.Accent, 0.09f),
            AccentGlow  = p.Accent.WithAlpha(p.Light ? (byte)46 : (byte)62),
        };
    }

    private static readonly SKColor White = new(255, 255, 255);
    private static readonly SKColor Black = new(0, 0, 0);

    /// <summary>What sits legibly on a solid fill of this palette's accent.
    ///
    /// Preference order, not a two-way pick: the palette's own ink, then its own ground, then
    /// white or black - the anchors, tried in the direction that extends the ground's own
    /// polarity (white for a light ground, black for a dark one), then the other one. The
    /// first candidate that clears 4.5 wins; if somehow none do, the best of the four.
    ///
    /// The anchors are a fallback for an accent no palette colour can sit on, not the normal
    /// path - five of the six built-in palettes settle on their own ground. Porcelain's red
    /// accent is the outlier: neither its ink (4.178) nor its ground (4.390) clears the floor,
    /// so it falls through to the anchor stage, where trying white before black keeps the
    /// conventional white-on-red look rather than flipping to black text on red (even though
    /// black's contrast there, 4.62, is technically higher).
    /// </summary>
    private static SKColor PickOnAccent(Palette p)
    {
        SKColor near = p.Light ? White : Black;
        SKColor far = p.Light ? Black : White;
        SKColor best = p.Ink;
        double bestContrast = Contrast(p.Ink, p.Accent);
        foreach (var candidate in new[] { p.Ink, p.Ground, near, far })
        {
            double c = Contrast(candidate, p.Accent);
            if (c >= 4.5) return candidate;
            if (c > bestContrast) { best = candidate; bestContrast = c; }
        }
        return best;
    }

    /// <summary>Opaque linear blend. Alpha stays at the base's - these are surfaces, not overlays.</summary>
    public static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)Math.Round(a.Red   + (b.Red   - a.Red)   * t),
        (byte)Math.Round(a.Green + (b.Green - a.Green) * t),
        (byte)Math.Round(a.Blue  + (b.Blue  - a.Blue)  * t),
        a.Alpha);

    /// <summary>WCAG relative-luminance contrast ratio, 1.0 to 21.0.</summary>
    public static double Contrast(SKColor a, SKColor b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(SKColor c) =>
        0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);

    private static double Channel(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
