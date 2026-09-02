using Findra;
using Findra.Pipe;
using Xunit;

public class JournalTests
{
    /// <summary>Every async test here reads from a pipe that a bug can leave silent. A CTS
    /// with a deadline turns "hangs CI forever" into "fails in ten seconds, with a name".</summary>
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(10));
    private const ulong Journal = 0xBEEF;

    private static NameIndex Sample()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        ix.Upsert(100, 5, NtfsVolume.FileAttributeDirectory, "Papers");
        ix.Upsert(101, 100, 0, "lease.pdf");
        return ix;
    }

    /// <summary>One volume, with the journal state a bare NameIndex cannot carry.</summary>
    private static Dictionary<char, VolumeView> One(long nextUsn = 5000)
        => new() { ['C'] = new VolumeView(Sample(), Journal, nextUsn, EnumerateMs: 1_840.0) };

    private static JournalEvent Event(ulong frn, string name, uint reason, long usn, ulong jid = Journal)
        => new('C', jid, frn, 100, 0, name, @"C:\Papers\" + name, reason, usn);

    /// <summary>A gap reader that hands back a fixed list, so the replay path is exercised
    /// with no volume handle and no elevation.</summary>
    private static NameServer.GapReader Gap(bool reachable, params JournalEvent[] events)
        => (_, _, _) => (reachable, events);

    [Fact]
    public async Task SubscribingIsAcknowledgedAndThenEventsArrive()
    {
        var (server, client) = NameServerTests.PairForTests();
        var bus = new JournalBroadcast();
        using var cts = Deadline();
        _ = NameServer.Serve(server, One(), new IndexLock(), bus, Gap(true), cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindSubscribe,
            new SubscribeRequest([])), cts.Token);
        Envelope ack = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!);
        Assert.Equal(Envelope.KindSubscribeReply, ack.Kind);

        // The ack is written UNDER the same write lock that already holds the registration,
        // so observing the ack proves the sink exists. Publishing here cannot race it.
        bus.Publish(Event(300, "invoice.pdf", NtfsVolume.ReasonFileCreate, 4242));

        Envelope pushed = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!);
        Assert.Equal(Envelope.KindJournal, pushed.Kind);
        JournalEvent e = pushed.Body<JournalEvent>();
        Assert.Equal("invoice.pdf", e.Name);
        Assert.Equal(4242, e.Usn);
        Assert.Equal('C', e.Volume);
        Assert.Equal(0xBEEFul, e.JournalId);          // a USN with no journal id is meaningless

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ASubscriberBehindTheTailIsSentTheGapItMissed()
    {
        // THE test for the ordinary sequence: quit Findra, save 500 documents, start Findra.
        // The helper is a logon task and never stopped, so nothing re-broadcasts what happened
        // while the UI was gone. Without a replay from the stored cursor those files are never
        // queued, and --searchindex reports the index as up to date. Spec 2a and 3.
        var (server, client) = NameServerTests.PairForTests();
        var bus = new JournalBroadcast();
        using var cts = Deadline();
        _ = NameServer.Serve(server, One(), new IndexLock(), bus,
            Gap(true,
                Event(700, "while-you-were-out-1.pdf", NtfsVolume.ReasonFileCreate, 71),
                Event(701, "while-you-were-out-2.pdf", NtfsVolume.ReasonFileCreate, 72)),
            cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindSubscribe,
            new SubscribeRequest([new VolumeCursor('C', 0xBEEF, 70)])), cts.Token);

        SubscribeReply ack = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!).Body<SubscribeReply>();
        VolumeResume c = Assert.Single(ack.Volumes, v => v.Volume == 'C');
        Assert.False(c.NeedsFullPass);          // the gap was reachable: replay, do not re-walk
        Assert.Equal(2, c.Replayed);

        var names = new List<string>();
        for (int i = 0; i < 2; i++)
            names.Add(Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!).Body<JournalEvent>().Name);

        Assert.Equal(["while-you-were-out-1.pdf", "while-you-were-out-2.pdf"], names);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task AGapTheJournalNoLongerReachesAsksForAFullPassInstead()
    {
        // The journal is a ring buffer. A machine that was off for a month, or a build that
        // wrapped it, cannot be caught up - and pretending otherwise loses files silently.
        var (server, client) = NameServerTests.PairForTests();
        using var cts = Deadline();
        _ = NameServer.Serve(server, One(), new IndexLock(), new JournalBroadcast(),
                             Gap(reachable: false), cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindSubscribe,
            new SubscribeRequest([new VolumeCursor('C', 0xBEEF, 70)])), cts.Token);

        SubscribeReply ack = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!).Body<SubscribeReply>();
        VolumeResume c = Assert.Single(ack.Volumes, v => v.Volume == 'C');
        Assert.True(c.NeedsFullPass);
        Assert.Equal(0, c.Replayed);
        Assert.Contains("no longer reaches", c.Note, StringComparison.OrdinalIgnoreCase);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task NothingIsPushedToASessionThatNeverSubscribed()
    {
        // A push channel nobody asked for is an unsolicited frame in the middle of someone
        // else's request/reply conversation. --searchprobe opens a session and never
        // subscribes; it must see exactly the replies it asked for.
        var (server, client) = NameServerTests.PairForTests();
        var bus = new JournalBroadcast();
        using var cts = Deadline();
        _ = NameServer.Serve(server, One(), new IndexLock(), bus, Gap(true), cts.Token);

        bus.Publish(Event(300, "invoice.pdf", NtfsVolume.ReasonFileCreate, 1));
        Assert.Equal(0, bus.SubscriberCount);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindStatus, new StatusRequest()), cts.Token);
        Envelope first = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!);
        Assert.Equal(Envelope.KindStatusReply, first.Kind);   // not a journal frame

        await cts.CancelAsync();
    }

    [Fact]
    public async Task AQueryAnsweredWhileEventsAreBeingPushedStaysAWholeFrame()
    {
        // The adversarial one. Two writers on one transport - the reply path and the push
        // path - interleave their bytes without a write lock, and a half-written frame
        // desynchronises the pipe permanently: every later read is garbage. That failure
        // does not announce itself, so it is asserted here.
        var (server, client) = NameServerTests.PairForTests();
        var bus = new JournalBroadcast();
        using var cts = Deadline();
        _ = NameServer.Serve(server, One(), new IndexLock(), bus, Gap(true), cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindSubscribe,
            new SubscribeRequest([])), cts.Token);
        Assert.Equal(Envelope.KindSubscribeReply,
                     Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!).Kind);

        var flood = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
                bus.Publish(Event((ulong)(1000 + i), $"f{i}.pdf", NtfsVolume.ReasonFileCreate, i));
        });
        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery,
            new QueryRequest(9, "lease", 20)), cts.Token);

        // Read until BOTH the reply and every pushed event have come back. Stopping at the reply
        // makes the interleaving assertion a coin toss - the reply legitimately wins the race to
        // the write lock some of the time, and the test then fails while nothing is wrong.
        // Draining all 201 frames is deterministic and strictly stronger: every frame on the
        // transport, in both directions of the interleave, has to unpack whole.
        QueryReply? reply = null;
        int journals = 0;
        while (reply is null || journals < 200)
        {
            // Unpack throws on a torn frame, which is exactly the failure under test.
            Envelope e = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!);
            if (e.Kind == Envelope.KindJournal) { e.Body<JournalEvent>(); journals++; continue; }
            Assert.Equal(Envelope.KindQueryReply, e.Kind);
            reply = e.Body<QueryReply>();
        }
        await flood;

        Assert.Equal(9, reply.Gen);
        Assert.Equal("lease.pdf", Assert.Single(reply.Rows).Name);
        Assert.Equal(200, journals);   // the flood really did share the transport with the query

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ASlowSubscriberDropsEventsAndIsToldSoRatherThanStallingTheTail()
    {
        // Back-pressure, server side. A client that stops reading must cost ITS OWN events,
        // never the journal tail: a tail parked on one stuck socket lets the journal wrap,
        // and then every subscriber has lost data. The dropped count reaches the UI through
        // the status reply, because a silent gap is worse than a reported one.
        var (server, client) = NameServerTests.PairForTests();
        var bus = new JournalBroadcast();
        using var cts = Deadline();
        _ = NameServer.Serve(server, One(), new IndexLock(), bus, Gap(true), cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindSubscribe,
            new SubscribeRequest([])), cts.Token);
        Assert.Equal(Envelope.KindSubscribeReply,
                     Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!).Kind);

        // Publish far past the per-session outbound bound without reading a single frame.
        // Publish must return promptly every time; if it blocks, the deadline fails the test.
        for (int i = 0; i < NameServer.MaxOutbound * 4; i++)
            bus.Publish(Event((ulong)(5000 + i), $"g{i}.pdf", NtfsVolume.ReasonFileCreate, i));

        // Drain until the status reply appears; the session must still be answering.
        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindStatus, new StatusRequest()), cts.Token);
        StatusReply? status = null;
        while (status is null)
        {
            Envelope e = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!);
            if (e.Kind == Envelope.KindStatusReply) status = e.Body<StatusReply>();
        }

        Assert.True(status.Volumes.Sum(v => v.Dropped) > 0,
                    "a session that never read must have dropped events, and must say how many");
        await cts.CancelAsync();
    }

    [Fact]
    public void ACursorFromADifferentJournalAsksForAFullPass()
    {
        // A recreated journal restarts USNs from zero, so a stored position from the old one
        // names a record that no longer exists - or worse, a different one. The id is what
        // makes the number mean anything. A cursor that merely LAGS is a different case: it
        // is replayed (see ASubscriberBehindTheTailIsSentTheGapItMissed), not re-walked.
        var ids = new Dictionary<char, ulong> { ['C'] = 0xBEEF, ['D'] = 0x1234 };
        var current = new Dictionary<char, long> { ['C'] = 5000, ['D'] = 77 };

        IReadOnlyList<VolumeResume> r = JournalTail.ResumeFrom(ids, current,
            [new VolumeCursor('C', 0xDEAD, 100), new VolumeCursor('D', 0x1234, 70)]);

        VolumeResume c = Assert.Single(r, v => v.Volume == 'C');
        Assert.True(c.NeedsFullPass);
        Assert.Equal(5000, c.Usn);            // resume from HERE, not from the stale 100
        Assert.Equal(0xBEEFul, c.JournalId);  // and under the CURRENT journal's id

        VolumeResume d = Assert.Single(r, v => v.Volume == 'D');
        Assert.False(d.NeedsFullPass);
        Assert.Equal(70, d.Usn);              // its own position is still good: replay from it
        Assert.Equal(0x1234ul, d.JournalId);
    }

    [Fact]
    public void AVolumeWithNoStoredCursorNeedsAFullPass()
    {
        IReadOnlyList<VolumeResume> r = JournalTail.ResumeFrom(
            new Dictionary<char, ulong> { ['C'] = 0xBEEF },
            new Dictionary<char, long> { ['C'] = 900 },
            []);

        VolumeResume c = Assert.Single(r);
        Assert.True(c.NeedsFullPass);
        Assert.Equal(900, c.Usn);
        Assert.Equal(0xBEEFul, c.JournalId);
    }

    [Fact]
    public void ApplyingAJournalCreateMakesTheNameSearchableAndADeleteRemovesIt()
    {
        NameIndex ix = Sample();
        var hits = new List<NameIndex.Hit>();

        JournalTail.Apply(ix, new NtfsVolume.Change(400, 100, 0, "invoice 2026.pdf",
            NtfsVolume.ReasonFileCreate | NtfsVolume.ReasonClose, 10));
        ix.Search(new SearchQuery("invoice"), hits);
        Assert.Single(hits);
        Assert.Equal(@"C:\Papers\invoice 2026.pdf", ix.PathOf(hits[0].Record));

        hits.Clear();
        JournalTail.Apply(ix, new NtfsVolume.Change(400, 100, 0, "invoice 2026.pdf",
            NtfsVolume.ReasonFileDelete | NtfsVolume.ReasonClose, 11));
        ix.Search(new SearchQuery("invoice"), hits);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task TheClientYieldsEveryEventTheServerPushes()
    {
        // NameClient's pump used to DROP journal frames on the floor - `case KindJournal:
        // break;`. Without the channel this test never yields its first item.
        var (server, client) = NameServerTests.PairForTests();
        var bus = new JournalBroadcast();
        using var cts = Deadline();
        _ = NameServer.Serve(server, One(), new IndexLock(), bus, Gap(true), cts.Token);

        await using var c = new NameClient(client);
        SubscribeReply ack = await c.SubscribeJournalAsync([], cts.Token);
        Assert.NotNull(ack);

        bus.Publish(Event(500, "a.pdf", NtfsVolume.ReasonFileCreate, 1));
        bus.Publish(Event(501, "b.pdf", NtfsVolume.ReasonFileCreate, 2));

        var got = new List<JournalEvent>();
        await foreach (JournalEvent e in c.JournalAsync(cts.Token))
        {
            got.Add(e);
            if (got.Count == 2) break;
        }

        Assert.Equal(["a.pdf", "b.pdf"], got.Select(e => e.Name));
        Assert.Equal([1L, 2L], got.Select(e => e.Usn));
        await cts.CancelAsync();
    }

    [Fact]
    public async Task TheClientsOwnChannelDropsWhatItCannotHoldAndCountsIt()
    {
        // The other half of the back-pressure story, and the half nothing else in the system
        // records. A server-side drop pushes a reset marker; a client-side drop cannot,
        // because nothing upstream knows it happened - JournalDropped is the ONLY trace of
        // it, and the feeder owes a fresh walk when that number moves.
        //
        // This is the test the drop-accounting correction exists for. Written as
        // `if (!writer.TryWrite(e)) dropped++` the count stays at zero forever, because a
        // DropOldest TryWrite always returns true and evicts silently; only the
        // CreateBounded overload that takes an itemDropped callback observes the eviction.
        // Every other test in this file passes either way, which is what makes this one
        // load-bearing rather than decorative.
        //
        // Frames go straight onto the transport rather than through a server, and the same
        // packed payload is reused, so the cost is the pump's decode and nothing else. It is
        // still MaxOutbound + 1,000 frames, hence its own longer deadline.
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var c = new NameClient(client);

        byte[] one = Envelope.Pack(Envelope.KindJournal,
            Event(900, "flood.pdf", NtfsVolume.ReasonFileCreate, 1));
        for (int i = 0; i < NameServer.MaxOutbound + 1_000; i++)
            await Frame.WriteAsync(server, one, cts.Token);

        // Nothing ever reads JournalAsync here, so the channel fills and evicts. The pump
        // handles frames in the order they arrive, so a status reply written behind the
        // flood proves every journal frame ahead of it has already been offered.
        Task<StatusReply> status = c.StatusAsync(cts.Token);
        await Frame.WriteAsync(server, Envelope.Pack(Envelope.KindStatusReply,
            new StatusReply(0, [])), cts.Token);
        await status;

        Assert.True(c.JournalDropped > 0,
                    "a client that never drains its journal channel must count what it lost");
        await cts.CancelAsync();
    }

    [Fact]
    public async Task NeitherSubscribingNorAPushMovesTheGenerationCounter()
    {
        // A subscription is a registration and a pushed event is not an answer to anything.
        // If either touched Generation, the next real reply would be rejected as stale and
        // the card would go blank on a keystroke. The capture is BEFORE the subscribe so
        // this covers both halves, not only the push.
        var (server, client) = NameServerTests.PairForTests();
        var bus = new JournalBroadcast();
        using var cts = Deadline();
        _ = NameServer.Serve(server, One(), new IndexLock(), bus, Gap(true), cts.Token);

        await using var c = new NameClient(client);
        long before = c.CurrentGeneration;

        await c.SubscribeJournalAsync([], cts.Token);
        Assert.Equal(before, c.CurrentGeneration);

        bus.Publish(Event(600, "c.pdf", NtfsVolume.ReasonFileCreate, 3));
        await foreach (JournalEvent _ in c.JournalAsync(cts.Token)) break;

        Assert.Equal(before, c.CurrentGeneration);
        QueryReply? reply = await c.SearchAsync("lease", 10, cts.Token);
        Assert.NotNull(reply);                       // not dropped as stale
        Assert.Single(reply!.Rows);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task AnEventPublishedDuringASubscribeArrivesAfterTheBacklogAndIsNeverLost()
    {
        // The ordering test, and the reason SubscribeWithBacklog is one call rather than
        // "read the gap, then Subscribe". Two failures are possible with separate steps and
        // this catches both:
        //
        //   * register AFTER the gap read - an event published in between reaches no sink and
        //     is not in the gap either, so it is lost. `seen` comes back without "live.pdf".
        //   * register BEFORE the gap read and enqueue the gap afterwards - the live event is
        //     queued AHEAD of the older replayed records, so `seen` comes back live-first.
        //     That inversion is not cosmetic: a create-then-delete replayed behind a newer
        //     create of the same FRN makes the feeder delete a file that exists, and Enqueue's
        //     upsert on (vol, frn) does not save it - last write wins, and the last write is
        //     the older one.
        //
        // The backlog here sleeps deliberately, so a racing publisher has every chance to get
        // in first. It cannot, because Publish and SubscribeWithBacklog are mutually
        // exclusive: Publish parks for the length of one gap read and loses nothing, because
        // the tail's own NextUsn is untouched and it catches up on its next pass.
        var bus = new JournalBroadcast();
        var seen = new List<string>();
        var backlogStarted = new ManualResetEventSlim();

        Task racer = Task.Run(() =>
        {
            backlogStarted.Wait(TimeSpan.FromSeconds(5));
            bus.Publish(Event(900, "live.pdf", NtfsVolume.ReasonFileCreate, 78));
        });

        using (bus.SubscribeWithBacklog(
            backlog: () =>
            {
                backlogStarted.Set();
                Thread.Sleep(100);
                return [Event(700, "gap-1.pdf", NtfsVolume.ReasonFileCreate, 71),
                        Event(701, "gap-2.pdf", NtfsVolume.ReasonFileCreate, 72)];
            },
            sink: e => { lock (seen) seen.Add(e.Name); }))
        {
            // The wait is inside the using deliberately: the registration is still live, so a
            // Publish that deadlocked behind the backlog would still be stuck here. Awaited
            // rather than blocked so the test holds no thread while it waits.
            Task finished = await Task.WhenAny(racer, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(finished == racer,
                        "Publish never returned - it must park for the backlog, not deadlock behind it");
        }

        lock (seen) Assert.Equal(["gap-1.pdf", "gap-2.pdf", "live.pdf"], seen);
    }

    [Fact]
    public void TheResetMarkerCarriesTheVolumesJournalIdLikeEveryOtherEvent()
    {
        // Benign today, because the feeder clears the position on a marker before it reads
        // the id. It stops being benign the moment anything downstream keys off the id, and
        // a zero that only works by accident is not worth keeping.
        var bus = new JournalBroadcast();
        JournalEvent? got = null;

        using (bus.SubscribeWithBacklog(backlog: () => [], sink: e => got = e))
            bus.Publish(JournalTail.ResetMarker('C', Journal));

        Assert.NotNull(got);
        Assert.Equal(Journal, got!.JournalId);
        Assert.Equal(0u, got.Reason);
        Assert.Equal("", got.Name);
        Assert.Equal("", got.Path);
    }

    [Fact]
    public async Task ConcurrentAppliesAndSearchesLeaveTheIndexIntact()
    {
        // A race detector, not a proof: without the lock this fails often on a multicore
        // machine, by a lost record or an exception out of the search. If you remove the
        // lock and it still passes, say so in your report rather than concluding it is safe.
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        var gate = new IndexLock();

        Task writer = Task.Run(() =>
        {
            for (ulong f = 1000; f < 3000; f++)
                using (gate.Write('C'))
                    JournalTail.Apply(ix, new NtfsVolume.Change(f, 5, 0, $"file{f}.pdf",
                        NtfsVolume.ReasonFileCreate | NtfsVolume.ReasonClose, (long)f));
        });
        Task reader = Task.Run(() =>
        {
            var mine = new List<NameIndex.Hit>();
            for (int i = 0; i < 2000; i++)
                using (gate.Read('C')) { mine.Clear(); ix.Search(new SearchQuery("file"), mine, 50); }
        });
        await Task.WhenAll(writer, reader);

        var hits = new List<NameIndex.Hit>();
        using (gate.Read('C')) ix.Search(new SearchQuery("file"), hits, 4000);
        Assert.Equal(2000, hits.Count);
    }

    [Fact]
    public void OneVolumesWriterDoesNotBlockAnotherVolumesReader()
    {
        // The lock is per volume because the indexes already are. A single global lock means
        // a journal batch on D: stalls every name query that only ever touches C:.
        var gate = new IndexLock();
        using (gate.Write('D'))
        using (gate.Read('C'))
        {
            // Both held at once. With one shared ReaderWriterLockSlim and NoRecursion this
            // throws or deadlocks; with a lock per volume it is simply correct.
        }
    }
}
