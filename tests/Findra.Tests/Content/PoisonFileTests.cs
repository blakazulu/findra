using Findra;
using Xunit;

/// <summary>
/// A file that takes the whole indexer down, rather than merely throwing.
///
/// <para>A managed throw was already handled - the row is recorded Failed and dequeued. One that
/// kills the PROCESS never reaches that code: an access violation inside an image, model or media
/// library, a stack overflow, an out-of-memory. The child was restarted, <c>TakeNext</c>'s
/// deterministic ordering handed back the same row, and it died again. The queue stopped for good
/// at that file, everything behind it was never read, and the only symptom was a repeating restart
/// line in a log nobody reads.</para>
/// </summary>
public sealed class PoisonFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-poison-" + Guid.NewGuid().ToString("N"));

    private ContentDb Open()
    {
        Directory.CreateDirectory(_dir);
        return new ContentDb(Path.Combine(_dir, "search.db"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void AttemptsAreCountedAndTheRowIsSpentOnlyAfterItsLast()
    {
        using ContentDb db = Open();
        db.Enqueue("C", 7, @"C:\a\poison.pdf", ResultKind.Document, "first pass");
        ContentDb.Pending item = db.TakeNext()!.Value;

        for (int i = 1; i <= ContentDb.MaxAttempts; i++)
            Assert.False(db.CountAttempt(item.Id), $"the row was written off on attempt {i}");

        Assert.True(db.CountAttempt(item.Id), "the row was never written off");
    }

    [Fact]
    public void TheCountSurvivesTheProcessThatWasCountingIt()
    {
        // This is the whole design. Counting after the file is read records nothing when the
        // attempt takes the process with it - and that is precisely the attempt worth counting.
        // Written and committed first, the raised count is on the disk for the restarted child.
        string path = Path.Combine(_dir, "search.db");
        Directory.CreateDirectory(_dir);
        long id;
        using (var first = new ContentDb(path))
        {
            first.Enqueue("C", 7, @"C:\a\poison.pdf", ResultKind.Document, "first pass");
            id = first.TakeNext()!.Value.Id;
            Assert.False(first.CountAttempt(id));
            Assert.False(first.CountAttempt(id));
        }

        // A new process over the same file, as a restarted child is.
        using var second = new ContentDb(path);
        Assert.Equal(2, second.TakeNext()!.Value.Attempts);
        Assert.False(second.CountAttempt(id));
        Assert.True(second.CountAttempt(id), "the count did not survive the process that wrote it");
    }

    [Fact]
    public void ARowThatIsTakenOnceIsNotWrittenOffOnTheNextFile()
    {
        // The counter is per row. A queue of a thousand ordinary files must not write any of them
        // off just because a thousand attempts have been made in total.
        using ContentDb db = Open();
        for (int i = 0; i < 5; i++)
            db.Enqueue("C", (ulong)(100 + i), $@"C:\a\{i}.pdf", ResultKind.Document, "first pass");

        for (int i = 0; i < 5; i++)
        {
            ContentDb.Pending p = db.TakeNext()!.Value;
            Assert.False(db.CountAttempt(p.Id));
            db.Dequeue(p.Id);
        }
    }

    [Fact]
    public void AnIndexWrittenBeforeTheColumnExistedGainsIt()
    {
        // CREATE TABLE IF NOT EXISTS does nothing to a table already there, so a column added to
        // the schema text reaches new databases only. Every machine upgrading has the old shape.
        string path = Path.Combine(_dir, "old.db");
        Directory.CreateDirectory(_dir);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE pending(
                id INTEGER PRIMARY KEY, vol TEXT NOT NULL, frn INTEGER NOT NULL, path TEXT NOT NULL,
                kind INTEGER NOT NULL, reason TEXT NOT NULL, queued_at INTEGER NOT NULL,
                UNIQUE(vol, frn));";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var db = new ContentDb(path);
        db.Enqueue("C", 7, @"C:\a\poison.pdf", ResultKind.Document, "first pass");
        ContentDb.Pending item = db.TakeNext()!.Value;
        Assert.Equal(0, item.Attempts);
        Assert.False(db.CountAttempt(item.Id));
    }
}
