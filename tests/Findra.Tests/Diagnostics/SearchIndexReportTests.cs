using System.Globalization;
using Findra;
using Findra.Diagnostics;
using Xunit;

[Collection("culture")]
public class SearchIndexReportTests
{
    private static IndexSnapshot Sample(
        IReadOnlyList<(string, string)>? failures = null,
        long failed = 2,
        IReadOnlyDictionary<ResultKind, long>? byKind = null) => new(
        Schema: 1, WasRebuilt: false,
        DbPath: @"C:\Users\liraz\AppData\Local\Findra\index\search.db",
        DbBytes: 12_582_912,
        Cursors: [('C', 0xBEEF, 4242), ('D', 0x1234, 77)],
        Queued: 12, Indexed: 3400, Failed: failed, Skipped: 900,
        ByKind: byKind ?? new Dictionary<ResultKind, long>
        {
            [ResultKind.Document] = 3400, [ResultKind.Photo] = 900,
            [ResultKind.Video] = 0, [ResultKind.Audio] = 0,
            [ResultKind.File] = 0, [ResultKind.Folder] = 0,
        },
        IndexerState: "indexing", IndexerCurrent: "lease.pdf", IndexerRate: "180/min",
        IndexerAlive: true, JournalDropped: 0,
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
