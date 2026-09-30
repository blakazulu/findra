using Findra;
using Xunit;

/// <summary>
/// What was opened from the card: kept on this machine, capped, used to order equally good
/// results and to list recent files on an empty field, and forgotten on request.
/// </summary>
public class OpenedHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-opened-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "opened.json");
    private DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public OpenedHistoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } GC.SuppressFinalize(this); }

    private OpenedHistory History() => new(File_, () => _now);

    [Fact]
    public void WhatIsOpenedIsRememberedAcrossRestarts()
    {
        OpenedHistory h = History();
        h.Note(@"C:\Docs\lease.pdf");
        h.Note(@"C:\Docs\lease.pdf");

        OpenedHistory again = History();
        OpenedHistory.Entry e = Assert.Single(again.Recent(10));
        Assert.Equal(@"C:\Docs\lease.pdf", e.Path);
        Assert.Equal(2, e.Count);
    }

    [Fact]
    public void RecentIsNewestFirstAndPathsMatchWhateverTheirCase()
    {
        OpenedHistory h = History();
        h.Note(@"C:\a.txt");
        _now = _now.AddMinutes(1);
        h.Note(@"C:\b.txt");
        _now = _now.AddMinutes(1);
        h.Note(@"c:\A.TXT");

        Assert.Equal([@"c:\A.TXT", @"C:\b.txt"], h.Recent(10).Select(e => e.Path));
        Assert.Equal(2, h.Recent(10)[0].Count);
    }

    [Fact]
    public void OnlyTheMostRecentFewHundredAreKept()
    {
        OpenedHistory h = new(null, () => _now);
        for (int i = 0; i < OpenedHistory.Keep + 25; i++)
        {
            _now = _now.AddSeconds(1);
            h.Note($@"C:\f{i}.txt");
        }
        Assert.Equal(OpenedHistory.Keep, h.Count);
        Assert.Equal($@"C:\f{OpenedHistory.Keep + 24}.txt", h.Recent(1)[0].Path);
        Assert.Equal(0f, h.Lift(@"C:\f0.txt"));
    }

    [Fact]
    public void TheLiftGrowsWithUseAndFadesWithTimeAndNeverPassesItsCap()
    {
        OpenedHistory h = History();
        Assert.Equal(0f, h.Lift(@"C:\never.txt"));

        h.Note(@"C:\once.txt");
        float once = h.Lift(@"C:\once.txt");
        for (int i = 0; i < 20; i++) h.Note(@"C:\often.txt");
        float often = h.Lift(@"C:\often.txt");
        Assert.True(often > once);
        Assert.Equal(OpenedHistory.MaxLift, often, 5);

        _now = _now.AddDays(40);
        Assert.True(h.Lift(@"C:\often.txt") < often);
        Assert.True(h.Lift(@"C:\often.txt") > 0);
    }

    [Fact]
    public void ForgettingEmptiesTheListAndDeletesTheFile()
    {
        OpenedHistory h = History();
        h.Note(@"C:\a.txt");
        Assert.True(File.Exists(File_));
        h.Forget();
        Assert.Equal(0, h.Count);
        Assert.False(File.Exists(File_));
        Assert.Equal(0, History().Count);
    }

    [Fact]
    public void ADamagedFileStartsAgainRatherThanFailing()
    {
        File.WriteAllText(File_, "{ not json");
        Assert.Equal(0, History().Count);
    }

    [Theory]
    [InlineData(0.2, "opened today")]
    [InlineData(1.5, "opened yesterday")]
    [InlineData(6, "opened 6 days ago")]
    public void WhenSaysItInWords(double daysAgo, string expected) =>
        Assert.Equal(expected, OpenedHistory.When(_now.AddDays(-daysAgo), _now));

    // ---- ordering ----------------------------------------------------------------------------

    private static SearchResult Row(string path, float score) =>
        new(ResultKind.Document, Path.GetFileName(path), path, score, "in the name");

    private static ResultMapper.Stat Here(string p, bool d) => new(1, new DateTime(2026, 1, 1), default, default);

    [Fact]
    public void AnOpenedFileGoesAheadOfAnEquallyGoodOneButNotAheadOfAMuchBetterOne()
    {
        OpenedHistory h = History();
        for (int i = 0; i < 8; i++) h.Note(@"C:\deep\er\report.pdf");

        List<SearchResult> rows =
        [
            Row(@"C:\report.pdf", 0.70f),
            Row(@"C:\deep\er\report.pdf", 0.70f),
            Row(@"C:\exact\report-final.pdf", 0.95f),
        ];
        List<SearchResult> ordered = ResultMapper.Finish(rows, new SearchQuery("report"), SearchSort.Best, Here, lift: h.Lift);

        Assert.Equal(@"C:\exact\report-final.pdf", ordered[0].Path);
        Assert.Equal(@"C:\deep\er\report.pdf", ordered[1].Path);
        // The score the card shows is the match's, not the lift's.
        Assert.Equal(0.70f, ordered[1].Score);
    }

    [Fact]
    public void WithoutAHistoryOrUnderAnotherSortTheOrderIsUnchanged()
    {
        OpenedHistory h = History();
        h.Note(@"C:\deep\er\report.pdf");
        List<SearchResult> rows = [Row(@"C:\deep\er\report.pdf", 0.70f), Row(@"C:\report.pdf", 0.70f)];

        Assert.Equal(@"C:\report.pdf", ResultMapper.Finish(rows, new SearchQuery("report"), SearchSort.Best, Here)[0].Path);
        Assert.Equal(@"C:\report.pdf", ResultMapper.Finish(rows, new SearchQuery("report"), SearchSort.Largest, Here, lift: h.Lift)[0].Path);
    }

    // ---- the card ----------------------------------------------------------------------------

    [Fact]
    public void AnEmptyFieldWithRecentRowsHasABodyAndSaysWhatTheRowsAre()
    {
        var recent = new SearchResults("", [Row(@"C:\a.pdf", 0f)], 0, 0, false);
        SearchCardState s = SearchCardState.Empty with { Results = recent, Rows = recent.Rows, Recent = true };
        Assert.False(s.HasQuery);
        Assert.True(s.ShowsBody);
        Assert.Equal((SearchCardPainter.RecentLabel, ""), SearchCardPainter.Header(s, 1));

        Assert.False(SearchCardState.Empty.ShowsBody);
        Assert.False((SearchCardState.Empty with { Recent = true }).ShowsBody);
    }

    [Fact]
    public void TheSettingIsOnByDefaultAndRoundTrips()
    {
        Assert.True(Config.Default.RememberOpened);
        Config off = Config.Default with { RememberOpened = false };
        Assert.False(Config.Load(off.ToJson()).RememberOpened);
        Assert.NotEqual(Config.Default, off);
    }
}
