using Findra;
using SkiaSharp;
using Xunit;

public class DerivedTests
{
    public static TheoryData<string> AllPalettes()
    {
        var d = new TheoryData<string>();
        foreach (Palette p in Palette.BuiltIn) d.Add(p.Name);
        return d;
    }

    // Not Palette.ByName: that resolves against palettes.json so a user's own entry wins, which
    // is right for the app and wrong for a test - a hand-written override of "Mond" on the
    // machine running this would silently change what is being measured.
    private static Derived Of(string name) =>
        Derived.From(Palette.BuiltIn.Single(p => p.Name == name));

    [Theory, MemberData(nameof(AllPalettes))]
    public void TextIsReadableOnEverySurfaceItLandsOn(string name)
    {
        // The point of the whole exercise: a palette must be legible in either mode without
        // anyone eyeballing it. 4.5 is the ordinary body-text threshold, and Dim is held to it
        // too: it is the only text on the resting capsule, at 15px regular, which WCAG does not
        // call large text (that is 18.66px bold or 24px regular). It was held to 3.0 and passing
        // at 3.51 on Blueprint - a threshold fitted to the value rather than to the type size.
        Derived d = Of(name);

        Assert.True(Derived.Contrast(d.Ink, d.Ground) >= 4.5, $"{name}: ink on ground");
        Assert.True(Derived.Contrast(d.Ink, d.Row) >= 4.5, $"{name}: ink on a result row");
        Assert.True(Derived.Contrast(d.Ink, d.RowHover) >= 4.5, $"{name}: ink on a hovered row");
        Assert.True(Derived.Contrast(d.Ink, d.RowSelected) >= 4.5, $"{name}: ink on the selected row");
        Assert.True(Derived.Contrast(d.Ink, d.Stage) >= 4.5, $"{name}: ink on a stage badge");
        Assert.True(Derived.Contrast(d.Ink, d.Chip) >= 4.5, $"{name}: ink on a filter chip");
        // Not d.Ground: the capsule paints AccentSoft across the whole bar (CapsulePainter.Paint)
        // and draws Dim on top of THAT, so AccentSoft is the pair actually on screen. Checking
        // against the ground alone is the same mistake ink-on-a-derived-surface already made and
        // was fixed for - a palette can clear 4.5 there and still fail on the real background.
        Assert.True(Derived.Contrast(d.Dim, d.AccentSoft) >= 4.5, $"{name}: dim text on the capsule's actual bar fill");
        Assert.True(Derived.Contrast(d.Dim, d.Ground) >= 4.5, $"{name}: dim text on the ground");
        Assert.True(Derived.Contrast(d.OnAccent, d.Accent) >= 4.5, $"{name}: text on an accent fill");
    }

    [Theory, MemberData(nameof(AllPalettes))]
    public void SurfacesStackAwayFromTheGroundInOrder(string name)
    {
        // Rows sit on the ground, tiles sit on rows. Each must be visibly distinct from what
        // is under it, and each must move in the same direction - away from the ground -
        // whichever side of the line the palette is on. Getting the direction wrong is the
        // classic light-mode bug: a "lighter" row that vanishes into a white page.
        Derived d = Of(name);
        double ground = Lum(d.Ground), row = Lum(d.Row), tile = Lum(d.Tile);

        if (d.Palette.Light)
        {
            Assert.True(row < ground, $"{name}: a row on a light ground must be darker than it");
            Assert.True(tile < row, $"{name}: a tile must sit deeper than its row");
        }
        else
        {
            Assert.True(row > ground, $"{name}: a row on a dark ground must be lighter than it");
            Assert.True(tile > row, $"{name}: a tile must sit above its row");
        }
    }

    [Theory, MemberData(nameof(AllPalettes))]
    public void EveryDerivedColourIsDistinctFromTheGround(string name)
    {
        // A luminance delta is the wrong metric for a property named "distinct" - relative
        // luminance flatters light grounds, so a threshold that looked comfortably satisfied
        // here still let a surface go nearly invisible on a light palette. L* is what the eye
        // actually perceives; ~1 L* is the just-noticeable difference, so 3.0 is "clearly
        // distinct" rather than merely "technically not equal".
        Derived d = Of(name);
        foreach ((string label, SKColor c) in new[]
        {
            ("row", d.Row), ("rowHover", d.RowHover), ("rowSelected", d.RowSelected), ("tile", d.Tile),
            ("chip", d.Chip), ("edge", d.Edge), ("stage", d.Stage),
        })
            Assert.True(Math.Abs(Lstar(c) - Lstar(d.Ground)) >= 3.0,
                $"{name}: {label} is indistinguishable from the ground");
    }

    private static double Lstar(SKColor c)
    {
        double y = PaletteTests.Luminance(c);
        return y > 0.008856 ? 116 * Math.Cbrt(y) - 16 : 903.3 * y;
    }

    [Theory, MemberData(nameof(AllPalettes))]
    public void TheSelectedRowIsAccentTintedAndStillNotTheAccent(string name)
    {
        // The selection reads as "this one" without becoming a solid accent block that the
        // ink then has to fight. Both floors are set where they could actually bite: the
        // selected row measures 6.4-9.3 L* from a resting one and 3.09-5.76 : 1 against the
        // accent, so 3.0 and 2.5 leave real headroom without being unfalsifiable. The old
        // pair - not-equal, and a contrast floor of 1.3 with 3.09 as the closest palette -
        // could not fail for any tint this derivation is capable of producing.
        Derived d = Of(name);
        Assert.True(Math.Abs(Lstar(d.RowSelected) - Lstar(d.Row)) >= 3.0,
            $"{name}: the selected row is only {Math.Abs(Lstar(d.RowSelected) - Lstar(d.Row)):0.00} L* from a resting one");
        Assert.True(Derived.Contrast(d.RowSelected, d.Accent) >= 2.5,
            $"{name}: the selected row is too close to the accent to sit under it");
    }

    [Theory, MemberData(nameof(AllPalettes))]
    public void RowStatesEscalateAwayFromTheGroundInSteps(string name)
    {
        // Four rungs, not three. Row is the resting fill - the list leaves it unpainted today,
        // but the popup's inputs and its weakest button take it, and the settings window's rows
        // will - so it has to be pinned in the chain too. Leaving it out is how RowHover came to
        // sit within 1.4 L* of Row, and INVERTED on Mond and Brass: harmless only for as long as
        // nothing paints a resting row next to a hovered one.
        //
        // ~2 L* is roughly the threshold a glance registers (1 L* is the just-noticeable
        // difference), so every step must clear it, and each rung must be further from the
        // ground than the one below - measured toward the ground's own direction, so the same
        // assertion holds on a light palette where "further" means darker.
        Derived d = Of(name);
        double sign = d.Palette.Light ? -1 : 1;
        double ground = Lstar(d.Ground);
        double row = (Lstar(d.Row) - ground) * sign;
        double hover = (Lstar(d.RowHover) - ground) * sign;
        double selected = (Lstar(d.RowSelected) - ground) * sign;

        Assert.True(row >= 2.0, $"{name}: a resting row is only {row:0.00} L* from the ground");
        Assert.True(hover - row >= 2.0, $"{name}: hover is only {hover - row:0.00} L* from a resting row");
        Assert.True(selected - hover >= 2.0, $"{name}: selected is only {selected - hover:0.00} L* from hover");
    }

    [Theory, MemberData(nameof(AllPalettes))]
    public void SecondaryInkGetsTheSameLightGroundCorrectionTheSurfacesDo(string name)
    {
        // Every faded line in the card and the popup asks for ink at an alpha, and an alpha is a
        // mix fraction in disguise - so it needs the same light-ground correction the lift does,
        // or it inherits exactly the asymmetry the lift was fixed for. At alpha 130 the ramp
        // measured 4.29/4.57/4.54 on the dark palettes and 3.23/3.15/3.66 on the light ones
        // before Fade existed; the card's placeholder at 90 fell from 2.70 to 2.11, and the
        // popup's at 70 from 2.12 to 1.75.
        Derived d = Of(name);
        double at130 = Derived.Contrast(Over(d.Fade(130), d.Ground), d.Ground);
        Assert.True(at130 >= 3.9, $"{name}: ink at 130 reads {at130:0.00}:1 on the ground");
    }

    [Fact]
    public void TheInkRampIsWithinAQuarterOfItselfAcrossLightAndDark()
    {
        // The property Fade actually has to hold: the same alpha buys close to the same weight
        // whichever mode the palette is in. Across the six it spanned 3.15-4.57 (1.45x) before
        // and 4.03-4.85 (1.20x) after.
        var at130 = Palette.BuiltIn
            .Select(p => Derived.From(p))
            .Select(d => Derived.Contrast(Over(d.Fade(130), d.Ground), d.Ground))
            .ToList();
        Assert.True(at130.Max() / at130.Min() <= 1.25,
            $"the ramp spans {at130.Min():0.00}-{at130.Max():0.00}, a factor of {at130.Max() / at130.Min():0.00}");
    }

    [Fact]
    public void FadeIsUntouchedOnADarkGroundAndSaturatesRatherThanWrapping()
    {
        // A byte that overflows would wrap to near-zero and paint an invisible line, which is
        // the worst possible failure for a correction meant to make text more readable.
        Assert.Equal((byte)130, Derived.From(Palette.Mond).Fade(130).Alpha);
        Assert.Equal((byte)255, Derived.From(Palette.Paper).Fade(255).Alpha);
        Assert.Equal((byte)255, Derived.From(Palette.Paper).Fade(220).Alpha);
        // 215 is the highest alpha anything paints today, and it must not be at the cap.
        Assert.True(Derived.From(Palette.Paper).Fade(215).Alpha < 255);
    }

    /// <summary>An alpha-carrying ink composited over an opaque background, the way Skia does.</summary>
    private static SKColor Over(SKColor fg, SKColor bg)
    {
        float a = fg.Alpha / 255f;
        return new SKColor(
            (byte)Math.Round(bg.Red   + (fg.Red   - bg.Red)   * a),
            (byte)Math.Round(bg.Green + (fg.Green - bg.Green) * a),
            (byte)Math.Round(bg.Blue  + (fg.Blue  - bg.Blue)  * a));
    }

    [Fact]
    public void ContrastIsSymmetricAndKnowsItsAnchors()
    {
        var black = new SKColor(0, 0, 0);
        var white = new SKColor(255, 255, 255);
        Assert.Equal(21.0, Derived.Contrast(black, white), 1);
        Assert.Equal(Derived.Contrast(black, white), Derived.Contrast(white, black), 6);
        Assert.Equal(1.0, Derived.Contrast(white, white), 6);
    }

    [Fact]
    public void OnAccentPrefersThePalettesOwnColoursBeforeFallingBackToAnAnchor()
    {
        // Mond's accent is a bright orange: its own dark ink already clears the floor.
        // Blueprint's is a deep indigo: its own light ground already clears the floor. Neither
        // needs the white/black anchors - most palettes settle on their own ink or ground.
        // Porcelain is the one that needs the fallback: its red accent is close enough to both
        // its own ink and its own ground that neither clears 4.5, so it falls through to the
        // anchors, landing on white (not black, despite black measuring higher) so the badge
        // keeps the conventional white-on-red look.
        Assert.True(Derived.Contrast(Derived.From(Palette.Mond).OnAccent, Palette.Mond.Accent) >= 4.5);
        Assert.True(Derived.Contrast(Derived.From(Palette.Blueprint).OnAccent, Palette.Blueprint.Accent) >= 4.5);
        Assert.Equal(Derived.From(Palette.Porcelain).OnAccent, new SKColor(255, 255, 255));
    }

    private static double Lum(SKColor c) => PaletteTests.Luminance(c);
}
