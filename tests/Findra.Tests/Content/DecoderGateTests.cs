using System.Reflection;
using Findra;
using Xunit;

public class DecoderGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-gate-" + Guid.NewGuid().ToString("N"));

    public DecoderGateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ContentDb Open() => new(Path.Combine(_dir, "search.db"));

    private string File_(string name, string text = "the quarterly lease agreement and its deposit")
    {
        string p = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    /// <summary>
    /// One fake, and it answers <c>CanRead</c> with the SAME static rule the real Decoders uses.
    ///
    /// <para>That is what makes "the decoder was never asked" an assertion rather than a
    /// restatement: a mutation of <see cref="Decoders.Covers"/> changes what this fake says, so
    /// a gate that is unconditional in either direction shows up as a call count that is wrong
    /// in that direction. A fake with its own opinion about gating would test the fake.</para>
    ///
    /// <para>It answers <c>Decode</c> with one segment carrying a real-looking vector row, so the
    /// rows an upsert or a delete hands back are visible in <see cref="Released"/>.</para>
    /// </summary>
    private sealed class Fake(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public List<(ResultKind Kind, string Path)> Asked { get; } = [];
        public List<long> Released { get; } = [];
        public int Flushes;
        public long NextRow = 100;

        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);

        public KindResult Decode(ResultKind kind, string path, long bytes)
        {
            Asked.Add((kind, path));
            return new KindResult([new ContentDb.Segment(ContentDb.SegImage, -1, -1, NextRow++, "")], null);
        }

        public void Flush() => Flushes++;
        public void Release(IReadOnlyList<long> vectorRows) => Released.AddRange(vectorRows);
        public void Dispose() { }
    }

    // ---- the gate ----

    [Fact]
    public void APhotoIsOfferedToTheDecoderOnlyWhenTheModelsForItAreThere()
    {
        // BOTH halves. Without the second, an implementation that skips every photo
        // unconditionally - which is exactly what this build does today - passes.
        string photo = File_("holiday.jpg");

        using (ContentDb db = Open())
        {
            db.Enqueue("C", 1, photo, ResultKind.Photo, "test");
            var without = new Fake(CapabilitySet.None);
            Indexer.DrainOnce(db, _ => { }, without);

            Assert.Empty(without.Asked);
            Assert.Equal(ContentDb.StateSkipped, db.StateOf("C", 1));
        }

        using (ContentDb db = Open())
        {
            db.Enqueue("C", 2, photo, ResultKind.Photo, "test");
            var with = new Fake(Set(Capability.Photos));
            Indexer.DrainOnce(db, _ => { }, with);

            Assert.Single(with.Asked);
            Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 2));
        }
    }

    [Fact]
    public void SpeechAndPicturesAreGatedSeparatelyAndNotTogether()
    {
        // The all-or-nothing gate this replaces failed both together. With photos installed and
        // speech not, a picture must index and a sound file must skip, in one drain.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("holiday.jpg"), ResultKind.Photo, "test");
        db.Enqueue("C", 2, File_("voice.m4a"), ResultKind.Audio, "test");

        var d = new Fake(Set(Capability.Photos));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal([ResultKind.Photo], d.Asked.Select(a => a.Kind).ToArray());
        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));
        Assert.Equal(ContentDb.StateSkipped, db.StateOf("C", 2));
    }

    [Fact]
    public void AVideoIsWorthOpeningForItsFramesOrForItsSoundAndNotOnlyForBoth()
    {
        // Video is the one kind two capabilities cover. A reverse lookup of "which capability
        // covers this kind" returns Photos and silently drops every video on a speech-only
        // machine; an AND drops them on both single-capability machines.
        Assert.True(Decoders.Covers(ResultKind.Video, Set(Capability.Photos)));
        Assert.True(Decoders.Covers(ResultKind.Video, Set(Capability.Speech, Capability.Meaning)));
        Assert.False(Decoders.Covers(ResultKind.Video, CapabilitySet.None));

        // and the gate the indexer consults says the same thing
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("clip.mp4"), ResultKind.Video, "test");
        var d = new Fake(Set(Capability.Speech, Capability.Meaning));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Single(d.Asked);
        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));
    }

    [Fact]
    public void AMissingCapabilityIsNeverAFailure()
    {
        // Spec §6: a missing model is a normal state, not an error state. A Failed row would put
        // the file in the failure sample of --searchindex, where nobody can act on it, and
        // RequeueKinds deliberately leaves Failed rows alone - so it would never be picked up
        // when the capability finally arrived.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("a.jpg"), ResultKind.Photo, "test");
        db.Enqueue("C", 2, File_("b.m4a"), ResultKind.Audio, "test");
        db.Enqueue("C", 3, File_("c.mp4"), ResultKind.Video, "test");

        Indexer.DrainOnce(db, _ => { }, new Fake(CapabilitySet.None));

        (long _, long _, long failed, long skipped) = db.Counts();
        Assert.Equal(0, failed);
        Assert.Equal(3, skipped);
        Assert.Empty(db.RecentFailures(10));
    }

    [Fact]
    public void AFileSkippedForWantOfAModelSaysThatIsWhy()
    {
        // The reason string is what CapabilityGate's exclusion list and --searchindex's models
        // section both key on. An empty reason, or one borrowed from the size gates, makes a
        // photo waiting for a download indistinguishable from a photo that is too big to read.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("a.jpg"), ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, new Fake(CapabilitySet.None));

        Assert.Equal(Decoders.NoModel, db.RecentSkips(10).Single().Error);
    }

    [Fact]
    public void WordsInDocumentsStillWorkWithNoModelAtAll()
    {
        // Free of charge, which is what makes declining every download a complete answer rather
        // than a broken one. (Free of CONSENT is a different question: nothing is read at all
        // until content indexing is turned on, and that is the queue's pause, not this gate.)
        // A gate that accidentally covers Document takes full-text search away from everybody
        // who declined the download.
        Assert.True(Decoders.Covers(ResultKind.Document, CapabilitySet.None));

        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("notes.txt"), ResultKind.Document, "test");

        // The real Decoders, with an empty model folder and a throwaway vector store - never
        // Decoders.ForThisMachine(), which opens a writer on the REAL index directory.
        using var vectors = new VectorStore(Path.Combine(_dir, "vectors.bin"), writer: true);
        using var real = new Decoders(() => CapabilitySet.Installed(_dir), vectors, modelDir: _dir);
        Indexer.DrainOnce(db, _ => { }, real);

        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));
        Assert.Single(db.Fts("deposit", 5));
    }

    [Fact]
    public void TheDecodersNoticeACapabilityThatArrivesWhileTheyAreRunning()
    {
        // What is installed is read through a delegate, exactly as the transcription limit is,
        // and for the same reason: both change while the child is running, and the child is
        // started once. Captured instead, a model installed while Findra is open reaches nothing
        // until the next launch - and the files queued for it in the meantime are drained by a
        // child that cannot read them, recorded skipped a second time, and written off, because
        // the record that the backlog was cleared has already been written.
        //
        // The same object throughout. A test that built a second Decoders would prove only that
        // a new child sees a new model, which was never in doubt.
        CapabilitySet have = CapabilitySet.None;
        using var vectors = new VectorStore(Path.Combine(_dir, "vectors.bin"), writer: true);
        using var d = new Decoders(() => have, vectors, modelDir: _dir);

        Assert.False(d.CanRead(ResultKind.Photo));
        Assert.False(d.Installed.Has(Capability.Photos));

        have = Set(Capability.Photos);

        Assert.True(d.CanRead(ResultKind.Photo));
        Assert.True(d.Installed.Has(Capability.Photos));
    }

    [Fact]
    public void ACapabilityThatGoesAwayIsNoticedTheSameWay()
    {
        // The other direction, which is not symmetry for its own sake: somebody who clears
        // %LOCALAPPDATA%\Findra\models by hand must stop having their photos opened, rather than
        // have every one of them recorded as a failure by a decoder reaching for a file that is
        // no longer there.
        CapabilitySet have = Set(Capability.Photos);
        using var vectors = new VectorStore(Path.Combine(_dir, "vectors.bin"), writer: true);
        using var d = new Decoders(() => have, vectors, modelDir: _dir);

        Assert.True(d.CanRead(ResultKind.Photo));

        have = CapabilitySet.None;

        Assert.False(d.CanRead(ResultKind.Photo));
    }

    // ---- the vector rows a replace or a delete hands back ----

    [Fact]
    public void AReplacedFilesOldVectorRowsAreReleased()
    {
        // Upsert hands back the vector rows the segments it replaced were pointing at. This
        // build discards that return (`_ = _db.Upsert(...)`, Indexer.cs:321), which was correct
        // while every segment carried -1 and is a leak the moment they carry a row: the old
        // embedding of an edited document keeps matching queries for ever, beside the new one.
        using ContentDb db = Open();
        string doc = File_("contract.txt");
        var d = new Fake(Set(Capability.Photos, Capability.Meaning));

        db.Enqueue("C", 1, doc, ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, d);
        Assert.Empty(d.Released);                        // nothing to release the first time

        db.Enqueue("C", 1, doc, ResultKind.Photo, Indexer.Recheck);
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal([100L], d.Released);                // the first pass's row, handed back
    }

    [Fact]
    public void ADeletedFilesVectorRowsAreReleasedToo()
    {
        // Delete hands back the same list, and it is discarded in the same place
        // (Indexer.cs:274). A photo deleted a year ago answering a query is the visible form.
        using ContentDb db = Open();
        var d = new Fake(Set(Capability.Photos));

        db.Enqueue("C", 1, File_("gone.jpg"), ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, d);
        d.Released.Clear();

        db.Enqueue("C", 1, Path.Combine(_dir, "gone.jpg"), ResultKind.Photo, ContentDb.ReasonDelete);
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal([100L], d.Released);
    }

    [Fact]
    public void AFileThatFailsWhileBeingReadAlsoReleasesTheRowsItHeld()
    {
        // The third discarded return (Indexer.cs:343), on the failure path. A file that indexed
        // once and later throws - a PDF replaced by a broken one - keeps its old vector rows for
        // ever, and nothing will ever tombstone them because the item now says Failed.
        using ContentDb db = Open();
        string doc = File_("contract.txt");
        var d = new Fake(Set(Capability.Photos));

        db.Enqueue("C", 1, doc, ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, d);
        d.Released.Clear();

        // A decoder that throws on the second pass, standing in for a malformed file.
        var boom = new ThrowingDecoders(Set(Capability.Photos), d.Released);
        db.Enqueue("C", 1, doc, ResultKind.Photo, Indexer.Recheck);
        Indexer.DrainOnce(db, _ => { }, boom);

        Assert.Equal(ContentDb.StateFailed, db.StateOf("C", 1));
        Assert.Equal([100L], d.Released);
    }

    private sealed class ThrowingDecoders(CapabilitySet installed, List<long> released) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes)
            => throw new InvalidDataException("the file is malformed");
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) => released.AddRange(rows);
        public void Dispose() { }
    }

    // ---- the two orderings ----

    [Fact]
    public void TheVectorStoreIsFlushedBeforeTheDatabaseCommitsAndReleasedAfter()
    {
        // Flush before commit: a database row pointing past the vector header's count is a
        // segment that silently never matches again, for ever. Release after commit: a rollback
        // that has already zeroed the old rows leaves the surviving segments pointing at
        // nothing. The two orderings run in opposite directions and both are load-bearing.
        //
        // The commit is OBSERVED rather than announced, and that needs no seam in the shipping
        // code: a second, read-only connection sees the last committed snapshot, so while the
        // indexer's transaction is open it still reports the row as pending, and the moment the
        // transaction commits it reports the queue as empty. Asking that question at each event
        // is what turns "before" and "after" into two assertions rather than a list of strings
        // whose order nothing enforces.
        //
        // The SECOND drain is the one measured. The first indexes the file so there is a vector
        // row to release; on a re-check the transaction is the only thing standing between a
        // pending row and an empty queue.
        using ContentDb db = Open();
        using var reader = new ContentDb(db.Path, readOnly: true);
        var d = new OrderRecordingDecoders(Set(Capability.Photos), () => reader.PendingCount() == 0);

        db.Enqueue("C", 1, File_("a.jpg"), ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, d);

        db.Enqueue("C", 1, Path.Combine(_dir, "a.jpg"), ResultKind.Photo, Indexer.Recheck);
        d.Reset();
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.True(d.Flushed, "the vector store was never flushed");
        Assert.True(d.ReleasedRows, "no vector row was handed back to be released");
        Assert.False(d.CommitHadHappenedAtFlush,
            "the vector store was flushed AFTER the commit that referenced its rows - a child that " +
            "dies in between leaves segments pointing past the header's count, for ever");
        Assert.True(d.CommitHadHappenedAtRelease,
            "vector rows were released INSIDE the transaction - a rollback then leaves the " +
            "surviving segments pointing at zeroed vectors");
    }

    /// <summary>
    /// Answers, at each of the two events, whether the indexer's transaction had already
    /// committed - through a <paramref name="committed"/> probe the test builds from a second
    /// read-only connection. Nothing in the shipping code is instrumented for this.
    /// </summary>
    private sealed class OrderRecordingDecoders(CapabilitySet installed, Func<bool> committed) : IDecoders
    {
        private long _next = 100;
        public CapabilitySet Installed { get; } = installed;
        public bool Flushed { get; private set; }
        public bool ReleasedRows { get; private set; }
        public bool CommitHadHappenedAtFlush { get; private set; }
        public bool CommitHadHappenedAtRelease { get; private set; }

        public void Reset()
        {
            Flushed = ReleasedRows = CommitHadHappenedAtFlush = CommitHadHappenedAtRelease = false;
        }

        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);

        public KindResult Decode(ResultKind kind, string path, long bytes)
            => new([new ContentDb.Segment(ContentDb.SegImage, -1, -1, _next++, "")], null);

        public void Flush()
        {
            Flushed = true;
            CommitHadHappenedAtFlush = committed();
        }

        public void Release(IReadOnlyList<long> rows)
        {
            if (rows.Count == 0) return;       // the first drain has nothing to hand back
            ReleasedRows = true;
            CommitHadHappenedAtRelease = committed();
        }

        public void Dispose() { }
    }

    // ---- speech, and what makes Hebrew a second pass ----

    [Fact]
    public void TheGeneralModelIsAlwaysTheFirstPassAndTheFineTuneIsOnlyEverTheSecond()
    {
        // Spec §6: turbo runs first for language detection and only the files it calls Hebrew
        // are re-run through the fine-tune. An implementation that loads the fine-tune INSTEAD
        // when Hebrew is installed transcribes every English file with a Hebrew model, and
        // nothing about the output would look wrong enough to notice.
        Assert.Equal(ModelStore.WhisperTurbo, Decoders.SpeechModels(Set(Capability.Speech, Capability.Meaning)).General);
        Assert.Null(Decoders.SpeechModels(Set(Capability.Speech, Capability.Meaning)).Hebrew);

        var both = Decoders.SpeechModels(new CapabilitySet(Presets.Everything));
        Assert.Equal(ModelStore.WhisperTurbo, both.General);
        Assert.Equal(ModelStore.WhisperHebrew, both.Hebrew);

        // there is no arrangement of capabilities in which the fine-tune is the first pass
        foreach (Capability[] set in new[]
        {
            new[] { Capability.Hebrew }, [Capability.Hebrew, Capability.Speech],
            [Capability.Speech], [Capability.Photos, Capability.Hebrew],
        })
            Assert.Equal(ModelStore.WhisperTurbo, Decoders.SpeechModels(new CapabilitySet(Capabilities.Close(set))).General);
    }

    // ---- indexed, with a note ----

    [Fact]
    public void ALongVideoWhoseFramesWereReadIsIndexedAndSaysWhatItDidNotHear()
    {
        // The row Task 11's limit re-queue is built on, produced by the real path rather than
        // written by hand. Skip decides the state and Note does not: deriving the state from
        // "is there a reason at all" - which is what the indexer did before KindResult grew a
        // third field - marks every film whose frames were read as SKIPPED, and --searchindex
        // then reports a whole video library as unread.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("film.mp4"), ResultKind.Video, "test");

        var d = new NotingFake(Set(Capability.Photos, Capability.Speech, Capability.Meaning));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));   // its frames were read
        Assert.Equal(1, db.CountRecorded(Decoders.TooLong));         // and the note says what was not
        Assert.Empty(db.RecentSkips(10));
    }

    [Fact]
    public void AFileWithNothingReadAtAllIsStillSkipped()
    {
        // The control. Note must not become a way for everything to report itself indexed: a
        // decoder that read nothing sets Skip, and Skip still decides the state.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("film.mp4"), ResultKind.Video, "test");

        var d = new SkippingFake(Set(Capability.Photos));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal(ContentDb.StateSkipped, db.StateOf("C", 1));
        Assert.Equal(Decoders.TooLong, db.RecentSkips(10).Single().Error);
    }

    /// <summary>Reads something and leaves something else undone - the long-video shape.</summary>
    private sealed class NotingFake(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes)
            => new([new ContentDb.Segment(ContentDb.SegFrame, 0, 0, 100, "")],
                   Skip: null, Note: Decoders.TooLong);
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Dispose() { }
    }

    /// <summary>Reads nothing, for the same reason.</summary>
    private sealed class SkippingFake(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes)
            => new([], Decoders.TooLong);
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Dispose() { }
    }

    // ---- how long a recording is worth transcribing ----

    [Fact]
    public void ARecordingLongerThanTheLimitIsSkippedForAReasonOfItsOwn()
    {
        // Not TooLarge, and not silence. This is the fifth meaning of StateSkipped and the only
        // one a user can change from a settings control, so raising the limit later has to
        // re-queue exactly these files - which it can only do if the reason is exact.
        Assert.NotEqual(Decoders.TooLarge, Decoders.TooLong);
        Assert.NotEqual(Decoders.NoModel, Decoders.TooLong);
        Assert.NotEqual(Decoders.NoText, Decoders.TooLong);
        Assert.NotEqual(Decoders.NoFormatReader, Decoders.TooLong);
        Assert.NotEqual(Decoders.AnIcon, Decoders.TooLong);
        Assert.Contains("long", Decoders.TooLong, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPerKindRecordingConstantSurvivesBesideTheSetting()
    {
        // An hour for audio and three minutes for video is a rule hidden in two constants, and
        // the setting exists to replace it (spec §6). Keeping either alongside the setting
        // re-introduces an asymmetry nobody can see: a video and a sound file of the same length
        // would behave differently for no reason the interface could show.
        string[] fields = [.. typeof(Decoders)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Select(f => f.Name)];

        Assert.DoesNotContain("MaxAudioSeconds", fields);
        Assert.DoesNotContain("MaxVideoSpeechSeconds", fields);
        // and the one number that replaced them is where the rule lives
        Assert.Contains("MaxDecodeSeconds", fields);          // a memory bound, not a policy one
        Assert.Equal(5, TranscribeLimit.Default);
    }

    // ---- the size gates, applied ----

    [Theory]
    [InlineData(ResultKind.Photo, 1_000, "an icon, not a picture")]
    [InlineData(ResultKind.Photo, 200L << 20, "too large")]
    [InlineData(ResultKind.Photo, 2L << 20, null)]
    [InlineData(ResultKind.Document, 300L << 20, "too large")]
    [InlineData(ResultKind.Document, 4_000, null)]
    [InlineData(ResultKind.Video, 10L << 30, "too large")]
    [InlineData(ResultKind.Video, 100L << 20, null)]
    public void ASizeGateIsAppliedAndNotMerelyDeclared(ResultKind kind, long bytes, string? skip)
    {
        // Asserting the constants against each other only proves arithmetic between four
        // literals in one file, and a port that keeps the constants and stops USING them passes
        // it. This asks the function the decode arms actually call. Below the icon floor every
        // favicon on the disk matches every query a little, which is worse than not being there.
        Assert.Equal(skip, Decoders.SizeGate(kind, bytes));
    }
}
