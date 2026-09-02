using Findra;
using SkiaSharp;
using Xunit;

public class CardFooterTests
{
    /// <summary>The face --searchshot draws the card with, so what is measured here is what is
    /// painted there.</summary>
    private static readonly SKTypeface Face = SKTypeface.Default;

    private const float Size = SearchCardLayout.FooterTextSize;

    /// <summary>The footer's ordinary index line on a one-drive machine, both halves joined the
    /// way CardWindow.IndexLine joins them.</summary>
    private const string Typical = "1.5M names on C: (helper pid 12345) · indexing 4,120 · 300 done";

    /// <summary>Both halves at their longest reachable state: three drives, the helper still
    /// reading, and a backlog left by a previous session. Every clause here is a sentence
    /// IndexStatus.Line or IndexLineFormatter can actually produce.</summary>
    private const string Worst =
        "1.5M names on C:, D:, E: (helper pid 12345) (still reading the drive) · " +
        "1,234,567 waiting - indexing is paused while Findra is closed";

    private static void AssertFits(string index)
    {
        (string hint, string shown) = SearchCardLayout.FooterHalves(index, Face, Size);

        float left = CardText.Measure(hint, Face, Size);
        float right = CardText.Measure(shown, Face, Size);
        float room = SearchCardLayout.Width - 2 * (SearchCardLayout.Pad + 4);

        Assert.True(left + right <= room,
            $"the two halves measure {left:0.0} + {right:0.0} = {left + right:0.0} px in {room:0.0} px of footer");
    }

    [Fact]
    public void TheOrdinaryIndexLineDoesNotRunIntoTheHint()
    {
        // Measured on the shipping card: the hint is 465.9 px, drawn from Pad + 4, and this index
        // line is 325.7 px, drawn right-aligned at Width - Pad - 4. Neither was truncated or
        // clipped, so the two overlapped by 7.6 px on a single-drive machine in the ordinary
        // state - the state nearly every user is in nearly all of the time.
        AssertFits(Typical);
    }

    [Fact]
    public void TheLongestReachableIndexLineDoesNotRunIntoTheHintEither()
    {
        // 646.5 px against 465.9, an overlap of 328 px: the right half drawn straight across the
        // left one. Both sentences are reachable states, not hypothetical ones.
        AssertFits(Worst);
    }

    [Fact]
    public void AShortIndexLineIsLeftWholeAndSoIsTheHint()
    {
        // The cut must be a response to a real shortage, not a permanent tax. On an empty index
        // the index line is empty and the hint has the whole footer.
        (string hint, string shown) = SearchCardLayout.FooterHalves("", Face, Size);

        Assert.Equal(SearchCardLayout.FooterHint, hint);
        Assert.Equal("", shown);

        (string hint2, string shown2) = SearchCardLayout.FooterHalves("index up to date", Face, Size);
        Assert.Equal(SearchCardLayout.FooterHint, hint2);
        Assert.Equal("index up to date", shown2);
    }

    [Fact]
    public void TheLiveHalfKeepsItsBeginningWhenSomethingHasToGive()
    {
        // Which half is cut, and where. The index line carries the live facts and the hint is
        // boilerplate people learn once, so the hint yields first - and when even the index line
        // is too long for its share it keeps its head, where the counts are, rather than its
        // tail. A cut that lost "1,234,567 waiting" and kept "closed" would be worse than the
        // overlap it replaced.
        (string hint, string shown) = SearchCardLayout.FooterHalves(Worst, Face, Size);

        Assert.StartsWith("1.5M names on C:, D:, E:", shown, StringComparison.Ordinal);
        Assert.StartsWith("Enter opens", hint, StringComparison.Ordinal);
        Assert.True(hint.Length > 10, "the hint is shortened, not deleted");
    }
}
