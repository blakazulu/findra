using Findra;
using Findra.Pipe;
using Xunit;

// It assigns CurrentCulture, and CultureCollection exists to stop a parallel test observing
// de-DE and failing for a reason that has nothing to do with it.
[Collection("culture")]
public class IndexLineFormatterTests
{
    [Fact]
    public void NoVolumesSaysSo()
    {
        var reply = new StatusReply(4321, Array.Empty<VolumeStatus>());

        Assert.Equal("no volumes indexed (helper pid 4321)", IndexLineFormatter.IndexLineFor(reply));
    }

    [Fact]
    public void OneLiveVolumeNamesItWithNoCaveat()
    {
        var reply = new StatusReply(100, new[] { new VolumeStatus('C', 1_500_000, 1048576, Live: true) });

        string line = IndexLineFormatter.IndexLineFor(reply);

        Assert.Equal("1.5M names on C: (helper pid 100)", line);
        Assert.DoesNotContain("still reading", line);
    }

    [Fact]
    public void AVolumeStillEnumeratingAppendsTheCaveat()
    {
        var reply = new StatusReply(100, new[] { new VolumeStatus('C', 1200, 1048576, Live: false) });

        string line = IndexLineFormatter.IndexLineFor(reply);

        Assert.StartsWith("1k names on C: (helper pid 100)", line);
        Assert.EndsWith(" (still reading the drive)", line);
    }

    [Fact]
    public void OneLiveAndOneStillReadingVolumeStillAppendsTheCaveat()
    {
        var reply = new StatusReply(7, new[]
        {
            new VolumeStatus('C', 1_000_000, 1048576, Live: true),
            new VolumeStatus('D', 500, 1048576, Live: false),
        });

        string line = IndexLineFormatter.IndexLineFor(reply);

        Assert.Contains("C:, D:", line);
        Assert.EndsWith(" (still reading the drive)", line);
    }

    [Fact]
    public void TheNameCountReadsTheSameOnEveryMachine()
    {
        // 1.5M renders as "1,5M" under de-DE, in the same footer line as IndexStatus.Line,
        // which is careful to be invariant. One half of one sentence.
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var reply = new StatusReply(4321, [new VolumeStatus('C', 1_500_000, 0, Live: true)]);
            Assert.Contains("1.5M", IndexLineFormatter.IndexLineFor(reply), StringComparison.Ordinal);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }
}
