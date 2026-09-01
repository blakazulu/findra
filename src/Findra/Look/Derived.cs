using SkiaSharp;

namespace Findra;

/// <summary>
/// Every colour the card paints, worked out from a palette's four values.
///
/// The one idea here is direction. A surface that sits *on* the ground moves away from it -
/// lighter on a dark ground, darker on a light one - so the same arithmetic produces a
/// readable card in either mode. Hardcoding "slightly lighter" is what makes a dark theme
/// unreadable when someone inverts it, and it is the reason this type exists at all.
/// </summary>
public sealed class Derived
{
    public required Palette Palette { get; init; }

    public required SKColor Accent { get; init; }
    public required SKColor Ink { get; init; }
    public required SKColor Ground { get; init; }

    /// <summary>Secondary text: paths, counts, the timing line.</summary>
    public required SKColor Dim { get; init; }
    /// <summary>A result row's fill.</summary>
    public required SKColor Row { get; init; }
    /// <summary>The highlighted row - accent-tinted, never solid accent.</summary>
    public required SKColor RowSelected { get; init; }
    /// <summary>The kind tile inside a row. Unused by the card today - its kind badge paints a
    /// name-hashed gradient pinned dark on every palette, like a file-type tag that should not
    /// shift with theme - but this is the natural token for "a surface above a row", which the
    /// settings window will want.</summary>
    public required SKColor Tile { get; init; }
    /// <summary>A filter chip's fill.</summary>
    public required SKColor Chip { get; init; }
    /// <summary>Hairline borders and separators.</summary>
    public required SKColor Edge { get; init; }
    /// <summary>The preview panel behind a thumbnail.</summary>
    public required SKColor Stage { get; init; }
    /// <summary>The card's drop shadow. Ground-independent by nature.</summary>
    public required SKColor Shadow { get; init; }
    /// <summary>Whatever is legible when drawn on top of a solid accent fill.</summary>
    public required SKColor OnAccent { get; init; }
    /// <summary>A wash of accent, for a field's interior.</summary>
    public required SKColor AccentSoft { get; init; }
    /// <summary>The halo around the focused field.</summary>
    public required SKColor AccentGlow { get; init; }

    public static Derived From(Palette p)
    {
        // Away from the ground: toward white on a dark ground, toward black on a light one.
        //
        // Equal arithmetic does not produce equal perception. Lightness (L*) follows a
        // cube-root curve against relative luminance, and that curve is steep near black and
        // shallow near white: the same mix fraction `t` buys far less perceived separation on
        // a light ground than on a dark one. Scaling t by 1.4 on light palettes is what makes
        // Paper's rows read as clearly as Mond's instead of nearly vanishing into the page.
        SKColor Lift(float t) => Mix(p.Ground, p.Light ? Black : White, p.Light ? t * 1.4f : t);
        SKColor Tint(float t, float toward) => Mix(Lift(t), p.Accent, toward);

        return new Derived
        {
            Palette = p,
            Accent = p.Accent,
            Ink = p.Ink,
            Ground = p.Ground,

            Dim         = Mix(p.Ground, p.Ink, 0.55f),
            Row         = Lift(0.055f),
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
    private static SKColor Mix(SKColor a, SKColor b, float t) => new(
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
