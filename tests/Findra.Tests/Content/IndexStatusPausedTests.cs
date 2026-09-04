using Findra;
using Xunit;

/// <summary>
/// Which of the two pauses the status line names, and in what order it decides.
///
/// <para>A paused index has no child by design - nothing starts one while the queue is not moving
/// - so asking "is a child alive" first answers no for both pauses and then blames the one cause
/// that is not true. Somebody watching a paused index inside a running Findra was told indexing
/// was paused because Findra was closed. It is the state the whole first-run download sits in,
/// where reading is held until the last question is answered, so it was the first sentence many
/// people ever read from this line.</para>
/// </summary>
public class IndexStatusPausedTests
{
    private const string Closed = "Findra is closed";

    [Fact]
    public void APausedIndexIsNotBlamedOnFindraBeingClosed()
    {
        string line = IndexStatus.Line(contentEnabled: true, state: "paused",
                                       pending: 1_838, indexed: 0, alive: false, rebuilt: false);

        Assert.DoesNotContain(Closed, line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("paused", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1,838", line);
    }

    [Fact]
    public void ABacklogWithNothingPausingItStillSaysFindraWasClosed()
    {
        // The other half, and the reason the sentence exists: indexing only happens while Findra
        // is open, so a backlog left by a previous session has to be explained rather than look
        // stuck.
        string line = IndexStatus.Line(contentEnabled: true, state: "",
                                       pending: 1_838, indexed: 0, alive: false, rebuilt: false);

        Assert.Contains(Closed, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadingTurnedOffIsNeitherOfThem()
    {
        string line = IndexStatus.Line(contentEnabled: false, state: "paused",
                                       pending: 1_838, indexed: 12, alive: false, rebuilt: false);

        Assert.DoesNotContain(Closed, line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("off", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARebuildOutranksBothBecauseItAnswersADifferentQuestion()
    {
        string line = IndexStatus.Line(contentEnabled: true, state: "paused",
                                       pending: 9, indexed: 0, alive: false, rebuilt: true);

        Assert.Contains("rebuild", line, StringComparison.OrdinalIgnoreCase);
    }
}
