using Avalonia;
using Findra;
using SkiaSharp;
using Xunit;

// Where the card lands when the widget is clicked. The window cannot be constructed without a
// display, so this is the whole of the placement that is verifiable headlessly - and the units it
// gets wrong at 150% are exactly what a screenshot would not have caught either.
public class CardOverPlacementTests
{
    // What CapsuleWindow.BarRect is over CapsuleLayout's 560 x 128.
    private static readonly SKRect Bar = new(0, 38, 560, 90);

    private const int Wide = 2560, High = 1440;

    [Fact]
    public void AtOneHundredPercentTheCardsFieldLandsOnTheCapsuleBar()
    {
        PixelPoint at = CardOverPlacement.Over(new PixelPoint(600, 300), 1.0, Bar,
            new PixelRect(0, 0, Wide, High), 1.0);

        // The bar's left minus the card's padding, the bar's top minus the field's top.
        Assert.Equal(600 + (0 - 14), at.X);
        Assert.Equal(300 + (38 - 12), at.Y);
    }

    [Fact]
    public void AtOneHundredAndFiftyPercentTheOffsetIsScaledToo()
    {
        // The offset is a distance in layout units: it goes through the monitor's scaling before
        // it can be added to a physical position. Multiplying by the zoom alone put the card's
        // field 7 px right and 13 px high of the bar it replaces.
        PixelPoint at = CardOverPlacement.Over(new PixelPoint(600, 300), 1.0, Bar,
            new PixelRect(0, 0, Wide, High), 1.5);

        Assert.Equal(600 + (int)System.Math.Round((0 - 14) * 1.5), at.X);
        Assert.Equal(300 + (int)System.Math.Round((38 - 12) * 1.5), at.Y);
    }

    [Fact]
    public void TheCardIsPhysicalPixelsWideAndTall()
    {
        PixelSize one = CardOverPlacement.GrownSize(1.0, 1.0);
        PixelSize half = CardOverPlacement.GrownSize(1.0, 1.5);

        Assert.Equal(820, one.Width);
        Assert.Equal(1230, half.Width);
        Assert.Equal((int)System.Math.Ceiling(one.Height * 1.5), half.Height);
    }

    [Fact]
    public void AWidgetNearTheRightEdgeKeepsTheWholeCardOnTheScreen()
    {
        var screen = new PixelRect(0, 0, Wide, High);
        PixelSize card = CardOverPlacement.GrownSize(1.0, 1.0);

        PixelPoint at = CardOverPlacement.Over(new PixelPoint(2400, 200), 1.0, Bar, screen, 1.0);

        Assert.True(at.X + card.Width <= screen.X + screen.Width,
            $"right edge {at.X + card.Width} is past {screen.X + screen.Width}");
    }

    [Fact]
    public void AWidgetNearTheRightEdgeAtOneHundredAndFiftyPercentKeepsTheWholeCardOnTheScreen()
    {
        // The case the old arithmetic got wrong by 410 px: the clamp reserved 820 x 626 while the
        // card was really 1230 x 939 physical, so a third of it hung off the right of the monitor.
        var screen = new PixelRect(0, 0, Wide, High);
        PixelSize card = CardOverPlacement.GrownSize(1.0, 1.5);

        PixelPoint at = CardOverPlacement.Over(new PixelPoint(1900, 200), 1.0, Bar, screen, 1.5);

        Assert.Equal(screen.X + screen.Width - card.Width, at.X);
        Assert.True(at.X + card.Width <= screen.X + screen.Width);
    }

    [Fact]
    public void AWidgetNearTheBottomAtOneHundredAndFiftyPercentKeepsTheWholeCardOnTheScreen()
    {
        var screen = new PixelRect(0, 0, 1920, 1080);
        PixelSize card = CardOverPlacement.GrownSize(1.0, 1.5);

        PixelPoint at = CardOverPlacement.Over(new PixelPoint(200, 1000), 1.0, Bar, screen, 1.5);

        Assert.Equal(screen.Y + screen.Height - card.Height, at.Y);
        Assert.True(at.Y + card.Height <= screen.Y + screen.Height);
    }

    [Fact]
    public void TheClampIsRelativeToTheMonitorTheWidgetIsOnNotToTheOrigin()
    {
        // A second monitor to the left of the primary has negative coordinates; clamping to zero
        // would throw the card onto the other screen.
        var screen = new PixelRect(-1920, 0, 1920, 1080);

        PixelPoint at = CardOverPlacement.Over(new PixelPoint(-1900, 40), 1.0, Bar, screen, 1.0);

        Assert.True(at.X >= screen.X, $"{at.X} is off the left of {screen.X}");
        Assert.True(at.Y >= screen.Y);
    }

    [Fact]
    public void ACardTallerThanTheScreenIsPinnedToTheTopLeftRatherThanPlacedNegatively()
    {
        // Math.Max in the clamp: on a screen too small to hold the grown card, the upper bound
        // would otherwise fall below the lower one and the card would be positioned off-screen.
        var screen = new PixelRect(0, 0, 640, 480);

        PixelPoint at = CardOverPlacement.Over(new PixelPoint(300, 300), 1.0, Bar, screen, 1.0);

        Assert.Equal(screen.X, at.X);
        Assert.Equal(screen.Y, at.Y);
    }

    [Fact]
    public void AScalingOfZeroIsReadAsOneRatherThanCollapsingTheCard()
    {
        PixelPoint at = CardOverPlacement.Over(new PixelPoint(600, 300), 1.0, Bar,
            new PixelRect(0, 0, Wide, High), 0);

        Assert.Equal(CardOverPlacement.Over(new PixelPoint(600, 300), 1.0, Bar,
            new PixelRect(0, 0, Wide, High), 1.0), at);
    }
}
