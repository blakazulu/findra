using Findra;
using Xunit;

public sealed class ContentDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-db-" + Guid.NewGuid().ToString("N"));

    private ContentDb Open()
    {
        Directory.CreateDirectory(_dir);
        return new ContentDb(Path.Combine(_dir, "search.db"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void Put(ContentDb db, string path, ulong frn, ResultKind kind, params string[] chunks)
    {
        using var tx = db.Begin();
        var segs = chunks.Select(c => new ContentDb.Segment(ContentDb.SegText, -1, -1, -1, c)).ToList();
        db.Upsert("C", frn, path, kind, mtime: 1, size: 100, ContentDb.StateIndexed, null, segs, tx);
        tx.Commit();
    }

    [Fact]
    public void EnqueueUpsertsOnVolumeAndFrn()
    {
        using ContentDb db = Open();

        db.Enqueue("C", 42, @"C:\old\report.pdf", ResultKind.Document, "new");
        db.Enqueue("C", 42, @"C:\new\report.pdf", ResultKind.Document, "rename");

        Assert.Equal(1, db.PendingCount());
        ContentDb.Pending? next = db.TakeNext();
        Assert.Equal(@"C:\new\report.pdf", next!.Value.Path);
        Assert.Equal("rename", next.Value.Reason);
    }

    [Fact]
    public void TakeNextTakesDeletesFirstThenOldest()
    {
        // Deletes free storage and unblock re-indexes of the same path, so they jump the
        // queue. Ordering by id alone would answer with the .pdf here.
        using ContentDb db = Open();
        db.Enqueue("C", 1, @"C:\a.pdf", ResultKind.Document, "new");
        db.Enqueue("C", 2, @"C:\b.pdf", ResultKind.Document, "new");
        db.Enqueue("C", 3, "", ResultKind.File, ContentDb.ReasonDelete);

        ContentDb.Pending first = db.TakeNext()!.Value;
        Assert.Equal(ContentDb.ReasonDelete, first.Reason);
        db.Dequeue(first.Id);

        Assert.Equal(@"C:\a.pdf", db.TakeNext()!.Value.Path);
    }

    [Fact]
    public void UpsertAndDeleteAreSymmetric()
    {
        using ContentDb db = Open();
        Put(db, @"C:\a\lease.pdf", 10, ResultKind.Document, "the quarterly lease agreement", "signed in March");

        Assert.Equal((1L, 2L, 0L), db.Stats());

        using (var tx = db.Begin()) { db.Delete("C", 10, tx); tx.Commit(); }

        Assert.Equal((0L, 0L, 0L), db.Stats());
        Assert.Empty(db.Fts("lease", 10));      // the FTS rows went with the segments
    }

    [Fact]
    public void FtsFindsAWordThatIsThereAndNotOneThatIsNot()
    {
        using ContentDb db = Open();
        Put(db, @"C:\a\lease.pdf", 10, ResultKind.Document, "the quarterly lease agreement");

        ContentDb.SegmentHit hit = Assert.Single(db.Fts("lease", 10));
        Assert.Equal(@"C:\a\lease.pdf", hit.Path);
        Assert.Equal(ResultKind.Document, hit.Kind);

        Assert.Empty(db.Fts("bicycle", 10));
    }

    [Fact]
    public void FtsFindsAHebrewWord()
    {
        using ContentDb db = Open();
        Put(db, @"C:\a\שכירות.pdf", 11, ResultKind.Document, "חוזה שכירות דירה בתל אביב");

        Assert.Single(db.Fts("שכירות", 10));
        Assert.Empty(db.Fts("מכונית", 10));
    }

    [Fact]
    public void FtsSurvivesAHyphenAndAQuoteInTheQuery()
    {
        // Every one of these is FTS5 syntax. Reaching the parser raw is a SqliteException
        // that the store swallows - so the visible symptom is "search finds nothing", for
        // every query with a hyphen in it, forever.
        using ContentDb db = Open();
        Put(db, @"C:\a\inv.pdf", 12, ResultKind.Document, "invoice 2026 total due on receipt");

        Assert.Single(db.Fts("invoice-2026", 10));    // becomes the phrase "invoice-2026"*
        Assert.Single(db.Fts("invoice \"total\"", 10));

        // NEAR is neutralised into a quoted literal term - "invoice"* AND "NEAR"* AND
        // "total"* - so it matches nothing, because no word in the document starts "near".
        // Empty is the CORRECT answer here; the property under test is that an operator in
        // the query text cannot reach the parser as an operator, and cannot throw.
        Assert.Empty(db.Fts("invoice NEAR total", 10));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a", "")]                       // one character is not a term
    [InlineData("ab", "\"ab\"")]                // two characters: exact, no prefix star
    [InlineData("lease", "\"lease\"*")]         // three or more: a prefix term
    [InlineData("lease agreement", "\"lease\"* AND \"agreement\"*")]
    public void FtsQueryQuotesEveryTermAndAndsThem(string raw, string expected)
        => Assert.Equal(expected, ContentDb.FtsQuery(raw));

    [Fact]
    public void AnEmptyQueryMatchesNothingRatherThanEverything()
    {
        using ContentDb db = Open();
        Put(db, @"C:\a.pdf", 20, ResultKind.Document, "alpha");
        Put(db, @"C:\b.pdf", 21, ResultKind.Document, "beta");
        Put(db, @"C:\c.pdf", 22, ResultKind.Document, "gamma");

        Assert.Empty(db.Fts("", 10));
        Assert.Empty(db.Fts("   ", 10));
    }

    [Fact]
    public void IsCurrentIsTrueOnlyForThisFileAtThisMtimeInAFinishedState()
    {
        using ContentDb db = Open();
        using (var tx = db.Begin())
        {
            db.Upsert("C", 30, @"C:\a.pdf", ResultKind.Document, mtime: 777, size: 5,
                      ContentDb.StateIndexed, null, [], tx);
            db.Upsert("C", 31, @"C:\b.pdf", ResultKind.Document, mtime: 777, size: 5,
                      ContentDb.StateQueued, null, [], tx);
            tx.Commit();
        }

        Assert.True(db.IsCurrent("C", 30, 777));
        Assert.False(db.IsCurrent("C", 30, 778));    // edited since
        Assert.False(db.IsCurrent("D", 30, 777));    // another volume's frn 30
        Assert.False(db.IsCurrent("C", 99, 777));    // never seen
        Assert.False(db.IsCurrent("C", 31, 777));    // written but not finished
    }

    [Fact]
    public void RequeueKindsTakesOnlyTheKindsItIsGiven()
    {
        // This is the seam the all-or-nothing gate becomes: enabling a capability later
        // re-queues ONLY the files it covers (spec §6).
        using ContentDb db = Open();
        Put(db, @"C:\a\lease.pdf", 40, ResultKind.Document, "text");
        Put(db, @"C:\a\sunset.jpg", 41, ResultKind.Photo);
        Put(db, @"C:\a\talk.mp3", 42, ResultKind.Audio);

        int n = db.RequeueKinds([(int)ResultKind.Photo], "photos enabled");

        Assert.Equal(1, n);
        (long, string) only = Assert.Single(db.PendingPaths());
        Assert.Equal(@"C:\a\sunset.jpg", only.Item2);
    }

    [Fact]
    public void RequeueKindsPicksUpSkippedFilesAndNotJustIndexedOnes()
    {
        // THE test for Step 5a. Every photo this plan meets ends at StateSkipped, so a
        // RequeueKinds that only sees state=1 finds nothing to re-queue and the entire
        // "Plan 5 picks up exactly those" story silently does not happen. Against the
        // unmodified source this returns 0.
        using ContentDb db = Open();
        using (var tx = db.Begin())
        {
            db.Upsert("C", 50, @"C:\a\sunset.jpg", ResultKind.Photo, 1, 1,
                      ContentDb.StateSkipped, "no decoder for this kind yet", [], tx);
            db.Upsert("C", 51, @"C:\a\beach.jpg", ResultKind.Photo, 1, 1,
                      ContentDb.StateIndexed, null, [], tx);
            db.Upsert("C", 52, @"C:\a\broken.jpg", ResultKind.Photo, 1, 1,
                      ContentDb.StateFailed, "SKException: not an image", [], tx);
            tx.Commit();
        }

        int n = db.RequeueKinds([(int)ResultKind.Photo], "photos enabled");

        Assert.Equal(2, n);                                        // skipped AND indexed
        string[] queued = db.PendingPaths().Select(p => p.Path).Order().ToArray();
        Assert.Equal([@"C:\a\beach.jpg", @"C:\a\sunset.jpg"], queued);
        // A file the decoder genuinely could not read is NOT retried by enabling a
        // capability - nothing about it changed, and retrying it every install is a loop.
        Assert.DoesNotContain(@"C:\a\broken.jpg", queued);
    }
}
