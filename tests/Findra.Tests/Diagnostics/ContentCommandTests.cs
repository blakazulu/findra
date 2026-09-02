using Findra;
using Findra.Diagnostics;
using Xunit;

// Assigns CultureInfo.CurrentCulture (TheStatusReadsTheSameOnEveryMachine), so it joins the
// collection that stops xUnit running it beside a class formatting a number on a pool thread.
[Collection("culture")]
public class ContentCommandTests
{
    private static IndexSnapshot Empty(long indexed = 0, long queued = 0)
        => SearchIndexReportTests.Sample() with { Indexed = indexed, Queued = queued };

    [Fact]
    public void AFreshInstallSaysReadingInsideFilesIsOffAndHowToStart()
    {
        // Spec §6: off until asked for, and the interface says which state it is in rather than
        // looking idle. A fresh install's queue is empty and nothing is running, which is
        // byte-for-byte what a finished index looks like.
        string text = ContentCommand.RenderStatus(Config.Default, Empty());

        Assert.Contains("off", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--content on", text, StringComparison.Ordinal);
        Assert.DoesNotContain("up to date", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurnedOffAfterReadingSomethingSaysHowMuchItKept()
    {
        // Turning it off does not throw away what has been read, and saying so is the difference
        // between a switch somebody will use and one they will not touch again.
        string text = ContentCommand.RenderStatus(Config.Default, Empty(indexed: 9_000));

        Assert.Contains("9,000", text, StringComparison.Ordinal);
        Assert.Contains("off", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurnedOnItReportsTheQueueRatherThanTheSwitch()
    {
        string text = ContentCommand.RenderStatus(Config.Default with { IndexContent = true },
                                                  Empty(indexed: 40, queued: 1_200));

        Assert.Contains("1,200", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--content on", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTranscriptionLimitIsAlwaysNamedInWordsSomebodyCanRead()
    {
        Assert.Contains("5 minutes", ContentCommand.RenderStatus(Config.Default, Empty()), StringComparison.Ordinal);
        Assert.Contains("No limit", ContentCommand.RenderStatus(
            Config.Default with { TranscribeMinutes = TranscribeLimit.NoLimit }, Empty()), StringComparison.Ordinal);
        Assert.Contains("17 minutes", ContentCommand.RenderStatus(
            Config.Default with { TranscribeMinutes = 17 }, Empty()), StringComparison.Ordinal);
        Assert.Contains("Off", ContentCommand.RenderStatus(
            Config.Default with { TranscribeMinutes = TranscribeLimit.Off }, Empty()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusReadsTheSameOnEveryMachine()
    {
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            string de = ContentCommand.RenderStatus(Config.Default, Empty(indexed: 9_000));
            // Added while mutation testing: OFF, every number on this status comes from
            // IndexStatus.Line and TranscribeLimit.Describe, which are somebody else's invariant
            // code - so putting this command's own counts on the current culture left the test
            // green. Turned ON is where this file formats its own numbers, and it is the only
            // render that catches that.
            string deOn = ContentCommand.RenderStatus(Config.Default with { IndexContent = true },
                                                      Empty(indexed: 9_000, queued: 1_200));
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(ContentCommand.RenderStatus(Config.Default, Empty(indexed: 9_000)), de);
            Assert.Equal(ContentCommand.RenderStatus(Config.Default with { IndexContent = true },
                                                     Empty(indexed: 9_000, queued: 1_200)), deOn);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }
}
