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
        //
        // Counting the rows alone cannot fail this: the branch collects into a dictionary keyed by
        // path, which yields one row per file whether it keeps the first hit, the last, or a coin
        // toss. So the surviving row is checked as well. The middle chunk is the shortest and says
        // "lease" the most often, which is exactly what bm25 ranks first, and the excerpt on the
        // row has to come from THAT chunk - the marker tells the three apart. Keeping the last hit
        // instead (drop the ContainsKey guard and assign anyway) leaves gamma on the row and fails
        // here while the count still says 1.
        using ContentDb db = Open();
        Put(db, @"C:\Papers\lease.pdf", 1, ResultKind.Document,
            "alphamarker: this agreement, dated March, sets out the terms under which the " +
            "premises are occupied, and the lease begins on the first day of the following month",
            "betamarker: lease, lease, lease",
            "gammamarker: termination of this agreement requires ninety days of written notice " +
            "to the other party, at which point the lease ends and the deposit is returned");

        SearchResults r = ContentBranch.Search(db, "lease", 20);

        SearchResult row = Assert.Single(r.Rows);
        Assert.Contains("betamarker", row.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("alphamarker", row.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("gammamarker", row.Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBestMatchingFileComesFirst()
    {
        // bm25 order has to survive the finish-and-order pass. Every content row sits in one score
        // band, and the shared pass breaks a tie by the shorter path - so if the rank were not
        // carried in the score, the deep file that is actually about leases would lose to the
        // shallow one that mentions the word once.
        using ContentDb db = Open();
        Put(db, @"C:\a.pdf", 1, ResultKind.Document,
            "a note about the office move, the new desks, the parking, and one lease");
        Put(db, @"C:\Papers\Contracts\2026\lease.pdf", 2, ResultKind.Document, "lease lease lease");

        SearchResults r = ContentBranch.Search(db, "lease", 20);

        Assert.Equal(@"C:\Papers\Contracts\2026\lease.pdf", r.Rows[0].Path);
    }

    [Fact]
    public void AQueryOfFiltersAloneFindsNothingAndSaysWhy()
    {
        // "ext:pdf" with no words used to reach FTS5 as the raw string, which tokenises to "ext"
        // and "pdf" - so a document that happens to contain those words came back as a match for a
        // query that never asked for a word. There is nothing to look for; the honest answer is
        // no rows and a sentence saying which half of the query is missing.
        using ContentDb db = Open();
        Put(db, @"C:\Papers\manual.pdf", 1, ResultKind.Document,
            "set the ext value in the pdf export dialog before you print");

        SearchResults r = ContentBranch.Search(db, "ext:pdf", 20);

        Assert.Empty(r.Rows);
        Assert.Equal(ContentBranch.NoWords, r.Note);
        Assert.True(r.ContentReady);
    }

    [Fact]
    public void SizeAndDateFiltersApplyToAContentSearchToo()
    {
        // A full-text hit carries no directory entry, so these filters can only be applied by the
        // finish-and-order pass both halves of the pill now share. Without it they sit on the card
        // looking applied and doing nothing.
        using ContentDb db = Open();
        Put(db, @"C:\Papers\big.txt", 1, ResultKind.Document, "the quarterly lease agreement");
        Put(db, @"C:\Papers\small.txt", 2, ResultKind.Document, "the quarterly lease agreement");

        SearchResults r = ContentBranch.Search(db, "lease size:>1mb", 20, SearchSort.Best, Stat);

        Assert.Equal(@"C:\Papers\big.txt", Assert.Single(r.Rows).Path);
    }

    [Fact]
    public void TheSortChipsReorderAContentSearchToo()
    {
        using ContentDb db = Open();
        Put(db, @"C:\Papers\big.txt", 1, ResultKind.Document, "lease lease lease");
        Put(db, @"C:\Papers\small.txt", 2, ResultKind.Document, "the quarterly lease agreement");

        Assert.Equal(@"C:\Papers\big.txt",
                     ContentBranch.Search(db, "lease", 20, SearchSort.Largest, Stat).Rows[0].Path);
        Assert.Equal(@"C:\Papers\small.txt",
                     ContentBranch.Search(db, "lease", 20, SearchSort.Newest, Stat).Rows[0].Path);
    }

    /// <summary>A disk described rather than had: big.txt is large and old, small.txt is small and
    /// new, so one fixture separates the size filter, the Largest chip and the Newest chip.</summary>
    private static ResultMapper.Stat Stat(string path, bool _)
        => path.EndsWith("big.txt", StringComparison.Ordinal)
            ? new ResultMapper.Stat(5_000_000, new DateTime(2020, 1, 1), new DateTime(2020, 1, 1), new DateTime(2020, 1, 1))
            : new ResultMapper.Stat(500, new DateTime(2026, 1, 1), new DateTime(2026, 1, 1), new DateTime(2026, 1, 1));

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
