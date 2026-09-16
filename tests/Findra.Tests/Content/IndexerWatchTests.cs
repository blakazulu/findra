using System.Collections.Generic;
using Findra;
using Xunit;

public class IndexerWatchTests
{
    private const long Now = 1_000_000;
    private static string Beat(long secondsAgo) => (Now - secondsAgo).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void AChildThatBeatRecentlyIsLeftAlone()
        => Assert.False(IndexerWatch.ShouldRestart(true, true, 10, Beat(5), Now));

    [Fact]
    public void AChildThatHasNotBeatenForThreeMinutesIsRestarted()
        => Assert.True(IndexerWatch.ShouldRestart(true, true, 10, Beat(IndexerWatch.StallSeconds + 1), Now));

    [Fact]
    public void NothingIsRestartedWhenThereIsNoChild()
        => Assert.False(IndexerWatch.ShouldRestart(false, true, 10, Beat(9999), Now));

    [Fact]
    public void NothingIsRestartedWhenReadingIsOff()
        => Assert.False(IndexerWatch.ShouldRestart(true, false, 10, Beat(9999), Now));

    [Fact]
    public void NothingIsRestartedWhenTheQueueIsEmpty()
        => Assert.False(IndexerWatch.ShouldRestart(true, true, 0, Beat(9999), Now));

    [Fact]
    public void AMissingOrUnreadableBeatIsNotEvidenceOfAStall()
    {
        Assert.False(IndexerWatch.ShouldRestart(true, true, 10, null, Now));
        Assert.False(IndexerWatch.ShouldRestart(true, true, 10, "not a number", Now));
    }

    [Fact]
    public void ABeatFromTheFutureIsAClockThatMovedRatherThanAStall()
        => Assert.False(IndexerWatch.ShouldRestart(true, true, 10, (Now + 500).ToString(System.Globalization.CultureInfo.InvariantCulture), Now));

    [Fact]
    public void KillingSaysWhyAndOnlySaysItWhenSomethingWasKilled()
    {
        var said = new List<string>();
        IndexerHost.Kill("no progress on big.avi for 3 min", said.Add, () => true);
        Assert.Single(said);

        said.Clear();
        IndexerHost.Kill("nothing to kill", said.Add, () => false);
        Assert.Empty(said);
    }
}
