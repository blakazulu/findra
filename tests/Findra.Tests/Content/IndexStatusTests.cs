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
        string s = IndexStatus.Line(contentEnabled: true, state: "off", pending: 42, indexed: 1000, alive: false, rebuilt: false);

        Assert.Contains("42", s);
        Assert.Contains("indexing is paused while Findra is closed", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkInProgressCountsBothWays()
    {
        string s = IndexStatus.Line(contentEnabled: true, state: "indexing", pending: 42, indexed: 1000, alive: true, rebuilt: false);

        Assert.Contains("42", s);
        Assert.Contains("1,000", s);
        Assert.DoesNotContain("paused", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFinishedIndexSaysItIsFinished()
    {
        string s = IndexStatus.Line(contentEnabled: true, state: "idle", pending: 0, indexed: 1000, alive: true, rebuilt: false);

        Assert.Contains("1,000", s);
        Assert.Contains("up to date", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingToSayIsSaidWithNothing()
    {
        // A permanently visible empty progress line is what makes an idle widget feel busy;
        // the capsule painter draws this only when it is non-empty.
        Assert.Equal("", IndexStatus.Line(contentEnabled: true, state: "off", pending: 0, indexed: 0, alive: false, rebuilt: false));
    }

    [Fact]
    public void APausedIndexerSaysPausedByYouNotPausedByUs()
    {
        // Two different pauses reach the same line and mean opposite things: one is the
        // user's switch, one is Findra not running. Telling them apart is the whole job.
        string s = IndexStatus.Line(contentEnabled: true, state: "paused", pending: 42, indexed: 1000, alive: true, rebuilt: false);

        Assert.Contains("paused", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("while Findra is closed", s, StringComparison.Ordinal);
    }

    [Fact]
    public void ARebuiltIndexSaysSoRatherThanLookingLikeAFreshInstall()
    {
        // Spec §2a: "Index missing or unreadable - rebuilt, and the UI says so rather than
        // looking idle." Without this, someone whose index was corrupted sees a full queue
        // and a machine that indexes for an hour, with no idea why.
        string s = IndexStatus.Line(contentEnabled: true, state: "indexing", pending: 9000, indexed: 0, alive: true, rebuilt: true);

        Assert.Contains("rebuilding", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9,000", s);
    }

    [Fact]
    public void TheRebuiltNoticeOutlivesTheQueueSoItIsStillThereWhenItFinishes()
    {
        string s = IndexStatus.Line(contentEnabled: true, state: "idle", pending: 0, indexed: 9000, alive: true, rebuilt: true);

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
            string s = IndexStatus.Line(contentEnabled: true, state: "indexing", pending: 9000, indexed: 1234567, alive: true, rebuilt: false);

            Assert.Contains("9,000", s);
            Assert.Contains("1,234,567", s);
            Assert.DoesNotContain("9.000", s);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    /// <summary>A pid belonging to somebody else, so the tests below exercise the heartbeat rule
    /// and not the process rule beside it.</summary>
    private const string Elsewhere = "4242";

    [Fact]
    public void AFreshHeartbeatIsALiveIndexerAndAStaleOneIsNot()
    {
        // The line above turns `alive` into two opposite sentences, so the rule that decides it
        // has to be checkable on its own rather than buried in a window no test can construct.
        Assert.True(IndexStatus.Alive("1000", Elsewhere, thisProcess: 7, nowUnixSeconds: 1000));
        Assert.True(IndexStatus.Alive("1000", Elsewhere, 7, 1000 + IndexStatus.BeatStaleSeconds));
        Assert.False(IndexStatus.Alive("1000", Elsewhere, 7, 1000 + IndexStatus.BeatStaleSeconds + 1));
    }

    [Fact]
    public void NoHeartbeatAtAllIsNotAliveRatherThanUnknown()
    {
        // An indexer that has never run has never written the row. Reading an absent row as
        // "running" is what makes a queue that will never move look like ordinary progress.
        Assert.False(IndexStatus.Alive(null, Elsewhere, 7, 1000));
        Assert.False(IndexStatus.Alive("", Elsewhere, 7, 1000));
        Assert.False(IndexStatus.Alive("not a number", Elsewhere, 7, 1000));
    }

    [Fact]
    public void AHeartbeatFromTheFutureIsStillALiveIndexer()
    {
        // A clock that resynced under a running child dates its last beat ahead of now. That is
        // a clock, not a dead process, and calling it dead would tell someone indexing is paused
        // while the queue is visibly draining.
        Assert.True(IndexStatus.Alive("2000", Elsewhere, thisProcess: 7, nowUnixSeconds: 1000));
    }

    [Fact]
    public void ALiveIndexerWithNothingInHandStillReadsAsASentence()
    {
        // An idle child on a finished machine is the ORDINARY state, and it has no current file
        // and no rate. Interpolating both regardless printed "idle -  ()" on a real machine - a
        // dash, two spaces and an empty pair of brackets. Both --searchindex and --searchprobe
        // describe this same pair of rows, so they share the sentence rather than each inventing
        // one and drifting.
        Assert.Equal("running (pid 10052) - idle", IndexStatus.Running("10052", "idle", "", ""));
        Assert.Equal("running (pid 10052) - indexing lease.pdf, 180/min",
                     IndexStatus.Running("10052", "indexing", "lease.pdf", "180/min"));
        Assert.Equal("running (pid 10052) - indexing lease.pdf", IndexStatus.Running("10052", "indexing", "lease.pdf", ""));
        Assert.Equal("running (pid 10052) - indexing, 180/min", IndexStatus.Running("10052", "indexing", "", "180/min"));

        // No pid row - an index written by an older build - says nothing rather than "(pid ?)",
        // and a child that has written no state yet is working, not nameless.
        Assert.Equal("running - working", IndexStatus.Running(null, "", "", ""));
    }

    [Fact]
    public void AHeartbeatThisProcessWroteItselfIsNotALiveIndexerChild()
    {
        // Indexer.DrainOnce writes indexer:beat and indexer:pid exactly as a running --index
        // child does, so any process that queues and drains in place leaves a fresh heartbeat
        // behind and would read its own one-shot work back as a live child, with a state and a
        // rate. The recorded pid is the only thing that tells the two apart.
        //
        // It lives here, beside the timestamp rule, because four surfaces read the same two rows
        // - the card's footer, the capsule's progress line, --searchprobe and --searchindex - and
        // three of them used to answer this question a weaker way. One rule, one answer.
        Assert.False(IndexStatus.Alive("1000", pid: "4242", thisProcess: 4242, nowUnixSeconds: 1000));
        Assert.True(IndexStatus.Alive("1000", pid: "4242", thisProcess: 99, nowUnixSeconds: 1000));

        // An index written before the pid row existed answers off the heartbeat alone. Reading a
        // missing pid as "it must have been me" would call every such index idle for ever.
        Assert.True(IndexStatus.Alive("1000", pid: null, thisProcess: 4242, nowUnixSeconds: 1000));
        Assert.True(IndexStatus.Alive("1000", pid: "", thisProcess: 4242, nowUnixSeconds: 1000));

        // And a stale beat is a dead indexer whoever wrote it.
        Assert.False(IndexStatus.Alive("1000", "99", 4242, 1000 + IndexStatus.BeatStaleSeconds + 1));
    }

    [Fact]
    public void AnIndexNobodyHasAskedForSaysSoRatherThanLookingIdle()
    {
        // Spec §6: "the interface says which state it is in rather than looking idle". On a
        // fresh install the queue is empty and nothing is running, which is byte-for-byte what
        // a FINISHED index looks like - so without this the card says "up to date · 0 files"
        // about a machine that has never read anything.
        string line = IndexStatus.Line(contentEnabled: false, state: "", pending: 0, indexed: 0,
                                       alive: false, rebuilt: false);

        Assert.NotEqual("", line);
        Assert.Contains("off", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurningItOffAfterReadingSomethingSaysHowMuchItAlreadyHas()
    {
        // The other half: this is not a fresh install, it is somebody who turned it off, and
        // telling them their 9,000 indexed files are gone would be a lie.
        string line = IndexStatus.Line(contentEnabled: false, state: "", pending: 0, indexed: 9_000,
                                       alive: false, rebuilt: false);

        Assert.Contains("off", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9,000", line, StringComparison.Ordinal);
    }

    [Fact]
    public void OffIsNotTheSameSentenceAsPausedWhileFindraIsClosed()
    {
        // Two states that both mean "nothing is happening" and have opposite answers: one is
        // "turn it on", the other is "leave Findra open".
        string off = IndexStatus.Line(false, "", 1_200, 40, alive: false, rebuilt: false);
        string closed = IndexStatus.Line(true, "", 1_200, 40, alive: false, rebuilt: false);

        Assert.NotEqual(off, closed);
        Assert.Contains("Findra is closed", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("Findra is closed", off, StringComparison.Ordinal);
    }
}
