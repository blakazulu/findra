using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The column of pills beside the field, which is the only part of the card that is not about
/// the query itself.
///
/// <para>Settings joined it because it could be reached from exactly two places, and both were
/// hidden: the tray icon's menu and a right-click on the capsule. Nothing a person looking at
/// Findra could see mentioned it, so the capability list, the transcription limit and the switch
/// that starts reading inside files all read as features that do not exist.</para>
/// </summary>
public class CardPillTests
{
    private static SKRect Content => SearchCardLayout.ContentRect();
    private static SKRect Adv => SearchCardLayout.AdvRect();
    private static SKRect Settings => SearchCardLayout.SettingsRect();

    [Fact]
    public void SettingsSitsUnderAdvancedInTheSameColumnOnTheSameTerms()
    {
        // The same column and the same shape as the two above it, or it reads as something
        // bolted on rather than as the third of three.
        Assert.Equal(Adv.Left, Settings.Left, 3);
        Assert.Equal(Adv.Right, Settings.Right, 3);
        Assert.Equal(Adv.Height, Settings.Height, 3);

        // The same air between Advanced and Settings as between Content and Advanced.
        Assert.Equal(Adv.Top - Content.Bottom, Settings.Top - Adv.Bottom, 3);
    }

    [Fact]
    public void TheEmptyCardIsTallEnoughToHoldTheWholePillColumn()
    {
        // The card with nothing typed into it used to end 120px down, which is five pixels below
        // where the third pill now ends. A pill drawn over the card's own bottom edge is the one
        // state of this surface a person sees before they have done anything at all.
        float h = SearchCardLayout.Height(0, hasQuery: false);
        Assert.True(h >= Settings.Bottom + SearchCardLayout.Pad,
            $"the empty card is {h}px tall and the pill column ends at {Settings.Bottom}");
    }

    [Fact]
    public void TheHeaderLineStopsBeforeThePillColumnRatherThanRunningUnderIt()
    {
        // The count and the timing share the header row with the pill column now. The right half
        // is right-aligned, so without this it is drawn straight across the Settings pill on
        // every search that reports a timing - which is every search.
        Assert.True(SearchCardLayout.HeaderRight <= Settings.Left,
            $"the header line ends at {SearchCardLayout.HeaderRight} and the pill column starts at {Settings.Left}");
    }

    [Fact]
    public void TheEmptyCardsHintStopsShortOfThePillColumn()
    {
        // Measured in the shipped face at the size the painter draws it, in both moods: the
        // grammar hint is the longest line on the empty card and it runs the width of it.
        foreach (string hint in SearchCardPainter.EmptyHints)
        {
            float ends = SearchCardLayout.Pad + 6 + CardText.Measure(hint, Parts.Face, 12.5f);
            Assert.True(ends <= Settings.Left,
                $"the hint ends at {ends:F1} and the pill column starts at {Settings.Left}");
        }
    }

    [Fact]
    public void EveryPillLabelFitsThePillItIsDrawnIn()
    {
        // The card's pills draw their label centred and DO NOT ellipsise, so a label that does
        // not fit is drawn over both ends of its own outline rather than cut.
        foreach ((string label, SKRect r) in new[]
        {
            (SearchCardPainter.ContentLabel, Content),
            (SearchCardPainter.AdvancedLabel, Adv),
            (SearchCardPainter.SettingsLabel, Settings),
        })
        {
            float need = CardText.Measure(label, Parts.Face, SearchCardPainter.PillTextSize);
            Assert.True(need <= r.Width - 12,
                $"'{label}' needs {need:F1}px and its pill gives {r.Width - 12:F1}px");
        }
    }

    [Fact]
    public void APressOnSettingsIsAnsweredWhateverElseTheCardIsShowing()
    {
        // Empty, with a query, and with the advanced popup open - the popup overlays the card and
        // answers first, and the pill column is deliberately outside what it swallows.
        foreach ((bool hasQuery, bool advOpen) in new[] { (false, false), (true, false), (false, true) })
        {
            SearchHit hit = SearchCardLayout.HitTest(
                Settings.MidX, Settings.MidY, count: 3, scroll: 0, hasQuery: hasQuery, advOpen: advOpen);
            Assert.Equal(SearchTarget.Settings, hit.Target);
        }
    }

    [Fact]
    public void ThePillColumnDoesNotOverlapAnythingElseTheCardHitTests()
    {
        // The three pills are stacked in a column the field, the chips and the rows all end
        // before. A pill whose rectangle overlapped a chip would take the chip's clicks, because
        // the pills are tested first.
        Assert.True(SearchCardLayout.FieldRect().Right <= Settings.Left);
        for (int i = 0; i < SearchCardLayout.ChipLabels.Length; i++)
            Assert.True(SearchCardLayout.ChipRect(i).Right <= Settings.Left);
        Assert.True(SearchCardLayout.RowRect(0).Right <= Settings.Left);
    }
}
