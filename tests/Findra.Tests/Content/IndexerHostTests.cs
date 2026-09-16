using System.Globalization;
using Findra;
using Xunit;

/// <summary>
/// The supervision SEQUENCE, driven through the real host: start a child, kill it for making no
/// progress, and see what the next turn of the interface's pump does with the one that replaces it.
///
/// <para><c>IndexerWatchTests</c> covers the pure rule, and a pure rule cannot see any of this -
/// what goes wrong here is the composition of three facts that are each correct on their own: a
/// watchdog kill resets the backoff so the restart is immediate, the beat row is written by the
/// child and outlives the child that wrote it, and the pump asks both questions on the same
/// 400 ms turn.</para>
/// </summary>
public class IndexerHostTests
{
    /// <summary>A child that is alive until something ends it, and remembers being ended. The seam
    /// exists because a <see cref="System.Diagnostics.Process"/> cannot be faked without starting
    /// one, and this sequence has to be driven without starting anything.</summary>
    private sealed class FakeChild : IndexerHost.IChild
    {
        public bool Alive { get; private set; } = true;
        public int ExitCode => 0;
        public bool Killed { get; private set; }
        public void Kill() { Killed = true; Alive = false; }
        public bool WaitForExit(int milliseconds) => !Alive;
        public void Dispose() { }
    }

    private static string Beat(DateTime at) => new DateTimeOffset(at).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    private static long Unix(DateTime at) => new DateTimeOffset(at).ToUnixTimeSeconds();

    [Fact]
    public void AChildStartedToReplaceAStalledOneIsNotKilledOnItsPredecessorsLastBeat()
    {
        // The defect this exists for, end to end. Tick N kills a child that has said nothing for
        // three minutes. Tick N+1 starts a fresh one - immediately, because a watchdog kill resets
        // the backoff on purpose - and then asks the same question against the same beat row, which
        // NOTHING has written since: the child that wrote it is dead, and a new one writes no beat
        // until it has taken a row off the queue and begun reading it. Judged on that, the
        // replacement is killed microseconds after it starts, and so is the one after that, every
        // 400 ms for as long as Findra runs.
        //
        // The attempts counter cannot rescue it either: an attempt is committed by the CHILD once
        // it has taken a row, so a child killed before it gets that far commits nothing, the file is
        // never written off, and the queue never moves. What ships then does the exact opposite of
        // what it promises - that one file can no longer hold up everything behind it.
        DateTime now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var started = new List<FakeChild>();
        using var host = new IndexerHost(() => { var c = new FakeChild(); started.Add(c); return c; }, () => now);

        host.EnsureRunning();
        DateTime lastBeat = now;                       // the only beat this index will ever hold

        // three minutes later, with work queued and nothing reported
        now = now.AddSeconds(IndexerWatch.StallSeconds + 5);
        Assert.True(IndexerWatch.ShouldRestart(host.Running, reading: true, pending: 10, Beat(lastBeat), Unix(now), host.SinceStart),
                    "a live child with work to do that has reported nothing for three minutes is the case this exists for");

        host.Kill("no progress on big.avi");
        Assert.True(Assert.Single(started).Killed);

        // the next turn of the pump, 400 ms later
        now = now.AddMilliseconds(400);
        host.EnsureRunning();
        Assert.Equal(2, started.Count);                 // the restart is immediate, by design

        Assert.False(IndexerWatch.ShouldRestart(host.Running, reading: true, pending: 10, Beat(lastBeat), Unix(now), host.SinceStart),
                     "a child that has not had time to report anything cannot be stalled: judged on the beat its "
                     + "predecessor left behind, every replacement is killed on the turn it is started");
        Assert.False(started[1].Killed);
    }

    [Fact]
    public void TheReplacementIsStillKilledOnceItHasHadTheWholeWindowToItself()
    {
        // The control, and it is not decoration: the cheap way to pass the test above is to stop
        // killing anything, which gives back the unbounded stall the watchdog exists to bound. Once
        // a child has been running longer than the stall window, a beat older than that window is
        // about THIS child and is evidence again.
        DateTime now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var started = new List<FakeChild>();
        using var host = new IndexerHost(() => { var c = new FakeChild(); started.Add(c); return c; }, () => now);

        host.EnsureRunning();
        DateTime lastBeat = now;
        now = now.AddSeconds(IndexerWatch.StallSeconds + 5);
        host.Kill("no progress");
        host.EnsureRunning();

        now = now.AddSeconds(IndexerWatch.StallSeconds + 1);
        Assert.True(IndexerWatch.ShouldRestart(host.Running, reading: true, pending: 10, Beat(lastBeat), Unix(now), host.SinceStart),
                    "a child that has had the whole window to itself and still reported nothing is stalled");
    }

    [Fact]
    public void AChildThatDiedOnItsOwnIsStillRestartedOnBackoffRatherThanAtOnce()
    {
        // The other lifetime, unchanged by any of this: a child that CRASHES walks the 5 s, 10, 20
        // backoff, so a file that takes the process down every time does not become a process storm.
        // Only a watchdog kill resets it.
        DateTime now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var started = new List<FakeChild>();
        using var host = new IndexerHost(() => { var c = new FakeChild(); started.Add(c); return c; }, () => now);

        host.EnsureRunning();
        started[0].Kill();                              // it died of its own accord

        now = now.AddSeconds(1);
        host.EnsureRunning();
        Assert.Single(started);                          // too soon: the backoff is five seconds

        now = now.AddSeconds(5);
        host.EnsureRunning();
        Assert.Equal(2, started.Count);
    }
}
