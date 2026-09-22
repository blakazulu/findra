using Findra;
using Xunit;

public sealed class IndexGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-gate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly CapabilitySet WordsOnly = CapabilitySet.None;
    private static readonly CapabilitySet Meaning = new(new HashSet<Capability> { Capability.Meaning });

    [Fact]
    public void ADeleteIsNeverHeldBackBecauseItLoadsNothing()
    {
        GateVerdict v = IndexGate.Decide(isDelete: true, usesModels: true, fullscreen: true, "a fullscreen game",
                                         gpuBusy: true, "ollama");
        Assert.True(v.Run);
    }

    [Fact]
    public void FullscreenIsAskedBeforeTheCard()
    {
        GateVerdict v = IndexGate.Decide(isDelete: false, usesModels: true, fullscreen: true, "a fullscreen game",
                                         gpuBusy: true, "ollama");
        Assert.False(v.Run);
        Assert.Equal(IndexGate.Fullscreen, v.State);
        Assert.Equal("a fullscreen game", v.Reason);
    }

    [Fact]
    public void ABusyCardHoldsBackOnlyAFileThatWouldLoadAModel()
    {
        GateVerdict heavy = IndexGate.Decide(isDelete: false, usesModels: true, fullscreen: false, "",
                                             gpuBusy: true, "python is using 90% of the card");
        Assert.False(heavy.Run);
        Assert.Equal(IndexGate.GpuBusy, heavy.State);

        // Pulling words out of a document is not graphics work, and waiting on a card it never
        // touches would stop the one part of indexing that does not need one.
        Assert.True(IndexGate.Decide(isDelete: false, usesModels: false, fullscreen: false, "",
                                     gpuBusy: true, "python is using 90% of the card").Run);
    }

    [Fact]
    public void NothingInTheWayIsAGo()
        => Assert.True(IndexGate.Decide(false, true, false, "", false, "").Run);

    [Fact]
    public void ADocumentUsesAModelOnlyWhenMeaningIsInstalled()
    {
        Assert.False(IndexGate.UsesModels(ResultKind.Document, WordsOnly));
        Assert.True(IndexGate.UsesModels(ResultKind.Document, Meaning));
        Assert.True(IndexGate.UsesModels(ResultKind.Photo, WordsOnly));
        Assert.True(IndexGate.UsesModels(ResultKind.Audio, WordsOnly));
        Assert.True(IndexGate.UsesModels(ResultKind.Video, WordsOnly));
    }

    private const long GB = 1L << 30;

    [Fact]
    public void AFullCardIsBusyWhenWhatFindraWouldLoadNoLongerFits()
        // A 16 GB card with 10.9 GB held by a local language model, and the whole model set
        // installed: loading it would squeeze the other program off its own card.
        => Assert.True(IndexGate.MemoryBusy(16 * GB, (long)(10.9 * GB), (long)(3.7 * GB) + IndexGate.Margin));

    [Fact]
    public void AnOrdinaryDesktopOnASmallCardIsNeverCalledBusy()
        // Less free than the model set needs, but nothing else is really there. Holding back here
        // would mean a modest card never reads anything at all.
        => Assert.False(IndexGate.MemoryBusy(4 * GB, (long)(1.2 * GB), (long)(3.7 * GB) + IndexGate.Margin));

    [Fact]
    public void ACardThatCouldNotBeMeasuredIsNotBusy()
        => Assert.False(IndexGate.MemoryBusy(0, 12 * GB, 6 * GB));

    [Fact]
    public void TheCardIsBusyAtOnceAndFreeOnlyAfterAWholeQuietMinute()
    {
        var hold = new GpuHold();
        Assert.False(hold.Update(false, "", 0).Busy);

        var first = hold.Update(true, "python is using 90% of the card", 10);
        Assert.True(first.Busy);
        Assert.True(first.Changed);

        // A pipeline pausing between steps: free for a few seconds is not free.
        Assert.True(hold.Update(false, "", 12).Busy);
        Assert.True(hold.Update(false, "", 12 + IndexGate.ClearSeconds - 1).Busy);

        // A busy reading inside the window starts the minute again.
        Assert.True(hold.Update(true, "python is using 90% of the card", 70).Busy);
        Assert.True(hold.Update(false, "", 72).Busy);
        var still = hold.Update(false, "", 72 + IndexGate.ClearSeconds - 1);
        Assert.True(still.Busy);
        Assert.Equal("python is using 90% of the card", still.Reason);

        var clear = hold.Update(false, "", 72 + IndexGate.ClearSeconds);
        Assert.False(clear.Busy);
        Assert.True(clear.Changed);
    }

    [Fact]
    public void AHeldBackFileIsNotOpenedNotCountedAndTheModelsAreLetGo()
    {
        Directory.CreateDirectory(_dir);
        string song = Path.Combine(_dir, "ep101.wav");
        File.WriteAllBytes(song, new byte[64]);

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        db.Enqueue("C", 1, song, ResultKind.Audio, "journal");
        var d = new UnloadCounting(new CapabilitySet(new HashSet<Capability> { Capability.Speech, Capability.Meaning }));

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: d,
                     gate: (_, _) => new GateVerdict(false, IndexGate.GpuBusy, "python is using 90% of the card"));

        Assert.Equal(0, d.DecodeCalls);
        Assert.True(d.UnloadCalls > 0);
        Assert.Equal(IndexGate.GpuBusy, db.Get("indexer:state"));
        Assert.Equal("python is using 90% of the card", db.Get("indexer:current"));
        // Still queued, and not a single attempt spent: an evening of somebody else's work must
        // not write off a healthy file.
        ContentDb.Pending? next = db.TakeNext();
        Assert.NotNull(next);
        Assert.Equal(0, next.Value.Attempts);
    }

    private static readonly GateVerdict CardBusy = new(false, IndexGate.GpuBusy, "python is using 90% of the card");

    private static void Indexed(ContentDb db, ulong frn, string path, ResultKind kind)
    {
        using var tx = db.Begin();
        db.Upsert("C", frn, path, kind, new FileInfo(path).LastWriteTimeUtc.Ticks, new FileInfo(path).Length,
                  ContentDb.StateIndexed, null, Array.Empty<ContentDb.Segment>(), tx);
        tx.Commit();
    }

    [Fact]
    public void ABusyCardDoesNotHoldBackARowThatOnlyNeedsItsDateCompared()
    {
        // Photos moved between folders arrive from the journal one row each, with bytes that did
        // not change. Settling one is a stat and a lookup; waiting on the card for it left the
        // whole batch sitting in the count for as long as somebody else's model was loaded.
        Directory.CreateDirectory(_dir);
        string photo = Path.Combine(_dir, "harbour.jpg");
        File.WriteAllBytes(photo, new byte[64]);

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        Indexed(db, 1, photo, ResultKind.Photo);
        db.Enqueue("C", 1, photo, ResultKind.Photo, "change");
        var d = new UnloadCounting(new CapabilitySet(new HashSet<Capability> { Capability.Photos }));

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: d, gate: (_, _) => CardBusy);

        Assert.Equal(0, d.DecodeCalls);
        Assert.Null(db.TakeNext());
    }

    [Fact]
    public void ABusyCardDoesNotHoldBackARowWhoseFileIsGone()
    {
        Directory.CreateDirectory(_dir);
        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        db.Enqueue("C", 1, Path.Combine(_dir, "moved-away.jpg"), ResultKind.Photo, "change");
        var d = new UnloadCounting(new CapabilitySet(new HashSet<Capability> { Capability.Photos }));

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: d, gate: (_, _) => CardBusy);

        Assert.Null(db.TakeNext());
    }

    [Fact]
    public void ABusyCardStillHoldsBackAFileThatHasToBeRead()
    {
        Directory.CreateDirectory(_dir);
        string photo = Path.Combine(_dir, "harbour.jpg");
        File.WriteAllBytes(photo, new byte[64]);

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        Indexed(db, 1, photo, ResultKind.Photo);
        File.SetLastWriteTimeUtc(photo, DateTime.UtcNow.AddMinutes(5));
        db.Enqueue("C", 1, photo, ResultKind.Photo, "change");
        // A recheck is a request to read again whatever the date says.
        string other = Path.Combine(_dir, "quay.jpg");
        File.WriteAllBytes(other, new byte[64]);
        Indexed(db, 2, other, ResultKind.Photo);
        db.Enqueue("C", 2, other, ResultKind.Photo, Indexer.Recheck);
        var d = new UnloadCounting(new CapabilitySet(new HashSet<Capability> { Capability.Photos }));

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: d, gate: (_, _) => CardBusy);

        Assert.Equal(0, d.DecodeCalls);
        Assert.Equal(2L, db.PendingCount());
        Assert.Equal(IndexGate.GpuBusy, db.Get("indexer:state"));
    }

    [Fact]
    public void FullscreenHoldsBackEvenARowThatOnlyNeedsItsDateCompared()
    {
        // A game may be loading from the same disk; a batch of stats is still disk work.
        Directory.CreateDirectory(_dir);
        string photo = Path.Combine(_dir, "harbour.jpg");
        File.WriteAllBytes(photo, new byte[64]);

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        Indexed(db, 1, photo, ResultKind.Photo);
        db.Enqueue("C", 1, photo, ResultKind.Photo, "change");
        var d = new UnloadCounting(new CapabilitySet(new HashSet<Capability> { Capability.Photos }));

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: d,
                     gate: (_, _) => new GateVerdict(false, IndexGate.Fullscreen, "a fullscreen game"));

        Assert.Equal(1L, db.PendingCount());
    }

    [Fact]
    public void APausedIndexerHoldsNoModels()
    {
        Directory.CreateDirectory(_dir);
        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        db.Set("index:paused", "1");
        var d = new UnloadCounting(CapabilitySet.None);

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: d);

        Assert.True(d.UnloadCalls > 0);
    }

    [Fact]
    public void TheWaitIsSaidOnThePillAndTheLineRatherThanLookingStuck()
    {
        IndexProgress pill = IndexStatus.Pill(true, nameof(ResultKind.Audio), 40, 60, alive: true, IndexGate.GpuBusy);
        Assert.True(pill.Show);
        Assert.Equal("waiting for the GPU", pill.Label);
        Assert.Equal("waiting: fullscreen",
                     IndexStatus.Pill(true, nameof(ResultKind.Audio), 40, 60, alive: true, IndexGate.Fullscreen).Label);
        Assert.Equal("indexing recordings",
                     IndexStatus.Pill(true, nameof(ResultKind.Audio), 40, 60, alive: true, "indexing").Label);

        Assert.Equal("40 waiting - another program is using the graphics card",
                     IndexStatus.Line(true, IndexGate.GpuBusy, 40, 60, alive: true, rebuilt: false));
        Assert.Equal("40 waiting - something is fullscreen",
                     IndexStatus.Line(true, IndexGate.Fullscreen, 40, 60, alive: true, rebuilt: false));
    }

    private sealed class UnloadCounting(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public int DecodeCalls, UnloadCalls;
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes) { DecodeCalls++; return new KindResult([], null); }
        public KindResult DecodeFrames(string path) { DecodeCalls++; return new KindResult([], null); }
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Unload() => UnloadCalls++;
        public void Dispose() { }
    }
}
