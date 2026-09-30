using Findra;
using Findra.Diagnostics;
using Xunit;

/// <summary>
/// `like:` in the grammar: the query "More like this" writes into the field. A pure parse, like
/// the rest of it - the same string always names the same file.
/// </summary>
public class LikeQueryTests
{
    [Theory]
    [InlineData(@"like:C:\Photos\a.jpg", @"C:\Photos\a.jpg")]
    [InlineData(@"like:""D:\Photos\2025\08 Crete\IMG_4471.HEIC""", @"D:\Photos\2025\08 Crete\IMG_4471.HEIC")]
    [InlineData(@"LIKE:""C:\a b\c.pdf""", @"C:\a b\c.pdf")]
    [InlineData(@"like:""C:\a.pdf"" type:doc", @"C:\a.pdf")]
    [InlineData(@"like:""C:\a.pdf"" like:""C:\b.pdf""", @"C:\b.pdf")]
    public void ALikeTermNamesOneFileWithItsQuotesOff(string raw, string path)
    {
        var q = new SearchQuery(raw);
        Assert.True(q.IsLike);
        Assert.Equal(path, q.Like);
        Assert.Equal(path, SearchQuery.LikeOf(raw));
        // The path is not a word to look for and not a path needle: it names the question.
        Assert.Empty(q.Words);
        Assert.Equal("", q.PathNeedle);
        Assert.Equal("", q.ContentText);
    }

    [Theory]
    [InlineData("like")]
    [InlineData("like:")]
    [InlineData(@"like:""""")]
    [InlineData(@"""like:C:\a.jpg""")]
    [InlineData("sunset")]
    public void AnythingElseIsNotALikeQuery(string raw)
    {
        Assert.False(new SearchQuery(raw).IsLike);
        Assert.Equal("", SearchQuery.LikeOf(raw));
    }

    [Fact]
    public void TheFiltersBesideItStillParse()
    {
        var q = new SearchQuery(@"like:""D:\Photos\a b.jpg"" type:photo in:Crete ext:heic -draft size:>1mb modified:2025");
        Assert.Equal(@"D:\Photos\a b.jpg", q.Like);
        Assert.Contains(ResultKind.Photo, q.Kinds);
        Assert.Contains("Crete", q.Under);
        Assert.Contains("heic", q.Exts);
        Assert.Contains("draft", q.NotWords);
        Assert.True(q.MinBytes > 0);
        Assert.NotNull(q.ModifiedAfter);
    }

    [Fact]
    public void WhatTheButtonWritesReadsBackAsTheSameFile()
    {
        foreach (string path in new[] { @"C:\a.jpg", @"D:\Photos\2025\08 Crete\IMG_4471.HEIC", @"E:\תמונות\טיול\a b.jpg" })
        {
            string written = SearchQuery.LikeQuery(path);
            Assert.StartsWith("like:\"", written, StringComparison.Ordinal);
            Assert.Equal(path, new SearchQuery(written).Like);
            // One term, whatever spaces the path has, so an OR split cannot cut it.
            Assert.Single(SearchQuery.OrParts(written));
        }
    }

    [Fact]
    public void ALikeQueryAloneDoesNotLatchTheAdvancedPill()
    {
        // The pill says a rule from the form is on. "More like this" is a question of its own, and
        // a filter typed beside it is what latches the pill.
        Assert.False(SearchQuery.IsAdvanced(SearchQuery.LikeQuery(@"C:\a.jpg")));
        Assert.True(SearchQuery.IsAdvanced(SearchQuery.LikeQuery(@"C:\a.jpg") + " type:photo"));
    }

    [Theory]
    [InlineData(@"like:C:\My Photos\a.jpg", @"C:\My Photos\a.jpg")]
    [InlineData(@"like:""C:\My Photos\a.jpg""", @"C:\My Photos\a.jpg")]
    [InlineData("sunset", "")]
    public void ATerminalsLikeKeepsAPathWithSpacesWhole(string arg, string path)
    {
        // A shell takes the quotes off q:like:"C:\My Photos\a.jpg" before --searchindex sees it.
        Assert.Equal(path, new SearchQuery(SearchIndex.Query(arg)).Like);
    }
}
