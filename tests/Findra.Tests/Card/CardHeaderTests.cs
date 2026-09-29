using Findra;
using Xunit;

/// <summary>
/// The line above the rows: on the left, what the rows ARE (how many, and which query they
/// answer); on the right, what is happening (timing, a note, or that a newer search is running).
/// </summary>
public class CardHeaderTests
{
    private static SearchCardState Answered(string asked, string typed, bool searching, int rows = 3,
                                            string note = "", bool loading = false)
    {
        var list = Enumerable.Range(0, rows)
            .Select(i => new SearchResult(ResultKind.Document, $"f{i}.txt", $@"C:\a\f{i}.txt", 0.9f, "contains the words"))
            .ToList();
        var r = new SearchResults(asked, list, 2.4, 0, false, note, ModelsLoading: loading);
        return SearchCardState.Empty with { Query = typed, Results = r, Rows = list, Searching = searching, Content = true };
    }

    [Fact]
    public void WhileANewerSearchRunsTheRowsKeepTheQueryTheyAnswer()
    {
        // The rows on screen belong to the last answer. Labelling them with the text just typed
        // made a search still running look finished, and a wrong answer look like the answer.
        (string left, string right) = SearchCardPainter.Header(Answered("sunset over w", "sunset over water", searching: true), 3);

        Assert.Equal("3 results for \u201csunset over w\u201d", left);
        Assert.Equal(SearchCardPainter.SearchingLabel, right);
    }

    [Fact]
    public void WithNothingOnScreenTheSearchIsSaidOnTheLeft()
    {
        (string left, _) = SearchCardPainter.Header(Answered("", "sun", searching: true, rows: 0), 0);

        Assert.Equal(SearchCardPainter.SearchingLabel, left);
    }

    [Fact]
    public void ASettledAnswerShowsItsTimingOnTheRight()
    {
        (string left, string right) = SearchCardPainter.Header(Answered("sunset", "sunset", searching: false), 3);

        Assert.Equal("3 results for \u201csunset\u201d", left);
        Assert.Equal("names 2.4 ms", right);
    }

    [Fact]
    public void ANoteTakesTheRightSlotOnceNothingIsRunning()
    {
        string note = ContentBranch.StillLoading(words: true, pictures: true);
        SearchCardState s = Answered("a dog on a beach", "a dog on a beach", searching: false, note: note, loading: true);

        Assert.Equal(note, SearchCardPainter.Header(s, 3).Right);
        Assert.Equal(SearchCardPainter.SearchingLabel, SearchCardPainter.Header(s with { Searching = true }, 3).Right);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EveryLoadingNoteLeavesRoomForAnOrdinaryCount(bool words, bool pictures)
    {
        // Two halves of one line, drawn from either end. A note long enough to reach the count
        // would be drawn over it.
        string left = "12 results for \u201ca dog on a beach\u201d";
        string right = ContentBranch.StillLoading(words, pictures);
        float leftEnd = SearchCardLayout.Pad + 4 + CardText.Measure(left, Parts.Face, SearchCardPainter.CountSize);
        float rightStart = SearchCardLayout.HeaderRight - CardText.Measure(right, Parts.Face, SearchCardPainter.NoteSize);

        Assert.True(leftEnd + 12 <= rightStart, $"'{right}' starts at {rightStart:0.0}, the count ends at {leftEnd:0.0}");
    }

    [Fact]
    public void AnAnswerGivenWhileTheModelsLoadedIsAskedAgainOnceTheyHave()
    {
        SearchCardState s = Answered("a dog on a beach", "a dog on a beach", searching: false, loading: true);

        Assert.False(SearchCardState.AskAgain(s, stillLoading: true));
        Assert.True(SearchCardState.AskAgain(s, stillLoading: false));
        // Not over a search already running, not for text somebody has since changed, and not for
        // an answer that was never short of anything.
        Assert.False(SearchCardState.AskAgain(s with { Searching = true }, stillLoading: false));
        Assert.False(SearchCardState.AskAgain(s with { Query = "a dog on a boat" }, stillLoading: false));
        Assert.False(SearchCardState.AskAgain(Answered("sunset", "sunset", searching: false), stillLoading: false));
    }
}
