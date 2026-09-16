using System.Collections.Generic;
using Findra;
using Xunit;

public class IndexerWatchTests
{
    private const long Now = 1_000_000;
    private static string Beat(long secondsAgo) => (Now - secondsAgo).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A child that has been running longer than the whole stall window, which is what
    /// every case below except the young-child one is about: pass an age that is not itself the
    /// reason for the answer, so each test still turns on the clause it names.</summary>
    private static readonly TimeSpan Old = TimeSpan.FromSeconds(IndexerWatch.StallSeconds + 1);

    [Fact]
    public void AChildThatBeatRecentlyIsLeftAlone()
        => Assert.False(IndexerWatch.ShouldRestart(true, true, 10, Beat(5), Now, Old));

    [Fact]
    public void AChildThatHasNotBeatenForThreeMinutesIsRestarted()
        => Assert.True(IndexerWatch.ShouldRestart(true, true, 10, Beat(IndexerWatch.StallSeconds + 1), Now, Old));

    [Fact]
    public void NothingIsRestartedWhenThereIsNoChild()
        => Assert.False(IndexerWatch.ShouldRestart(false, true, 10, Beat(9999), Now, Old));

    [Fact]
    public void NothingIsRestartedWhenReadingIsOff()
        => Assert.False(IndexerWatch.ShouldRestart(true, false, 10, Beat(9999), Now, Old));

    [Fact]
    public void NothingIsRestartedWhenTheQueueIsEmpty()
        => Assert.False(IndexerWatch.ShouldRestart(true, true, 0, Beat(9999), Now, Old));

    [Fact]
    public void AMissingOrUnreadableBeatIsNotEvidenceOfAStall()
    {
        Assert.False(IndexerWatch.ShouldRestart(true, true, 10, null, Now, Old));
        Assert.False(IndexerWatch.ShouldRestart(true, true, 10, "not a number", Now, Old));
    }

    [Fact]
    public void ABeatFromTheFutureIsAClockThatMovedRatherThanAStall()
        => Assert.False(IndexerWatch.ShouldRestart(true, true, 10, (Now + 500).ToString(System.Globalization.CultureInfo.InvariantCulture), Now, Old));

    [Fact]
    public void AChildTooYoungToHaveReportedAnythingIsNotJudgedOnTheBeatBeforeIt()
    {
        // The beat row belongs to whichever child last wrote it, and a child that is killed leaves
        // its own behind. A replacement cannot have gone three minutes without reporting when it
        // has only existed for one second, so the stale row it would be judged on is about the
        // child before it. An unknown age is refused on the same terms.
        //
        // IndexerHostTests drives the same rule through the real host, which is where this one
        // actually bites: the pure rule here passed throughout the defect's life.
        Assert.False(IndexerWatch.ShouldRestart(true, true, 10, Beat(9999), Now, TimeSpan.FromSeconds(1)));
        Assert.False(IndexerWatch.ShouldRestart(true, true, 10, Beat(9999), Now, null));
        Assert.True(IndexerWatch.ShouldRestart(true, true, 10, Beat(9999), Now, Old));
    }

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
