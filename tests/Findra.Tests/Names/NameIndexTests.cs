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
}
