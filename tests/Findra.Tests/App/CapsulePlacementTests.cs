using Avalonia;
using Findra;

namespace Findra.Tests.App;

// A window cannot be unit tested - there is no screen, no message loop, and no Z order in a test
// run. What CAN be tested is every decision taken before a window is touched, which is why the
// arithmetic lives in these two static classes rather than inside the window code.
public class CapsulePlacementTests
{
    private static readonly IReadOnlyList<PixelRect> TwoScreens =
    [
        new PixelRect(0, 0, 2560, 1440),
        new PixelRect(2560, 0, 2560, 1440),
    ];

    [Fact]
    public void APositionOnTheSecondMonitorIsKept()
        => Assert.True(CapsulePlacement.IsOnAnyScreen(new PixelRect(3000, 1200, 560, 128), TwoScreens));

    [Fact]
    public void APositionOnAMonitorThatIsNoLongerThereIsRejected()
    {
        // The saved position was fine when it was written; the second monitor has since been
        // unplugged. Nothing here may leave the capsule invisible on a desktop.
        Assert.False(CapsulePlacement.IsOnAnyScreen(
            new PixelRect(3000, 1200, 560, 128), [new PixelRect(0, 0, 2560, 1440)]));
    }

    [Fact]
    public void ASliverPeekingOverTheEdgeDoesNotCount()
    {
        // Twenty pixels of a 560-wide capsule is not something a person can grab.
        Assert.False(CapsulePlacement.IsOnAnyScreen(
            new PixelRect(2540, 1200, 560, 128), [new PixelRect(0, 0, 2560, 1440)]));
    }

    [Fact]
    public void NoScreensAtAllIsNotOnAScreen()
        => Assert.False(CapsulePlacement.IsOnAnyScreen(new PixelRect(0, 0, 560, 128), []));

    [Fact]
    public void TheDefaultIsCentredAboveTheBottomOfTheWorkingArea()
    {
        PixelPoint at = CapsulePlacement.BottomCentre(new PixelRect(0, 0, 2560, 1400), 560, 128);
        Assert.Equal((2560 - 560) / 2, at.X);
        Assert.Equal(1400 - 128 - CapsulePlacement.BottomMargin, at.Y);
    }

    [Fact]
    public void TheDefaultRespectsAWorkingAreaThatDoesNotStartAtTheOrigin()
    {
        PixelPoint at = CapsulePlacement.BottomCentre(new PixelRect(2560, 40, 1920, 1000), 560, 128);
        Assert.Equal(2560 + (1920 - 560) / 2, at.X);
        Assert.Equal(40 + 1000 - 128 - CapsulePlacement.BottomMargin, at.Y);
    }

    [Fact]
    public void ClampPullsAPositionBackOntoTheScreen()
    {
        PixelPoint at = CapsulePlacement.Clamp(new PixelPoint(2400, 1390), new PixelRect(0, 0, 2560, 1440), 560, 128);
        Assert.Equal(2560 - 560, at.X);
        Assert.Equal(1440 - 128, at.Y);
    }

    [Fact]
    public void ClampNeverInvertsOnAnAreaSmallerThanTheCapsule()
    {
        // A 320-wide working area cannot hold a 560-wide capsule. The answer is the top-left
        // corner, not a negative coordinate off the side of the desktop.
        PixelPoint at = CapsulePlacement.Clamp(new PixelPoint(900, 900), new PixelRect(100, 50, 320, 100), 560, 128);
        Assert.Equal(100, at.X);
        Assert.Equal(50, at.Y);
    }

    [Fact]
    public void TheScreenUnderAPointIsFound()
    {
        Assert.Equal(0, CapsulePlacement.ScreenIndexAt(new PixelPoint(10, 10), TwoScreens));
        Assert.Equal(1, CapsulePlacement.ScreenIndexAt(new PixelPoint(3000, 700), TwoScreens));
    }

    [Fact]
    public void APointOnNoScreenReportsNoScreen()
        => Assert.Equal(-1, CapsulePlacement.ScreenIndexAt(new PixelPoint(-40, 700), TwoScreens));
}
