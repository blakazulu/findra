using System;
using System.Collections.Generic;
using Findra;
using Findra.Pipe;
using Xunit;

// The mapper is the whole of Task 5 that is pure: a NameRow off the wire in, a SearchResult the
// card can paint out. The async plumbing around it (the lazy connect, the per-search cancellation,
// the dispatch back onto the UI thread) lives inside a Window and cannot be exercised without a
// display, so it is verified by --searchprobe against a real helper instead.
public class ResultMapperTests
{
    private const uint Dir = 0x10;
    private const uint Arch = 0x20;

    private static NameRow Row(string name, string path = "", uint attributes = Arch,
                               float score = 0.9f, int match = 1)
        => new('C', 1, name, path.Length > 0 ? path : @"C:\x\" + name, attributes, score, match);

    private static ResultMapper.Stat Found(long size, DateTime modified)
        => new(size, modified, modified, modified);

    // ---- kinds ----

    [Fact]
    public void TheDirectoryAttributeMakesAFolder()
    {
        SearchResult r = ResultMapper.Map(Row("Sunsets", attributes: Dir), ResultMapper.Stat.Missing);
        Assert.Equal(ResultKind.Folder, r.Kind);
    }

    [Fact]
    public void ADirectoryIsAFolderEvenWhenItsNameLooksLikeAFile()
    {
        // "My.jpg" has an extension as far as a string is concerned; the attribute decides
        SearchResult r = ResultMapper.Map(Row("My.jpg", attributes: Dir | Arch), ResultMapper.Stat.Missing);
        Assert.Equal(ResultKind.Folder, r.Kind);
    }

    [Theory]
    [InlineData("IMG_4471.HEIC", ResultKind.Photo)]
    [InlineData("GX010233.MP4", ResultKind.Video)]
    [InlineData("Voice 014.m4a", ResultKind.Audio)]
    [InlineData("Q3-revenue-review.pdf", ResultKind.Document)]
    [InlineData("sunset-preset.lrtemplate", ResultKind.File)]
    [InlineData("LICENSE", ResultKind.File)]
    public void TheExtensionDecidesTheKindWhenTheAttributeIsNotADirectory(string name, ResultKind kind)
    {
        Assert.Equal(kind, ResultMapper.Map(Row(name), ResultMapper.Stat.Missing).Kind);
    }

    // ---- why ----

    [Theory]
    [InlineData(0, "exact name")]
    [InlineData(1, "name starts with it")]
    [InlineData(2, "a word in the name")]
    [InlineData(3, "in the name")]
    [InlineData(4, "close to the name")]
    [InlineData(5, "matches the pattern")]
    [InlineData(6, "matches the filters")]
    public void EveryMatchClassHasItsOwnWords(int match, string why)
    {
        Assert.Equal(why, ResultMapper.Why(match));
        Assert.Equal(why, ResultMapper.Map(Row("a.txt", match: match), ResultMapper.Stat.Missing).Why);
    }

    [Fact]
    public void AMatchClassNobodyKnowsStillReadsAsAName()
    {
        // a helper one version ahead may send a class this build has never heard of; the card
        // must still have something to put on its "match" line
        Assert.Equal("in the name", ResultMapper.Why(99));
        Assert.False(string.IsNullOrWhiteSpace(ResultMapper.Why(-1)));
    }

    // ---- the rest of the row ----

    [Fact]
    public void NamePathAndScoreCrossUnchanged()
    {
        SearchResult r = ResultMapper.Map(Row("sunset.txt", @"D:\Notes\sunset.txt", score: 0.73f),
                                          ResultMapper.Stat.Missing);
        Assert.Equal("sunset.txt", r.Name);
        Assert.Equal(@"D:\Notes\sunset.txt", r.Path);
        Assert.Equal(0.73f, r.Score, 4);
    }

    [Fact]
    public void AStatThatWasNotTakenLeavesSizeAndModifiedAtTheirDefaults()
    {
        SearchResult r = ResultMapper.Map(Row("a.txt"), ResultMapper.Stat.Missing);
        Assert.Equal(-1, r.Size);
        Assert.Equal(default, r.Modified);
    }

    [Fact]
    public void AStatThatWasTakenReachesTheCard()
    {
        var when = new DateTime(2026, 3, 4, 5, 6, 7);
        SearchResult r = ResultMapper.Map(Row("a.txt"), Found(4096, when));
        Assert.Equal(4096, r.Size);
        Assert.Equal(when, r.Modified);
    }

    // ---- the stat filters ----

    [Fact]
    public void SizeAndDateFiltersAreAppliedHereBecauseTheHelperHasNoStats()
    {
        var rows = new List<NameRow> { Row("small.txt"), Row("big.txt") };
        SearchResults r = ResultMapper.Build("report size:>1mb", rows, new SearchQuery("report size:>1mb"),
            SearchSort.Best, 1.0,
            (path, _) => path.EndsWith("big.txt", StringComparison.Ordinal)
                ? Found(5_000_000, new DateTime(2026, 1, 1))
                : Found(500, new DateTime(2026, 1, 1)));
        Assert.Single(r.Rows);
        Assert.Equal("big.txt", r.Rows[0].Name);
    }

    [Fact]
    public void ARowWhoseStatCouldNotBeTakenCannotSatisfyAStatFilter()
    {
        // the file was deleted between the helper's answer and the stat: it cannot be shown to
        // pass "bigger than a megabyte", so it does not survive the filter
        var rows = new List<NameRow> { Row("gone.txt") };
        SearchResults r = ResultMapper.Build("gone size:>1mb", rows, new SearchQuery("gone size:>1mb"),
            SearchSort.Best, 1.0, (_, _) => ResultMapper.Stat.Missing);
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void AQueryWithNoStatFilterKeepsRowsThatCouldNotBeStatted()
    {
        var rows = new List<NameRow> { Row("gone.txt") };
        SearchResults r = ResultMapper.Build("gone", rows, new SearchQuery("gone"), SearchSort.Best, 1.0,
            (_, _) => ResultMapper.Stat.Missing);
        Assert.Single(r.Rows);
    }

    [Fact]
    public void TheStatIsTakenOncePerRow()
    {
        int calls = 0;
        var rows = new List<NameRow> { Row("a.txt"), Row("b.txt"), Row("c.txt") };
        ResultMapper.Build("x", rows, new SearchQuery("x"), SearchSort.Best, 1.0,
            (_, _) => { calls++; return ResultMapper.Stat.Missing; });
        Assert.Equal(3, calls);
    }

    [Fact]
    public void TheDirectoryFlagReachesTheStat()
    {
        bool? sawDirectory = null;
        ResultMapper.Build("x", new List<NameRow> { Row("Sunsets", attributes: Dir) },
            new SearchQuery("x"), SearchSort.Best, 1.0,
            (_, dir) => { sawDirectory = dir; return ResultMapper.Stat.Missing; });
        Assert.True(sawDirectory);
    }

    // ---- order ----

    [Fact]
    public void BestPutsTheHighestScoreFirst()
    {
        var rows = new List<NameRow> { Row("b.txt", score: 0.4f), Row("a.txt", score: 0.8f) };
        SearchResults r = ResultMapper.Build("x", rows, new SearchQuery("x"), SearchSort.Best, 1.0,
            (_, _) => ResultMapper.Stat.Missing);
        Assert.Equal(new[] { "a.txt", "b.txt" }, r.Rows.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void NewestAndLargestReorderTheSameRows()
    {
        var rows = new List<NameRow> { Row("old-big.txt", score: 0.9f), Row("new-small.txt", score: 0.1f) };
        ResultMapper.Stat StatOf(string path, bool _) => path.Contains("old-big", StringComparison.Ordinal)
            ? Found(9_000_000, new DateTime(2020, 1, 1))
            : Found(10, new DateTime(2026, 1, 1));

        SearchResults newest = ResultMapper.Build("x", rows, new SearchQuery("x"), SearchSort.Newest, 1.0, StatOf);
        Assert.Equal("new-small.txt", newest.Rows[0].Name);

        SearchResults largest = ResultMapper.Build("x", rows, new SearchQuery("x"), SearchSort.Largest, 1.0, StatOf);
        Assert.Equal("old-big.txt", largest.Rows[0].Name);
    }

    [Fact]
    public void TheQueryAndTheTimingRideAlongOnTheAnswer()
    {
        SearchResults r = ResultMapper.Build("sunset", new List<NameRow> { Row("a.txt") },
            new SearchQuery("sunset"), SearchSort.Best, 12.5, (_, _) => ResultMapper.Stat.Missing);
        Assert.Equal("sunset", r.Query);
        Assert.Equal(12.5, r.NamesMs, 3);
    }

    [Fact]
    public void AnEmptyAnswerIsAnEmptyResultSetNotANull()
    {
        SearchResults r = ResultMapper.Build("sunset", Array.Empty<NameRow>(), new SearchQuery("sunset"),
            SearchSort.Best, 0.4, (_, _) => ResultMapper.Stat.Missing);
        Assert.Empty(r.Rows);
        Assert.Equal("sunset", r.Query);
    }

    // ---- the total order ----
    //
    // List.Sort is unstable, so a comparer that returns zero for two rows leaves their order to
    // whatever the sort happened to do with them - and the same query would then shuffle its own
    // results between runs, which reads as the card flickering. The tiebreaks are the only thing
    // stopping that, and until these tests existed, replacing both of them with `return 0` left
    // every test in this file green.

    private static string[] NamesOf(IEnumerable<NameRow> rows, SearchSort sort = SearchSort.Best)
        => ResultMapper.Build("x", rows.ToList(), new SearchQuery("x"), sort, 1.0,
                              (_, _) => ResultMapper.Stat.Missing)
                       .Rows.Select(x => x.Path).ToArray();

    [Fact]
    public void RowsTiedOnScoreAreOrderedByTheShorterPath()
    {
        // A hit near the top of the disk explains itself; it goes first.
        var deep = Row("a.txt", @"C:\projects\archive\2019\notes\a.txt");
        var shallow = Row("a.txt", @"C:\a.txt");

        Assert.Equal(new[] { @"C:\a.txt", @"C:\projects\archive\2019\notes\a.txt" },
                     NamesOf(new[] { deep, shallow }));
    }

    [Fact]
    public void RowsTiedOnScoreAndPathLengthAreOrderedByThePathItself()
    {
        // Same score, same kind, same name, same path length: ordinal on the path is the last
        // tiebreak, and it is what makes the order a total one rather than an arbitrary one.
        var b = Row("a.txt", @"C:\bbb\a.txt");
        var a = Row("a.txt", @"C:\aaa\a.txt");

        Assert.Equal(new[] { @"C:\aaa\a.txt", @"C:\bbb\a.txt" }, NamesOf(new[] { b, a }));
        Assert.Equal(new[] { @"C:\aaa\a.txt", @"C:\bbb\a.txt" }, NamesOf(new[] { a, b }));
    }

    [Fact]
    public void TheSameRowsInAnyStartingOrderComeOutTheSameWay()
    {
        // The mutation this catches: with the tiebreaks gone, these permutations come back in
        // different orders and the card appears to shuffle itself between identical queries.
        var rows = new List<NameRow>
        {
            Row("a.txt", @"C:\ddd\a.txt"), Row("a.txt", @"C:\aaa\a.txt"),
            Row("a.txt", @"C:\ccc\a.txt"), Row("a.txt", @"C:\bbb\a.txt"),
            Row("a.txt", @"C:\eee\deeper\a.txt"), Row("a.txt", @"C:\f\a.txt"),
        };

        string[] expected = NamesOf(rows);
        var reversed = new List<NameRow>(rows);
        reversed.Reverse();
        var rotated = new List<NameRow>(rows.Skip(3));
        rotated.AddRange(rows.Take(3));

        Assert.Equal(expected, NamesOf(reversed));
        Assert.Equal(expected, NamesOf(rotated));
        Assert.Equal(expected, NamesOf(rows.OrderBy(x => x.Path, StringComparer.Ordinal)));
    }

    [Fact]
    public void NewestAndLargestFallBackToTheSameTotalOrderWhenTheirKeyTies()
    {
        // Every row here has the same missing stat, so Newest and Largest tie on their own key
        // for all of them and land entirely on the tiebreak. Both must still be deterministic.
        var rows = new List<NameRow>
        {
            Row("a.txt", @"C:\ccc\a.txt"), Row("a.txt", @"C:\aaa\a.txt"), Row("a.txt", @"C:\bbb\a.txt"),
        };
        string[] expected = { @"C:\aaa\a.txt", @"C:\bbb\a.txt", @"C:\ccc\a.txt" };

        Assert.Equal(expected, NamesOf(rows, SearchSort.Newest));
        Assert.Equal(expected, NamesOf(rows, SearchSort.Largest));

        var reversed = new List<NameRow>(rows);
        reversed.Reverse();
        Assert.Equal(expected, NamesOf(reversed, SearchSort.Newest));
        Assert.Equal(expected, NamesOf(reversed, SearchSort.Largest));
    }

    // ---- the score clamp ----

    [Theory]
    [InlineData(2.5f, 1f)]
    [InlineData(1.0001f, 1f)]
    [InlineData(-0.5f, 0f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(float.NegativeInfinity, 0f)]
    public void AScoreFromOffProcessIsClampedIntoZeroToOne(float given, float expected)
    {
        // The score arrives over a pipe. The card paints it as a bar and the comparer sorts on
        // it, so a value outside 0..1 - from a helper one version off, or a corrupted frame -
        // must not reach either.
        Assert.Equal(expected, ResultMapper.Map(Row("a.txt", score: given), ResultMapper.Stat.Missing).Score);
    }

    [Fact]
    public void AScoreAlreadyInRangeIsLeftExactlyAsItArrived()
    {
        Assert.Equal(0f, ResultMapper.Map(Row("a.txt", score: 0f), ResultMapper.Stat.Missing).Score);
        Assert.Equal(1f, ResultMapper.Map(Row("a.txt", score: 1f), ResultMapper.Stat.Missing).Score);
        Assert.Equal(0.42f, ResultMapper.Map(Row("a.txt", score: 0.42f), ResultMapper.Stat.Missing).Score, 5);
    }
}
