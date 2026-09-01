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

    [Theory, MemberData(nameof(AllPalettes))]
    public void TextIsReadableOnEverySurfaceItLandsOn(string name)
    {
        // The point of the whole exercise: a palette must be legible in either mode without
        // anyone eyeballing it. 4.5 is the ordinary body-text threshold; the dim secondary
        // line is held to 3.0, which is the large-text threshold and what it actually is.
        Derived d = Derived.From(Palette.ByName(name)!);

        Assert.True(Derived.Contrast(d.Ink, d.Ground) >= 4.5, $"{name}: ink on ground");
        Assert.True(Derived.Contrast(d.Ink, d.Row) >= 4.5, $"{name}: ink on a result row");
        Assert.True(Derived.Contrast(d.Ink, d.RowSelected) >= 4.5, $"{name}: ink on the selected row");
        Assert.True(Derived.Contrast(d.Ink, d.Stage) >= 4.5, $"{name}: ink on the preview panel");
        Assert.True(Derived.Contrast(d.Ink, d.Chip) >= 4.5, $"{name}: ink on a filter chip");
        Assert.True(Derived.Contrast(d.Dim, d.Ground) >= 3.0, $"{name}: dim text on ground");
        Assert.True(Derived.Contrast(d.OnAccent, d.Accent) >= 4.5, $"{name}: text on an accent fill");
    }

    [Theory, MemberData(nameof(AllPalettes))]
    public void SurfacesStackAwayFromTheGroundInOrder(string name)
    {
        // Rows sit on the ground, tiles sit on rows. Each must be visibly distinct from what
        // is under it, and each must move in the same direction - away from the ground -
        // whichever side of the line the palette is on. Getting the direction wrong is the
        // classic light-mode bug: a "lighter" row that vanishes into a white page.
        Derived d = Derived.From(Palette.ByName(name)!);
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
        Derived d = Derived.From(Palette.ByName(name)!);
        foreach ((string label, SKColor c) in new[]
        {
            ("row", d.Row), ("rowSelected", d.RowSelected), ("tile", d.Tile),
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
        // ink then has to fight.
        Derived d = Derived.From(Palette.ByName(name)!);
        Assert.NotEqual(d.RowSelected, d.Row);
        Assert.True(Derived.Contrast(d.RowSelected, d.Accent) > 1.3,
            $"{name}: the selected row is too close to the accent to sit under it");
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
