using Findra;
using Findra.Pipe;
using Xunit;

public sealed class QueueFeederTests : IDisposable
{
    private const ulong Journal = 0xBEEF;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-feed-" + Guid.NewGuid().ToString("N"));

    private string DbPath { get { Directory.CreateDirectory(_dir); return Path.Combine(_dir, "search.db"); } }
    private ContentDb Open() => new(DbPath);
    private static Config Cfg => Config.Default;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static JournalEvent Change(ulong frn, string name, string path, uint reason, long usn,
                                       char vol = 'C', ulong journal = Journal)
        => new(vol, journal, frn, 10, 0, name, path, reason, usn);

    /// <summary>The tail's reset marker: Reason 0, no name, no path, real journal id.</summary>
    private static JournalEvent Marker(char vol = 'C') => new(vol, Journal, 0, 0, 0, "", "", 0, 0);

    private static EnumeratedFile[] Disk =>
    [
        new(101, @"C:\Papers\lease.pdf"),
        new(102, @"C:\Papers\notes.txt"),
        new(103, @"C:\Papers\deck.pptx"),
    ];

    /// <summary>Mark everything queued as finished, the way the indexer child would.</summary>
    private static void DrainAsIndexed(ContentDb db)
    {
        while (db.TakeNext() is { } p)
        {
            using var tx = db.Begin();
            db.Upsert("C", p.Frn, p.Path, p.Kind, mtime: 0, size: 0, ContentDb.StateIndexed, null, [], tx);
            db.Dequeue(p.Id, tx);
            tx.Commit();
        }
    }

    // ---- eligibility ----

    [Theory]
    [InlineData(@"C:\Users\liraz\Documents\lease.pdf", true)]
    [InlineData(@"C:\Users\liraz\Documents\photo.jpg", true)]      // a content kind with no decoder yet is STILL eligible
    [InlineData(@"C:\Users\liraz\Documents\build.exe", false)]     // not a content kind
    [InlineData(@"C:\Windows\System32\readme.txt", false)]         // an excluded folder
    [InlineData(@"C:\Users\liraz\AppData\Local\x\a.pdf", false)]
    [InlineData(@"C:\Users\liraz\proj\node_modules\pkg\readme.md", false)]
    public void TheDefaultRulesDecideWhatIsWorthOpening(string path, bool expected)
    {
        ResultKind kind = FileKinds.Classify(Path.GetFileName(path), false);
        Assert.Equal(expected, QueueFeeder.Eligible(path, kind, FileKinds.DefaultExclusions, []));
    }

    [Fact]
    public void ARepoRootExcludesWhatIsUnderItAndNotWhatMerelyStartsWithIt()
    {
        // "C:\Code\findra" must not exclude "C:\Code\findra-notes". A StartsWith without the
        // separator check silently drops a whole sibling folder, and nobody finds out.
        string[] roots = [@"C:\Code\findra"];

        Assert.False(QueueFeeder.Eligible(@"C:\Code\findra\docs\spec.md", ResultKind.Document, [], roots));
        Assert.True(QueueFeeder.Eligible(@"C:\Code\findra-notes\spec.md", ResultKind.Document, [], roots));
        Assert.True(QueueFeeder.Eligible(@"C:\Code\other\spec.md", ResultKind.Document, [], roots));
    }

    // ---- journal events ----

    [Fact]
    public void ADeleteIsQueuedOnlyForAFileTheIndexActuallyHolds()
    {
        // Every deletion on the volume arrives here - browser caches, build outputs, tens of
        // thousands an hour. Queuing them all buries the real work behind no-op deletes.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);

        feeder.Consume([Change(999, "gone.pdf", @"C:\a\gone.pdf", NtfsVolume.ReasonFileDelete, 1)]);
        Assert.Equal(0, db.PendingCount());

        using (var tx = db.Begin())
        {
            db.Upsert("C", 999, @"C:\a\gone.pdf", ResultKind.Document, 1, 1, ContentDb.StateIndexed, null, [], tx);
            tx.Commit();
        }
        feeder.Consume([Change(999, "gone.pdf", "", NtfsVolume.ReasonFileDelete, 2)]);

        Assert.Equal(1, db.PendingCount());
        ContentDb.Pending queued = db.TakeNext()!.Value;
        Assert.Equal("delete", queued.Reason);
        Assert.Equal(999ul, queued.Frn);
    }

    [Fact]
    public void AnIneligibleChangeIsNotQueuedButAFileMovedIntoAnExcludedPlaceIsRemoved()
    {
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        using (var tx = db.Begin())
        {
            db.Upsert("C", 50, @"C:\Users\liraz\lease.pdf", ResultKind.Document, 1, 1,
                      ContentDb.StateIndexed, null, [], tx);
            tx.Commit();
        }

        feeder.Consume([
            Change(60, "temp.exe", @"C:\Users\liraz\temp.exe", NtfsVolume.ReasonFileCreate, 3),
            Change(50, "lease.pdf", @"C:\Windows\Temp\lease.pdf", NtfsVolume.ReasonRenameNewName, 4),
        ]);

        ContentDb.Pending only = db.TakeNext()!.Value;
        Assert.Equal("delete", only.Reason);
        Assert.Equal(50ul, only.Frn);
        db.Dequeue(only.Id);
        Assert.Equal(0, db.PendingCount());        // the .exe was never queued
    }

    [Fact]
    public void TheConsumedPositionRecordsTheJournalItCameFromNotJustTheNumber()
    {
        // A USN is a coordinate inside ONE journal. Storing 30 with no id - or with 0 - means
        // the next launch compares it against the volume's real journal id, finds a mismatch,
        // declares NeedsFullPass, and re-walks the whole disk. Every launch. Forever.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);

        feeder.Consume([
            Change(1, "a.pdf", @"C:\a.pdf", NtfsVolume.ReasonFileCreate, 10),
            Change(2, "b.pdf", @"C:\b.pdf", NtfsVolume.ReasonFileCreate, 30),
            Change(3, "c.pdf", @"D:\c.pdf", NtfsVolume.ReasonFileCreate, 7, vol: 'D', journal: 0x1234),
        ]);

        Assert.Equal((Journal, 30L), db.UsnPosition('C'));
        Assert.Equal((0x1234ul, 7L), db.UsnPosition('D'));

        feeder.Consume([Change(4, "d.pdf", @"C:\d.pdf", NtfsVolume.ReasonFileCreate, 20)]);
        Assert.Equal((Journal, 30L), db.UsnPosition('C'));   // a late, lower USN must not rewind it
    }

    [Fact]
    public void AnEventFromANewJournalReplacesThePositionRatherThanBeingIgnored()
    {
        // The volume's journal was deleted and recreated: USNs restart from zero, so the
        // "only forwards" rule would pin the position in a journal that no longer exists and
        // the feeder would never record anything again.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);

        feeder.Consume([Change(1, "a.pdf", @"C:\a.pdf", NtfsVolume.ReasonFileCreate, 5000)]);
        feeder.Consume([Change(2, "b.pdf", @"C:\b.pdf", NtfsVolume.ReasonFileCreate, 12, journal: 0xF00D)]);

        Assert.Equal((0xF00Dul, 12L), db.UsnPosition('C'));
    }

    [Fact]
    public void AResetMarkerLeavesAFreshWalkOwedThatLaterEventsCannotErase()
    {
        // The tail publishes Reason 0 with an empty name when NtfsVolume.Read says the journal
        // wrapped past us. Clearing the position is necessary and NOT sufficient: the very
        // next batch writes a fresh position from later events, because `had` is now null, and
        // within milliseconds the index is back to claiming it is caught up over a range it
        // never saw. So the assertion is made AFTER more events have arrived - an earlier
        // draft of this test stopped at the clear and passed while the behaviour was wrong.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        feeder.Consume([Change(1, "a.pdf", @"C:\a.pdf", NtfsVolume.ReasonFileCreate, 10)]);
        Assert.NotNull(db.UsnPosition('C'));
        Assert.False(feeder.NeedsFreshWalk('C'));

        feeder.Consume([Marker()]);

        Assert.Null(db.UsnPosition('C'));
        Assert.True(feeder.NeedsFreshWalk('C'));
        Assert.Empty(feeder.StoredCursors());

        feeder.Consume([Change(2, "b.pdf", @"C:\b.pdf", NtfsVolume.ReasonFileCreate, 90)]);

        Assert.True(feeder.NeedsFreshWalk('C'),
            "later events must not erase the debt - a position written over a hole is a silent lie");
    }

    [Fact]
    public void TheWalkOwedByADropSurvivesARestart()
    {
        // The debt is a row in meta, not a field, and this is the whole reason why: a restart
        // is precisely when an in-memory latch is discharged for free and the hole becomes
        // permanent. Nothing else in the system would ever notice.
        string path = DbPath;
        using (var db = new ContentDb(path))
        using (var feeder = new QueueFeeder(db, () => Cfg))
            feeder.Consume([Marker()]);

        using (var db = new ContentDb(path))
        using (var feeder = new QueueFeeder(db, () => Cfg))
            Assert.True(feeder.NeedsFreshWalk('C'));
    }

    [Fact]
    public void ADropInTheClientsOwnChannelAlsoOwesAWalk()
    {
        // The helper marks its own drops with a reset marker. A drop in NameClient's receive
        // channel has nothing upstream that knows it happened, so if the UI does not report
        // the counter this hole is the one thing in the design that nothing records.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        feeder.NoteClientDrops(0);
        Assert.False(feeder.NeedsFreshWalk('C'));

        feeder.Consume([Change(1, "a.pdf", @"C:\a.pdf", NtfsVolume.ReasonFileCreate, 10)]);
        feeder.NoteClientDrops(17);

        Assert.True(feeder.NeedsFreshWalk('C'));
        Assert.True(db.WalkOwed('C'));

        // A count that has not moved is not a new drop; re-reporting the same total must not
        // re-owe a walk that FillFrom has since discharged.
        db.ClearWalkOwed('C');
        feeder.NoteClientDrops(17);
        Assert.False(feeder.NeedsFreshWalk('C'));
    }

    [Fact]
    public void ADropInASecondConnectionOwesAWalkEvenThoughItsCounterStartsLower()
    {
        // JournalDropped belongs to ONE NameClient, and the shell opens a new one every time the
        // helper goes away and comes back - so the counter restarts at zero while the feeder
        // outlives it. Reading 3 after 17 as "nothing new" is how the whole of a second session's
        // losses hide behind the first session's larger number, on the one path in the design
        // that has nothing upstream to notice a hole.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        feeder.Consume([Change(1, "a.pdf", @"C:\a.pdf", NtfsVolume.ReasonFileCreate, 10)]);

        feeder.NoteClientDrops(17);
        db.ClearWalkOwed('C');

        feeder.NoteClientDrops(3);          // a new connection, three events already lost

        Assert.True(db.WalkOwed('C'));
    }

    [Fact]
    public void ASecondConnectionThatLostTheSameNumberOfEventsStillOwesAWalk()
    {
        // "The same total is not a new drop" is true WITHIN one connection and false across two.
        // The counter belongs to a NameClient and starts again at zero on every reconnection,
        // while this feeder lives for the whole process, so two sessions that each lose seventeen
        // events report seventeen twice - and the second seventeen is seventeen real files the
        // index will never hear about. A lower number gives the feeder a hint that the channel
        // restarted; an equal one gives it nothing at all, which is why the baseline has to be
        // told when the session changed rather than inferred from the number.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        feeder.Consume([Change(1, "a.pdf", @"C:\a.pdf", NtfsVolume.ReasonFileCreate, 10)]);

        long dropped = 0;
        feeder.NoteSessionStarted(() => dropped);
        dropped = 17;
        feeder.NoteClientDrops(dropped);
        db.ClearWalkOwed('C');                    // a full pass discharged it

        // The helper went away and came back. A new client, a new counter - which has already
        // lost exactly as many events as the whole of the last session did.
        dropped = 17;
        feeder.NoteSessionStarted(() => dropped);
        feeder.NoteClientDrops(dropped);

        Assert.True(db.WalkOwed('C'));

        // And the row --searchindex prints counts what this index has lost, not what the current
        // connection has lost. A per-session baseline that also stored the session's own number
        // would make the report shrink on every reconnection.
        Assert.Equal("34", db.Get("journal:dropped"));
    }

    [Fact]
    public void ADropIsRecordedWhereTheIndexReportReadsIt()
    {
        // --searchindex prints this row, and it is the only count in that report a dropped event
        // ever moves - the index otherwise looks finished. Nothing wrote it until now, so the
        // report said "0 dropped" however many files the feeder never heard about.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        feeder.Consume([Change(1, "a.pdf", @"C:\a.pdf", NtfsVolume.ReasonFileCreate, 10)]);

        Assert.Null(db.Get("journal:dropped"));
        feeder.NoteClientDrops(17);

        Assert.Equal("17", db.Get("journal:dropped"));
    }

    [Fact]
    public void AFullPassDischargesTheDebtAndNothingElseDoes()
    {
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        feeder.Consume([Marker()]);
        Assert.True(feeder.NeedsFreshWalk('C'));

        feeder.Consume([Change(1, "a.pdf", @"C:\a.pdf", NtfsVolume.ReasonFileCreate, 10)]);
        feeder.Reconcile();
        Assert.True(feeder.NeedsFreshWalk('C'), "only a completed walk clears it");

        feeder.FillFrom('C', Journal, 4242, Disk);

        Assert.False(feeder.NeedsFreshWalk('C'));
        Assert.False(db.WalkOwed('C'));
    }

    [Fact]
    public void ADropIsRecordedInsideTheBatchThatMovesThePositionPastIt()
    {
        // This batch's transaction advances usn:D over a range the receive channel may have
        // eaten. Writing the debt AFTER that transaction commits leaves a window in which a
        // crash takes the debt and leaves the position - an index claiming coverage nothing ever
        // read, owing nobody a walk, which is the single state the walk debt exists to make
        // impossible. Writing it BEFORE loses any volume this batch is the FIRST to give a
        // position to, because the charge is spread over the volumes the index knows about, and
        // D: is not one of them until these rows land. Inside the transaction, after the
        // positions, is the only order that is both complete and atomic.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);

        long dropped = 0;
        feeder.NoteSessionStarted(() => dropped);

        dropped = 3;
        feeder.Consume([Change(1, "a.pdf", @"D:\a.pdf", NtfsVolume.ReasonFileCreate, 500, vol: 'D')]);

        Assert.Equal(500, db.UsnPosition('D')!.Value.Usn);
        Assert.True(db.WalkOwed('D'), "the position covers a range three events are missing from");
    }

    [Fact]
    public void APassDoesNotDischargeAHoleThatOpenedWhileItWasRunning()
    {
        // A full pass over a real disk takes minutes, and the events it lost while it ran are
        // PAST the position it is about to stamp - so a pass that cleared the debt on its way out
        // would discharge a hole it never covered, and nothing would ever walk that range again.
        //
        // The pass has to read the counter itself, at the moment it commits. Reading a field that
        // only the caller updates makes the check unfalsifiable: the interface's loop reports
        // drops and awaits the walk on one thread, so between the walk starting and finishing the
        // field cannot move, and the taint is always false.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        feeder.Consume([Change(1, "a.pdf", @"C:\a.pdf", NtfsVolume.ReasonFileCreate, 10)]);

        long dropped = 0;
        feeder.NoteSessionStarted(() => dropped);
        db.ClearWalkOwed('C');

        feeder.NoteWalkStarted('C');
        dropped = 4;                        // the receive channel evicts while the walk is in flight
        feeder.FillFrom('C', Journal, 4242, Disk);

        Assert.True(db.WalkOwed('C'), "the pass covered a range four events are missing from");
        Assert.True(feeder.NeedsFreshWalk('C'));

        // And a pass with a quiet channel under it still discharges the debt, or the volume walks
        // for ever.
        feeder.NoteWalkStarted('C');
        feeder.FillFrom('C', Journal, 5000, Disk);
        Assert.False(db.WalkOwed('C'));
    }

    // ---- the first pass, and resuming ----

    [Fact]
    public void AFinishedIndexQueuesNothingOnTheSecondPass()
    {
        // Spec §2a: "Re-downloading 2.9 GB of models, or re-indexing a disk that was already
        // done, because an upgrade did not look first, is the single most annoying thing this
        // product could do to someone. It gets a test." This is that test.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);

        Assert.Equal(3, feeder.FillFrom('C', Journal, 4242, Disk));
        Assert.Equal(3, db.PendingCount());

        DrainAsIndexed(db);

        Assert.Equal(0, feeder.FillFrom('C', Journal, 4242, Disk));
        Assert.Equal(0, db.PendingCount());
    }

    [Fact]
    public void AWalkThatRestartedAndSentAFileTwiceCountsItOnce()
    {
        // The enumerate walk restarts from record zero when the journal moves under it, so one
        // (frn, path) can legitimately arrive twice in a single stream. The count FillFrom
        // returns is compared against PendingCount by everything that reports progress, so it
        // has to be distinct FRNs and not Enqueue calls - which would say 4 here while the
        // queue holds 3. Every id in the ordinary fixture is already distinct, so nothing else
        // in this file can tell the two apart.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);

        EnumeratedFile[] withARepeat = [.. Disk, new EnumeratedFile(101, @"C:\Papers\lease.pdf")];

        Assert.Equal(3, feeder.FillFrom('C', Journal, 4242, withARepeat));
        Assert.Equal(3, db.PendingCount());
    }

    [Fact]
    public void AfterAFullPassAndARestartTheHelperIsAskedToResumeNotToReWalk()
    {
        // The end-to-end shape of spec §2a, at the level where it actually breaks: the cursor
        // the feeder WROTE is handed back to ResumeFrom exactly as the UI would hand it to
        // SubscribeJournalAsync. If FillFrom stored no journal id - or a zero - this asserts
        // NeedsFullPass and the disk is re-walked at every launch.
        string path = DbPath;
        using (var db = new ContentDb(path))
        using (var feeder = new QueueFeeder(db, () => Cfg))
        {
            feeder.FillFrom('C', Journal, throughUsn: 4242, Disk);
            DrainAsIndexed(db);
        }

        IReadOnlyList<VolumeCursor> cursors;
        using (var db = new ContentDb(path))
        using (var feeder = new QueueFeeder(db, () => Cfg))
            cursors = feeder.StoredCursors();

        VolumeCursor c = Assert.Single(cursors);
        Assert.Equal('C', c.Volume);
        Assert.Equal(Journal, c.JournalId);
        Assert.Equal(4242, c.Usn);

        VolumeResume resume = Assert.Single(JournalTail.ResumeFrom(
            new Dictionary<char, ulong> { ['C'] = Journal },
            new Dictionary<char, long> { ['C'] = 4300 },
            cursors));

        Assert.False(resume.NeedsFullPass);
        Assert.Equal(4242, resume.Usn);       // resume from where we got to, replay 4242..4300
    }

    [Fact]
    public void ASkippedFileIsNotQueuedAgainEither()
    {
        // A photo with no decoder is DONE for now, not outstanding. Re-queuing skipped files
        // every launch makes the pending count never reach zero and the card never say "done".
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        using (var tx = db.Begin())
        {
            db.Upsert("C", 201, @"C:\Papers\sunset.jpg", ResultKind.Photo, 0, 0,
                      ContentDb.StateSkipped, "no decoder for this kind yet", [], tx);
            tx.Commit();
        }

        Assert.Equal(0, feeder.FillFrom('C', Journal, 1, [new EnumeratedFile(201, @"C:\Papers\sunset.jpg")]));
    }

    [Fact]
    public void ANonEmptyQueueSurvivesTheProcessAndIsResumedNotCleared()
    {
        // Spec §2a: "Index present, schema current, queue non-empty -> resumed."
        string path = DbPath;
        using (var db = new ContentDb(path))
        using (var feeder = new QueueFeeder(db, () => Cfg))
        {
            feeder.FillFrom('C', Journal, 4242,
                Enumerable.Range(1, 7).Select(i => new EnumeratedFile((ulong)i, $@"C:\Papers\f{i}.pdf")));
            Assert.Equal(7, db.PendingCount());
        }

        using (var db = new ContentDb(path))
        {
            Assert.Equal(7, db.PendingCount());
            Assert.Equal((Journal, 4242L), db.UsnPosition('C'));
            Assert.NotNull(db.TakeNext());
        }
    }

    [Fact]
    public void AChangedSuffixSetForcesAFreshWalkOfAnAlreadyFinishedDisk()
    {
        // FileKinds' extension tables are code. When a later version adds an extension, every
        // existing install would otherwise keep asking the helper for the OLD list and those
        // files would never be enumerated at all - on any machine that had already finished.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Cfg);
        feeder.FillFrom('C', Journal, 4242, Disk);
        DrainAsIndexed(db);
        Assert.False(feeder.NeedsFreshWalk('C'));

        db.SetSuffixVersion([".pdf"]);          // pretend an older build walked with a smaller set

        Assert.True(feeder.NeedsFreshWalk('C'));
    }

    [Fact]
    public void ReconcileDropsWhatTheRulesNoLongerCover()
    {
        // Exclusions are config: they change between launches. What they now exclude must
        // leave the queue, and what is already indexed under them must be queued for removal.
        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Config.Default with { SearchExclusions = [@"\Papers\"] });
        db.Enqueue("C", 300, @"C:\Papers\lease.pdf", ResultKind.Document, "new");
        using (var tx = db.Begin())
        {
            db.Upsert("C", 301, @"C:\Papers\old.pdf", ResultKind.Document, 0, 0, ContentDb.StateIndexed, null, [], tx);
            tx.Commit();
        }

        feeder.Reconcile();

        List<ContentDb.Pending> left = [];
        while (db.TakeNext() is { } p) { left.Add(p); db.Dequeue(p.Id); }
        ContentDb.Pending only = Assert.Single(left);
        Assert.Equal("delete", only.Reason);
        Assert.Equal(301ul, only.Frn);
    }

    [Fact]
    public void TheSuffixListTheHelperIsGivenCoversEveryContentKindAndNothingElse()
    {
        IReadOnlyList<string> s = QueueFeeder.ContentSuffixes();

        Assert.Contains(".pdf", s);
        Assert.Contains(".jpg", s);
        Assert.Contains(".mp3", s);
        Assert.Contains(".mp4", s);
        Assert.DoesNotContain(".exe", s);
        Assert.DoesNotContain(".dll", s);
        Assert.All(s, x => Assert.StartsWith(".", x));
        Assert.All(s, x => Assert.Equal(x.ToLowerInvariant(), x));
        // Every suffix it asks for must be one it will accept when the rows come back.
        Assert.All(s, x => Assert.True(FileKinds.HasContent(FileKinds.Classify("f" + x, false)),
                                       $"{x} is asked for but not a content kind"));
    }
}
