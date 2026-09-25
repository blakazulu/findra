using Findra;
using Xunit;

/// <summary>
/// The speech runtime keeps a pool of video memory for the life of the process once it has done
/// real transcription: disposing every model leaves about 1.5 GB charged to the indexer, measured
/// on the card after three full recordings, and nothing Findra can call gives it back. Ending the
/// process does. So an indexer that lets its models go and then finds memory still on the card
/// exits with <see cref="IndexerHost.RecycleExitCode"/>, and the host starts a fresh one at once.
/// </summary>
public class IndexerRecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-recycle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ContentDb PausedDb()
    {
        Directory.CreateDirectory(_dir);
        var db = new ContentDb(Path.Combine(_dir, "search.db"));
        db.Set("index:paused", "1");     // the quickest way into a release that holds no file
        return db;
    }

    private const long Mb = 1024L * 1024;

    [Fact]
    public void MemoryLeftOnTheCardAfterTheModelsWentEndsTheLoopForARecycle()
    {
        using ContentDb db = PausedDb();
        var d = new Holding();
        int passes = 0, measured = 0;

        bool recycle = Indexer.Loop(db, parentPid: 0, running: () => passes++ < 5, decoders: d,
                                    leftOnCard: () => { measured++; return 1_500 * Mb; });

        Assert.True(recycle);
        Assert.Equal(1, passes);          // it stopped after the release, not after five passes
        Assert.Equal(1, measured);
    }

    [Fact]
    public void WhatAModelThatWasReallyFreedLeavesBehindIsNotAReason()
    {
        // DirectML gives back every byte; a tiny transcription leaves about 27 MB.
        using ContentDb db = PausedDb();
        int passes = 0;
        bool recycle = Indexer.Loop(db, parentPid: 0, running: () => passes++ < 3, decoders: new Holding(),
                                    leftOnCard: () => 27 * Mb);
        Assert.False(recycle);
        Assert.Equal(4, passes);
    }

    [Fact]
    public void NothingIsMeasuredWhenNothingWasLoaded()
    {
        // A pause or a wait with nothing on the card releases nothing, so there is nothing to have
        // outlived it - and the reading costs a second and a half.
        using ContentDb db = PausedDb();
        int passes = 0, measured = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 3, decoders: new Holding { Loaded = false },
                     leftOnCard: () => { measured++; return 1_500 * Mb; });
        Assert.Equal(0, measured);
    }

    [Fact]
    public void AReadingThatCouldNotBeTakenIsNotAReason()
    {
        using ContentDb db = PausedDb();
        int passes = 0;
        Assert.False(Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: new Holding(),
                                  leftOnCard: () => null));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(27L * 1024 * 1024, false)]
    [InlineData(Indexer.RecycleAboveBytes, false)]
    [InlineData(Indexer.RecycleAboveBytes + 1, true)]
    [InlineData(1_470L * 1024 * 1024, true)]
    public void TheLineSitsBetweenWhatAFreedModelLeavesAndWhatTheSpeechRuntimeKeeps(long? left, bool recycle) =>
        Assert.Equal(recycle, Indexer.ShouldRecycle(left));

    // ---- the host's side ----

    private sealed class Child(int exitCode) : IndexerHost.IChild
    {
        public bool Alive { get; set; } = true;
        public int ExitCode { get; } = exitCode;
        public void Kill() => Alive = false;
        public bool WaitForExit(int milliseconds) => !Alive;
        public void Dispose() { }
    }

    [Fact]
    public void ARecycledChildIsReplacedOnTheNextTurnWithNoBackoff()
    {
        DateTime now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var started = new List<Child>();
        int code = IndexerHost.RecycleExitCode;
        using var host = new IndexerHost(() => { var c = new Child(code); started.Add(c); return c; }, () => now);

        host.EnsureRunning();
        for (int i = 1; i <= 4; i++)
        {
            started[^1].Alive = false;
            Assert.Equal(0, host.RestartIn);   // never "restarting in 5 s" for something that is not a crash
            now = now.AddSeconds(2);
            host.EnsureRunning();
            Assert.Equal(i + 1, started.Count);
        }
    }

    [Fact]
    public void ACrashAfterRecyclesStillWaitsOnlyTheFirstStepOfTheBackoff()
    {
        DateTime now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var started = new List<Child>();
        var codes = new Queue<int>([IndexerHost.RecycleExitCode, IndexerHost.RecycleExitCode, 1, 0]);
        using var host = new IndexerHost(() => { var c = new Child(codes.Dequeue()); started.Add(c); return c; }, () => now);

        host.EnsureRunning();
        started[^1].Alive = false; host.EnsureRunning();   // recycled
        started[^1].Alive = false; host.EnsureRunning();   // recycled
        Assert.Equal(3, started.Count);

        started[^1].Alive = false;                          // crashed
        host.EnsureRunning();
        Assert.Equal(3, started.Count);                     // backing off
        Assert.InRange(host.RestartIn, 1, 5);               // the first step, not one built up by the recycles
        now = now.AddSeconds(6);
        host.EnsureRunning();
        Assert.Equal(4, started.Count);
    }

    /// <summary>Holds models until they are let go, like the real decoders.</summary>
    private sealed class Holding : IDecoders
    {
        public bool Loaded { get; set; } = true;
        public CapabilitySet Installed => CapabilitySet.None;
        public bool CanRead(ResultKind kind) => false;
        public KindResult Decode(ResultKind kind, string path, long bytes) => new([], null);
        public KindResult DecodeFrames(string path) => new([], null);
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Unload() => Loaded = false;
        public void Dispose() { }
    }
}
