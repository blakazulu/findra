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
    public void FindrasOwnCardIsNotAFullscreenAppToWaitFor()
    {
        // The card dims the whole monitor behind it, and Windows answers "a fullscreen app" for
        // as long as it is up. Measured on a real session: every opening of the card paused
        // reading a second later and made the indexer let go of its models.
        Assert.False(IndexGate.Holds(2, foregroundIsFindra: true).Hold);
        Assert.False(IndexGate.Holds(3, foregroundIsFindra: true).Hold);
        Assert.False(IndexGate.Holds(4, foregroundIsFindra: true).Hold);
        // Somebody else's fullscreen window still holds.
        Assert.Equal((true, "a fullscreen app"), IndexGate.Holds(2, foregroundIsFindra: false));
    }

    [Fact]
    public void OnlyAFullscreenAppAGameOrAPresentationHoldsReadingBack()
    {
        Assert.Equal((true, "a fullscreen app"), IndexGate.Holds(2));
        Assert.Equal((true, "a fullscreen game"), IndexGate.Holds(3));
        Assert.Equal((true, "presentation mode"), IndexGate.Holds(4));
        // Quiet time is the first hour after a first sign-in on a fresh install - exactly when a
        // new machine's owner installs Findra - and a locked screen is the best time to read.
        Assert.False(IndexGate.Holds(6).Hold);
        Assert.False(IndexGate.Holds(1).Hold);
        Assert.False(IndexGate.Holds(5).Hold);
        Assert.False(IndexGate.Holds(7).Hold);
    }

    [Fact]
    public void ACardTooFullForTheModelsSendsPicturesAndMeaningToTheProcessorRatherThanWaiting()
    {
        // A 3 GB card whose ordinary desktop holds 2 GB of it: waiting for room that never comes
        // was the whole afternoon. Nothing Findra does on the processor touches the card.
        GateVerdict photo = IndexGate.Decide(isDelete: false, usesModels: true, usesSpeech: false,
                                             fullscreen: false, "", GpuPressure.Memory, "other programs hold 2.04 GB");
        Assert.True(photo.Run);
        Assert.Equal(IndexGate.OnProcessor, photo.State);
        Assert.Equal("other programs hold 2.04 GB", photo.Reason);
    }

    [Fact]
    public void SpeechStillWaitsForRoomOnTheCardBecauseItCannotMoveToTheProcessorMidSession()
    {
        GateVerdict v = IndexGate.Decide(isDelete: false, usesModels: true, usesSpeech: true,
                                         fullscreen: false, "", GpuPressure.Memory, "other programs hold 2.04 GB");
        Assert.False(v.Run);
        Assert.Equal(IndexGate.GpuBusy, v.State);
    }

    [Fact]
    public void ACardSomebodyIsWorkingStillMakesEverythingThatNeedsItWait()
    {
        GateVerdict v = IndexGate.Decide(isDelete: false, usesModels: true, usesSpeech: false,
                                         fullscreen: false, "", GpuPressure.Engine, "game is using 90% of the card");
        Assert.False(v.Run);
        Assert.Equal(IndexGate.GpuBusy, v.State);
        Assert.True(IndexGate.Decide(false, usesModels: false, usesSpeech: false, false, "", GpuPressure.Engine, "x").Run);
        Assert.False(IndexGate.Decide(false, true, false, fullscreen: true, "a fullscreen game", GpuPressure.Memory, "x").Run);
    }

    [Fact]
    public void OnlyTheSpeechModelMakesAFileASpeechFile()
    {
        var speech = new CapabilitySet(new HashSet<Capability> { Capability.Speech, Capability.Meaning, Capability.Photos });
        var photos = new CapabilitySet(new HashSet<Capability> { Capability.Photos });
        Assert.True(IndexGate.UsesSpeech(ResultKind.Audio, speech));
        Assert.True(IndexGate.UsesSpeech(ResultKind.Video, speech));
        Assert.False(IndexGate.UsesSpeech(ResultKind.Video, photos));
        Assert.False(IndexGate.UsesSpeech(ResultKind.Photo, speech));
        Assert.False(IndexGate.UsesSpeech(ResultKind.Document, speech));
    }

    [Fact]
    public void TheHoldRemembersWhetherTheCardWasFullOrWorked()
    {
        var hold = new GpuHold();
        hold.Update(true, "other programs hold 2 GB", 0, GpuPressure.Memory);
        Assert.Equal(GpuPressure.Memory, hold.Pressure);
        // Somebody starts a game: working the card outranks a full one.
        hold.Update(true, "game is using 90% of the card", 5, GpuPressure.Engine);
        Assert.Equal(GpuPressure.Engine, hold.Pressure);
        // The game closes and the card is merely full again: a minute of that before the hold
        // steps down, or a full card would keep everything waiting for good.
        hold.Update(true, "other programs hold 2 GB", 10, GpuPressure.Memory);
        Assert.Equal(GpuPressure.Engine, hold.Pressure);
        var down = hold.Update(true, "other programs hold 2 GB", 10 + IndexGate.ClearSeconds, GpuPressure.Memory);
        Assert.Equal(GpuPressure.Memory, hold.Pressure);
        Assert.True(down.Changed);
        Assert.Equal("other programs hold 2 GB", down.Reason);
        // Free for less than the minute: still held, still for the same reason.
        hold.Update(false, "", 100);
        Assert.Equal(GpuPressure.Memory, hold.Pressure);
        hold.Update(false, "", 100 + IndexGate.ClearSeconds);
        Assert.Equal(GpuPressure.None, hold.Pressure);
    }

    [Fact]
    public void TheIndexerReadsOnTheProcessorWhileTheGateSaysSoAndGoesBackWhenItDoesNot()
    {
        Directory.CreateDirectory(_dir);
        string a = Path.Combine(_dir, "harbour.jpg"), b = Path.Combine(_dir, "quay.jpg");
        File.WriteAllBytes(a, new byte[64]);
        File.WriteAllBytes(b, new byte[64]);

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        db.Enqueue("C", 1, a, ResultKind.Photo, "new");
        db.Enqueue("C", 2, b, ResultKind.Photo, "new");
        var d = new UnloadCounting(new CapabilitySet(new HashSet<Capability> { Capability.Photos }));

        int passes = 0, asked = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 2, decoders: d,
                     gate: (_, _) => asked++ == 0
                         ? new GateVerdict(true, IndexGate.OnProcessor, "other programs hold 2.04 GB")
                         : GateVerdict.Go);

        Assert.Equal(2, d.DecodeCalls);
        Assert.Equal(new[] { true, false }, d.OnProcessorCalls);
        Assert.Equal(0L, db.PendingCount());
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
    public void APhotoHeldBackForTheCardDoesNotHoldBackTheDocumentsBehindIt()
    {
        // The queue is taken in order, and one photo at the front used to stop every document
        // behind it for as long as somebody else's model sat on the card - reading words out of a
        // document never touches the card at all.
        Directory.CreateDirectory(_dir);
        string photo = Path.Combine(_dir, "harbour.jpg");
        File.WriteAllBytes(photo, new byte[64]);
        string doc = Path.Combine(_dir, "minutes.txt");
        File.WriteAllText(doc, "the harbour board met on tuesday");

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        db.Enqueue("C", 1, photo, ResultKind.Photo, "new");
        db.Enqueue("C", 2, doc, ResultKind.Document, "new");
        var d = new UnloadCounting(new CapabilitySet(new HashSet<Capability> { Capability.Photos }));

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: d,
                     gate: (_, kind) => kind == ResultKind.Photo ? CardBusy : GateVerdict.Go);

        Assert.Equal(new[] { ResultKind.Document }, d.Decoded);
        ContentDb.Pending? left = db.TakeNext();
        Assert.NotNull(left);
        Assert.Equal(ResultKind.Photo, left.Value.Kind);
        Assert.Equal(1L, db.PendingCount());
    }

    [Fact]
    public void WhenEveryKindQueuedIsHeldBackTheIndexerWaitsAndSaysSo()
    {
        Directory.CreateDirectory(_dir);
        string photo = Path.Combine(_dir, "harbour.jpg");
        File.WriteAllBytes(photo, new byte[64]);
        string clip = Path.Combine(_dir, "quay.mp4");
        File.WriteAllBytes(clip, new byte[64]);

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        db.Enqueue("C", 1, photo, ResultKind.Photo, "new");
        db.Enqueue("C", 2, clip, ResultKind.Video, "new");
        var d = new UnloadCounting(new CapabilitySet(new HashSet<Capability> { Capability.Photos }));

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: d,
                     gate: (_, kind) => kind == ResultKind.Document ? GateVerdict.Go : CardBusy);

        Assert.Equal(0, d.DecodeCalls);
        Assert.Equal(2L, db.PendingCount());
        Assert.Equal(IndexGate.GpuBusy, db.Get("indexer:state"));
    }

    [Fact]
    public void FullscreenHoldsBackTheDocumentsBehindAPhotoToo()
    {
        Directory.CreateDirectory(_dir);
        string photo = Path.Combine(_dir, "harbour.jpg");
        File.WriteAllBytes(photo, new byte[64]);
        string doc = Path.Combine(_dir, "minutes.txt");
        File.WriteAllText(doc, "the harbour board met on tuesday");

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        db.Enqueue("C", 1, photo, ResultKind.Photo, "new");
        db.Enqueue("C", 2, doc, ResultKind.Document, "new");
        var d = new UnloadCounting(new CapabilitySet(new HashSet<Capability> { Capability.Photos }));

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: d,
                     gate: (_, _) => new GateVerdict(false, IndexGate.Fullscreen, "a fullscreen game"));

        Assert.Equal(0, d.DecodeCalls);
        Assert.Equal(2L, db.PendingCount());
    }

    [Fact]
    public void TheNextRowOfAKindSkipsEveryOtherKindAndKeepsQueueOrder()
    {
        Directory.CreateDirectory(_dir);
        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        db.Enqueue("C", 1, @"C:\a.jpg", ResultKind.Photo, "new");
        db.Enqueue("C", 2, @"C:\b.txt", ResultKind.Document, "new");
        db.Enqueue("C", 3, @"C:\c.mp4", ResultKind.Video, "new");
        db.Enqueue("C", 4, @"C:\d.txt", ResultKind.Document, "new");

        Assert.Equal(2UL, db.TakeNextOf([ResultKind.Document, ResultKind.Video])!.Value.Frn);
        Assert.Equal(3UL, db.TakeNextOf([ResultKind.Video])!.Value.Frn);
        Assert.Null(db.TakeNextOf([ResultKind.Audio]));
        Assert.Null(db.TakeNextOf([]));
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
        Assert.Equal("paused · another app is busy", pill.Label);
        Assert.Equal("paused · full-screen app",
                     IndexStatus.Pill(true, nameof(ResultKind.Audio), 40, 60, alive: true, IndexGate.Fullscreen).Label);
        Assert.Equal("reading recordings",
                     IndexStatus.Pill(true, nameof(ResultKind.Audio), 40, 60, alive: true, "indexing").Label);

        Assert.Equal("paused while another app is busy · 40 files to go",
                     IndexStatus.Line(true, IndexGate.GpuBusy, 40, 60, alive: true, rebuilt: false));
        Assert.Equal("paused while you're in a full-screen app · 40 files to go",
                     IndexStatus.Line(true, IndexGate.Fullscreen, 40, 60, alive: true, rebuilt: false));
    }

    private sealed class UnloadCounting(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public int DecodeCalls, UnloadCalls;
        public readonly List<ResultKind> Decoded = [];
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes) { DecodeCalls++; Decoded.Add(kind); return new KindResult([], null); }
        public KindResult DecodeFrames(string path) { DecodeCalls++; return new KindResult([], null); }
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Unload() => UnloadCalls++;
        public readonly List<bool> OnProcessorCalls = [];
        private bool? _onProcessor;
        public void OnProcessor(bool yes, string why)
        {
            if (_onProcessor == yes) return;
            _onProcessor = yes;
            OnProcessorCalls.Add(yes);
        }
        public void Dispose() { }
    }
}
