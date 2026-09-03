using Microsoft.Data.Sqlite;
using System.Globalization;

using Findra;
using Xunit;

public sealed class SchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-schema-" + Guid.NewGuid().ToString("N"));

    private string Db(string name = "search.db")
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void TheBundledSqliteHasFts5()
    {
        // Everything in this plan is FTS5. A bundle without it fails at the CREATE VIRTUAL
        // TABLE with a message about an unknown module, three layers into an indexer child
        // process nobody is watching - so it is asserted here, once, in the open.
        IReadOnlyList<string> opts = ContentDb.CompileOptions();
        Assert.Contains(opts, o => o.Contains("FTS5", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReopeningACurrentIndexRunsNoMigrationOverTheFilesItAlreadyHas()
    {
        // The case that actually matters: an UPGRADE over a finished index. The step below
        // would re-queue both documents if it ran, and there are real rows for it to find -
        // asserting this against an empty database proves nothing, because RequeueKinds
        // returns 0 over no items whether or not the migration fired.
        string path = Db();
        var everything = new[]
        {
            new ContentDb.Migration(1, [(int)ResultKind.Document, (int)ResultKind.Photo], "everything re-extracted"),
        };

        using (var db = new ContentDb(path))
        {
            using var tx = db.Begin();
            db.Upsert("C", 100, @"C:\a\lease.pdf", ResultKind.Document, 7, 10, ContentDb.StateIndexed, null, [], tx);
            db.Upsert("C", 200, @"C:\a\sunset.jpg", ResultKind.Photo, 7, 10, ContentDb.StateSkipped,
                      "no decoder for this kind yet", [], tx);
            tx.Commit();
            Assert.Equal(ContentDb.SchemaVersion.ToString(), db.Get("schema"));
        }

        using (var db = new ContentDb(path, migrations: everything))
        {
            Assert.Equal(ContentDb.SchemaVersion.ToString(), db.Get("schema"));
            Assert.Equal(0, db.PendingCount());       // nothing was re-queued
        }
    }

    [Fact]
    public void ABrandNewDatabaseIsStampedCurrentRatherThanTreatedAsVersionZero()
    {
        // The stamp alone could never fail for the case this name states. BOTH branches of
        // OpenSchema stamp SchemaVersion on the way out, so "treated as current" and "treated as
        // version zero, migrated, then stamped" are indistinguishable to it - and a fresh database
        // has no items, so a step that re-queues kinds moves no count either. The fact that
        // separates them is whether a step RAN, and that is what is asserted here.
        //
        // Harmless while Migrations is empty. From the first real step it means a brand-new
        // install runs every migration over an empty database - free if every step is a requeue,
        // and not free at all if any step is DDL.
        var everything = new[]
        {
            new ContentDb.Migration(1, [(int)ResultKind.Document], "documents re-extracted"),
        };

        using var db = new ContentDb(Db(), migrations: everything);

        Assert.Equal(ContentDb.SchemaVersion.ToString(), db.Get("schema"));
        Assert.Equal(ContentDb.SchemaVersion, db.OpenedFromSchema);
        Assert.Empty(db.MigrationsRun);
    }

    [Fact]
    public void AnUnstampedDatabaseThatAlreadyHoldsFilesIsTheOneTreatedAsVersionZero()
    {
        // The other side of the same decision, so "never migrate anything" cannot pass the test
        // above. An index written before the stamp existed has real rows in it, and those rows
        // were written by a build whose schema this one has to migrate from.
        string path = Db();
        using (var db = new ContentDb(path))
        {
            using var tx = db.Begin();
            db.Upsert("C", 100, @"C:\a\lease.pdf", ResultKind.Document, 7, 10, ContentDb.StateIndexed, null, [], tx);
            tx.Commit();
        }

        // Unstamped, from the side, because there is no API for un-stamping and there should not
        // be. This is what an index written before the stamp existed looks like on disk.
        using (var side = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            side.Open();
            using SqliteCommand cmd = side.CreateCommand();
            cmd.CommandText = "DELETE FROM meta WHERE key='schema'";
            cmd.ExecuteNonQuery();
        }

        var step = new[] { new ContentDb.Migration(1, [(int)ResultKind.Document], "documents re-extracted") };
        using (var db = new ContentDb(path, migrations: step))
        {
            Assert.Equal(0, db.OpenedFromSchema);
            Assert.Equal(["documents re-extracted"], db.MigrationsRun);
            Assert.Equal(1, db.PendingCount());
        }
    }

    [Fact]
    public void AnOlderSchemaRunsTheMigrationAndRequeuesOnlyTheInvalidatedKinds()
    {
        string path = Db();
        var step = new[] { new ContentDb.Migration(1, [(int)ResultKind.Document], "documents re-extracted") };

        using (var db = new ContentDb(path))
        {
            using var tx = db.Begin();
            db.Upsert("C", 100, @"C:\a\lease.pdf", ResultKind.Document, 7, 10, ContentDb.StateIndexed, null, [], tx);
            db.Upsert("C", 200, @"C:\a\sunset.jpg", ResultKind.Photo, 7, 10, ContentDb.StateIndexed, null, [], tx);
            tx.Commit();
            db.Set("schema", "0");
        }

        using (var db = new ContentDb(path, migrations: step))
        {
            // The stamp is the BUILD's version, not the last step's: OpenSchema runs every step
            // standing between what it found and what this build claims, then stamps its own.
            Assert.Equal(ContentDb.SchemaVersion.ToString(CultureInfo.InvariantCulture), db.Get("schema"));
            List<(long Id, string Path)> queued = db.PendingPaths();
            (long, string) only = Assert.Single(queued);
            Assert.Equal(@"C:\a\lease.pdf", only.Item2);
        }
    }

    [Fact]
    public void TheUsnPositionIsPerVolumeAndAbsentUntilItIsWritten()
    {
        using var db = new ContentDb(Db());

        Assert.Null(db.UsnPosition('C'));

        db.SetUsnPosition('C', journalId: 0xABCDEF, usn: 90210);

        Assert.Equal((0xABCDEFul, 90210L), db.UsnPosition('C'));
        Assert.Null(db.UsnPosition('D'));          // one key per volume, not one key
        Assert.Equal(new[] { 'C' }, db.KnownVolumes());
    }

    [Fact]
    public void TheUsnPositionSurvivesClosingTheDatabase()
    {
        // The whole point of moving this out of a text file and into the index: it is the
        // fact that decides whether a restart resumes or re-walks the disk.
        string path = Db();
        using (var db = new ContentDb(path)) db.SetUsnPosition('C', 5, 1234);
        using (var db = new ContentDb(path)) Assert.Equal((5ul, 1234L), db.UsnPosition('C'));
    }

    [Fact]
    public void AnUnreadableIndexIsMovedAsideAndRebuiltRatherThanThrowing()
    {
        // Spec §2a: "Index missing or unreadable - rebuilt, and the UI says so rather than
        // looking idle." A SqliteException out of the constructor lands in a background loop
        // with nobody to catch it, and content search is dead for that install forever.
        string path = Db();
        File.WriteAllText(path, "this is not a database, it is a text file");

        using ContentDb db = ContentDb.OpenOrRebuild(path);

        Assert.Equal(ContentDb.SchemaVersion.ToString(), db.Get("schema"));
        Assert.Equal(0, db.PendingCount());
        Assert.True(db.WasRebuilt);
        Assert.True(File.Exists(path + ".corrupt"), "the unreadable file is kept, not deleted");
    }

    [Fact]
    public void AHealthyIndexIsNotRebuilt()
    {
        // The other half: OpenOrRebuild must not be a "delete the index on every launch"
        // path. Without this, the rebuild branch could fire always and nothing would notice
        // until someone asked why indexing never finishes.
        string path = Db();
        using (var db = new ContentDb(path)) db.Enqueue("C", 1, @"C:\a.pdf", ResultKind.Document, "new");

        using ContentDb reopened = ContentDb.OpenOrRebuild(path);

        Assert.False(reopened.WasRebuilt);
        Assert.Equal(1, reopened.PendingCount());
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void ADirectorySittingAtTheDatabasePathIsMovedAsideAndRebuiltRatherThanThrowing()
    {
        // MoveAside used to check File.Exists per tail, which is false for a directory - so it
        // silently did nothing and the second construction attempt threw, uncaught, straight
        // out of OpenOrRebuild. A stray directory at the target path is not exotic: a wrong
        // recursive mkdir, a half-finished install, anything.
        string path = Db();
        Directory.CreateDirectory(path);

        using ContentDb db = ContentDb.OpenOrRebuild(path);

        Assert.True(db.WasRebuilt);
        Assert.Equal(ContentDb.SchemaVersion.ToString(), db.Get("schema"));
        Assert.True(Directory.Exists(path + ".corrupt"), "the directory is kept, not deleted");
    }

    [Fact]
    public void AFileLockedByAnotherProcessFallsBackToAnInMemoryStoreRatherThanThrowing()
    {
        // Both the move and the delete fallback in MoveAside fail against a file another
        // process holds open with FileShare.None (an AV scan, a leftover handle from a crashed
        // prior instance) - and the fixed-name AND timestamped rungs both retry the SAME locked
        // path, so both fail too. The only way OpenOrRebuild can keep its promise here is the
        // third rung: an in-memory store, still clearly marked as rebuilt so the caller does not
        // mistake it for a healthy, populated index.
        string path = Db();
        File.WriteAllText(path, "this is not a database, it is a text file");
        using FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        using ContentDb db = ContentDb.OpenOrRebuild(path);

        Assert.True(db.WasRebuilt);
        Assert.Equal(ContentDb.SchemaVersion.ToString(), db.Get("schema"));
        db.Enqueue("C", 1, @"C:\a.pdf", ResultKind.Document, "new");   // a usable store, not a shell
        Assert.Equal(1, db.PendingCount());
    }

    [Fact]
    public void AZeroByteFileIsOpenedAsAFreshDatabaseRatherThanRebuilt()
    {
        // An empty file is not corrupt to SQLite - it is a valid, freshly-initialisable
        // database. Confirms the benign path stays benign: no rebuild, no ".corrupt" left
        // behind, still a usable store.
        string path = Db();
        File.WriteAllBytes(path, []);

        using ContentDb db = ContentDb.OpenOrRebuild(path);

        Assert.False(db.WasRebuilt);
        Assert.Equal(ContentDb.SchemaVersion.ToString(), db.Get("schema"));
        Assert.False(File.Exists(path + ".corrupt"));
        db.Enqueue("C", 1, @"C:\a.pdf", ResultKind.Document, "new");
        Assert.Equal(1, db.PendingCount());
    }

    [Fact]
    public void TheSuffixSetIsVersionedSoAChangeToItForcesAFreshWalk()
    {
        // FileKinds' extension tables are code. If a later version adds ".pages" to the
        // document list, every existing install would keep asking the helper for the OLD
        // suffix list forever, and those files would never be enumerated at all.
        using var db = new ContentDb(Db());

        Assert.Null(db.Get("suffixes:C"));                    // never walked yet
        db.SetSuffixVersion('C', [".pdf", ".txt"]);
        string stamped = db.Get("suffixes:C")!;

        Assert.False(db.SuffixesChanged('C', [".pdf", ".txt"]));
        Assert.False(db.SuffixesChanged('C', [".txt", ".pdf"]));   // a set, not a sequence
        Assert.True(db.SuffixesChanged('C', [".pdf"]));            // one fewer extension is a change
        Assert.True(db.SuffixesChanged('C', [".pdf", ".txt", ".pages"]));   // one more is too
        Assert.Equal(stamped, db.Get("suffixes:C"));          // asking did not rewrite it
    }

    [Fact]
    public void TheSuffixStampIsPerVolumeAndFallsBackToWhatAnEarlierBuildWrote()
    {
        // The stamp answers "does THIS drive owe a walk", so it is a row per drive. Stamping C:
        // must say nothing about D:, or the first drive walked after an upgrade discharges the
        // question for every other drive on the machine, permanently.
        using var db = new ContentDb(Db());

        db.SetSuffixVersion('C', [".pdf", ".txt"]);

        Assert.False(db.SuffixSetOutOfDate('C', [".pdf", ".txt"]));
        Assert.True(db.SuffixSetOutOfDate('C', [".pdf", ".txt", ".pages"]));
        Assert.False(db.SuffixSetOutOfDate('D', [".pdf", ".txt", ".pages"]),
                     "D: has never been walked, so it owes nothing by this route");

        // An index written before the per-volume rows carries one stamp for the whole database.
        // Reading that as "never walked" on every drive would skip the re-walk the extension
        // list actually changed under, silently, on exactly the installs that have been running
        // longest.
        using var older = new ContentDb(Db("older.db"));
        older.SetSuffixVersion([".pdf"]);

        Assert.True(older.SuffixSetOutOfDate('C', [".pdf", ".txt"]));
        Assert.True(older.SuffixSetOutOfDate('D', [".pdf", ".txt"]));

        // And a drive that has since been walked by this build answers off its own row.
        older.SetSuffixVersion('C', [".pdf", ".txt"]);
        Assert.False(older.SuffixSetOutOfDate('C', [".pdf", ".txt"]));
        Assert.True(older.SuffixSetOutOfDate('D', [".pdf", ".txt"]));
    }

    [Fact]
    public void AStepThatChangesWhatIsEligibleAsksForTheWholeDiskAgain()
    {
        // A re-queue moves rows that already exist. A file that was never offered to the queue at
        // all - everything inside a checkout, under the rule that refused them outright - has no
        // row to move, and nothing else will ever reach it: the journal reports what CHANGES, and
        // a folder of finished work never changes again. Forgetting the volume position is the
        // only thing that says "look at the whole disk".
        string path = Db();
        var step = new[] { new ContentDb.Migration(2, [(int)ResultKind.Photo], "eligibility", ReWalk: true) };

        using (var db = new ContentDb(path))
        {
            db.SetUsnPosition('C', journalId: 0xABCDEF, usn: 90210);
            db.SetUsnPosition('D', journalId: 0x123456, usn: 4242);
            db.Set("schema", "1");
        }

        using (var db = new ContentDb(path, migrations: step))
        {
            Assert.Null(db.UsnPosition('C'));
            Assert.Null(db.UsnPosition('D'));
        }
    }

    [Fact]
    public void AStepThatOnlyChangesWhatIsStoredLeavesThePositionAlone()
    {
        // The other half, and the one that matters more: re-walking a finished disk for a change
        // that only affects rows already in the index is the expensive mistake. ReWalk is opt-in
        // per step for exactly that reason.
        string path = Db();
        var step = new[] { new ContentDb.Migration(2, [(int)ResultKind.Document], "stored differently") };

        using (var db = new ContentDb(path))
        {
            db.SetUsnPosition('C', journalId: 0xABCDEF, usn: 90210);
            db.Set("schema", "1");
        }

        using (var db = new ContentDb(path, migrations: step))
            Assert.Equal((0xABCDEFUL, 90210L), db.UsnPosition('C'));
    }
}
