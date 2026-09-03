using Avalonia;
using Findra;

namespace Findra.Tests.App;

public class CardPlacementTests
{
    [Fact]
    public void TheCardIsCentredAndSitsAboutAThirdOfTheWayDown()
    {
        PixelPoint at = CardPlacement.Centred(new PixelRect(0, 0, 2560, 1400), 820, 130);
        Assert.Equal((2560 - 820) / 2, at.X);
        Assert.Equal((int)Math.Round(1400 * CardPlacement.FromTop), at.Y);
    }

    [Fact]
    public void TheCardIsOffsetOntoTheMonitorItWasAskedFor()
    {
        PixelPoint at = CardPlacement.Centred(new PixelRect(-1920, 200, 1920, 1080), 820, 130);
        Assert.Equal(-1920 + (1920 - 820) / 2, at.X);
        Assert.Equal(200 + (int)Math.Round(1080 * CardPlacement.FromTop), at.Y);
    }

    [Fact]
    public void ACardTallerThanTheScreenStillStartsInsideIt()
    {
        PixelPoint at = CardPlacement.Centred(new PixelRect(0, 0, 1024, 600), 820, 900);
        Assert.Equal(0, at.Y);
    }

    [Fact]
    public void TheGrownSizeIsTheCardWithAFullPageOfResults()
    {
        // Not the empty card: the window grows in place the moment results land and is never
        // moved again, so the height that has to fit on screen is the tallest one - and that is
        // the WINDOW with the progress pill hanging under the card, not the card alone.
        PixelSize grown = CardPlacement.GrownSize(1.0, 1.0);
        Assert.Equal((int)Math.Round(SearchCardLayout.Width), grown.Width);
        Assert.Equal((int)Math.Round(SearchCardLayout.WindowHeight(SearchCardLayout.MaxRows, true, progress: true)), grown.Height);
        Assert.True(grown.Height > SearchCardLayout.Height(SearchCardLayout.MaxRows, true));
    }

    [Fact]
    public void ACardOpenedFromTheHotkeyStillFitsOnASmallScreenOnceItGrows()
    {
        // 1366x768 is the case that used to fail: placed against the empty card's 120 px, the
        // grown card ran about 100 px off the bottom.
        var work = new PixelRect(0, 0, 1366, 768);
        PixelSize grown = CardPlacement.GrownSize(1.0, 1.0);
        PixelPoint at = CardPlacement.CentredGrown(work, 1.0, 1.0);

        Assert.True(at.Y >= work.Y, $"the card starts above the screen at y={at.Y}");
        Assert.True(at.Y + grown.Height <= work.Bottom,
            $"the grown card ends at y={at.Y + grown.Height}, below the screen's {work.Bottom}");
        Assert.True(at.X >= work.X, $"the card starts left of the screen at x={at.X}");
        Assert.True(at.X + grown.Width <= work.Right,
            $"the grown card ends at x={at.X + grown.Width}, right of the screen's {work.Right}");
    }

    [Fact]
    public void TheGrownCardIsKeptOnTheScreenAtAHigherScalingToo()
    {
        var work = new PixelRect(0, 0, 1920, 1040);
        PixelSize grown = CardPlacement.GrownSize(1.0, 1.5);
        PixelPoint at = CardPlacement.CentredGrown(work, 1.0, 1.5);

        Assert.True(at.Y + grown.Height <= work.Bottom,
            $"the grown card ends at y={at.Y + grown.Height}, below the screen's {work.Bottom}");
        Assert.True(at.X + grown.Width <= work.Right,
            $"the grown card ends at x={at.X + grown.Width}, right of the screen's {work.Right}");
    }
}
