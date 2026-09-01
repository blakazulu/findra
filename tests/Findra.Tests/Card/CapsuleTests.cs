using Findra;
using SkiaSharp;
using Xunit;

public class CapsuleTests
{
    private static SKBitmap Render(Palette p, string progress = "", float fraction = 0)
    {
        var info = new SKImageInfo((int)CapsuleLayout.Width, (int)CapsuleLayout.Height,
                                   SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        CapsulePainter.Paint(surface.Canvas, "Search 1.5M files", progress, fraction,
                             Derived.From(p), SKTypeface.Default);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    [Fact]
    public void ItPaintsSomethingInEveryPalette()
    {
        foreach (Palette p in Palette.BuiltIn)
        {
            using SKBitmap bmp = Render(p);
            SKColor centre = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
            Assert.True(centre.Alpha > 0, $"{p.Name}: the capsule's middle is transparent");
        }
    }

    [Fact]
    public void TheGroundIsTheGround()
    {
        // The capsule paints its own ground rather than borrowing the wallpaper's.
        foreach (Palette p in Palette.BuiltIn)
        {
            using SKBitmap bmp = Render(p);
            SKColor centre = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
            Assert.True(Derived.Contrast(centre, p.Ground) < 1.6,
                $"{p.Name}: the capsule's interior does not read as its own ground");
        }
    }

    [Fact]
    public void TheCornersAreTransparentBecauseItIsARoundedCapsule()
    {
        using SKBitmap bmp = Render(Palette.Mond);
        Assert.Equal(0, bmp.GetPixel(0, 0).Alpha);
        Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, bmp.Height - 1).Alpha);
    }

    [Fact]
    public void TheProgressLineOnlyExistsWhenThereIsSomethingToSay()
    {
        // At rest the capsule is just the capsule; a permanently visible empty progress bar
        // is the thing that makes a widget feel busy when it is idle.
        using SKBitmap idle = Render(Palette.Mond);
        using SKBitmap busy = Render(Palette.Mond, "indexing 4,120 to go", 0.62f);

        // Compare the two renders pixel by pixel in the band where the progress line lives.
        //
        // Two earlier versions of this assertion were wrong, both for the same reason - they
        // measured an absolute property of one image instead of the difference between two.
        // The first hashed 1024 bytes, which is part of row 0, transparent in both. The second
        // counted opaque pixels below the bar, but the halo's 10px blur is still fully opaque
        // across every column down to row 111, so idle and busy both counted 7623 and the
        // metric was saturated before the progress line was even drawn.
        //
        // Differencing cancels the glow: it is identical in both renders, so anything that
        // differs in this band is the progress line and nothing else.
        int differing = 0;
        int from = (int)((CapsuleLayout.Height + CapsuleLayout.BarH) / 2f) + 8;
        for (int y = from; y < idle.Height; y++)
            for (int x = 0; x < idle.Width; x++)
                if (idle.GetPixel(x, y) != busy.GetPixel(x, y)) differing++;

        // The track alone is 260x3; a real line clears this comfortably, and nothing else
        // in this band changes between the two renders.
        Assert.True(differing > 200,
            $"the progress line was not drawn below the bar ({differing} pixels differ)");

        // That proves the band responds to the progress arguments. It does NOT yet prove the
        // guard, because idle and busy differ in fraction as well as text - deleting the
        // guard only drops the count, it does not collapse it. This third render isolates it:
        // a fraction is supplied but there is nothing to say, so a working guard draws
        // nothing and the band must be pixel-identical to idle. Without the guard, the
        // fraction alone paints a 62% track and this fails.
        using SKBitmap nothingToSay = Render(Palette.Mond, "", 0.62f);
        Assert.Equal(0, DifferBelowTheBar(idle, nothingToSay));
    }

    /// <summary>Pixels that differ between two renders in the band where progress is drawn.</summary>
    private static int DifferBelowTheBar(SKBitmap a, SKBitmap b)
    {
        int n = 0, from = (int)((CapsuleLayout.Height + CapsuleLayout.BarH) / 2f) + 8;
        for (int y = from; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }
}
