using Findra;
using Findra.Diagnostics;
using Xunit;

/// <summary>
/// "I can see this file and searching does not find it" had no answer in the product. Every other
/// diagnostic describes the whole index; none could say anything about a FILE, which is the only
/// thing anybody asks about. The facts were all recorded and unreachable.
/// </summary>
public sealed class ExplainFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-why-" + Guid.NewGuid().ToString("N"));

    private ContentDb Open()
    {
        Directory.CreateDirectory(_dir);
        return new ContentDb(Path.Combine(_dir, "search.db"));
    }

    private string Make(string name, string text = "hello")
    {
        Directory.CreateDirectory(_dir);
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void AFileTheIndexHasNeverSeenSaysSo()
    {
        using ContentDb db = Open();
        string p = Make("unseen.txt");

        ExplainFile.Standing s = ExplainFile.Look(db, p, []);

        Assert.True(s.OnDisk);
        Assert.False(s.Known);
        Assert.Contains("never been offered", ExplainFile.Verdict(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsGoneIsNamedBeforeAnythingElse()
    {
        // Decision order matters: a diagnostic that names a reason which is not THE reason sends
        // somebody hunting. A file that is not there explains everything downstream of it.
        using ContentDb db = Open();
        ExplainFile.Standing s = ExplainFile.Look(db, Path.Combine(_dir, "nope.txt"), []);

        Assert.False(s.OnDisk);
        Assert.Contains("not on the disk", ExplainFile.Verdict(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExcludedPathSaysWhichRuleCoversIt()
    {
        using ContentDb db = Open();
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        string p = Make(Path.Combine("node_modules", "thing.txt"));

        ExplainFile.Standing s = ExplainFile.Look(db, p, [@"\node_modules\"]);

        Assert.True(s.Excluded);
        Assert.Contains("skipped-folder", ExplainFile.Verdict(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AQueuedFileSaysWhatItIsWaitingFor()
    {
        using ContentDb db = Open();
        string p = Make("waiting.txt");
        db.Enqueue("C", 42, p, ResultKind.Document, "first pass");

        ExplainFile.Standing s = ExplainFile.Look(db, p, []);

        Assert.Equal("first pass", s.QueuedFor);
        Assert.Contains("waiting to be read", ExplainFile.Verdict(s), StringComparison.Ordinal);
    }

    [Fact]
    public void APassedOverFileCarriesTheReasonItWasPassedOverFor()
    {
        using ContentDb db = Open();
        string p = Make("tiny.png");
        using (var tx = db.Begin()) { db.Upsert("C", 7, p, ResultKind.Photo, 0, 0, ContentDb.StateSkipped,
                  "too small to be a picture", [], tx); tx.Commit(); }

        ExplainFile.Standing s = ExplainFile.Look(db, p, []);

        Assert.Contains("too small to be a picture", ExplainFile.Verdict(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileReadWithNothingToShowForItIsNotCalledSearchable()
    {
        // "Read" and "findable" are different facts, and reporting the first as the second is how
        // an empty answer looks like a broken search.
        using ContentDb db = Open();
        string p = Make("blank.txt", "");
        using (var tx = db.Begin()) { db.Upsert("C", 8, p, ResultKind.Document, 0, 0, ContentDb.StateIndexed, null, [], tx); tx.Commit(); }

        ExplainFile.Standing s = ExplainFile.Look(db, p, []);

        Assert.Empty(s.Segments);
        Assert.Contains("nothing searchable", ExplainFile.Verdict(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileEditedSinceItWasReadSaysTheIndexAnswersForTheOlderCopy()
    {
        using ContentDb db = Open();
        string p = Make("edited.txt");
        // Read when it was an hour older than it is now.
        long then = File.GetLastWriteTimeUtc(p).AddHours(-1).Ticks;
        using (var tx = db.Begin()) { db.Upsert("C", 9, p, ResultKind.Document, then, 0, ContentDb.StateIndexed, null,
                  [new ContentDb.Segment(ContentDb.SegText, -1, -1, -1, "hello")], tx); tx.Commit(); }

        ExplainFile.Standing s = ExplainFile.Look(db, p, []);

        Assert.True(s.StaleBy > 0);
        Assert.Contains("edited since", ExplainFile.Verdict(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AReadFileReportsWhatCameOutOfIt()
    {
        using ContentDb db = Open();
        string p = Make("doc.txt");
        using (var tx = db.Begin()) { db.Upsert("C", 10, p, ResultKind.Document, File.GetLastWriteTimeUtc(p).Ticks, 0,
                  ContentDb.StateIndexed, null,
                  [new ContentDb.Segment(ContentDb.SegText, -1, -1, -1, "one"),
                   new ContentDb.Segment(ContentDb.SegText, -1, -1, -1, "two")], tx); tx.Commit(); }

        ExplainFile.Standing s = ExplainFile.Look(db, p, []);

        Assert.Equal("read and searchable", ExplainFile.Verdict(s));
        Assert.Equal(2, s.Segments.Single().Count);
        Assert.Equal(ContentDb.SegText, s.Segments.Single().SegKind);
    }

    [Fact]
    public void AScoreIsAlwaysCompanionedByTheFloorItIsJudgedOn()
    {
        // The floors come from ContentBranch's own constants, so this can never describe a
        // threshold the engine does not apply.
        var above = new ExplainFile.SegmentScore(ContentDb.SegImage, 3, ContentBranch.PhotoFloor + 0.01f,
                                                 ContentBranch.PhotoFloor, Live: true, "");
        var below = above with { Cosine = ContentBranch.PhotoFloor - 0.01f };

        Assert.Contains("a match", ExplainFile.Says(above), StringComparison.Ordinal);
        Assert.Contains("below it", ExplainFile.Says(below), StringComparison.Ordinal);
        Assert.Contains(ContentBranch.PhotoFloor.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                        ExplainFile.Says(below), StringComparison.Ordinal);
    }

    [Fact]
    public void ADiscardedVectorIsReportedRatherThanScored()
    {
        // Its bytes are still on the disk, so dotting them yields a confident number for a
        // segment that no longer belongs to anything.
        var gone = new ExplainFile.SegmentScore(ContentDb.SegImage, 5, 0.9f, ContentBranch.PhotoFloor,
                                                Live: false, "");
        Assert.Contains("discarded", ExplainFile.Says(gone), StringComparison.Ordinal);
    }

    [Fact]
    public void ASegmentWithNoVectorSaysItIsFoundByItsWords()
    {
        var words = new ExplainFile.SegmentScore(ContentDb.SegText, -1, 0f, 0f, Live: true, "hello");
        Assert.Contains("words only", ExplainFile.Says(words), StringComparison.Ordinal);
    }
}
