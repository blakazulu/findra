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
        using var db = new ContentDb(Db());

        Assert.Equal(ContentDb.SchemaVersion.ToString(), db.Get("schema"));
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
            Assert.Equal("1", db.Get("schema"));
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
    public void TheSuffixSetIsVersionedSoAChangeToItForcesAFreshWalk()
    {
        // FileKinds' extension tables are code. If a later version adds ".pages" to the
        // document list, every existing install would keep asking the helper for the OLD
        // suffix list forever, and those files would never be enumerated at all.
        using var db = new ContentDb(Db());

        Assert.Null(db.Get("suffixes"));                      // never walked yet
        db.SetSuffixVersion([".pdf", ".txt"]);
        string stamped = db.Get("suffixes")!;

        Assert.False(db.SuffixesChanged([".pdf", ".txt"]));
        Assert.False(db.SuffixesChanged([".txt", ".pdf"]));   // a set, not a sequence
        Assert.True(db.SuffixesChanged([".pdf"]));            // one fewer extension is a change
        Assert.True(db.SuffixesChanged([".pdf", ".txt", ".pages"]));   // one more is too
        Assert.Equal(stamped, db.Get("suffixes"));            // asking did not rewrite it
    }
}
