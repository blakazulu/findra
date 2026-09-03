using Findra;
using Xunit;

public class CapabilityGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-gate5-" + Guid.NewGuid().ToString("N"));

    public CapabilityGateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ContentDb Open() => new(Path.Combine(_dir, "search.db"));
    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    /// <summary>Stamps that say "these capabilities have already had their backlog cleared, at
    /// the current version of their family".</summary>
    private static Dictionary<Capability, string> Done(params Capability[] caps)
    {
        var d = new Dictionary<Capability, string>();
        foreach (Capability c in caps) d[c] = CapabilityGate.StampFor(c);
        return d;
    }

    /// <summary>
    /// One item in the index. <paramref name="mtime"/> defaults to 0, which is fine for every test
    /// here that only inspects a plan or a queue - and is WRONG for any test that drains.
    ///
    /// <para><see cref="ContentDb.IsCurrent"/> compares the stored mtime against the file's real
    /// one, so a stored 0 makes the freshness check false and the indexer opens the row whatever
    /// its reason says. A draining test built on the default would pass with a free-text reason
    /// and prove nothing at all - which is exactly what this file's most important test did in an
    /// earlier draft. If you drain, pass <c>new FileInfo(path).LastWriteTimeUtc.Ticks</c>.</para>
    /// </summary>
    private static void Item(ContentDb db, ulong frn, ResultKind kind, int state, string? error, string path,
                             long mtime = 0)
    {
        using var tx = db.Begin();
        db.Upsert("C", frn, path, kind, mtime, 10, state, error, [], tx);
        tx.Commit();
    }

    private string File_(string name, string text = "the tenant shall pay monthly in advance")
    {
        string p = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    // ---- the reason, which is the whole of C-1 ----

    [Fact]
    public void EveryRequeueCarriesTheReasonTheIndexerReopensAnIndexedFileFor()
    {
        // Indexer.cs:298-300 dequeues a row untouched when the reason is not Recheck, the row is
        // not Skipped, and the bytes have not moved - which describes every document Plan 4 has
        // already read. A free-text reason therefore queues everything and does nothing.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Document, ContentDb.StateIndexed, null, File_("a.txt"));

        CapabilityGate.Apply(db, CapabilityGate.Plan(Set(Capability.Meaning), Done()));

        ContentDb.Pending row = Assert.Single(db.PendingRows());
        Assert.Equal(Indexer.Recheck, row.Reason);
    }

    [Fact]
    public void ADocumentAlreadyIndexedIsOpenedAgainWhenMeaningArrives()
    {
        // The end-to-end mirror of the skipped-file test, and the one the first draft of this
        // plan did not have. It drains a re-queued StateIndexed row whose bytes have not changed
        // and asserts the decoder was ASKED. With a free-text reason it is dequeued "current",
        // Asked is empty, and nothing anywhere reports a problem.
        //
        // THE REAL MODIFICATION TIME IS THE WHOLE FIXTURE. The freshness check is three clauses
        // AND-ed together, and this test is about the first of them; storing 0 falsifies the
        // third instead, so the row is opened whatever the reason says and the test passes
        // against the very bug it exists to catch. "The bytes have not changed" has to be true
        // on disk, not just in the sentence describing the test.
        using ContentDb db = Open();
        string doc = File_("contract.txt");
        Item(db, 1, ResultKind.Document, ContentDb.StateIndexed, null, doc,
             mtime: new FileInfo(doc).LastWriteTimeUtc.Ticks);

        Assert.Equal(1, CapabilityGate.Apply(db, CapabilityGate.Plan(Set(Capability.Meaning), Done())));

        var d = new AskRecordingDecoders(Set(Capability.Meaning));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal([doc], d.Asked);
        Assert.Equal(0, db.PendingCount());
        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));
    }

    [Fact]
    public void AnOrdinaryQueueEntryStillLeavesAnUnchangedFileAlone()
    {
        // The control, and it is not decoration: the cheap way to make the test above pass is to
        // delete the freshness check, and then every journal event re-reads a file whose bytes
        // did not change. Plan 4's AnIndexedFileWhoseBytesDidNotChangeIsStillDequeuedUntouched
        // covers the same rule from the other side and must also stay green.
        using ContentDb db = Open();
        string doc = File_("contract.txt");
        long mtime = new FileInfo(doc).LastWriteTimeUtc.Ticks;
        using (var tx = db.Begin())
        {
            db.Upsert("C", 1, doc, ResultKind.Document, mtime, 10, ContentDb.StateIndexed, null, [], tx);
            tx.Commit();
        }
        db.Enqueue("C", 1, doc, ResultKind.Document, "change");

        var d = new AskRecordingDecoders(Set(Capability.Meaning));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Empty(d.Asked);
        Assert.Equal(0, db.PendingCount());
    }

    private sealed class AskRecordingDecoders(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public List<string> Asked { get; } = [];
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes)
        {
            Asked.Add(path);
            return new KindResult([new ContentDb.Segment(ContentDb.SegText, -1, -1, -1, "words")], null);
        }
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Dispose() { }
    }

    // ---- the plan ----

    [Fact]
    public void EnablingPicturesQueuesThePicturesAndNothingElse()
    {
        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(Set(Capability.Photos), Done());

        Requeue r = Assert.Single(plan);
        Assert.Equal(Capability.Photos, r.Capability);
        Assert.Equal([(int)ResultKind.Photo, (int)ResultKind.Video], r.Kinds);
        Assert.DoesNotContain((int)ResultKind.Document, r.Kinds);
    }

    [Fact]
    public void ACapabilityWhoseBacklogIsAlreadyClearedQueuesNothing()
    {
        // The control that stops an unconditional plan. Without the stamp check, every launch
        // re-queues every photo on the disk, for ever - spec §2a's worst case, on a loop.
        Assert.Empty(CapabilityGate.Plan(Set(Capability.Photos), Done(Capability.Photos)));
    }

    [Fact]
    public void ACapabilityAddedAfterAnotherInTheSameFamilyStillClearsItsBacklog()
    {
        // C-3, and it is the ordinary path: somebody takes Recommended, and later adds Speech.
        // Speech, Meaning and Hebrew all embed with e5, so a stamp keyed on the model FAMILY is
        // already current and the plan comes back empty - every audio file on the disk stays
        // skipped for ever, and nothing short of the file being modified picks it up.
        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(
            Set(Capability.Photos, Capability.Meaning, Capability.Speech),
            Done(Capability.Photos, Capability.Meaning));

        Requeue r = Assert.Single(plan);
        Assert.Equal(Capability.Speech, r.Capability);
        Assert.Equal([(int)ResultKind.Audio, (int)ResultKind.Video], r.Kinds);
    }

    [Fact]
    public void AddingASecondCapabilityLeavesTheFirstAlone()
    {
        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(
            Set(Capability.Photos, Capability.Meaning), Done(Capability.Photos));

        Requeue r = Assert.Single(plan);
        Assert.Equal(Capability.Meaning, r.Capability);
        Assert.Equal([(int)ResultKind.Document], r.Kinds);
    }

    [Fact]
    public void AChangeToOneModelFamilysVersionDoesNotDisturbTheOther()
    {
        // The stamp's VALUE carries the family version, so bumping the picture space clears the
        // photo backlog and leaves documents alone. ONE version string covering both families -
        // the obvious shortcut - re-reads every photo for a change to the document model.
        var stamps = Done(Capability.Photos, Capability.Meaning);
        stamps[Capability.Photos] = "siglip@0";              // an older picture space

        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(Set(Capability.Photos, Capability.Meaning), stamps);

        Requeue r = Assert.Single(plan);
        Assert.Equal(Capability.Photos, r.Capability);
    }

    [Fact]
    public void SpeechAndHebrewEachClearTheirOwnBacklogAndTheQueueIsNotDoubled()
    {
        // Both cover audio and video, so both appear in the plan - Hebrew's backlog is a real
        // and separate fact, and merging them would let one stamp discharge the other's debt.
        // The queue is keyed on (volume, frn), so two passes over the same rows is an upsert,
        // not a duplicate: three files, three pending rows.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.NoModel, File_("a.m4a"));
        Item(db, 2, ResultKind.Audio, ContentDb.StateSkipped, Decoders.NoModel, File_("b.m4a"));
        Item(db, 3, ResultKind.Video, ContentDb.StateSkipped, Decoders.NoModel, File_("c.mp4"));

        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(
            Set(Capability.Meaning, Capability.Speech, Capability.Hebrew), Done(Capability.Meaning));

        Assert.Equal([Capability.Speech, Capability.Hebrew], plan.Select(r => r.Capability).ToArray());

        int queued = CapabilityGate.Apply(db, plan);

        Assert.Equal(3, db.PendingCount());
        // And the number it REPORTS is the number of rows that moved. Summing the two
        // RequeueKinds returns says six, because both entries cover the same three files and the
        // queue's UNIQUE(vol, frn) turns the second pass into an upsert. Six is what the log
        // line and --models' closing sentence would then tell somebody about a three-file queue.
        Assert.Equal(3, queued);
    }

    [Fact]
    public void NothingInstalledPlansNothing()
    {
        Assert.Empty(CapabilityGate.Plan(CapabilitySet.None, Done()));
    }

    // ---- applying it ----

    [Fact]
    public void ApplyingThePlanQueuesTheSkippedFilesAndRecordsThatItDid()
    {
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Photo, ContentDb.StateSkipped, Decoders.NoModel, File_("a.jpg"));
        Item(db, 2, ResultKind.Document, ContentDb.StateIndexed, null, File_("b.txt"));

        int n = CapabilityGate.Apply(db, CapabilityGate.Plan(Set(Capability.Photos), Done()));

        Assert.Equal(1, n);
        Assert.Equal(1, db.PendingCount());
        Assert.Equal(CapabilityGate.StampFor(Capability.Photos),
                     db.Get(CapabilityGate.StampPrefix + "photos"));
        Assert.Empty(CapabilityGate.Plan(Set(Capability.Photos), CapabilityGate.StampsIn(db)));
    }

    [Fact]
    public void ACapabilityWhoseBacklogWasQueuedAndNeverReadStillOwesItAtTheNextStart()
    {
        // The debt is recorded the moment the backlog is QUEUED, and the queue is drained by an
        // indexer child that read what was installed when it started. Install a capability while
        // Findra is open and that child records every one of those files skipped again, for want
        // of a model sitting on the disk it has not looked at - and the stamp then says the debt
        // is paid, so nothing ever queues them again. Every photo on the machine stays unread
        // until somebody edits it.
        //
        // Whether the backlog is cleared is a fact the index holds - files this capability covers
        // that nothing has read and nothing is going to read - and not the stamp on its own.
        using ContentDb db = Open();
        string photo = File_("a.jpg");
        Item(db, 1, ResultKind.Photo, ContentDb.StateSkipped, Decoders.NoModel, photo,
             mtime: new FileInfo(photo).LastWriteTimeUtc.Ticks);

        CapabilitySet have = Set(Capability.Photos);
        Assert.Equal(1, CapabilityGate.Apply(db, CapabilityGate.Plan(have, CapabilityGate.StampsIn(db))));

        // and the child that drains it cannot read them
        var stale = new AskRecordingDecoders(CapabilitySet.None);
        Indexer.DrainOnce(db, _ => { }, stale);

        Assert.Empty(stale.Asked);
        Assert.Equal(0, db.PendingCount());
        Assert.Equal(ContentDb.StateSkipped, db.StateOf("C", 1));

        Requeue owed = Assert.Single(CapabilityGate.Plan(have, CapabilityGate.StampsIn(db)));
        Assert.Equal(Capability.Photos, owed.Capability);
        Assert.Equal(1, CapabilityGate.Apply(db, CapabilityGate.Plan(have, CapabilityGate.StampsIn(db))));
        Assert.Equal(1, db.PendingCount());
    }

    [Fact]
    public void QueuedAndNotYetReadIsNotTheSameAsNeverRead()
    {
        // The control for the pair above, and the one that stops the recovery becoming a loop:
        // the backlog is owed again only when nothing is going to look at those files. Between
        // the re-queue and the drain they are skipped AND pending, which is work in hand rather
        // than work lost - counting those as unpaid re-queues the whole backlog on every launch
        // and on every install.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Photo, ContentDb.StateSkipped, Decoders.NoModel, File_("a.jpg"));

        CapabilitySet have = Set(Capability.Photos);
        Assert.Equal(1, CapabilityGate.Apply(db, CapabilityGate.Plan(have, CapabilityGate.StampsIn(db))));

        Assert.Equal(1, db.PendingCount());
        Assert.Empty(CapabilityGate.Plan(have, CapabilityGate.StampsIn(db)));
    }

    [Fact]
    public void TheSecondAttemptQueuesOnlyTheFilesThatWereNeverRead()
    {
        // The recovery is the NARROW re-queue and not the first one over again. Speech covers
        // audio and video together, and a machine with photos has already read every video for
        // its frames; re-queueing those to recover one unheard sound file is hours of somebody's
        // machine for rows the index already holds.
        using ContentDb db = Open();
        string clip = File_("film.mp4");
        Item(db, 1, ResultKind.Video, ContentDb.StateIndexed, null, clip,
             mtime: new FileInfo(clip).LastWriteTimeUtc.Ticks);
        string voice = File_("voice.m4a");
        Item(db, 2, ResultKind.Audio, ContentDb.StateSkipped, Decoders.NoModel, voice,
             mtime: new FileInfo(voice).LastWriteTimeUtc.Ticks);
        Stamped(db, Capability.Meaning);
        Stamped(db, Capability.Speech);

        int queued = CapabilityGate.Apply(db, CapabilityGate.Plan(
            Set(Capability.Meaning, Capability.Speech), CapabilityGate.StampsIn(db)));

        Assert.Equal(1, queued);
        Assert.Equal(voice, Assert.Single(db.PendingRows()).Path);
    }

    /// <summary>Write the stamp that says this capability's backlog was cleared, the way
    /// <see cref="CapabilityGate.Apply"/> writes it.</summary>
    private static void Stamped(ContentDb db, Capability c)
        => db.Set(CapabilityGate.StampPrefix + c.ToString().ToLowerInvariant(), CapabilityGate.StampFor(c));

    [Fact]
    public void ApplyingThePlanTouchesNoOtherProcessesMetaRows()
    {
        // The meta table has four writers with four prefixes, and reusing one is a collision
        // nothing would report. `models:` is this plan's, and nothing else may be written here.
        using ContentDb db = Open();
        db.Set("indexer:state", "idle");
        db.Set("index:paused", "0");
        db.Set("usn:C", "1 2");
        db.Set("schema", "1");

        CapabilityGate.Apply(db, CapabilityGate.Plan(Set(Capability.Photos), Done()));

        Assert.Equal("idle", db.Get("indexer:state"));
        Assert.Equal("0", db.Get("index:paused"));
        Assert.Equal("1 2", db.Get("usn:C"));
        Assert.Equal("1", db.Get("schema"));
        Assert.All(CapabilityGate.StampsIn(db).Keys,
                   c => Assert.NotNull(db.Get(CapabilityGate.StampPrefix + c.ToString().ToLowerInvariant())));

        // And the prefix really is this plan's. The four assertions above catch a stamp write
        // that CLOBBERS one of those rows, and nothing else: a prefix of "index:" writes
        // `index:photos`, which collides with no key anybody holds, so all four still pass while
        // the namespace rule is broken. What is being claimed is that the whole namespace belongs
        // to this plan - four writers, four prefixes - so that is what is asserted.
        Assert.StartsWith("models:", CapabilityGate.StampPrefix, StringComparison.Ordinal);
        Assert.StartsWith("models:", CapabilityGate.LimitKey, StringComparison.Ordinal);
    }

    // ---- what RequeueKinds must and must not pick up ----

    [Fact]
    public void ARequeueForNoKindsIsNothingRatherThanACrash()
    {
        // The IN () clause is built by string concatenation, so an empty array emits `IN ()`.
        // MEASURED, not assumed: the bundled SQLite (3.53.3) accepts that as an empty list and
        // matches nothing, so it is NOT the SqliteException it looks like - which is why the
        // third assertion below exists and the first two alone would prove nothing.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Photo, ContentDb.StateSkipped, Decoders.NoModel, File_("a.jpg"));

        Assert.Equal(0, db.RequeueKinds([], Indexer.Recheck));
        Assert.Equal(0, db.PendingCount());

        // And the guard has to be a guard rather than a comment. The bundled SQLite turns out to
        // ACCEPT `IN ()` as an empty list and match nothing, so the two assertions above pass
        // whether or not the early return is there - which would make this test unable to fail
        // for the defect it names. What the early return really buys is "does not touch the
        // database": without it an empty re-queue opens a transaction, and a caller already
        // inside one gets a nested-transaction InvalidOperationException out of a flow that has
        // no catch for it. Plan 6 inherits exactly that shape, because its first-run screen calls
        // the gate from inside the content loop.
        using (var tx = db.Begin())
        {
            Assert.Equal(0, db.RequeueKinds([], Indexer.Recheck));
            tx.Commit();
        }
    }

    [Fact]
    public void TheDocumentRequeueLeavesAloneWhatNoModelCouldHelp()
    {
        // StateSkipped means four different things - no model for the kind, no reader for the
        // format, no text in it, too large. A new document model helps the first two and can do
        // nothing about the last two, and re-opening a 200 MB database dump on every install is
        // work with a guaranteed outcome.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Document, ContentDb.StateSkipped, Decoders.NoFormatReader, File_("a.rtf"));
        Item(db, 2, ResultKind.Document, ContentDb.StateSkipped, Decoders.TooLarge, File_("b.txt"));
        Item(db, 3, ResultKind.Document, ContentDb.StateSkipped, Decoders.NoText, File_("c.txt"));

        int n = db.RequeueKinds([(int)ResultKind.Document], Indexer.Recheck,
                                notBecause: [Decoders.TooLarge, Decoders.NoText]);

        Assert.Equal(1, n);
        Assert.EndsWith("a.rtf", db.PendingRows()[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExclusionOnlyAppliesToSkippedRowsAndNeverToIndexedOnes()
    {
        // An indexed row has no skip reason at all, and filtering on `error` would drop every
        // one of them - which would mean a new model never re-embeds anything already read, and
        // would hide C-1 behind a second bug with the same symptom.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Document, ContentDb.StateIndexed, null, File_("a.txt"));

        Assert.Equal(1, db.RequeueKinds([(int)ResultKind.Document], Indexer.Recheck,
                                        notBecause: [Decoders.TooLarge, Decoders.NoText]));
    }

    [Fact]
    public void AFileThatGenuinelyFailedIsStillNeverRetried()
    {
        // state IN (1, 3) - indexed and skipped, never failed. A file the decoder could not read
        // has not changed because a capability arrived, and retrying it on every install is a
        // loop with no exit.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Document, ContentDb.StateFailed, "PdfDocumentFormatException: broken xref", File_("a.pdf"));

        Assert.Equal(0, db.RequeueKinds([(int)ResultKind.Document], Indexer.Recheck, null));
    }

    // ---- the transcription limit ----

    [Fact]
    public void RaisingTheLimitQueuesOnlyTheRecordingsItNewlyCovers()
    {
        // The filter runs the OTHER way round from the capability one: only the rows recorded
        // TooLong, and nothing else. Re-queueing everything Speech covers re-transcribes every
        // recording already done - hours of somebody's machine for no new result.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, File_("long.m4a"));
        Item(db, 2, ResultKind.Audio, ContentDb.StateIndexed, null, File_("short.m4a"));
        Item(db, 3, ResultKind.Audio, ContentDb.StateSkipped, Decoders.NoModel, File_("nomodel.m4a"));
        Item(db, 4, ResultKind.Document, ContentDb.StateSkipped, Decoders.TooLarge, File_("huge.txt"));

        int n = CapabilityGate.ApplyLimit(db, 120);

        Assert.Equal(1, n);
        Assert.EndsWith("long.m4a", db.PendingRows()[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongVideoIndexedForItsFramesAloneIsQueuedAgainWhenTheLimitRises()
    {
        // A video over the limit with photos installed keeps its frames, so it is INDEXED
        // and carries TooLong as a note about what was left undone. A filter that looked at the
        // state rather than the recorded reason would miss every one of them.
        //
        // The row is built by hand here because this test is about the QUERY. That the product
        // really writes it is Task 9's
        // ALongVideoWhoseFramesWereReadIsIndexedAndSaysWhatItDidNotHear, which drives the same
        // state out of the real indexer - the two together are what stop this being a test of a
        // shape nothing produces.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Video, ContentDb.StateIndexed, Decoders.TooLong, File_("film.mp4"));

        Assert.Equal(1, CapabilityGate.ApplyLimit(db, TranscribeLimit.NoLimit));
        Assert.Equal(Indexer.Recheck, db.PendingRows()[0].Reason);
    }

    [Fact]
    public void LoweringTheLimitQueuesNothing()
    {
        // Deleting transcripts somebody already paid for, because they moved a slider down, is
        // worse than keeping them. The new limit applies to what has not been read yet.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, File_("long.m4a"));
        db.Set(CapabilityGate.LimitKey, "120");

        Assert.Equal(0, CapabilityGate.ApplyLimit(db, 5));
        Assert.Equal(0, db.PendingCount());
    }

    [Fact]
    public void TurningTranscriptionOffQueuesNothingEitherWay()
    {
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, File_("long.m4a"));
        db.Set(CapabilityGate.LimitKey, "5");

        Assert.Equal(0, CapabilityGate.ApplyLimit(db, TranscribeLimit.Off));
    }

    [Fact]
    public void AnUnchangedLimitQueuesNothingOnEveryLaunch()
    {
        // The control. Without the recorded value this runs on every start, and on a machine
        // with a large archive that is a re-transcription every time Findra opens.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, File_("long.m4a"));

        Assert.Equal(1, CapabilityGate.ApplyLimit(db, 120));
        Assert.Equal(0, CapabilityGate.ApplyLimit(db, 120));
    }

    [Fact]
    public void ReconcilingTheLimitTellsTheIndexerWhatTheLimitIs()
    {
        // Raising the limit queues the recordings it newly covers and records the new limit, both
        // at once. The child that hears them reads the length from the index before each
        // recording, so unless that row moves with it the child passes over exactly the files
        // just queued for it, records them too long again, and the recorded limit then says
        // there is nothing left to hear - for ever.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, File_("long.m4a"));

        Assert.Equal(1, CapabilityGate.ApplyLimit(db, 120));

        Assert.Equal(120, Indexer.TranscribeMinutes(db));
    }

    [Fact]
    public void ARecordingQueuedByARaisedLimitIsHeardRatherThanPassedOverAgain()
    {
        // The same fact from the other end, through the drain. The decoders here are wired the
        // way `findra --index` wires the real ones: the limit is a delegate read per recording,
        // never a value captured when the child started.
        using ContentDb db = Open();
        string rec = File_("long.m4a");
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, rec,
             mtime: new FileInfo(rec).LastWriteTimeUtc.Ticks);
        db.Set(CapabilityGate.LimitKey, "5");
        db.Set(Indexer.TranscribeMinutesKey, "5");

        Assert.Equal(1, CapabilityGate.ApplyLimit(db, 120));

        var d = new LimitReadingDecoders(Set(Capability.Meaning, Capability.Speech),
                                         () => Indexer.TranscribeMinutes(db));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));
        Assert.Empty(db.RecentSkips(10));
    }

    /// <summary>The sound arm of the real decoders, standing in for an hour and a half of audio:
    /// the length is fixed, and the limit it is measured against is read per recording through
    /// the delegate the child hands in.</summary>
    private sealed class LimitReadingDecoders(CapabilitySet installed, Func<int> minutes) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes)
            => TranscribeLimit.Covers(minutes(), 90 * 60)
               ? new KindResult([new ContentDb.Segment(ContentDb.SegSpeech, 0, 5, -1, "what was said")], null)
               : new KindResult([], Decoders.TooLong);
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Dispose() { }
    }

    [Fact]
    public void NoLimitIsHigherThanEveryNumberAndNotLowerThanAllOfThem()
    {
        // The one place the sign convention bites: -1 means "no limit", so it must compare as
        // MORE permissive than 120 and not less. A plain `now > was` gets this exactly backwards
        // and "no limit" then queues nothing at all.
        Assert.NotNull(CapabilityGate.PlanForLimit(120, TranscribeLimit.NoLimit));
        Assert.Null(CapabilityGate.PlanForLimit(TranscribeLimit.NoLimit, 120));
        Assert.NotNull(CapabilityGate.PlanForLimit(TranscribeLimit.Off, 5));
        Assert.Null(CapabilityGate.PlanForLimit(5, TranscribeLimit.Off));
    }

    // ---- the schema, which this plan does not move ----

    [Fact]
    public void AFreshInstallRunsNoSchemaMigrationOverAnEmptyIndex()
    {
        // Plan 4 left `Migrations` empty and this plan does not add to it - but the guard has to
        // hold before somebody does. A brand-new database has never been written by an older
        // build, so there is nothing to migrate it FROM, and `OpenedFromSchema` plus
        // `MigrationsRun` are what make "treated as current" and "treated as version zero"
        // distinguishable at all.
        var step = new ContentDb.Migration(ContentDb.SchemaVersion, [(int)ResultKind.Photo], "a test step");
        using var db = new ContentDb(Path.Combine(_dir, "fresh.db"), migrations: [step]);

        Assert.Equal(ContentDb.SchemaVersion, db.OpenedFromSchema);
        Assert.Empty(db.MigrationsRun);
    }

    [Fact]
    public void EverySchemaStepIsReachableAndInvalidatesLessThanEverything()
    {
        // This replaced `Assert.Empty(ContentDb.Migrations)`, which held the place until a change
        // needed a step. It is now the traps that went live with the first one.
        IReadOnlyList<ContentDb.Migration> steps = ContentDb.Migrations;

        // 1. A step nobody can reach. OpenSchema skips `m.To > SchemaVersion`, so a step written
        //    for a version the build does not claim is silently never run - and the change it was
        //    meant to invalidate ships against indexes that still hold the old rows.
        foreach (ContentDb.Migration m in steps)
            Assert.InRange(m.To, 2, ContentDb.SchemaVersion);

        // 2. A version bumped with no step to go with it. An older index would migrate straight to
        //    the new stamp, re-queue nothing, and keep whatever the change invalidated for ever.
        //    The last step has to arrive at the version this build claims.
        Assert.Equal(ContentDb.SchemaVersion, steps[^1].To);

        // 3. Steps out of order, or two claiming the same version: OpenSchema runs them in list
        //    order and compares against `from`, so either one makes which steps run depend on how
        //    they were typed.
        for (int i = 1; i < steps.Count; i++)
            Assert.True(steps[i].To > steps[i - 1].To, "schema steps must be strictly increasing");

        // 4. And no step may invalidate every kind there is. That is a full re-index of a finished
        //    disk, which spec §2a calls the worst thing this product can do to somebody - and it
        //    is the easy thing to write when a change touches something shared.
        foreach (ContentDb.Migration m in steps)
        {
            Assert.NotEmpty(m.InvalidatedKinds);
            Assert.True(m.InvalidatedKinds.Length < Enum.GetValues<ResultKind>().Length,
                        $"'{m.Reason}' re-queues every kind, which is a full re-index");
            Assert.NotEqual("", m.Reason);
        }
    }
}
