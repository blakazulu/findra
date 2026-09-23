using Findra;
using Xunit;

/// <summary>
/// A card too full for the models, end to end on the real models: the gate says "processor", the
/// real decoders open the picture model there, and the photo comes out read with a vector. Skipped
/// on a machine without the picture model, like the other tests that need one.
/// </summary>
public sealed class ProcessorFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-cpu-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void APhotoIsReadOnTheProcessorWhileTheCardIsTooFull()
    {
        if (!File.Exists(ModelStore.PathOf(ModelStore.Siglip2Vision))) return;
        string shot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "shots", "results.png");
        if (!File.Exists(shot)) return;

        Directory.CreateDirectory(_dir);
        string photo = Path.Combine(_dir, "results.png");
        File.Copy(shot, photo);

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        using var vectors = new VectorStore(Path.Combine(_dir, "vectors.bin"), writer: true);
        var photos = new CapabilitySet(new HashSet<Capability> { Capability.Photos });
        using var decoders = new Decoders(() => photos, vectors);
        db.Enqueue("C", 1, photo, ResultKind.Photo, "new");

        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 2, decoders: decoders,
                     gate: (_, _) => new GateVerdict(true, IndexGate.OnProcessor, "other programs hold 2.04 GB of the card's 2.9 GB"));

        Assert.Equal(0L, db.PendingCount());
        Assert.Equal(1L, db.IndexedCount());
        Assert.Equal("other programs hold 2.04 GB of the card's 2.9 GB", db.Get("indexer:processor"));
        // A vector was written: the picture was looked at, not merely stored.
        ContentDb.ItemRow item = db.ItemByPath(photo) ?? throw new InvalidOperationException("the photo is not in the index");
        Assert.Contains(db.SegmentsOf(item.Id), seg => seg.Vec >= 0);
    }

    [Fact]
    public void APhotoLeftOutWhilePhotosWasOffIsReadWhenItIsTurnedBackOn()
    {
        // The whole off-and-on cycle through the real indexer and the real picture model: the off
        // list reaches the reader through the index, as it does from Settings, and turning it back
        // on catches up exactly the file it missed.
        if (!File.Exists(ModelStore.PathOf(ModelStore.Siglip2Vision))) return;
        string shot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "shots", "results.png");
        if (!File.Exists(shot)) return;

        Directory.CreateDirectory(_dir);
        string photo = Path.Combine(_dir, "results.png");
        File.Copy(shot, photo);

        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        using var vectors = new VectorStore(Path.Combine(_dir, "vectors.bin"), writer: true);
        var photos = new CapabilitySet(new HashSet<Capability> { Capability.Photos });
        using var decoders = new Decoders(() => photos.Without(AddOns.Parse(db.Get(AddOns.OffKey))), vectors);

        // Off.
        AddOns.NoteAway(db, Capability.Photos, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
        db.Set(AddOns.OffKey, AddOns.Format([Capability.Photos]));
        db.Enqueue("C", 1, photo, ResultKind.Photo, "new");
        int passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: decoders);
        ContentDb.ItemRow left = db.ItemByPath(photo) ?? throw new InvalidOperationException("not recorded");
        Assert.Equal(ContentDb.StateSkipped, left.State);

        // On again.
        db.Set(AddOns.OffKey, "");
        Assert.Equal(1, AddOns.CatchUp(db, photos));
        passes = 0;
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 1, decoders: decoders);
        ContentDb.ItemRow read = db.ItemByPath(photo) ?? throw new InvalidOperationException("not recorded");
        Assert.Equal(ContentDb.StateIndexed, read.State);
        Assert.Contains(db.SegmentsOf(read.Id), seg => seg.Vec >= 0);
    }
}
