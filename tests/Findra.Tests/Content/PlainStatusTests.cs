using Findra;
using Xunit;

/// <summary>
/// What the status says in the states that were silent: waiting for the card while documents go
/// on, reading on the processor, a reader that crashed and is coming back, a failure it cannot get
/// past, files left out. Every sentence is for somebody who has never heard of an index.
/// </summary>
public class PlainStatusTests
{
    [Fact]
    public void TheCountMovesForFilesLeftOutAndFilesThatCouldNotBeRead()
    {
        // 1 read, 4,210 left out, 12 failed, 273,845 to go: the pill used to say "1 of 278,067"
        // for as long as the pass was skipping, which read as frozen.
        IndexProgress p = IndexStatus.Pill(true, nameof(ResultKind.Photo), 273_845, 1, alive: true,
                                           extra: new IndexExtra(LeftOut: 4_210, Failed: 12));
        Assert.Equal("4,223 of 278,068", p.Count);
        Assert.True(p.Fraction > 0.015f);
    }

    [Fact]
    public void PhotosWaitingWhileDocumentsGoOnSaysBoth()
    {
        var extra = new IndexExtra(Around: nameof(ResultKind.Photo));
        Assert.Equal("photos waiting · reading documents",
                     IndexStatus.Pill(true, nameof(ResultKind.Document), 10, 5, alive: true, "indexing", extra).Label);
        Assert.Equal("Photos will wait while another app is busy. Documents are still being read.",
                     IndexStatus.Sentence(true, "indexing", 10, 5, alive: true, extra));
        Assert.Equal("Recordings will wait while another app is busy. Documents are still being read.",
                     IndexStatus.Sentence(true, "indexing", 10, 5, alive: true, new IndexExtra(Around: nameof(ResultKind.Audio))));
    }

    [Fact]
    public void ReadingOnTheProcessorSaysItIsSlowerAndWhy()
    {
        var extra = new IndexExtra(Processor: "other programs hold 2.04 GB of the card's 2.9 GB");
        Assert.Equal("reading photos, slowly",
                     IndexStatus.Pill(true, nameof(ResultKind.Photo), 10, 5, alive: true, "indexing", extra).Label);
        Assert.Equal("Reading more slowly, because the graphics card is nearly full.",
                     IndexStatus.Sentence(true, "indexing", 10, 5, alive: true, extra));
    }

    [Fact]
    public void AReaderThatCrashedSaysWhenItWillTryAgainInsteadOfSayingFindraIsClosed()
    {
        var extra = new IndexExtra(RestartIn: 110);
        string line = IndexStatus.Line(true, "", 10, 5, alive: false, rebuilt: false, extra);
        Assert.Equal("something went wrong - Findra will try again in 2 minutes", line);
        Assert.DoesNotContain("closed", line, StringComparison.Ordinal);
        Assert.Equal("Something went wrong. Findra will try again in 2 minutes.",
                     IndexStatus.Sentence(true, "", 10, 5, alive: false, extra));
        // And the bar stays up: there is still work in hand.
        IndexProgress p = IndexStatus.Pill(true, "", 10, 5, alive: false, "", extra);
        Assert.True(p.Show);
        Assert.Equal("a problem · trying again soon", p.Label);
    }

    [Fact]
    public void AStuckIndexerIsSaidOnEverySurface()
    {
        Assert.Equal("a problem · see Settings", IndexStatus.Pill(true, "", 10, 5, alive: true, IndexStatus.Stuck).Label);
        Assert.Equal("Findra can't save what it reads - your disk may be full",
                     IndexStatus.Line(true, IndexStatus.Stuck, 10, 5, alive: true, rebuilt: false));
        Assert.Equal("Findra can't save what it reads. Your disk may be full.",
                     IndexStatus.Sentence(true, IndexStatus.Stuck, 10, 5, alive: true));
    }

    [Fact]
    public void TheSettingsSentenceCoversEveryState()
    {
        Assert.Equal("", IndexStatus.Sentence(false, "", 10, 5, alive: true));
        Assert.Equal("Waiting until you finish setting up Findra.",
                     IndexStatus.Sentence(true, "", 10, 5, alive: false, new IndexExtra(Held: true)));
        Assert.Equal("All done. Your files are ready to search.", IndexStatus.Sentence(true, "idle", 0, 5, alive: true));
        Assert.Equal("Looking for files to read.", IndexStatus.Sentence(true, "", 0, 0, alive: true));
        Assert.Equal("Getting ready to read your files.", IndexStatus.Sentence(true, "", 10, 5, alive: false));
        Assert.Equal("Paused while you're in a full-screen app.",
                     IndexStatus.Sentence(true, IndexGate.Fullscreen, 10, 5, alive: true));
        Assert.Equal("Paused while another app is busy. Findra will carry on by itself.",
                     IndexStatus.Sentence(true, IndexGate.GpuBusy, 10, 5, alive: true));
        Assert.Equal("1,204 files read · 4,210 left out.",
                     IndexStatus.Sentence(true, "indexing", 10, 1_204, alive: true, new IndexExtra(LeftOut: 4_210)));
        Assert.Equal("1,204 files read · 4,210 left out · 12 could not be read.",
                     IndexStatus.Sentence(true, "indexing", 10, 1_204, alive: true, new IndexExtra(LeftOut: 4_210, Failed: 12)));
        Assert.Equal("1,204 files read.", IndexStatus.Sentence(true, "indexing", 10, 1_204, alive: true));
    }

    [Fact]
    public void AWhileIsNeverANumberOfSeconds()
    {
        Assert.Equal("in a moment", IndexStatus.InAWhile(5));
        Assert.Equal("in a minute", IndexStatus.InAWhile(80));
        Assert.Equal("in 2 minutes", IndexStatus.InAWhile(110));
        Assert.Equal("in 5 minutes", IndexStatus.InAWhile(300));
    }

    [Fact]
    public void NoSentenceUsesADashThatIsNotAHyphen()
    {
        var extras = new[]
        {
            default, new IndexExtra(Around: "Photo"), new IndexExtra(Processor: "x"), new IndexExtra(RestartIn: 200),
            new IndexExtra(Held: true), new IndexExtra(LeftOut: 3, Failed: 2),
        };
        foreach (IndexExtra e in extras)
            foreach (string state in new[] { "", "indexing", "paused", IndexGate.GpuBusy, IndexGate.Fullscreen, IndexStatus.Stuck })
                foreach (bool alive in new[] { true, false })
                {
                    string all = IndexStatus.Sentence(true, state, 10, 5, alive, e)
                                 + IndexStatus.Line(true, state, 10, 5, alive, false, e)
                                 + IndexStatus.Pill(true, "Photo", 10, 5, alive, state, e).Label;
                    Assert.DoesNotContain('—', all);
                    Assert.DoesNotContain('–', all);
                }
    }

    [Fact]
    public void TheDotSaysAtAGlanceWhetherItIsMovingWaitingOrStuck()
    {
        Assert.Equal(StatusTone.None, IndexStatus.Tone(false, "", 10, 5, alive: true));
        Assert.Equal(StatusTone.Reading, IndexStatus.Tone(true, "indexing", 10, 5, alive: true));
        Assert.Equal(StatusTone.Reading, IndexStatus.Tone(true, "indexing", 10, 5, alive: true, new IndexExtra(Processor: "x")));
        Assert.Equal(StatusTone.Waiting, IndexStatus.Tone(true, "indexing", 10, 5, alive: true, new IndexExtra(Around: "Photo")));
        Assert.Equal(StatusTone.Waiting, IndexStatus.Tone(true, IndexGate.GpuBusy, 10, 5, alive: true));
        Assert.Equal(StatusTone.Waiting, IndexStatus.Tone(true, IndexGate.Fullscreen, 10, 5, alive: true));
        Assert.Equal(StatusTone.Waiting, IndexStatus.Tone(true, "", 10, 5, alive: false, new IndexExtra(Held: true)));
        Assert.Equal(StatusTone.Waiting, IndexStatus.Tone(true, "", 10, 5, alive: false));
        Assert.Equal(StatusTone.Problem, IndexStatus.Tone(true, IndexStatus.Stuck, 10, 5, alive: true));
        Assert.Equal(StatusTone.Problem, IndexStatus.Tone(true, "", 10, 5, alive: false, new IndexExtra(RestartIn: 60)));
        Assert.Equal(StatusTone.Done, IndexStatus.Tone(true, "idle", 0, 5, alive: true));
        Assert.Equal(StatusTone.Waiting, IndexStatus.Tone(true, "", 0, 0, alive: true));
    }
}
