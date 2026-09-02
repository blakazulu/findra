using System.Globalization;
using Findra;
using Findra.Diagnostics;
using Xunit;

[Collection("culture")]
public sealed class SearchIndexReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-report-" + Guid.NewGuid().ToString("N"));

    private string Db()
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, "search.db");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    internal static IndexSnapshot Sample(
        IReadOnlyList<(string, string)>? failures = null,
        long failed = 2,
        IReadOnlyDictionary<ResultKind, long>? byKind = null) => new(
        Schema: 1, WasRebuilt: false,
        DbPath: @"C:\Users\liraz\AppData\Local\Findra\index\search.db",
        Stores: [("search.db", 12_582_912)],
        Cursors: [('C', 0xBEEF, 4242), ('D', 0x1234, 77)],
        Queued: 12, Indexed: 3400, Failed: failed, Skipped: 900,
        ByKind: byKind ?? new Dictionary<ResultKind, long>
        {
            [ResultKind.Document] = 3400, [ResultKind.Photo] = 900,
            [ResultKind.Video] = 0, [ResultKind.Audio] = 0,
            [ResultKind.File] = 0, [ResultKind.Folder] = 0,
        },
        IndexerState: "indexing", IndexerCurrent: "lease.pdf", IndexerRate: "180/min",
        IndexerPid: "10052", IndexerAlive: true, JournalDropped: 0,
        SessionFailures: 0, SessionFailure: "",
        Failures: failures ?? []);

    [Fact]
    public void ItReportsTheSchemaVersionAndWhereTheIndexLives()
    {
        string s = SearchIndexReport.Render(Sample());

        Assert.Contains("schema 1", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"AppData\Local\Findra\index\search.db", s);
        Assert.Contains("12.0 MB", s);
    }

    [Fact]
    public void EveryVolumeGetsItsOwnConsumedPosition()
    {
        string s = SearchIndexReport.Render(Sample());

        Assert.Contains("C:", s);
        Assert.Contains("4242", s);
        Assert.Contains("D:", s);
        Assert.Contains("77", s);
    }

    [Fact]
    public void AVolumeWithNoRecordedPositionSaysSoRatherThanShowingZero()
    {
        // Zero is a real USN. Printing it for "never consumed" is the difference between
        // "this drive is fresh" and "this drive has consumed nothing since the beginning".
        IndexSnapshot s = Sample() with { Cursors = [] };

        string text = SearchIndexReport.Render(s);

        Assert.Contains("no volume has a recorded position", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usn 0", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryKindIsListedIncludingTheOnesWithNothingInThem()
    {
        // "Why are none of my photos indexed" is unanswerable if the Photo row is missing
        // because its count is zero.
        string s = SearchIndexReport.Render(Sample());

        foreach (ResultKind k in Enum.GetValues<ResultKind>())
            Assert.Contains(FileKinds.Label(k), s);
        Assert.Contains("Video", s);
    }

    [Fact]
    public void TheIndexerStateAndWhatItIsOnAppear()
    {
        string s = SearchIndexReport.Render(Sample());

        Assert.Contains("indexing", s);
        Assert.Contains("lease.pdf", s);
        Assert.Contains("180/min", s);
        Assert.Contains("pid 10052", s);
    }

    [Fact]
    public void AnIdleIndexerWithNoFileAndNoRateStillReadsAsASentence()
    {
        // The ordinary steady state on a finished machine: a live child with nothing in hand.
        // Both existing tests fill all three fields, so the empty case was rendered nowhere in
        // the suite - and on a real machine this line came out literally as "idle -  ()": a
        // dash, two spaces and an empty pair of brackets. It is the line most people will see.
        string s = SearchIndexReport.Render(Sample() with { IndexerState = "idle", IndexerCurrent = "", IndexerRate = "" });

        Assert.Contains("indexer  : running (pid 10052) - idle\n", s);
        Assert.DoesNotContain("()", s);
        Assert.DoesNotContain("- \n", s);
    }

    [Fact]
    public void TheIndexerLineNamesTheProcessAnsweringJustAsTheProbeDoes()
    {
        // --searchprobe reads the same two meta rows and has always printed the pid. Without it
        // the reader cannot tell a real --index child from a one-shot drain some other terminal
        // is running, and the two mean opposite things about whether the queue will keep moving.
        Assert.Contains("pid 10052", SearchIndexReport.Render(Sample()));

        // An index written before the pid row existed says nothing rather than "(pid ?)".
        string older = SearchIndexReport.Render(Sample() with { IndexerPid = "" });
        Assert.Contains("indexer  : running - indexing", older);
        Assert.DoesNotContain("pid", older);
    }

    [Fact]
    public void TheIndexSizeCountsTheWriteAheadLogAndTheSharedMemoryFileToo()
    {
        // Measured on a real machine: this line printed "4.0 KB" for an index that was 68 KB the
        // moment the connection closed and checkpointed, with a 988 KB -wal beside it. Those are
        // real bytes on the user's disk, and "how big is my index" is the whole point of the
        // line. --searchbench has sized all three files since it was written and its comment says
        // the sidecars are routinely the larger ones; the same fact was reported two ways.
        string s = SearchIndexReport.Render(Sample() with
        {
            Stores = [("search.db", 68 * 1024), ("search.db-wal", 988 * 1024), ("search.db-shm", 32 * 1024)],
        });

        Assert.Contains("1.1 MB", s);            // the total, not the database on its own
        Assert.DoesNotContain("(68.0 KB)", s);
        Assert.Contains("search.db-wal 988.0 KB", s);
        Assert.Contains("search.db-shm 32.0 KB", s);
    }

    [Fact]
    public void AnIndexWithNoSidecarsIsSizedWithoutABreakdownNobodyNeeds()
    {
        // A checkpointed index is one file. Printing "search.db 12.0 MB" underneath "12.0 MB" is
        // the noise that trains people to stop reading the report.
        string s = SearchIndexReport.Render(Sample());

        Assert.Contains("(12.0 MB)", s);
        Assert.DoesNotContain("search.db 12.0 MB", s);
    }

    [Fact]
    public void ARebuildTheRunningInterfacePerformedIsAnnouncedHereToo()
    {
        // WasRebuilt is a fact about the OPEN that rebuilt the file, and this diagnostic opens
        // its own connection, which never rebuilt anything. The interface records the fact in the
        // index itself precisely because a flag cannot cross a connection, and the card and the
        // capsule both read that row. The worst shape this can take is the one measured: the card
        // says the index was thrown away, and the diagnostic the user runs to find out why says
        // nothing at all.
        using var db = new ContentDb(Db());
        Assert.False(db.WasRebuilt);
        db.Set("index:rebuilt", "1");

        string s = SearchIndexReport.Render(SearchIndex.Snapshot(db));

        Assert.Contains("rebuilt", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnIndexNobodyRebuiltSaysNothingAboutRebuilding()
    {
        // The pair, so a notice printed unconditionally cannot pass the test above.
        using var db = new ContentDb(Db());

        Assert.DoesNotContain("rebuilt", SearchIndexReport.Render(SearchIndex.Snapshot(db)),
                              StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnIndexerThatIsNotRunningIsNotReportedAsBusy()
    {
        string s = SearchIndexReport.Render(Sample() with { IndexerAlive = false, IndexerState = "indexing" });

        Assert.Contains("not running", s, StringComparison.OrdinalIgnoreCase);
        // a stale heartbeat must not be read back as live work
        Assert.DoesNotContain("180/min", s);
    }

    [Fact]
    public void AtMostTenFailuresAreListedAndTheRestAreCountedFromTheRealTotal()
    {
        // The remainder comes from Failed, NOT from the length of the sample. The reader is
        // handed ten rows out of four thousand; deriving "and N more" from the ten it can see
        // makes a catastrophic run and a trivial one print the same sentence.
        var sample = Enumerable.Range(1, 10).Select(i => ($@"C:\a\f{i}.pdf", "PdfException: broken")).ToArray();

        string s = SearchIndexReport.Render(Sample(failures: sample, failed: 4000));

        Assert.Contains(@"C:\a\f1.pdf", s);
        Assert.Contains(@"C:\a\f10.pdf", s);
        Assert.Contains("3,990 more", s);
    }

    [Fact]
    public void AFewFailuresAreAllListedWithNoMoreLine()
    {
        var sample = Enumerable.Range(1, 3).Select(i => ($@"C:\a\f{i}.pdf", "PdfException: broken")).ToArray();

        string s = SearchIndexReport.Render(Sample(failures: sample, failed: 3));

        Assert.Contains(@"C:\a\f3.pdf", s);
        Assert.DoesNotContain("more", s.Split("failures")[^1]);
    }

    [Fact]
    public void AFailureAlwaysCarriesItsReason()
    {
        string s = SearchIndexReport.Render(Sample(failures: [(@"C:\a\x.pdf", "PdfException: broken xref")], failed: 1));

        Assert.Contains("PdfException: broken xref", s);
    }

    [Fact]
    public void ARebuiltIndexIsAnnouncedAtTheTop()
    {
        // Someone running --searchindex after a corruption needs the first line to explain
        // why the counts are small, not to have to infer it from them.
        string s = SearchIndexReport.Render(Sample() with { WasRebuilt = true });

        Assert.Contains("rebuilt", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DroppedJournalEventsAreReportedBecauseNothingElseWouldShowThem()
    {
        // A dropped event is a file the feeder never saw. It cannot be inferred from any
        // count in this report - the index simply looks finished - so it is printed, and
        // only when non-zero, so an ordinary run is not noisy.
        Assert.DoesNotContain("dropped", SearchIndexReport.Render(Sample()), StringComparison.OrdinalIgnoreCase);

        string s = SearchIndexReport.Render(Sample() with { JournalDropped = 118 });

        Assert.Contains("118", s);
        Assert.Contains("dropped", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionsThatCouldNotFeedTheQueueAreCountedWhereSomebodyWillSeeThem()
    {
        // The question this answers is "why is nothing being indexed", and the honest answer is
        // often "this install has not been able to reach the helper four hundred times". That
        // number lived only in a log line that said itself once per process and then went quiet,
        // so the person asking had neither the count nor, after the first minute, the line.
        string s = SearchIndexReport.Render(Sample() with
        {
            SessionFailures = 412,
            SessionFailure = "TimeoutException: The operation has timed out.",
        });

        Assert.Contains("412", s);
        Assert.Contains("TimeoutException", s);
    }

    [Fact]
    public void AnInstallThatHasNeverFailedToReachTheHelperSaysNothingAboutIt()
    {
        // Printed only when non-zero, like the dropped-events line. A report that always says
        // "0 failed sessions" trains people to stop reading it, and this line has to be noticed
        // on the one machine where it is not zero.
        Assert.DoesNotContain("failed session", SearchIndexReport.Render(Sample()),
                              StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNumbersReadTheSameOnEveryMachine()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string s = SearchIndexReport.Render(Sample());

            Assert.Contains("3,400", s);
            Assert.DoesNotContain("3.400", s);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }
}
