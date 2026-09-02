using Findra;
using Findra.Diagnostics;
using SkiaSharp;
using Xunit;

public class ShotTests
{
    public static TheoryData<string, string> EveryStateInEveryPalette()
    {
        var d = new TheoryData<string, string>();
        foreach (string s in SearchShot.States)
            foreach (Palette p in Palette.BuiltIn) d.Add(s, p.Name);
        return d;
    }

    [Theory, MemberData(nameof(EveryStateInEveryPalette))]
    public void EveryStateRendersInEveryPalette(string state, string palette)
    {
        // 54 renders. This is the whole safety net for a repaint that has no unit tests: if any
        // state in any palette throws or comes out empty, it fails here rather than on someone's
        // desktop. Exit code, file size and bitmap size alone did not earn that description -
        // every one of them passes on a card painted entirely in one colour, which is exactly
        // what a broken derivation produces. So the shot has to be shown to have COLOUR in it.
        string path = Path.Combine(Path.GetTempPath(), $"findra-shot-{state}-{palette}.png");
        try
        {
            Assert.Equal(0, SearchShot.Render(path, state, palette));
            Assert.True(new FileInfo(path).Length > 1024, "the PNG is suspiciously small");

            using SKBitmap bmp = SKBitmap.Decode(path);
            Assert.True(bmp.Width > 100 && bmp.Height > 50);

            // Measured, not guessed. With Derived.From returning the ground for all fourteen
            // fields the nine states span 46-191 distinct colours; as they actually paint they
            // span 382-2583. 280 sits between the two with about a third of headroom either way.
            int colours = Distinct(bmp, 0, bmp.Width);
            Assert.True(colours >= 280,
                $"{state}/{palette}: only {colours} distinct colours - the render is flat");

            // The global count is blind in the three states that draw result rows: their kind
            // tiles and stage art are name-hashed gradients painted from literal HSL, deliberately
            // outside Derived, and those alone keep a fully flattened card at 1181-1611 colours.
            // The list's text column carries none of them, so it is where flatness shows: 7-24
            // flattened against 231-338 as painted.
            if (state is "results" or "many" or "opening")
            {
                int column = Distinct(bmp, 60, 500);
                Assert.True(column >= 120,
                    $"{state}/{palette}: the list column has only {column} distinct colours");
            }
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>Distinct pixel colours in a vertical band of the bitmap.</summary>
    private static int Distinct(SKBitmap b, int x0, int x1)
    {
        var seen = new HashSet<uint>();
        for (int y = 0; y < b.Height; y++)
            for (int x = x0; x < Math.Min(x1, b.Width); x++)
                seen.Add((uint)b.GetPixel(x, y));
        return seen.Count;
    }

    [Fact]
    public void EverySectionOfTheSettingsWindowHasAShotOfItsOwn()
    {
        // "--searchshot must learn every new palette and every new surface as it is written"
        // (spec 9). A section added later with no shot is invisible to every automated check in
        // the project, including the render sweep above and the legibility pass.
        foreach (Section s in RailLayout.Sections)
        {
            string want = "settings" + s.ToString().ToLowerInvariant();
            Assert.True(SearchShot.States.Contains(want) || (s == Section.Look && SearchShot.States.Contains("settings")),
                $"no --searchshot state for the {s} section");
        }
    }

    [Fact]
    public void AnUnknownStateOrPaletteFailsLoudlyRatherThanRenderingSomethingWrong()
    {
        string path = Path.Combine(Path.GetTempPath(), "findra-shot-bad.png");
        Assert.NotEqual(0, SearchShot.Render(path, "nonesuch", "Mond"));
        Assert.NotEqual(0, SearchShot.Render(path, "results", "Nonesuch"));
    }
}
