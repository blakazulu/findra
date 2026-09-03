using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The card's progress pill hangs UNDER the card, outside its shape, the way the capsule's hangs
/// under the bar. It used to sit inside the card between the field and the hints, and the card's
/// body was drawn at the height without it while the window was sized with it - so the hints were
/// painted onto the desktop below the card's bottom edge. These hold the geometry and the pixels.
/// </summary>
public class CardProgressTests
{
    private static readonly IndexProgress Working =
        IndexStatus.Pill(true, nameof(ResultKind.Document), pending: 342, indexed: 688, alive: true);

    public static IEnumerable<object[]> Shapes() =>
    [
        [0, false, false],   // the empty card
        [0, false, true],    // the empty card with the advanced form open
        [5, true, false],    // results
        [0, true, false],    // no results
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ThePillIsBelowTheCardWhateverTheCardIsShowing(int count, bool hasQuery, bool advOpen)
    {
        float card = SearchCardLayout.Height(count, hasQuery, advOpen);
        SKRect pill = SearchCardLayout.ProgressRect(count, hasQuery, advOpen);

        Assert.True(pill.Top >= card + SearchCardLayout.ProgressGap,
            $"the pill starts at {pill.Top} on a card that ends at {card}");
        Assert.Equal(SearchCardLayout.Pad, pill.Left, 3);
        Assert.Equal(SearchCardLayout.Width - SearchCardLayout.Pad, pill.Right, 3);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheWindowHoldsThePillAndNothingElseGrows(int count, bool hasQuery, bool advOpen)
    {
        // The card is the same height with the pill and without it: the pill adds a band to the
        // WINDOW. Anything that read the card's height as the window's put the hints outside.
        float card = SearchCardLayout.Height(count, hasQuery, advOpen);
        Assert.Equal(card, SearchCardLayout.WindowHeight(count, hasQuery, advOpen, progress: false), 3);
        Assert.True(SearchCardLayout.WindowHeight(count, hasQuery, advOpen, progress: true)
                    >= SearchCardLayout.ProgressRect(count, hasQuery, advOpen).Bottom);
    }

    [Fact]
    public void TheCardEndsWhereItSaysAndThePillIsPaintedInTheGapBelowIt()
    {
        // Rendered rather than measured: the defect was a painter that drew the card's body to one
        // height and the window to another, and no rectangle can see that. A column of pixels
        // down the middle has to read card, then desktop, then pill.
        SearchCardState s = SearchCardState.Empty with { Clock = 0.2, Progress = Working };
        int w = (int)SearchCardLayout.Width;
        int h = (int)Math.Ceiling(SearchCardLayout.WindowHeight(0, false, false, progress: true));
        float card = SearchCardLayout.Height(0, false);
        SKRect pill = SearchCardLayout.ProgressRect(0, false);

        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
            SearchCardPainter.Paint(canvas, s, Derived.From(Palette.Mond), Parts.Face);

        int x = w / 2;
        Assert.True(bmp.GetPixel(x, (int)card - 3).Alpha > 200, "the card's body stops short of its own bottom edge");
        Assert.Equal(0, bmp.GetPixel(x, (int)(card + SearchCardLayout.ProgressGap / 2)).Alpha);
        Assert.True(bmp.GetPixel(x, (int)pill.MidY).Alpha > 200, "nothing is painted where the pill should be");
        Assert.Equal(0, bmp.GetPixel(x, h - 1).Alpha);
    }

    [Fact]
    public void NoPillMeansNoBandUnderTheCard()
    {
        // Show false is no pill at all, not a pill at zero - and no gap reserved for one either.
        SearchCardState s = SearchCardState.Empty with { Clock = 0.2 };
        Assert.False(s.Progress.Show);
        Assert.Equal(SearchCardLayout.Height(0, false),
                     SearchCardLayout.WindowHeight(0, false, false, s.Progress.Show), 3);
    }
}
