using Findra;
using Xunit;

public class NameIndexTests
{
    private static NameIndex Sample()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5,   0, NtfsVolume.FileAttributeDirectory, "C:");
        ix.Upsert(100, 5, NtfsVolume.FileAttributeDirectory, "Photos");
        ix.Upsert(101, 100, 0, "sunset over water.jpg");
        ix.Upsert(102, 100, 0, "SUNSET-final.png");
        ix.Upsert(103, 100, 0, "invoice.pdf");
        ix.Upsert(104, 100, 0, "הסכם-שכירות.docx");
        return ix;
    }

    [Fact]
    public void ResidentBytesCountsEveryArrayAndNotOnlyTheNames()
    {
        // The name buffers were once published as the index's memory, and they are about a third
        // of it: every record also carries its FRN, parent, attributes, offset, two lengths and
        // its depth (29 bytes), a segment-table entry (8) and a slot in the FRN map (12, at most
        // 60% full).
        var ix = new NameIndex('C');
        for (int i = 0; i < 200_000; i++)
            ix.Upsert((ulong)(i + 100), 5, 0, $"file number {i}.txt");
        ix.Trim();

        long floor = ix.BufferBytes + (long)ix.Count * (29 + 8 + 12);
        Assert.InRange(ix.ResidentBytes, floor, floor * 2);
    }

    [Fact]
    public void FindsBySubstringCaseInsensitively()
    {
        var hits = new List<NameIndex.Hit>();
        Sample().Search(new SearchQuery("sunset"), hits);
        Assert.Equal(2, hits.Count);
    }

    // NameIndex.Search only checks q.Exts in the regex branch and the "no name terms at all"
    // branch (see the two ExtMatches call sites in NameIndex.cs) - not in the vectorised
    // word-scan branch this query takes, so "ext:" alongside a plain word does not narrow the
    // candidates here. SearchQuery.Allows applies the extension filter for real once the caller
    // has a path; that is a separate, later stage this test does not reach.
    [Fact]
    public void DoesNotFilterByExtensionWhenAWordTermIsPresent()
    {
        var ix = Sample();
        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("sunset ext:png"), hits);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void FindsNonAsciiNames()
    {
        var ix = Sample();
        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("שכירות"), hits);
        Assert.Single(hits);
        Assert.Equal("הסכם-שכירות.docx", ix.Name(hits[0].Record));
    }

    [Fact]
    public void BuildsAFullPathFromTheParentChain()
    {
        var ix = Sample();
        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("invoice"), hits);
        Assert.Equal(@"C:\Photos\invoice.pdf", ix.PathOf(hits[0].Record));
    }

    [Fact]
    public void RemoveTakesARecordOutOfResults()
    {
        var ix = Sample();
        Assert.True(ix.Remove(103));
        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("invoice"), hits);
        Assert.Empty(hits);
    }

    [Fact]
    public void RespectsTheMaxArgument()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        for (ulong i = 0; i < 50; i++) ix.Upsert(100 + i, 5, 0, $"report{i}.txt");

        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("report"), hits, max: 10);
        Assert.Equal(10, hits.Count);
    }

    [Fact]
    public void KeepsTheBestMatchesRatherThanTheFirstOnesItMeets()
    {
        // Names sit in the index in the order the disk listed them. A scan that stops at `max`
        // hits ranks whichever came first, and the exact name listed last never reaches it.
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        for (ulong i = 0; i < 50; i++) ix.Upsert(100 + i, 5, 0, $"old sunset {i}.jpg");
        ix.Upsert(999, 5, 0, "sunset");

        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("sunset"), hits, max: 10);

        Assert.Equal(10, hits.Count);
        Assert.Equal("sunset", ix.Name(hits[0].Record));
    }

    // C:\a\b\notes.txt, its depth asked once so the index has remembered it
    private static (NameIndex Ix, int Notes) Nested()
    {
        const uint dir = NtfsVolume.FileAttributeDirectory;
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, dir, "C:");
        ix.Upsert(10, 5, dir, "a");
        ix.Upsert(11, 10, dir, "b");
        ix.Upsert(12, 11, 0, "notes.txt");
        Assert.True(ix.TryIndexOf(12, out int notes));
        Assert.Equal(3, ix.Depth(notes));
        return (ix, notes);
    }

    [Fact]
    public void DepthFollowsAFolderThatMoves()
    {
        var (ix, notes) = Nested();
        ix.Upsert(11, 5, NtfsVolume.FileAttributeDirectory, "b");   // b moves up to the root
        Assert.Equal(2, ix.Depth(notes));
    }

    [Fact]
    public void DepthFollowsAFileThatMoves()
    {
        var (ix, notes) = Nested();
        ix.Upsert(12, 5, 0, "notes.txt");
        Assert.Equal(1, ix.Depth(notes));
    }

    [Fact]
    public void UnderADeletedFolderThereIsNoDepthJustAsThereIsNoPath()
    {
        var (ix, notes) = Nested();
        ix.Remove(11);
        Assert.Null(ix.PathOf(notes));
        Assert.Equal(-1, ix.Depth(notes));
    }

    // Every branch of the scan: plain words, a wildcard, a pattern, and filters alone.
    [Theory]
    [InlineData(".claude")]
    [InlineData(".claude*")]
    [InlineData(@"regex:^\.claude$")]
    [InlineData("ext:claude")]
    public void AmongEquallyGoodNamesTheShallowestWin(string query)
    {
        // Every project has a .claude folder four levels down; the one in the profile is three
        // down and was listed last. Searching ".claude" means that one.
        const uint dir = NtfsVolume.FileAttributeDirectory;
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, dir, "C:");
        ix.Upsert(10, 5, dir, "Code");
        ix.Upsert(11, 10, dir, "Work");
        for (ulong i = 0; i < 20; i++)
        {
            ix.Upsert(100 + i, 11, dir, $"project{i}");
            ix.Upsert(200 + i, 100 + i, dir, ".claude");
        }
        ix.Upsert(20, 5, dir, "Users");
        ix.Upsert(21, 20, dir, "me");
        ix.Upsert(22, 21, dir, ".claude");

        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery(query), hits, max: 5);

        Assert.Equal(5, hits.Count);
        Assert.Equal(@"C:\Users\me\.claude", ix.PathOf(hits[0].Record));
    }
}
