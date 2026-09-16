using Findra;
using Xunit;

public class BeatTests
{
    [Fact]
    public void AFloodOfProgressBecomesOneWritePerInterval()
    {
        long now = 1000;
        var written = new List<long>();
        Action beat = Beat.Throttled(() => now, written.Add, everySeconds: 2);

        for (int i = 0; i < 100; i++) beat();          // all in the same second
        Assert.Single(written);

        now += 1;
        beat();
        Assert.Single(written);                        // not yet

        now += 2;
        beat();
        Assert.Equal(2, written.Count);
        Assert.Equal(1003, written[1]);
    }
}
