using Findra;
using SkiaSharp;
using Xunit;

public class VideoGeometryTests
{
    private static SKBitmap Solid(int w, int h, SKColor colour)
    {
        var b = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var c = new SKCanvas(b)) c.Clear(colour);
        return b;
    }

    [Fact]
    public void ThePaddedRowsADecoderAddsAreCroppedAway()
    {
        // H.264 decodes padded to a multiple of 16: 536 rows arrive as 544, and the last 8 are
        // whatever the decoder left there.
        using SKBitmap raw = Solid(1280, 544, SKColors.Red);
        using SKBitmap fitted = VideoGeometry.Apply(raw, new FrameShape(1280, 544, 1280 * 4, 1280, 536, 0, 1, 1), 320);
        Assert.Equal(320, fitted.Width);
        Assert.Equal((int)Math.Round(320 * 536.0 / 1280), fitted.Height);
    }

    [Fact]
    public void APictureStoredRotatedIsTurnedBackUpright()
    {
        // A phone records 1920x1080 and says "rotated 90 counter-clockwise". Upright it is taller
        // than it is wide.
        using SKBitmap raw = Solid(1920, 1080, SKColors.Blue);
        using SKBitmap fitted = VideoGeometry.Apply(raw, new FrameShape(1920, 1080, 1920 * 4, 1920, 1080, 90, 1, 1), 320);
        Assert.True(fitted.Height > fitted.Width);
    }

    [Fact]
    public void NonSquarePixelsAreStretchedBeforeTheFit()
    {
        using SKBitmap raw = Solid(720, 576, SKColors.Green);
        using SKBitmap fitted = VideoGeometry.Apply(raw, new FrameShape(720, 576, 720 * 4, 720, 576, 0, 4, 3), 320);
        // 720 * 4/3 = 960 wide against 576 tall, so the fitted picture is wider than it is tall.
        Assert.True(fitted.Width > fitted.Height);
    }

    [Fact]
    public void AFrameAlreadySmallerThanTheFitIsLeftAlone()
    {
        using SKBitmap raw = Solid(208, 160, SKColors.Gray);
        using SKBitmap fitted = VideoGeometry.Apply(raw, new FrameShape(208, 160, 208 * 4, 208, 160, 0, 1, 1), 320);
        Assert.Equal(208, fitted.Width);
        Assert.Equal(160, fitted.Height);
    }
}
