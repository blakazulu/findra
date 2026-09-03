using System.Text.RegularExpressions;
using System.Xml.Linq;

using Findra;

using SkiaSharp;

using Xunit;

/// <summary>
/// Findra's mark, checked as pixels rather than as a file that exists.
///
/// <para>An icon is the one asset in this tree that nothing else notices when it breaks. A
/// truncated <c>.ico</c>, a directory entry pointing past the end of the file, a rasteriser that
/// emitted a fully transparent square - every one of those builds, publishes, installs and ships,
/// and the first person to find out is a stranger looking at a blank taskbar button. So these
/// tests decode the bytes that will be compiled into the executable and look at named pixels.
/// </para>
///
/// <para>They also hold the mark's four copies together. The geometry lives in
/// <c>build/Make-Icon.mjs</c>; from it come <c>assets/icon/findra.ico</c>, the two SVGs, and the
/// site's favicon, while <c>TrayIconFactory</c> paints the same numbers in C# and the site's
/// header carries the path data inside a stylesheet. That is five places one logo can be, and
/// nothing but these tests would notice four of them agreeing while the fifth did not.</para>
/// </summary>
public class IconTests
{
    private const string Ico = "assets/icon/findra.ico";
    private const string Svg = "assets/icon/findra.svg";
    private const string Flat = "assets/icon/findra-flat.svg";

    private static readonly SKColor Plate = new(0x14, 0x14, 0x1A);
    private static readonly SKColor Accent = new(0xFA, 0x7E, 0x00);

    /// <summary>The sizes the shell actually asks for. 20 and 40 look like padding and are not:
    /// they are what Windows picks at 125% and 150% display scaling, and an icon without them is
    /// downscaled from 32 and 48, which throws away the hand-hinting at exactly the sizes the
    /// hinting was for.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private sealed record Entry(int Size, int Planes, int Bpp, byte[] Payload);

    private static List<Entry> Entries()
    {
        byte[] b = File.ReadAllBytes(Repo.Path_(Ico));
        Assert.True(b.Length > 6, "assets/icon/findra.ico is empty or truncated");
        Assert.Equal(0, BitConverter.ToUInt16(b, 0));    // reserved
        Assert.Equal(1, BitConverter.ToUInt16(b, 2));    // type 1: icon, not cursor

        int n = BitConverter.ToUInt16(b, 4);
        var found = new List<Entry>(n);
        for (int i = 0; i < n; i++)
        {
            int at = 6 + i * 16;
            // The width and height fields are one byte each, so 256 is recorded as 0.
            int w = b[at] == 0 ? 256 : b[at];
            int h = b[at + 1] == 0 ? 256 : b[at + 1];
            Assert.Equal(w, h);

            int len = (int)BitConverter.ToUInt32(b, at + 8);
            int off = (int)BitConverter.ToUInt32(b, at + 12);
            Assert.InRange(off, 6 + n * 16, b.Length);
            Assert.InRange(len, 1, b.Length - off);

            found.Add(new Entry(w, BitConverter.ToUInt16(b, at + 4), BitConverter.ToUInt16(b, at + 6),
                                b[off..(off + len)]));
        }
        return found;
    }

    /// <summary>A pixel named in the mark's own 256-unit design space, whatever size the image
    /// happens to be. Every sample below is written as the coordinate it was designed at, so the
    /// test says which PART of the mark it is looking at rather than which pixel.</summary>
    private static SKColor At(SKBitmap bmp, double x, double y) =>
        bmp.GetPixel((int)(x * bmp.Width / 256.0), (int)(y * bmp.Height / 256.0));

    private static bool Nearer(SKColor c, SKColor a, SKColor b)
    {
        static int D(SKColor p, SKColor q) =>
            (p.Red - q.Red) * (p.Red - q.Red) +
            (p.Green - q.Green) * (p.Green - q.Green) +
            (p.Blue - q.Blue) * (p.Blue - q.Blue);
        return D(c, a) < D(c, b);
    }

    [Fact]
    public void TheIconCarriesEveryShellSizeAsAThirtyTwoBitPng()
    {
        List<Entry> entries = Entries();
        Assert.Equal(Sizes, entries.Select(e => e.Size).ToArray());

        foreach (Entry e in entries)
        {
            Assert.Equal(1, e.Planes);
            Assert.Equal(32, e.Bpp);

            // A PNG payload, and its OWN header agreeing with the directory entry that points at
            // it. A directory that promises 48 and a payload that holds 32 is an icon Windows
            // scales, silently, and only on some surfaces.
            Assert.Equal(0x89, e.Payload[0]);
            Assert.Equal("PNG", System.Text.Encoding.ASCII.GetString(e.Payload, 1, 3));
            Assert.Equal(e.Size, BitConverter.ToInt32(e.Payload[16..20].Reverse().ToArray(), 0));
            Assert.Equal(e.Size, BitConverter.ToInt32(e.Payload[20..24].Reverse().ToArray(), 0));
        }
    }

    [Fact]
    public void EverySizeIsActuallyDrawnAndNotAnEmptySquare()
    {
        foreach (Entry e in Entries())
        {
            using SKBitmap bmp = SKBitmap.Decode(e.Payload);
            Assert.NotNull(bmp);
            Assert.Equal(e.Size, bmp.Width);

            // Outside the plate's rounded corner: transparent, or the mark is a square.
            Assert.Equal(0, bmp.GetPixel(0, 0).Alpha);

            // Inside the plate, well clear of the lens and the handle: the Mond ground, opaque.
            SKColor ground = At(bmp, 200, 60);
            Assert.Equal(255, ground.Alpha);
            Assert.True(Nearer(ground, Plate, Accent), $"{e.Size}px: the plate is not the Mond ground");

            // On the lens, above the slot at every size: the accent.
            SKColor lens = At(bmp, 110, 72);
            Assert.Equal(255, lens.Alpha);
            Assert.True(Nearer(lens, Accent, Plate), $"{e.Size}px: the lens is not drawn");

            // On the handle, past the lens: the accent again. A mark that lost its handle is
            // still a plausible-looking disc, which is why this is checked rather than eyeballed.
            SKColor handle = At(bmp, 188, 186);
            Assert.Equal(255, handle.Alpha);
            Assert.True(Nearer(handle, Accent, Plate), $"{e.Size}px: the handle is not drawn");
        }
    }

    [Fact]
    public void TheSlotIsCutOutAtEverySizeThatCanHoldOneAndDroppedAtTheOneThatCannot()
    {
        foreach (Entry e in Entries())
        {
            using SKBitmap bmp = SKBitmap.Decode(e.Payload);
            SKColor middle = At(bmp, 110, 108);   // the centre of the lens, where the slot is

            if (e.Size == 16)
            {
                // Deliberate: at 16 px the slot is under two pixels tall and renders as a grey
                // smear rather than a hole, so the hinting drops it and the mark is honestly a
                // plain lens. If this ever starts passing as a hole, the hint was lost.
                Assert.True(Nearer(middle, Accent, Plate),
                    "16px: the slot is still being cut, and at that size it is a smudge");
            }
            else
            {
                Assert.True(Nearer(middle, Plate, Accent),
                    $"{e.Size}px: the lens has no slot cut out of it");
            }
        }
    }

    [Fact]
    public void TheApplicationIconIsTheGeneratedFileAndNotACopySomebodyMade()
    {
        // ApplicationIcon is what puts the mark on the taskbar button, Alt-Tab, both shortcuts,
        // the Explorer listing and the Add/Remove Programs entry. None of those can be painted at
        // runtime: the shell reads them off the binary.
        XDocument xml = XDocument.Load(Repo.Path_("src/Findra/Findra.csproj"));
        string declared = Assert.Single(xml.Descendants("ApplicationIcon")).Value.Trim();

        Assert.Equal(Path.GetFullPath(Repo.Path_(Ico)),
                     Path.GetFullPath(Path.Combine(Repo.Path_("src/Findra"),
                                                   declared.Replace('\\', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void TheInstallerIsBuiltWithTheSameIconTheApplicationCarries()
    {
        string script = Repo.Read("installer/findra.iss");

        Match m = Regex.Match(script, @"^SetupIconFile=(.+)$", RegexOptions.Multiline);
        Assert.True(m.Success, "the installer script sets no SetupIconFile");
        Assert.Equal(Path.GetFullPath(Repo.Path_(Ico)), Resolve(m.Groups[1].Value));

        // The mark in the corner of every wizard page. A missing file here is not a compile
        // error somewhere else - ISCC fails outright on it - so what this catches is the file
        // being renamed or dropped from the generator while the directive stays behind.
        Match w = Regex.Match(script, @"^WizardSmallImageFile=(.+)$", RegexOptions.Multiline);
        Assert.True(w.Success, "the installer script sets no WizardSmallImageFile");
        Assert.True(File.Exists(Resolve(w.Groups[1].Value)),
                    "the installer's wizard image is not where the script says it is");

        static string Resolve(string declared) =>
            Path.GetFullPath(Path.Combine(Repo.Path_("installer"),
                                          declared.Trim().Replace('\\', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void TheSiteFaviconIsTheApplicationMarkRatherThanSomethingThatLooksLikeIt()
    {
        // Both are written by build/Make-Icon.mjs in one run. If they differ, one of them was
        // hand-edited, and the one that was hand-edited is the one nobody will think to check.
        Assert.Equal(Repo.Read(Svg), Repo.Read("website/public/favicon.svg"));
    }

    [Fact]
    public void TheHeaderMarkAndBothSvgsCarryTheOneLensPath()
    {
        string d = Path_D(Repo.Read(Flat));

        // The plated icon and the unplated header mark are the same drawing with and without a
        // background - not two drawings that resemble each other.
        Assert.Equal(d, Path_D(Repo.Read(Svg)));

        // The site header inlines the glyph as a data URI, so it is a fifth copy of the path.
        // This is a text assertion because there is no code to run: a stylesheet is data.
        Assert.Contains(d, Repo.Read("website/public/styles.css"), StringComparison.Ordinal);

        static string Path_D(string svg)
        {
            Match m = Regex.Match(svg, @"\sd=""([^""]+)""");
            Assert.True(m.Success, "the mark's SVG carries no path");
            return m.Groups[1].Value;
        }
    }

    [Theory]
    [InlineData("Mond")]
    [InlineData("Verdigris")]
    [InlineData("Paper")]
    public void TheTrayDrawsTheSameMarkInThePaletteAndLeavesTheSlotAsAHole(string name)
    {
        Palette p = Palette.BuiltIn.Single(q => q.Name == name);
        using SKData png = TrayIconFactory.Render(p);
        using SKBitmap bmp = SKBitmap.Decode(png.ToArray());

        Assert.Equal(TrayIconFactory.Size, bmp.Width);
        Assert.Equal(TrayIconFactory.Size, bmp.Height);

        // Design units, mapped the way TrayIconFactory maps them: the glyph's own bounds fitted
        // into the icon with a pixel of margin, rather than the 256 square the .ico uses.
        float k = (TrayIconFactory.Size - 2f) / TrayIconFactory.Bounds.Width;
        SKColor Tray(float x, float y) =>
            bmp.GetPixel((int)((x - TrayIconFactory.Bounds.Left) * k + 1f),
                         (int)((y - TrayIconFactory.Bounds.Top) * k + 1f));

        // No plate. A tray icon is composited onto a taskbar Windows chose the colour of, so a
        // filled corner here is a dark square sitting on somebody's light taskbar.
        Assert.Equal(0, bmp.GetPixel(0, 0).Alpha);

        // The lens, in THIS palette's accent - not a hardcoded orange.
        SKColor lens = Tray(110f, 72f);
        Assert.Equal(255, lens.Alpha);
        Assert.Equal(p.Accent.Red, lens.Red);
        Assert.Equal(p.Accent.Green, lens.Green);
        Assert.Equal(p.Accent.Blue, lens.Blue);

        // The slot is a HOLE, not a shape filled with the palette's ground. Filling it looks
        // identical on a dark taskbar and is a smudge on a light one, which is the whole reason
        // this method was split out of Draw to be reachable from a test at all.
        Assert.Equal(0, Tray(110f, 108f).Alpha);
    }
}
