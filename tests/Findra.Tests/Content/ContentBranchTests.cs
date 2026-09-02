using Findra;
using Xunit;

public sealed class ContentBranchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-branch-" + Guid.NewGuid().ToString("N"));

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
        db.Upsert("C", frn, path, kind, 1, 100, ContentDb.StateIndexed, null, segs, tx);
        tx.Commit();
    }

    [Fact]
    public void AWordInsideAFileFindsTheFileAndCarriesTheSentenceAroundIt()
    {
        using ContentDb db = Open();
        Put(db, @"C:\Papers\contract.pdf", 1, ResultKind.Document,
            "This agreement is between the parties. The deposit is returned at the end of the term.");
        Put(db, @"C:\Papers\menu.pdf", 2, ResultKind.Document, "Soup, bread and a coffee.");

        SearchResults r = ContentBranch.Search(db, "deposit", 20);

        SearchResult row = Assert.Single(r.Rows);
        Assert.Equal(@"C:\Papers\contract.pdf", row.Path);
        Assert.Equal("contract.pdf", row.Name);
        Assert.Equal(ResultKind.Document, row.Kind);
        Assert.Contains("deposit", row.Excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("contains the words", row.Why);
        Assert.True(r.ContentReady);
    }

    [Fact]
    public void OneRowPerFileEvenWhenSeveralChunksMatch()
    {
        // A 200-page PDF that says "lease" on every page must be one result, not two hundred.
        using ContentDb db = Open();
        Put(db, @"C:\Papers\lease.pdf", 1, ResultKind.Document,
            "the lease begins in March and the lease is annual",
            "the lease may be renewed once, and the lease then continues",
            "termination of the lease requires notice, and the lease ends");

        SearchResults r = ContentBranch.Search(db, "lease", 20);

        Assert.Single(r.Rows);
    }

    [Fact]
    public void TheGrammarStillAppliesToWhatTheWordsFound()
    {
        // "ext:txt" is a filter on the FILE, not on its text. Dropping it here would make the
        // Content pill quietly ignore half the query language the card advertises.
        using ContentDb db = Open();
        Put(db, @"C:\Papers\lease.pdf", 1, ResultKind.Document, "the quarterly lease agreement");
        Put(db, @"C:\Papers\lease.txt", 2, ResultKind.Document, "the quarterly lease agreement");

        SearchResults r = ContentBranch.Search(db, "lease ext:txt", 20);

        Assert.Equal(@"C:\Papers\lease.txt", Assert.Single(r.Rows).Path);
    }

    [Fact]
    public void AnEmptyIndexSaysSoRatherThanLookingLikeNoMatch()
    {
        // "0 results" and "nothing has been indexed yet" are different facts, and only one of
        // them is the user's fault. An empty index must never read as an answer.
        using ContentDb db = Open();

        SearchResults r = ContentBranch.Search(db, "lease", 20);

        Assert.Empty(r.Rows);
        Assert.Contains("nothing indexed yet", r.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARealMissWithANonEmptyIndexCarriesNoNote()
    {
        using ContentDb db = Open();
        Put(db, @"C:\Papers\menu.pdf", 1, ResultKind.Document, "soup, bread and a coffee");

        SearchResults r = ContentBranch.Search(db, "helicopter", 20);

        Assert.Empty(r.Rows);
        Assert.Equal("", r.Note);
    }

    [Fact]
    public void TheExcerptCentresOnTheQueryWordRatherThanStartingAtTheTop()
    {
        string filler = string.Join(" ", Enumerable.Repeat("padding", 60));
        string text = filler + " the deposit is returned " + filler;

        string ex = ContentBranch.Excerpt(text, "deposit");

        Assert.Contains("deposit", ex, StringComparison.Ordinal);
        Assert.StartsWith("…", ex);
        Assert.EndsWith("…", ex);
        Assert.True(ex.Length <= 230, $"an excerpt of {ex.Length} chars will not fit a row");
    }

    [Fact]
    public void AShortChunkIsShownWholeWithNoEllipsis()
    {
        string ex = ContentBranch.Excerpt("the deposit is returned", "deposit");

        Assert.Equal("the deposit is returned", ex);
    }

    [Fact]
    public void AnExcerptForAWordThatIsNotInTheChunkStartsAtTheBeginning()
    {
        // FTS matched on a prefix or on another chunk of the same file; the excerpt must
        // still show something rather than an empty cell.
        string ex = ContentBranch.Excerpt("alpha beta gamma delta", "epsilon");

        Assert.StartsWith("alpha", ex);
        Assert.DoesNotContain("…", ex);
    }
}
