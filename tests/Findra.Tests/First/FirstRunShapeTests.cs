using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The welcome screen's two acts have to be the same object. The window resizes when the screen is
/// answered and again when the download ends, and the painter went on drawing the card's round
/// rect and its hairline out to the choosing act's constant - so both bottom corners fell off the
/// bottom of the settled window and the screen went from a rounded card to a square-bottomed one
/// the moment somebody answered it.
/// </summary>
public class FirstRunShapeTests
{
    private static FirstRunState At(FirstRunStage stage, bool contentOn, bool hebrew = false) => new()
    {
        Stage = stage,
        ContentOn = contentOn,
        HebrewOffered = hebrew,
        Chosen = Capabilities.Close([Capability.Speech]),
        OnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    };

    [Theory]
    [InlineData(FirstRunStage.Choosing)]
    [InlineData(FirstRunStage.Downloading)]
    [InlineData(FirstRunStage.Finished)]
    public void ThePaintedCardIsExactlyAsTallAsTheWindowItIsPaintedInto(FirstRunStage stage)
    {
        FirstRunState s = At(stage, contentOn: true);
        float surface = FirstRunLayout.SurfaceHeight(s);

        int w = (int)FirstRunLayout.Width, h = (int)Math.Ceiling(surface);
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
            FirstRunPainter.Paint(canvas, s, Derived.From(Palette.Mond), Parts.Face);

        // A rounded corner leaves the very corner pixel transparent. A square one does not, and
        // that is the whole difference the eye reads.
        Assert.Equal(0, bmp.GetPixel(1, h - 2).Alpha);
        Assert.Equal(0, bmp.GetPixel(w - 2, h - 2).Alpha);
        // And the card really does reach the bottom rather than stopping short of it.
        Assert.True(bmp.GetPixel(w / 2, h - 2).Alpha > 100, "the card does not reach the bottom of its window");
    }

    [Fact]
    public void TheTranscriptionBandIsReservedOnlyWhileItIsDrawn()
    {
        // It is painted only while the screen is still the question, and it was reserved in every
        // act - so a machine offered Hebrew whose owner ticked Speech got a 64px hole between
        // Speech and Hebrew for the whole download, and a window that much taller than its content.
        FirstRunState asking = At(FirstRunStage.Choosing, contentOn: true, hebrew: true);
        FirstRunState settled = asking with { Stage = FirstRunStage.Downloading };

        Assert.True(FirstRunLayout.BandRow(asking) >= 0, "the question is meant to carry the band");
        Assert.Equal(-1, FirstRunLayout.BandRow(settled));
        Assert.Equal(-1, FirstRunLayout.BandRow(settled with { Stage = FirstRunStage.Finished }));

        // And the rows below Speech really do close up: the last row ends where it would with no
        // band at all, which is the 64px the hole was.
        int rows = FirstRun.Rows(settled).Count;
        Assert.Equal(FirstRunLayout.RowRect(rows - 1, -1).Bottom,
                     FirstRunLayout.RowRect(rows - 1, FirstRunLayout.BandRow(settled)).Bottom, 3);
        Assert.True(FirstRunLayout.RowRect(rows - 1, FirstRunLayout.BandRow(asking)).Bottom
                    > FirstRunLayout.RowRect(rows - 1, -1).Bottom,
                    "the question's own band is meant to move the rows below it");
    }
}
