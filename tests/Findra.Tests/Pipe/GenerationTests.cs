using Findra.Pipe;
using Xunit;

public class GenerationTests
{
    [Fact]
    public void NextIncreasesMonotonically()
    {
        var g = new Generation();
        Assert.Equal(1, g.Next());
        Assert.Equal(2, g.Next());
        Assert.Equal(3, g.Next());
        Assert.Equal(3, g.Current);
    }

    [Fact]
    public void AcceptsTheNewestGeneration()
    {
        var g = new Generation();
        long gen = g.Next();
        Assert.True(g.Accept(gen));
    }

    [Fact]
    public void RefusesAStaleGeneration()
    {
        var g = new Generation();
        long first = g.Next();
        g.Next();                        // the user typed another character
        Assert.False(g.Accept(first));
    }

    [Fact]
    public void RefusesADuplicateOfTheCurrentGeneration()
    {
        var g = new Generation();
        long gen = g.Next();
        Assert.True(g.Accept(gen));
        Assert.False(g.Accept(gen));
    }

    [Fact]
    public void RefusesAGenerationFromTheFuture()
    {
        // a reply claiming a generation never issued is a protocol fault, not a race
        var g = new Generation();
        g.Next();
        Assert.False(g.Accept(999));
    }

    [Fact]
    public void SlowFirstAnswerNeverBeatsFastSecondAnswer()
    {
        // the adversarial case the spec calls for: "sun" is slow, "sunset" is fast,
        // and the slow answer lands last.
        var g = new Generation();
        long slow = g.Next();      // "sun"
        long fast = g.Next();      // "sunset"

        Assert.True(g.Accept(fast));    // fast lands first and is shown
        Assert.False(g.Accept(slow));   // slow lands second and must be dropped
    }

    [Fact]
    public async Task IsSafeUnderConcurrentAccept()
    {
        var g = new Generation();
        long gen = g.Next();

        int accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (g.Accept(gen)) Interlocked.Increment(ref accepted);
        })));

        Assert.Equal(1, accepted);
    }
}
