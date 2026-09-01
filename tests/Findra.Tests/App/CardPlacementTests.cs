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
}
