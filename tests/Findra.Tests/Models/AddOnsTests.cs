using Findra;
using Xunit;

/// <summary>
/// Turning an add-on off, removing it, and catching up when it comes back. The rules are pure
/// where they can be; the catch-up runs against a real index, because "exactly the files read
/// while it was away" is a question only the index can answer.
/// </summary>
public sealed class AddOnsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-addons-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CapabilitySet Have(params Capability[] c) => new(new HashSet<Capability>(c));

    [Fact]
    public void TurningSpeechOffTurnsHebrewOffWithIt()
    {
        CapabilitySet all = Have(Capability.Photos, Capability.Meaning, Capability.Speech, Capability.Hebrew);
        CapabilitySet reading = all.Without([Capability.Speech]);
        Assert.False(reading.Has(Capability.Speech));
        Assert.False(reading.Has(Capability.Hebrew));
        Assert.True(reading.Has(Capability.Photos));
        Assert.True(reading.Has(Capability.Meaning));
    }

    [Fact]
    public void TurningMeaningOffLeavesSpeechReading()
    {
        // Speech embeds its transcripts with the same files, but "find documents by meaning" is a
        // choice about documents; it does not stop recordings being heard.
        CapabilitySet reading = Have(Capability.Meaning, Capability.Speech).Without([Capability.Meaning]);
        Assert.False(reading.Has(Capability.Meaning));
        Assert.True(reading.Has(Capability.Speech));
    }

    [Fact]
    public void TheOffListRoundTripsThroughTheIndexAndIgnoresWhatItCannotRead()
    {
        string row = AddOns.Format([Capability.Photos, Capability.Speech]);
        Assert.Equal([Capability.Photos, Capability.Speech], AddOns.Parse(row));
        Assert.Empty(AddOns.Parse(null));
        Assert.Empty(AddOns.Parse(""));
        Assert.Equal([Capability.Photos], AddOns.Parse("Photos,Nonsense,"));
    }

    [Fact]
    public void RemovingSpeechTakesHebrewWithIt()
    {
        CapabilitySet all = Have(Capability.Photos, Capability.Meaning, Capability.Speech, Capability.Hebrew);
        Assert.Equal(new HashSet<Capability> { Capability.Speech, Capability.Hebrew }, AddOns.RemovedWith(Capability.Speech, all));
        Assert.Equal(new HashSet<Capability> { Capability.Photos }, AddOns.RemovedWith(Capability.Photos, all));
    }

    [Fact]
    public void RemovingMeaningWhileSpeechNeedsItsFilesDeletesNothing()
    {
        CapabilitySet all = Have(Capability.Meaning, Capability.Speech);
        Assert.Empty(AddOns.FilesToDelete(Capability.Meaning, all));
        Assert.True(AddOns.OnlyTurnsOff(Capability.Meaning, all));
    }

    [Fact]
    public void RemovingSpeechDeletesItsOwnFilesAndHebrewsButNotTheMeaningPair()
    {
        CapabilitySet all = Have(Capability.Meaning, Capability.Speech, Capability.Hebrew);
        IReadOnlyList<string> files = AddOns.FilesToDelete(Capability.Speech, all);
        Assert.Contains(ModelStore.WhisperTurbo.File, files);
        Assert.Contains(ModelStore.WhisperHebrew.File, files);
        Assert.DoesNotContain(ModelStore.E5Base.File, files);
        Assert.False(AddOns.OnlyTurnsOff(Capability.Speech, all));
    }

    [Fact]
    public void RemovingMeaningAloneDeletesTheMeaningPair()
    {
        IReadOnlyList<string> files = AddOns.FilesToDelete(Capability.Meaning, Have(Capability.Meaning, Capability.Photos));
        Assert.Equal(new[] { ModelStore.E5Base.File, ModelStore.E5Spm.File }.Order(), files.Order());
    }

    [Fact]
    public void WhatRemovingFreesIsTheSizeOfWhatIsDeleted()
    {
        CapabilitySet all = Have(Capability.Meaning, Capability.Speech, Capability.Hebrew);
        Assert.Equal(ModelStore.WhisperTurbo.Bytes + ModelStore.WhisperHebrew.Bytes, AddOns.Frees(Capability.Speech, all));
        Assert.Equal(0, AddOns.Frees(Capability.Meaning, all));
    }

    [Fact]
    public void ComingBackReReadsExactlyWhatWasReadWhileItWasAway()
    {
        Directory.CreateDirectory(_dir);
        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        long away = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Read(db, 1, @"C:\before.jpg", ResultKind.Photo, at: away - 100);
        AddOns.NoteAway(db, Capability.Photos, away);
        // Read while Photos was off: stored, but nothing in it was looked at.
        Read(db, 2, @"C:\during.jpg", ResultKind.Photo, at: away + 5);
        Read(db, 3, @"C:\during.txt", ResultKind.Document, at: away + 5);

        int n = AddOns.CatchUp(db, Have(Capability.Photos));

        Assert.Equal(1, n);
        ContentDb.Pending? next = db.TakeNext();
        Assert.NotNull(next);
        Assert.Equal(2UL, next.Value.Frn);
        Assert.Equal(Indexer.Recheck, next.Value.Reason);
        // Done once: a second launch owes nothing.
        db.Dequeue(next.Value.Id);
        Assert.Equal(0, AddOns.CatchUp(db, Have(Capability.Photos)));
    }

    [Fact]
    public void NothingIsCaughtUpWhileTheAddOnIsStillAway()
    {
        Directory.CreateDirectory(_dir);
        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        long away = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AddOns.NoteAway(db, Capability.Photos, away);
        Read(db, 2, @"C:\during.jpg", ResultKind.Photo, at: away + 5);

        Assert.Equal(0, AddOns.CatchUp(db, Have(Capability.Meaning)));
        Assert.Equal(1, AddOns.CatchUp(db, Have(Capability.Photos)));
    }

    [Fact]
    public void GoingAwayTwiceKeepsTheEarlierTime()
    {
        Directory.CreateDirectory(_dir);
        using var db = new ContentDb(Path.Combine(_dir, "search.db"));
        AddOns.NoteAway(db, Capability.Photos, 100);
        AddOns.NoteAway(db, Capability.Photos, 200);
        Read(db, 2, @"C:\between.jpg", ResultKind.Photo, at: 150);
        Assert.Equal(1, AddOns.CatchUp(db, Have(Capability.Photos)));
    }

    [Fact]
    public void ForgettingRereadsWhatTheAddOnFoundSoItsFindingsAreDropped()
    {
        Assert.Contains(AddOns.Forget(Capability.Photos), f => f.Kinds.SequenceEqual([(int)ResultKind.Photo]) && f.Reason == Indexer.Recheck);
        // A video's pictures go and what was heard in it stays.
        Assert.Contains(AddOns.Forget(Capability.Photos), f => f.Kinds.SequenceEqual([(int)ResultKind.Video]) && f.Reason == Indexer.Reframe);
        Assert.Contains(AddOns.Forget(Capability.Meaning), f => f.Kinds.SequenceEqual([(int)ResultKind.Document]));
        Assert.Contains(AddOns.Forget(Capability.Speech), f => f.Kinds.Contains((int)ResultKind.Audio));
        // The Hebrew pass leaves ordinary transcripts; there is nothing of its own to forget.
        Assert.Empty(AddOns.Forget(Capability.Hebrew));
    }

    private static void Read(ContentDb db, ulong frn, string path, ResultKind kind, long? at = null)
    {
        using (var tx = db.Begin())
        {
            db.Upsert("C", frn, path, kind, 1, 1, ContentDb.StateIndexed, null, Array.Empty<ContentDb.Segment>(), tx);
            tx.Commit();
        }
        if (at is { } t) db.SetReadAt("C", frn, t);
    }
}
