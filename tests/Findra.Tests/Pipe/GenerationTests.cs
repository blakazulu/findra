using Findra.Pipe;
using System.Threading;
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
    public void RefusesGenerationZeroBeforeAnyQuery()
    {
        // A reply whose Gen field was never set arrives as 0. Nothing has been issued,
        // so nothing may be accepted.
        var g = new Generation();
        Assert.False(g.Accept(0));
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
    public void IsSafeUnderConcurrentAccept()
    {
        // Real threads released together on a barrier, repeated. Task.Run work items this
        // short are usually drained by a single pool thread before a second is scheduled,
        // so a pool-based version never actually overlaps and cannot fail at all.
        //
        // Be precise about what this does and does not prove. It races the duplicate axis
        // only - many replies carrying one generation, _issued pinned - and so it catches
        // unsynchronised duplicate suppression or a CAS with the wrong comparand. It does
        // NOT catch a Next() landing inside an in-flight Accept(); that axis is closed by
        // construction in Accept, not by this test. Claiming otherwise here would be the
        // same false confidence the pool version gave.
        for (int round = 0; round < 200; round++)
        {
            var g = new Generation();
            long gen = g.Next();

            int accepted = 0;
            using var ready = new Barrier(9);
            var threads = new Thread[8];
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(() =>
                {
                    ready.SignalAndWait();
                    if (g.Accept(gen)) Interlocked.Increment(ref accepted);
                });
                threads[i].Start();
            }

            ready.SignalAndWait();
            foreach (Thread t in threads) t.Join();

            Assert.Equal(1, accepted);
        }
    }
}
