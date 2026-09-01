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
    /// <summary>The kind tile inside a row.</summary>
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
        SKColor Lift(float t) => Mix(p.Ground, p.Light ? Black : White, t);
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
            OnAccent    = Contrast(Black, p.Accent) >= Contrast(White, p.Accent) ? Black : White,
            AccentSoft  = Mix(p.Ground, p.Accent, 0.09f),
            AccentGlow  = p.Accent.WithAlpha(p.Light ? (byte)46 : (byte)62),
        };
    }

    private static readonly SKColor White = new(255, 255, 255);
    private static readonly SKColor Black = new(0, 0, 0);

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
