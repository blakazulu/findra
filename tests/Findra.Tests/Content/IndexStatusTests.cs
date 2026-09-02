using System.Globalization;
using Findra;
using Xunit;

[Collection("culture")]
public class IndexStatusTests
{
    [Fact]
    public void ABacklogWithNoIndexerRunningSaysWhyRatherThanLookingIdle()
    {
        // Spec §3: indexing runs only while Findra runs, and "the UI must say so plainly
        // rather than looking idle". A silent progress bar at 60% is the failure this
        // sentence exists to prevent.
        string s = IndexStatus.Line(state: "off", pending: 42, indexed: 1000, alive: false, rebuilt: false);

        Assert.Contains("42", s);
        Assert.Contains("indexing is paused while Findra is closed", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkInProgressCountsBothWays()
    {
        string s = IndexStatus.Line("indexing", pending: 42, indexed: 1000, alive: true, rebuilt: false);

        Assert.Contains("42", s);
        Assert.Contains("1,000", s);
        Assert.DoesNotContain("paused", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFinishedIndexSaysItIsFinished()
    {
        string s = IndexStatus.Line("idle", pending: 0, indexed: 1000, alive: true, rebuilt: false);

        Assert.Contains("1,000", s);
        Assert.Contains("up to date", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingToSayIsSaidWithNothing()
    {
        // A permanently visible empty progress line is what makes an idle widget feel busy;
        // the capsule painter draws this only when it is non-empty.
        Assert.Equal("", IndexStatus.Line("off", pending: 0, indexed: 0, alive: false, rebuilt: false));
    }

    [Fact]
    public void APausedIndexerSaysPausedByYouNotPausedByUs()
    {
        // Two different pauses reach the same line and mean opposite things: one is the
        // user's switch, one is Findra not running. Telling them apart is the whole job.
        string s = IndexStatus.Line("paused", pending: 42, indexed: 1000, alive: true, rebuilt: false);

        Assert.Contains("paused", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("while Findra is closed", s, StringComparison.Ordinal);
    }

    [Fact]
    public void ARebuiltIndexSaysSoRatherThanLookingLikeAFreshInstall()
    {
        // Spec §2a: "Index missing or unreadable - rebuilt, and the UI says so rather than
        // looking idle." Without this, someone whose index was corrupted sees a full queue
        // and a machine that indexes for an hour, with no idea why.
        string s = IndexStatus.Line("indexing", pending: 9000, indexed: 0, alive: true, rebuilt: true);

        Assert.Contains("rebuilding", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9,000", s);
    }

    [Fact]
    public void TheRebuiltNoticeOutlivesTheQueueSoItIsStillThereWhenItFinishes()
    {
        string s = IndexStatus.Line("idle", pending: 0, indexed: 9000, alive: true, rebuilt: true);

        Assert.Contains("rebuilt", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9,000", s);
    }

    [Fact]
    public void TheNumbersReadTheSameOnEveryMachine()
    {
        // The project sets InvariantGlobalization=false, so a bare {n:N0} renders "9.000" in
        // German and "9,000" here - and this line is compared in tests and read by users.
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string s = IndexStatus.Line("indexing", pending: 9000, indexed: 1234567, alive: true, rebuilt: false);

            Assert.Contains("9,000", s);
            Assert.Contains("1,234,567", s);
            Assert.DoesNotContain("9.000", s);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void AFreshHeartbeatIsALiveIndexerAndAStaleOneIsNot()
    {
        // The line above turns `alive` into two opposite sentences, so the rule that decides it
        // has to be checkable on its own rather than buried in a window no test can construct.
        Assert.True(IndexStatus.Alive("1000", nowUnixSeconds: 1000));
        Assert.True(IndexStatus.Alive("1000", nowUnixSeconds: 1000 + IndexStatus.BeatStaleSeconds));
        Assert.False(IndexStatus.Alive("1000", nowUnixSeconds: 1000 + IndexStatus.BeatStaleSeconds + 1));
    }

    [Fact]
    public void NoHeartbeatAtAllIsNotAliveRatherThanUnknown()
    {
        // An indexer that has never run has never written the row. Reading an absent row as
        // "running" is what makes a queue that will never move look like ordinary progress.
        Assert.False(IndexStatus.Alive(null, 1000));
        Assert.False(IndexStatus.Alive("", 1000));
        Assert.False(IndexStatus.Alive("not a number", 1000));
    }

    [Fact]
    public void AHeartbeatFromTheFutureIsStillALiveIndexer()
    {
        // A clock that resynced under a running child dates its last beat ahead of now. That is
        // a clock, not a dead process, and calling it dead would tell someone indexing is paused
        // while the queue is visibly draining.
        Assert.True(IndexStatus.Alive("2000", nowUnixSeconds: 1000));
    }
}
