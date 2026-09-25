using Avalonia;
using Findra;
using Xunit;

/// <summary>
/// Every window Findra opens has to fit the screen it opens on, at every scaling Windows offers
/// that screen. The layouts are fixed in layout units and Windows multiplies them by the scaling,
/// so a 928-unit first-run screen is 1160 physical pixels at 125% - taller than a 1080p laptop,
/// with the only two buttons that answer it below the bottom edge. <see cref="ScreenFit"/> is
/// what shrinks a surface into the room it has; these hold every surface to it on the laptop the
/// Store's review used, at each scaling it offers.
/// </summary>
[Collection("culture")]
public class ScreenFitTests
{
    /// <summary>A 1920x1080 laptop with the taskbar along the bottom. The taskbar is 48 units
    /// tall, so it takes more physical pixels the higher the scaling.</summary>
    private static PixelRect WorkArea(double scaling) =>
        new(0, 0, 1920, 1080 - (int)Math.Ceiling(48 * scaling));

    public static TheoryData<double> Scalings => new() { 1.0, 1.25, 1.5, 1.75 };

    private static void AssertFits(string what, double width, double height, double scaling)
    {
        PixelRect room = WorkArea(scaling);
        double k = ScreenFit.Factor(width, height, room, scaling);
        double w = width * k * scaling, h = height * k * scaling;
        Assert.True(h <= room.Height && w <= room.Width,
            $"{what} is {w:0}x{h:0} physical pixels at {scaling:P0} and the screen has {room.Width}x{room.Height}");
    }

    [Theory]
    [MemberData(nameof(Scalings))]
    public void TheFirstRunScreenFits(double scaling) =>
        AssertFits("the first-run screen", FirstRunLayout.Width, FirstRunLayout.Height, scaling);

    [Theory]
    [MemberData(nameof(Scalings))]
    public void TheTallestWelcomePageFits(double scaling)
    {
        // Every bar the page can hold, a failure in the status line and the last question.
        FirstRunState tallest = new()
        {
            Chosen = Capabilities.Close([Capability.Photos, Capability.Meaning, Capability.Speech, Capability.Hebrew]),
            HebrewOffered = true,
            ContentOn = true,
            Stage = FirstRunStage.Finished,
            Problem = "No such host is known. (huggingface.co:443)",
            Downloads =
            [
                new CapabilityProgress(Capability.Photos, 0, 659_000_000),
                new CapabilityProgress(Capability.Meaning, 0, 283_000_000),
                new CapabilityProgress(Capability.Speech, 0, 574_000_000),
                new CapabilityProgress(Capability.Hebrew, 0, 1_549_000_000),
            ],
        };
        AssertFits("the welcome page", FirstRunLayout.Width, FirstRunLayout.SurfaceHeight(tallest), scaling);
    }

    [Theory]
    [MemberData(nameof(Scalings))]
    public void TheSettingsWindowFits(double scaling) =>
        AssertFits("the settings window", RailLayout.Width, RailLayout.Height, scaling);

    [Theory]
    [MemberData(nameof(Scalings))]
    public void TheCardFullyGrownFits(double scaling)
    {
        // Through the zoom the card actually opens at, and the size placement reserves for it.
        PixelRect room = WorkArea(scaling);
        double zoom = CardOverPlacement.FittedZoom(1.0, room, scaling);
        double tallest = SearchCardLayout.WindowHeight(SearchCardLayout.MaxRows, true, advOpen: true, progress: true);
        Assert.True(tallest * zoom * scaling <= room.Height,
            $"the card is {tallest * zoom * scaling:0} physical pixels tall at {scaling:P0} and the screen has {room.Height}");
        Assert.True(CardOverPlacement.GrownSize(zoom, scaling).Height <= room.Height);
    }

    [Fact]
    public void ASurfaceThatFitsIsNeverShrunk() =>
        Assert.Equal(1.0, ScreenFit.Factor(RailLayout.Width, RailLayout.Height, WorkArea(1.0), 1.0));

    [Fact]
    public void ASurfaceThatDoesNotFitIsShrunkOnlyAsFarAsItMust()
    {
        // 125%: 1020 physical pixels of room is 816 units, less the margin.
        double k = ScreenFit.Factor(FirstRunLayout.Width, FirstRunLayout.Height, WorkArea(1.25), 1.25);
        Assert.Equal((816 - ScreenFit.Margin) / FirstRunLayout.Height, k, 3);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 1080)]
    public void AScreenThatCannotBeMeasuredChangesNothing(int w, int h) =>
        Assert.Equal(1.0, ScreenFit.Factor(820, 928, new PixelRect(0, 0, Math.Max(0, w), h), 1.25));

    [Fact]
    public void NoScalingReadsAsOneToOne() =>
        Assert.Equal(ScreenFit.Factor(820, 928, WorkArea(1.0), 1.0), ScreenFit.Factor(820, 928, WorkArea(1.0), 0));
}
