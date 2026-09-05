using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Findra;

// The content index's database, and the ONLY channel between the UI and the indexer child.
//
// The UI writes the queue (`pending`) and the control rows - the schema stamp, the consumed USN
// position per volume, the suffix stamp, `paused`. The indexer writes everything else - items,
// segments and the FTS text - and its own status rows. Neither process ever holds a lock the other
// waits on for long: WAL mode, short transactions, and a busy timeout that covers the indexer's
// largest write. A second pipe would have needed a protocol, reconnect logic and a story for a dead
// peer; a table needs none of that and survives either process dying mid-write.
//
// Segment kinds: 0 = a whole image, 1 = a text chunk of a document, 2 = a transcript segment
// (audio or video speech), 3 = a video frame at t0. This build writes only kind 1 - words in
// documents is the one capability that needs no model - and the other three are the numbers a
// later capability will write, fixed here so the meaning of a stored row never shifts under it.
//
// ONE FLOW OWNS THE WRITER, AND THAT IS ENFORCED HERE. A SqliteConnection is not safe for two
// flows at once, and this one is not merely used carefully - it refuses. Every mutating method
// claims the connection on the way in, Begin holds the claim for the whole transaction, and a
// second flow arriving is an immediate InvalidOperationException naming the rule and the call that
// broke it. Everything that only reads - the card, --searchprobe, --searchtest, --searchbench -
// opens a read-only connection of its own, which is not guarded and does not need to be.
//
// The failure that buys is not hypothetical, and it is cheap to arrive at. An index thread and a
// debounce timer on the thread pool share one connection; both open a transaction; the provider
// refuses the second with an InvalidOperationException; a catch swallows it as noise - and the
// batch of journal changes has already been drained out of its queue into a local list, so those
// changes are gone and those files stay stale for good, while the log says so once per process and
// is silent afterwards. Findra survives that shape because the consumed USN position is
// written inside the same transaction as the enqueues it came from, so a throw rolls the position
// back and the next subscription replays the gap - do not change that either - but the swallowed
// exception is a bug that takes months to find, and the point of the guard is that it says which
// rule was broken rather than leaving somebody to infer it from "wrong transaction".
public sealed class ContentDb : IDisposable
{
    public const int SegImage = 0, SegText = 1, SegSpeech = 2, SegFrame = 3;
    public const int StateQueued = 0, StateIndexed = 1, StateFailed = 2, StateSkipped = 3;

    /// <summary>The queue reason that means "this file is gone, take it out of the index". It
    /// crosses from the interface into the indexer and into <see cref="TakeNext"/>'s ordering, so
    /// it is one constant rather than a literal per reader: a typo in any copy is silent, and
    /// deletes quietly stop being deletes.</summary>
    public const string ReasonDelete = "delete";

    /// <summary>The relational shape of this database. Bumped only when a change makes rows
    /// already on disk mean something different.</summary>
    public const int SchemaVersion = 4;

    /// <summary>One schema step. <c>InvalidatedKinds</c> is what that step made stale - and
    /// nothing else is re-queued. Re-indexing a finished disk because an upgrade did not look
    /// first is the worst thing this product can do to someone (spec §2a).</summary>
    /// <param name="ReWalk">Forget every volume's journal position, so the next start owes a full
    /// pass. For a step that changed WHICH FILES are eligible rather than what is stored about the
    /// ones already known: a re-queue moves rows that exist, and a file that was never queued at
    /// all has no row to move. Nothing else can reach it - the journal only reports what changes,
    /// and a folder of finished work never changes again.</param>
    public readonly record struct Migration(int To, int[] InvalidatedKinds, string Reason, bool ReWalk = false);

    /// <summary>
    /// A schema change appends the step that invalidates whatever it invalidated, and NOTHING
    /// else is re-queued - re-indexing a finished disk because an upgrade did not look first is
    /// the worst thing this product can do to somebody (spec §2a).
    ///
    /// <para>Step 2 invalidates images and only images. Three things changed about them at once
    /// and all three are about which pictures exist and what is stored for them: the size floor
    /// fell from 10 KB to 2 KB, so images previously recorded "an icon, not a picture" are real
    /// candidates; `ico` and `avif` joined the kinds this build reads; and the words recognised
    /// inside a picture stopped being embedded as prose. Documents, recordings and video are
    /// untouched by every one of those, so they are not in this list and are not read again.</para>
    /// </summary>
    public static readonly IReadOnlyList<Migration> Migrations =
    [
        new(To: 2, InvalidatedKinds: [(int)ResultKind.Photo],
            Reason: "images read differently, and folders are skipped only when asked",
            // Both halves are needed and they do different work. The re-queue picks up images
            // already known - the ones recorded "an icon, not a picture" under the old floor. The
            // re-walk is for files that were never offered at all: a checkout's contents were
            // refused outright, so nothing about them is in the index to re-queue.
            ReWalk: true),

        // Step 2 queued every photo on the machine and re-read none of them: it passed its own
        // prose as the queue reason, and the indexer dequeues a row untouched unless the reason is
        // Recheck. The bug was in the caller rather than in the step, so fixing it changes nothing
        // on a machine that already stamped 2 - the step will not run again. This is that step,
        // done properly, and it is the only way the photos on a machine that has already upgraded
        // are ever looked at again.
        //
        // No ReWalk. Step 2's re-walk DID happen - clearing the journal positions was never
        // conditioned on the reason - so every file a checkout used to hide is already in the
        // index. Walking the whole disk a second time for rows that are all present is exactly the
        // expensive mistake spec 2a names.
        new(To: 3, InvalidatedKinds: [(int)ResultKind.Photo],
            Reason: "pictures are read again, because the step that said so re-read none of them"),

        // Every e5 vector in the index was computed by a different model. The meaning model went
        // from a quantised export to a full-precision one, and the two are not interchangeable -
        // the same passage embedded by each comes back 0.974 apart, which is a different vector
        // space rather than a rounding difference. A stored vector from the old file scored against
        // a query from the new one is a number with nothing behind it.
        //
        // Documents, recordings and video, because those are the kinds that carry an e5 vector -
        // Decoders.Document embeds chunks and Speech.Merge embeds transcript lines. Photos do not:
        // their vectors come from SigLIP-2's vision tower, which has not changed, and re-reading
        // every picture on a disk for a change that cannot touch them is the expensive mistake
        // spec 2a names.
        //
        // No ReWalk. WHICH files are eligible is unchanged; only what is stored about the ones
        // already known. Every row that needs re-reading is already in the index to be re-queued.
        new(To: 4, InvalidatedKinds:
                [(int)ResultKind.Document, (int)ResultKind.Audio, (int)ResultKind.Video],
            Reason: "the meaning model is full precision now, so every vector it wrote is stale"),
    ];

    private readonly SqliteConnection _c;

    public string Path { get; }

    /// <summary>True when the file on disk could not be opened and a fresh index was built in its
    /// place. The UI reads this to say so; a log line alone would leave the card looking idle
    /// while an entire index quietly went missing (spec §2a).</summary>
    public bool WasRebuilt { get; private set; }

    // Paths.Index, NOT Paths.Ensure(Paths.Index): a property getter that creates a directory means
    // --searchtest and --searchprobe bring %LOCALAPPDATA%\Findra\index\ into existence merely by
    // asking whether a file is there. The Ensure belongs in the constructor, which is the one
    // place a write actually follows.
    public static string DefaultPath => System.IO.Path.Combine(Paths.Index, "search.db");

    public ContentDb(string? path = null, bool readOnly = false, IReadOnlyList<Migration>? migrations = null)
    {
        Path = path ?? DefaultPath;
        _guarded = !readOnly;
        // Skip for a bare DataSource with no directory component - ":memory:" (OpenOrRebuild's
        // last rung) and a bare relative filename both hit this. Directory.CreateDirectory("")
        // throws ArgumentException, which would defeat the very "this cannot throw" promise the
        // in-memory fallback exists to keep.
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Paths.Ensure(dir);
        _c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 15,
        }.ToString());
        try
        {
            // Open is INSIDE the try. It throws for a path that is a directory, a file another
            // process holds exclusively, or a database in a mode this connection cannot have -
            // and outside the try that throw leaves this connection undisposed, which on Windows
            // keeps a handle on the very file OpenOrRebuild then has to move aside.
            _c.Open();
            Exec("PRAGMA journal_mode=WAL");
            Exec("PRAGMA synchronous=NORMAL");
            Exec("PRAGMA busy_timeout=15000");
            if (!readOnly)
            {
                CreateSchema();
                OpenSchema(migrations ?? Migrations);
            }
        }
        catch
        {
            // Opening the connection succeeds even over a file that is not a database - the first
            // PRAGMA is where that is discovered. Throwing from here leaves nobody holding this
            // connection, and an undisposed handle keeps the file locked on Windows.
            _c.Dispose();
            throw;
        }
    }

    private void CreateSchema()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS items(
    id INTEGER PRIMARY KEY, vol TEXT NOT NULL, frn INTEGER NOT NULL, path TEXT NOT NULL,
    kind INTEGER NOT NULL, mtime INTEGER NOT NULL DEFAULT 0, size INTEGER NOT NULL DEFAULT 0,
    state INTEGER NOT NULL DEFAULT 0, error TEXT, indexed_at INTEGER NOT NULL DEFAULT 0,
    UNIQUE(vol, frn));
CREATE INDEX IF NOT EXISTS items_path ON items(path);
CREATE TABLE IF NOT EXISTS pending(
    id INTEGER PRIMARY KEY, vol TEXT NOT NULL, frn INTEGER NOT NULL, path TEXT NOT NULL,
    kind INTEGER NOT NULL, reason TEXT NOT NULL, queued_at INTEGER NOT NULL,
    attempts INTEGER NOT NULL DEFAULT 0,
    UNIQUE(vol, frn));
CREATE TABLE IF NOT EXISTS segments(
    id INTEGER PRIMARY KEY, item INTEGER NOT NULL, kind INTEGER NOT NULL,
    t0 REAL NOT NULL DEFAULT -1, t1 REAL NOT NULL DEFAULT -1,
    vec INTEGER NOT NULL DEFAULT -1, text TEXT NOT NULL DEFAULT '');
CREATE INDEX IF NOT EXISTS segments_item ON segments(item);
CREATE INDEX IF NOT EXISTS segments_vec ON segments(vec);
CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(text, content='segments', content_rowid='id',
    tokenize='unicode61 remove_diacritics 2');
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS opened(path TEXT PRIMARY KEY, count INTEGER NOT NULL, last INTEGER NOT NULL);");

        // CREATE TABLE IF NOT EXISTS does nothing to a table that is already there, so a column
        // added to the text above reaches new databases only. Every column added after the first
        // release needs this too, and it is deliberately NOT a numbered migration: those exist to
        // decide which files are stale, and adding a column that starts at its default makes no
        // row mean anything different. Asked of the table rather than of a stamp, so it is right
        // however the database got here.
        AddColumnIfMissing("pending", "attempts", "INTEGER NOT NULL DEFAULT 0");
    }

    private void AddColumnIfMissing(string table, string column, string decl)
    {
        bool has = false;
        using (var ask = _c.CreateCommand())
        {
            ask.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$n";
            ask.Parameters.AddWithValue("$n", column);
            has = ask.ExecuteScalar() is not null;
        }
        if (has) return;
        Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl}");
        Log.Info("index", $"the {table} table gained its {column} column");
    }

    // ---- the schema stamp, and the migrations it gates ----

    /// <summary>
    /// The schema version this open found on disk and migrated FROM.
    ///
    /// <para><see cref="SchemaVersion"/> for a database already current AND for a brand-new one:
    /// an unstamped database with nothing in it has never been written by an older build, so
    /// there is nothing to migrate it from. Reading it as version zero would run every step over
    /// an empty index on every fresh install - free while a step is only a re-queue, and not free
    /// at all if one is ever DDL.</para>
    ///
    /// <para>It is recorded because that decision is otherwise invisible. Both branches stamp
    /// <see cref="SchemaVersion"/> on the way out and a fresh database has no rows for a re-queue
    /// to move, so "treated as current" and "treated as version zero" leave identical evidence -
    /// which is exactly why the test that was supposed to stop the second could not fail.</para>
    /// </summary>
    public int OpenedFromSchema { get; private set; } = SchemaVersion;

    /// <summary>The reasons of the migration steps this open actually ran, in order. Empty for a
    /// database already at the current schema and for a brand-new one. It is the readable form of
    /// the log lines below, and the only way "no step ran" can be asserted rather than assumed.
    /// </summary>
    public IReadOnlyList<string> MigrationsRun => _migrationsRun;

    private readonly List<string> _migrationsRun = [];

    /// <summary>Read the recorded schema version, run whatever steps stand between it and this
    /// build, and stamp the result. A step re-queues only the kinds it invalidated; everything
    /// else on disk is left exactly as it was found.</summary>
    private void OpenSchema(IReadOnlyList<Migration> steps)
    {
        string? stamped = Get("schema");
        int from;
        if (int.TryParse(stamped, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) from = v;
        else from = HasAnyItem() ? 0 : SchemaVersion;   // never stamped + never written = brand new
        OpenedFromSchema = from;

        if (from == SchemaVersion) { Set("schema", Stamp(SchemaVersion)); return; }
        if (from > SchemaVersion)
        {
            // A newer Findra wrote this index. Read it as-is rather than "migrating" backwards.
            Log.Warn("index", $"index schema {from} is newer than this build's {SchemaVersion} - using it as-is");
            return;
        }

        foreach (Migration m in steps)
        {
            if (m.To <= from || m.To > SchemaVersion) continue;
            _migrationsRun.Add(m.Reason);
            if (m.ReWalk) ClearAllUsnPositions();
            // Indexer.Recheck, NOT the migration's prose. RequeueKinds says it in its own note:
            // the indexer dequeues a row untouched unless the reason is Recheck, the row is
            // Skipped, or the file's bytes have moved. Passing the sentence meant for the log
            // queued every photo on the machine and re-read none of them - the already-indexed
            // ones, which are the whole point of a migration that changes how a kind is read,
            // each came back "current" the moment they were taken. The pill counted them down,
            // the log said "re-queued", and the index was exactly as it had been.
            int n = RequeueKinds(m.InvalidatedKinds, Indexer.Recheck);
            Log.Info("index", $"schema {from} -> {m.To}: {m.Reason}, {n.ToString("N0", CultureInfo.InvariantCulture)} file(s) re-queued");
        }
        Set("schema", Stamp(SchemaVersion));
    }

    private static string Stamp(int n) => n.ToString(CultureInfo.InvariantCulture);

    private bool HasAnyItem()
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM items LIMIT 1";
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Open the index, or rebuild it if it cannot be opened. A database whose file is truncated,
    /// half-written by a lost power cycle, or replaced by something that is not a database throws
    /// out of the constructor - and the only caller is a background loop, so the exception would
    /// reach nobody and content search would be dead for that install with no message anywhere.
    /// The unreadable file is MOVED ASIDE rather than deleted: it is evidence, it costs a few
    /// megabytes, and its owner may want it. Spec §2a: rebuilt, and the UI says so.
    /// This method genuinely cannot throw - see <see cref="Rebuild"/> for the three rungs that
    /// make that true even when the path is a directory or is locked by another process.
    /// </summary>
    public static ContentDb OpenOrRebuild(string? path = null)
    {
        string p = path ?? DefaultPath;
        try
        {
            return new ContentDb(p);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            Log.Error("index", $"the index at {p} could not be opened, rebuilding it", ex);
            return Rebuild(p);
        }
    }

    // Three rungs, each strictly safer than the last, because a background loop has nobody to
    // catch whatever this throws:
    //  1. Move the broken path aside under the fixed ".corrupt" name and reopen at the real
    //     path - the common case (truncated file, wrong file type, a stray directory).
    //  2. If that did not clear the path - the fixed name is itself locked, or MoveAside could
    //     not touch what is there - try a timestamped name instead, in case the fixed name is
    //     specifically what is unavailable.
    //  3. If NOTHING at that path can be moved or deleted - typically an exclusive lock held by
    //     another process, which both rungs above retry against the SAME source and so both
    //     fail against - fall back to an in-memory store. ":memory:" touches no path and no
    //     lock, so this rung cannot fail the way the first two can. Content search runs
    //     degraded-but-alive for this session; WasRebuilt still reports true so the caller never
    //     mistakes it for a healthy, populated index.
    private static ContentDb Rebuild(string path)
    {
        MoveAside(path, ".corrupt");
        try { return new ContentDb(path) { WasRebuilt = true }; }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            Log.Error("index", $"{path} is still unusable after moving it aside, trying a timestamped name", ex);
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        MoveAside(path, ".corrupt-" + stamp);
        try { return new ContentDb(path) { WasRebuilt = true }; }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            Log.Error("index", $"{path} is still unusable after two rebuild attempts (likely locked by " +
                                 "another process) - falling back to an in-memory index for this session", ex);
        }

        return new ContentDb(":memory:") { WasRebuilt = true };
    }

    // The write-ahead log and shared-memory files belong to the database they sit beside; leaving
    // them behind hands the fresh database somebody else's journal. `tag` names where the
    // unreadable path goes - see Rebuild's rungs above.
    private static void MoveAside(string path, string tag)
    {
        foreach (string tail in new[] { "", "-wal", "-shm" })
        {
            string from = path + tail;
            string to = path + tag + tail;
            if (Directory.Exists(from))
            {
                // A directory sitting at the database path: File.Exists (and File.Move) below
                // silently ignore it, which is how this used to escape unnoticed - handled
                // explicitly rather than falling through to the file branch.
                try { Directory.Move(from, to); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Error("index", $"could not move directory {from} aside, deleting it instead :: {ex.Message}");
                    try { Directory.Delete(from, recursive: true); }
                    catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { }
                }
                continue;
            }
            if (!File.Exists(from)) continue;
            try { File.Move(from, to, overwrite: true); }
            catch (IOException ex)
            {
                Log.Error("index", $"could not move {from} aside, deleting it instead :: {ex.Message}");
                try { File.Delete(from); } catch (IOException) { }
            }
        }
    }

    public void Dispose() => _c.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- one flow at a time, enforced ---------------------------------------------------------

    /// <summary>Whether this connection polices its own ownership. Only a writer does: the card
    /// reads through its own read-only connection from pool threads, more than one of them over a
    /// session, and arming this there would break the card for no gain.</summary>
    private readonly bool _guarded;

    /// <summary>The managed thread id of the flow currently inside the writer, 0 when nobody is,
    /// and how deep it has re-entered. A detector rather than a lock: see <see cref="Claim"/>.
    /// </summary>
    private int _inside;
    private int _depth;

    /// <summary>
    /// Say that this flow is now inside the writer, and refuse if another one already is.
    ///
    /// <para>THE RULE IS ONE FLOW AT A TIME, and it is enforced here rather than remembered across
    /// three files. The failure it prevents is not hypothetical: an index thread and a debounce
    /// timer share one connection, both open a transaction, the provider refuses the second, the
    /// catch swallows it - and the batch of changes has already been drained out of its queue into
    /// a local list, so those files stay stale permanently, while the log says so once per process
    /// and is then silent.</para>
    ///
    /// <para>It refuses rather than waits, on purpose. A lock would let a second writer in by
    /// serialising it, which hides the design violation instead of reporting it; and the claim has
    /// to span a whole transaction, so a lock could be held for the length of a full disk walk.
    /// Nothing user-facing waits on this connection - the card has its own - so there is nothing to
    /// be gained by blocking and a rule to be lost.</para>
    ///
    /// <para>It is not pinned to the thread that constructed the writer, because the loop that owns
    /// it is async: it awaits a pipe session and a delay with <c>ConfigureAwait(false)</c>, so its
    /// continuations resume on whatever pool thread is free and the owning thread id changes
    /// several times a minute in ordinary running. A construction-thread check would fire on the
    /// first await and never stop. What is forbidden is OVERLAP, not movement.</para>
    ///
    /// <para>It is a detector and not a barrier: two threads arriving in the same few nanoseconds
    /// could both pass. Any overlap that lasts longer than that - which is every real one, because
    /// the shortest thing behind this guard is a SQLite command - is caught at the call that broke
    /// the rule.</para>
    /// </summary>
    private void Claim(string what)
    {
        if (!_guarded) return;
        int me = Environment.CurrentManagedThreadId;
        int held = Volatile.Read(ref _inside);
        if (held != 0 && held != me)
            throw new InvalidOperationException(
                $"ContentDb.{what} was called on thread {me.ToString(CultureInfo.InvariantCulture)} while " +
                $"thread {held.ToString(CultureInfo.InvariantCulture)} was still inside this writer. " +
                "One flow owns the writer connection at a time; everything else reads through a " +
                "read-only connection of its own. Two flows sharing one connection is how a batch of " +
                "journal changes gets lost to a swallowed nested-transaction error.");
        Volatile.Write(ref _inside, me);
        _depth++;
    }

    private void Leave()
    {
        if (!_guarded) return;
        if (--_depth <= 0) { _depth = 0; Volatile.Write(ref _inside, 0); }
    }

    /// <summary>One mutating call's claim on the writer.</summary>
    private readonly struct Use : IDisposable
    {
        private readonly ContentDb _db;
        public Use(ContentDb db, string what) { _db = db; db.Claim(what); }
        public void Dispose() => _db.Leave();
    }

    private Use Enter([CallerMemberName] string what = "") => new(this, what);

    /// <summary>
    /// One transaction, and the owning flow's claim on the writer for as long as it is open.
    ///
    /// <para>The claim has to span the transaction and not merely each call inside it: the damage
    /// in the ported engine happened between one flow's <c>BEGIN</c> and its <c>COMMIT</c>, which
    /// is a window no per-call check can see. Converting to <see cref="SqliteTransaction"/> is
    /// implicit so every existing call site keeps reading the way it did.</para>
    /// </summary>
    public sealed class Scope : IDisposable
    {
        private readonly ContentDb _db;
        private readonly SqliteTransaction _tx;
        private bool _left;

        internal Scope(ContentDb db, SqliteTransaction tx) { _db = db; _tx = tx; }

        public void Commit() => _tx.Commit();
        public void Rollback() => _tx.Rollback();

        public void Dispose()
        {
            if (_left) return;
            _left = true;
            try { _tx.Dispose(); } finally { _db.Leave(); }
        }

        public static implicit operator SqliteTransaction(Scope s) => s._tx;
    }

    public Scope Begin()
    {
        Claim(nameof(Begin));
        try { return new Scope(this, _c.BeginTransaction()); }
        catch { Leave(); throw; }
    }

    /// <summary>What the bundled SQLite was built with. Everything here is FTS5, and a bundle
    /// without it fails deep inside a child process nobody is watching - so it is checkable.</summary>
    public static IReadOnlyList<string> CompileOptions()
    {
        var opts = new List<string>();
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ":memory:" }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA compile_options";
        using var r = cmd.ExecuteReader();
        while (r.Read()) opts.Add(r.GetString(0));
        return opts;
    }

    // ---- meta: the status and control rows ----

    public string? Get(string key)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    // The transaction parameter exists so the queue feeder can record a USN position in the SAME
    // transaction as the enqueues it came from: a crash between the two either re-queues work that
    // was already done or loses work that was not.
    public void Set(string key, string value, SqliteTransaction? tx = null)
    {
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ---- the consumed USN position, one row per volume ----

    /// <summary>Where journal consumption for this volume got to, and which journal that was.
    /// A different journal id means the volume's journal was recreated and the position means
    /// nothing - the caller does a full pass instead of replaying from a number in another
    /// journal's coordinate space.</summary>
    public (ulong JournalId, long Usn)? UsnPosition(char volume)
    {
        string? v = Get("usn:" + char.ToUpperInvariant(volume));
        if (v is null) return null;
        string[] parts = v.Split(' ');
        if (parts.Length != 2) return null;
        return ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong jid)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long usn)
            ? (jid, usn) : null;
    }

    public void SetUsnPosition(char volume, ulong journalId, long usn, SqliteTransaction? tx = null)
        => Set("usn:" + char.ToUpperInvariant(volume),
               journalId.ToString(CultureInfo.InvariantCulture) + " " + usn.ToString(CultureInfo.InvariantCulture),
               tx);

    /// <summary>Forget where this volume got to. The row is DELETED rather than zeroed: usn 0 is
    /// a real position at the head of a journal, and a volume that has no position must also stop
    /// appearing in <see cref="KnownVolumes"/> and in the cursors handed to the helper, or the
    /// next subscribe resumes from a place the journal threw away.</summary>
    /// <summary>Forget every volume's position, which is how a migration says "what is eligible
    /// has changed, so look at the whole disk again". <c>JournalTail.ResumeFrom</c> reads an absent
    /// cursor as a full pass owed, which is exactly the answer wanted.</summary>
    public void ClearAllUsnPositions()
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "DELETE FROM meta WHERE key LIKE 'usn:%'";
        cmd.ExecuteNonQuery();
    }

    public void ClearUsnPosition(char volume, SqliteTransaction? tx = null)
    {
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", "usn:" + char.ToUpperInvariant(volume));
        cmd.ExecuteNonQuery();
    }

    // ---- the walk debt: a hole in the journal stream that only a full pass can close ----

    /// <summary>
    /// True when something lost journal events for this volume and nothing has walked it since.
    ///
    /// A ROW, not a field on whatever object noticed. A drop is discovered in one process and
    /// discharged in another launch, and an in-memory latch is discharged for free by the very
    /// restart that makes the hole permanent - the one moment nothing in the system would ever
    /// notice again.
    /// </summary>
    public bool WalkOwed(char volume) => Get("walk:" + char.ToUpperInvariant(volume)) == "1";

    public void SetWalkOwed(char volume, SqliteTransaction? tx = null)
        => Set("walk:" + char.ToUpperInvariant(volume), "1", tx);

    /// <summary>Discharged only by a completed full pass. Nothing else may call this.</summary>
    public void ClearWalkOwed(char volume, SqliteTransaction? tx = null)
        => Set("walk:" + char.ToUpperInvariant(volume), "0", tx);

    /// <summary>Every volume this index has ever recorded a position for.</summary>
    public IReadOnlyList<char> KnownVolumes()
    {
        var list = new List<char>();
        using (var cmd = _c.CreateCommand())
        {
            cmd.CommandText = "SELECT key FROM meta WHERE key LIKE 'usn:_'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                if (key.Length > 0) list.Add(key[^1]);
            }
        }
        list.Sort();
        return list;
    }

    // ---- the suffix stamp: which extension set each volume was walked with ----

    /// <summary>
    /// Record the extension set THIS VOLUME was walked with. FileKinds' tables are code: a later
    /// version that adds an extension would otherwise keep asking for the OLD suffix list
    /// forever, and files with the new extension would never be enumerated on any existing
    /// install.
    ///
    /// <para>One row per volume, like <c>usn:</c>, because the walk this stamp demands is per
    /// volume. A single row for the whole index is answered by whichever drive is walked first,
    /// and every other drive then finds the list already current and is skipped - permanently,
    /// since the stamp can never differ again. It is also what lets a drive that was plugged in
    /// after the last extension change be walked when it arrives.</para>
    /// </summary>
    public void SetSuffixVersion(char volume, IReadOnlyList<string> suffixes, SqliteTransaction? tx = null)
        => Set(SuffixKey(volume), SuffixHash(suffixes), tx);

    /// <summary>
    /// The stamp an index written before the per-volume rows carries: one row for the whole
    /// database. Nothing in this build writes it during normal operation - it is here so an index
    /// migrated from such a build still answers the question below for volumes that have no row
    /// of their own yet, rather than reading as never walked and quietly skipping the re-walk
    /// their extension list actually needs.
    /// </summary>
    public void SetSuffixVersion(IReadOnlyList<string> suffixes, SqliteTransaction? tx = null)
        => Set("suffixes", SuffixHash(suffixes), tx);

    /// <summary>Compare this volume's stamp against a suffix set without writing anything.</summary>
    public bool SuffixesChanged(char volume, IReadOnlyList<string> suffixes)
        => StampFor(volume) != SuffixHash(suffixes);

    /// <summary>
    /// True only when a PREVIOUS walk of THIS VOLUME was stamped and it used a different suffix
    /// set from this one. A volume that has never been walked answers false, which is not the
    /// same question <see cref="SuffixesChanged(char, IReadOnlyList{string})"/> answers: an absent
    /// stamp differs from every hash, and reading that as "the extension list changed" would say
    /// a fresh install owes a re-walk of a disk it has never walked - which is true, but said by
    /// the wrong mechanism, and it makes the debt that a lost journal event leaves
    /// indistinguishable from an empty database.
    /// </summary>
    public bool SuffixSetOutOfDate(char volume, IReadOnlyList<string> suffixes)
        => StampFor(volume) is { } stamped && stamped != SuffixHash(suffixes);

    private static string SuffixKey(char volume) => "suffixes:" + char.ToUpperInvariant(volume);

    // A volume with no stamp of its own falls back to the whole-index row an earlier build wrote,
    // so an upgrade in the middle of an index's life does not read every drive as never walked.
    private string? StampFor(char volume) => Get(SuffixKey(volume)) ?? Get("suffixes");

    // A hash of the SET, not of the sequence: the caller builds its list from HashSet enumeration,
    // whose order is not contractual, and a stamp that moved with it would re-walk every disk on
    // every launch.
    private static string SuffixHash(IReadOnlyList<string> suffixes)
    {
        var sorted = new List<string>(suffixes.Count);
        foreach (string s in suffixes) sorted.Add(s.ToLowerInvariant());
        sorted.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sorted))));
    }

    // ---- what was opened from the card (the UI's own; the indexer never touches it) ----

    public void RecordOpen(string path)
    {
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "INSERT INTO opened(path,count,last) VALUES($p,1,$t) ON CONFLICT(path) DO UPDATE SET count=count+1, last=excluded.last";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, (int Count, long Last)> Opened()
    {
        var d = new Dictionary<string, (int, long)>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT path, count, last FROM opened ORDER BY last DESC LIMIT 5000";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = (r.GetInt32(1), r.GetInt64(2));
        return d;
    }

    // ---- the queue (the UI writes, the indexer drains) ----

    public readonly record struct Pending(long Id, string Vol, ulong Frn, string Path, ResultKind Kind, string Reason, int Attempts = 0);

    /// <summary>
    /// How many times a file may be handed to the indexer before it is written off.
    ///
    /// <para>The count exists for the failure C# cannot catch. A managed throw is already handled
    /// - the row is recorded Failed and dequeued - but a decoder that takes the PROCESS down
    /// (an access violation inside an image, model or media library, a stack overflow, an
    /// out-of-memory) never reaches that code. The child is restarted, <see cref="TakeNext"/>'s
    /// deterministic ordering hands back the same row, and it dies again: the queue stops for good
    /// at that file, and everything behind it is never read. The only symptom is a repeating line
    /// in the log.</para>
    ///
    /// <para>Three, because the causes that clear on their own - a file being written as it was
    /// opened, a network share that blinked, a moment of memory pressure - almost never survive
    /// three passes, and a file that does is not going to be read by a fourth.</para>
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>Queue a file. Re-queuing a file already waiting just refreshes its path and reason.</summary>
    public void Enqueue(string vol, ulong frn, string path, ResultKind kind, string reason, SqliteTransaction? tx = null)
    {
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO pending(vol,frn,path,kind,reason,queued_at) VALUES($v,$f,$p,$k,$r,$t)
                            ON CONFLICT(vol,frn) DO UPDATE SET path=excluded.path, reason=excluded.reason, kind=excluded.kind";
        cmd.Parameters.AddWithValue("$v", vol);
        cmd.Parameters.AddWithValue("$f", unchecked((long)frn));
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$k", (int)kind);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public bool HasItem(string vol, ulong frn)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM items WHERE vol=$v AND frn=$f";
        cmd.Parameters.AddWithValue("$v", vol);
        cmd.Parameters.AddWithValue("$f", unchecked((long)frn));
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Queued deletes for files the index never held. Returns how many were dropped.</summary>
    public int PurgeOrphanDeletes(SqliteTransaction tx)
    {
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM pending WHERE reason='delete' AND NOT EXISTS (SELECT 1 FROM items i WHERE i.vol=pending.vol AND i.frn=pending.frn)";
        return cmd.ExecuteNonQuery();
    }

    public List<(long Id, string Path)> PendingPaths()
    {
        var list = new List<(long, string)>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT id, path FROM pending";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    /// <summary>Every queued row, whole. <see cref="PendingPaths"/> is not enough for the
    /// reconcile pass: a queued DELETE has to survive a rule change that now excludes its path,
    /// or the indexed row it was going to remove stays in the index forever, and the reason is
    /// the only thing that distinguishes the two.</summary>
    public List<Pending> PendingRows()
    {
        var list = new List<Pending>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT id, vol, frn, path, kind, reason FROM pending ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Pending(r.GetInt64(0), r.GetString(1), unchecked((ulong)r.GetInt64(2)),
                                 r.GetString(3), (ResultKind)r.GetInt32(4), r.GetString(5)));
        return list;
    }

    public List<(string Vol, ulong Frn, string Path)> ItemPaths()
    {
        var list = new List<(string, ulong, string)>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT vol, frn, path FROM items";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), unchecked((ulong)r.GetInt64(1)), r.GetString(2)));
        return list;
    }

    public long PendingCount()
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pending";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public long IndexedCount()
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE state=1";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>What the index holds, by state - the four numbers --searchindex reports.</summary>
    public (long Queued, long Indexed, long Failed, long Skipped) Counts()
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = @"SELECT (SELECT COUNT(*) FROM pending),
                                   (SELECT COUNT(*) FROM items WHERE state=1),
                                   (SELECT COUNT(*) FROM items WHERE state=2),
                                   (SELECT COUNT(*) FROM items WHERE state=3)";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }

    /// <summary>Items per kind, with EVERY kind present even at zero. A kind that vanishes from
    /// the report because it has no rows is how "why is nothing indexed" becomes unanswerable.</summary>
    public IReadOnlyDictionary<ResultKind, long> CountsByKind()
    {
        var d = new Dictionary<ResultKind, long>();
        foreach (ResultKind k in Enum.GetValues<ResultKind>()) d[k] = 0;
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT kind, COUNT(*) FROM items GROUP BY kind";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[(ResultKind)r.GetInt32(0)] = r.GetInt64(1);
        return d;
    }

    /// <summary>The files the decoder could not read, newest first - the part of --searchindex
    /// that turns "some files failed" into something a person can act on.</summary>
    public List<(string Path, string Error)> RecentFailures(int limit)
    {
        var list = new List<(string, string)>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT path, COALESCE(error,'') FROM items WHERE state=2 ORDER BY indexed_at DESC, id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>The files that were skipped rather than read, newest first. Skipping is a normal
    /// state and not a failure, so these never appear in <see cref="RecentFailures"/> - and
    /// without this the recorded reason a skip carries has no reader at all, which is how "waiting
    /// for a model" becomes indistinguishable from "too big to read".</summary>
    public List<(string Path, string Error)> RecentSkips(int limit)
    {
        var list = new List<(string, string)>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT path, COALESCE(error,'') FROM items WHERE state=3 ORDER BY indexed_at DESC, id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>How many items carry this recorded reason, whatever state they are in.
    ///
    /// <para>Deliberately no state clause. The reason column holds two different facts: why a file
    /// was skipped, and what was left undone on a file that really was indexed - a long video
    /// whose frames were read and whose sound track was not is INDEXED and carries
    /// <see cref="Decoders.TooLong"/>. Counting only skipped rows would report a whole film
    /// library as unread, and raising the transcription limit later would reach none of it.</para>
    /// </summary>
    public long CountRecorded(string reason)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE error = $r";
        cmd.Parameters.AddWithValue("$r", reason);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// How many SKIPPED items of these kinds carry this reason - "I turned photos on, what is it
    /// going to do".
    ///
    /// <para>Narrower than <see cref="CountRecorded"/> in both directions, and both matter. The
    /// state clause is there because the reason column also holds notes on rows that really were
    /// indexed, and a video read for its frames is not waiting on anything. The kind clause is
    /// there because one total across every kind does not tell anybody which download would clear
    /// it, which is the only reason the number is printed.</para>
    ///
    /// <para>An empty <paramref name="kinds"/> is zero without touching the database, for the
    /// reason <see cref="RequeueKinds"/> gives: the clause below is built by concatenation, and
    /// while the bundled SQLite does accept <c>IN ()</c> as an empty list, running the statement
    /// at all is work with a known answer.</para>
    /// </summary>
    public long CountSkippedFor(int[] kinds, string reason)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Length == 0) return 0;
        using var cmd = _c.CreateCommand();
        // The kinds stay concatenated - they are ints from an enum this code owns - and the
        // reason is a parameter, because a skip reason is free text out of an exception message.
        cmd.CommandText = $"SELECT COUNT(*) FROM items WHERE state={StateSkipped.ToString(CultureInfo.InvariantCulture)} " +
                          $"AND kind IN ({string.Join(",", kinds)}) AND error = $r";
        cmd.Parameters.AddWithValue("$r", reason);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// How many SKIPPED items of these kinds carry this reason and are NOT waiting in the queue -
    /// files nothing has read, and nothing is going to.
    ///
    /// <para>The queue clause is the whole point of this being separate from
    /// <see cref="CountSkippedFor"/>. A row that has just been queued for a capability that
    /// arrived is skipped and unread too, and reading that as work lost re-queues the entire
    /// backlog on every launch. What it answers is the narrower question: is anything that was
    /// once passed over for this reason now stranded, with no queue entry to bring it back.</para>
    /// </summary>
    public long CountSkippedAndNotQueued(int[] kinds, string reason)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Length == 0) return 0;
        using var cmd = _c.CreateCommand();
        // The kinds stay concatenated - ints from an enum this code owns - and the reason is a
        // parameter, because a skip reason is free text out of an exception message.
        cmd.CommandText = $"SELECT COUNT(*) FROM items i WHERE i.state={StateSkipped.ToString(CultureInfo.InvariantCulture)} " +
                          $"AND i.kind IN ({string.Join(",", kinds)}) AND i.error = $r " +
                          "AND NOT EXISTS (SELECT 1 FROM pending p WHERE p.vol = i.vol AND p.frn = i.frn)";
        cmd.Parameters.AddWithValue("$r", reason);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Is this file already indexed at this mtime? The UI asks before queuing during the
    /// first pass, so a restart does not re-queue a whole drive.</summary>
    public bool IsCurrent(string vol, ulong frn, long mtime)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT mtime, state FROM items WHERE vol=$v AND frn=$f";
        cmd.Parameters.AddWithValue("$v", vol);
        cmd.Parameters.AddWithValue("$f", unchecked((long)frn));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return false;
        int state = r.GetInt32(1);
        return r.GetInt64(0) == mtime && state is StateIndexed or StateFailed or StateSkipped;
    }

    /// <summary>The state recorded against this file, or <see cref="StateQueued"/> if the index
    /// has never seen it. A Skipped row is "finished" to the queue feeder - which must not queue
    /// it again on every pass - and "never opened" to the indexer, which has to run the decoder
    /// the moment one exists. This is what lets the two disagree without either being wrong, and
    /// it is why <see cref="IsCurrent"/> above keeps counting Skipped as finished.</summary>
    public int StateOf(string vol, ulong frn)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT state FROM items WHERE vol=$v AND frn=$f";
        cmd.Parameters.AddWithValue("$v", vol);
        cmd.Parameters.AddWithValue("$f", unchecked((long)frn));
        using var r = cmd.ExecuteReader();
        return r.Read() ? r.GetInt32(0) : StateQueued;
    }

    /// <summary>Every (vol, frn, mtime) the item table knows, for the first pass to diff against.</summary>
    public Dictionary<(string, ulong), long> KnownItems()
    {
        var d = new Dictionary<(string, ulong), long>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT vol, frn, mtime FROM items WHERE state IN (1,2,3)";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[(r.GetString(0), unchecked((ulong)r.GetInt64(1)))] = r.GetInt64(2);
        return d;
    }

    public Pending? TakeNext()
    {
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        // Deletes first, then oldest first. A delete takes rows OUT, so running it ahead of the
        // re-index of the same path keeps the index from briefly holding both.
        cmd.CommandText = "SELECT id, vol, frn, path, kind, reason, attempts FROM pending ORDER BY (reason=$d) DESC, id LIMIT 1";
        cmd.Parameters.AddWithValue("$d", ReasonDelete);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Pending(r.GetInt64(0), r.GetString(1), unchecked((ulong)r.GetInt64(2)), r.GetString(3),
                           (ResultKind)r.GetInt32(4), r.GetString(5), r.GetInt32(6));
    }

    /// <summary>
    /// Count one attempt at this row, and say whether it has had its last.
    ///
    /// <para><b>Called and committed BEFORE the file is opened</b>, which is the whole design.
    /// Counting afterwards records nothing when the attempt takes the process with it, and that is
    /// precisely the attempt worth counting - a managed throw already ends with the row recorded
    /// Failed and dequeued. Written first, a hard crash leaves the raised count on the disk, so the
    /// restarted child sees it and moves on rather than dying on the same file for ever.</para>
    /// </summary>
    public bool CountAttempt(long id)
    {
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "UPDATE pending SET attempts = attempts + 1 WHERE id=$i RETURNING attempts";
        cmd.Parameters.AddWithValue("$i", id);
        object? v = cmd.ExecuteScalar();
        return v is long n && n > MaxAttempts;
    }

    public void Dequeue(long id, SqliteTransaction? tx = null)
    {
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM pending WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    public void ClearQueue()
    {
        using var claim = Enter();
        Exec("DELETE FROM pending");
    }

    // ---- items and segments (the indexer writes) ----

    public readonly record struct Segment(int Kind, double T0, double T1, long Vec, string Text);

    /// <summary>Replace an item's segments with a fresh set.
    ///
    /// <para>Returns the <c>vec</c> values the replaced segments carried, for the caller to
    /// tombstone AFTER this transaction commits. Discarding them is a leak with no symptom
    /// anybody can see: the old embedding of an edited document goes on matching queries for
    /// ever, beside the new one.</para></summary>
    public List<long> Upsert(string vol, ulong frn, string path, ResultKind kind, long mtime, long size,
        int state, string? error, IReadOnlyList<Segment> segments, SqliteTransaction tx)
    {
        using var claim = Enter();
        long itemId;
        var dead = new List<long>();
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO items(vol,frn,path,kind,mtime,size,state,error,indexed_at)
                VALUES($v,$f,$p,$k,$m,$s,$st,$e,$t)
                ON CONFLICT(vol,frn) DO UPDATE SET path=excluded.path, kind=excluded.kind, mtime=excluded.mtime,
                    size=excluded.size, state=excluded.state, error=excluded.error, indexed_at=excluded.indexed_at
                RETURNING id";
            cmd.Parameters.AddWithValue("$v", vol);
            cmd.Parameters.AddWithValue("$f", unchecked((long)frn));
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$k", (int)kind);
            cmd.Parameters.AddWithValue("$m", mtime);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$st", state);
            cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            itemId = (long)cmd.ExecuteScalar()!;
        }
        dead.AddRange(DeleteSegments(itemId, tx));
        foreach (var s in segments)
        {
            using var cmd = _c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO segments(item,kind,t0,t1,vec,text) VALUES($i,$k,$a,$b,$v,$x) RETURNING id";
            cmd.Parameters.AddWithValue("$i", itemId);
            cmd.Parameters.AddWithValue("$k", s.Kind);
            cmd.Parameters.AddWithValue("$a", s.T0);
            cmd.Parameters.AddWithValue("$b", s.T1);
            cmd.Parameters.AddWithValue("$v", s.Vec);
            cmd.Parameters.AddWithValue("$x", s.Text);
            long segId = (long)cmd.ExecuteScalar()!;
            if (s.Text.Length > 0)
            {
                using var f = _c.CreateCommand();
                f.Transaction = tx;
                f.CommandText = "INSERT INTO fts(rowid, text) VALUES($r, $x)";
                f.Parameters.AddWithValue("$r", segId);
                f.Parameters.AddWithValue("$x", s.Text);
                f.ExecuteNonQuery();
            }
        }
        return dead;
    }

    /// <summary>Drop an item and every segment under it, and take its text back out of the
    /// full-text index. Returns the <c>vec</c> values those segments carried, for the reason
    /// <see cref="Upsert"/> gives - a deleted photo answering a query is the visible form.</summary>
    public List<long> Delete(string vol, ulong frn, SqliteTransaction tx)
    {
        using var claim = Enter();
        long? itemId;
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM items WHERE vol=$v AND frn=$f";
            cmd.Parameters.AddWithValue("$v", vol);
            cmd.Parameters.AddWithValue("$f", unchecked((long)frn));
            itemId = cmd.ExecuteScalar() as long?;
        }
        if (itemId is null) return new List<long>();
        var dead = DeleteSegments(itemId.Value, tx);
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM items WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", itemId.Value);
            cmd.ExecuteNonQuery();
        }
        return dead;
    }

    private List<long> DeleteSegments(long itemId, SqliteTransaction tx)
    {
        var dead = new List<long>();
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, vec, text FROM segments WHERE item=$i";
            cmd.Parameters.AddWithValue("$i", itemId);
            using var r = cmd.ExecuteReader();
            var rows = new List<(long Id, long Vec, string Text)>();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2)));
            r.Close();
            foreach (var (id, vec, text) in rows)
            {
                if (vec >= 0) dead.Add(vec);
                if (text.Length > 0)
                {
                    // external-content FTS5 needs the old text to remove the row
                    using var f = _c.CreateCommand();
                    f.Transaction = tx;
                    f.CommandText = "INSERT INTO fts(fts, rowid, text) VALUES('delete', $r, $x)";
                    f.Parameters.AddWithValue("$r", id);
                    f.Parameters.AddWithValue("$x", text);
                    f.ExecuteNonQuery();
                }
            }
        }
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM segments WHERE item=$i";
            cmd.Parameters.AddWithValue("$i", itemId);
            cmd.ExecuteNonQuery();
        }
        return dead;
    }

    // ---- reading (the UI, at query time) ----

    public readonly record struct SegmentHit(long SegmentId, string Path, ResultKind Kind, int SegKind, double T0, double T1, string Text, long Vec);

    /// <summary>The segments carrying these <c>vec</c> values. Nothing in this build fills that
    /// column, so nothing in this build calls this; it is the read side of a row shape the store
    /// already writes.</summary>
    public List<SegmentHit> SegmentsByVec(IReadOnlyList<long> vecs)
    {
        var list = new List<SegmentHit>(vecs.Count);
        if (vecs.Count == 0) return list;
        using var cmd = _c.CreateCommand();
        cmd.CommandText = $@"SELECT s.id, i.path, i.kind, s.kind, s.t0, s.t1, s.text, s.vec FROM segments s JOIN items i ON i.id=s.item
                             WHERE s.vec IN ({string.Join(",", vecs)})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SegmentHit(r.GetInt64(0), r.GetString(1), (ResultKind)r.GetInt32(2), r.GetInt32(3), r.GetDouble(4), r.GetDouble(5), r.GetString(6), r.GetInt64(7)));
        return list;
    }

    /// <summary>Exact-word hits from the FTS index, best first.</summary>
    /// <summary>What the index holds about ONE file, by path, or null when it holds nothing.
    ///
    /// <para>Every other reader here answers a question about the whole index - what is queued,
    /// what failed, what matched. Nothing could answer "what about THIS file", which is the only
    /// question anybody actually asks: they can see the file, they searched for it, it did not
    /// come back, and every fact needed to explain that was already recorded and unreachable.
    /// </para>
    /// </summary>
    public ItemRow? ItemByPath(string path)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT vol, frn, path, kind, mtime, size, state, COALESCE(error,''), indexed_at, id " +
                          "FROM items WHERE path = $p COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ItemRow(r.GetString(0), unchecked((ulong)r.GetInt64(1)), r.GetString(2),
                           (ResultKind)r.GetInt32(3), r.GetInt64(4), r.GetInt64(5), r.GetInt32(6),
                           r.GetString(7), r.GetInt64(8), r.GetInt64(9));
    }

    public readonly record struct ItemRow(string Vol, ulong Frn, string Path, ResultKind Kind,
                                          long Mtime, long Size, int State, string Error,
                                          long IndexedAt, long Id);

    /// <summary>Is this file waiting, and what for? A path can be queued without an item row at
    /// all - a file offered by the walk that has never been read - so this is asked separately.
    /// </summary>
    public (string Reason, int Attempts)? QueuedAs(string path)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT reason, attempts FROM pending WHERE path = $p COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt32(1)) : null;
    }

    /// <summary>Every segment an item holds, in the order they were written. The text is carried
    /// so a caller can show what a chunk actually says - which is how "it matched on words" is
    /// told from "it matched on meaning" without guessing.</summary>
    public List<SegmentHit> SegmentsOf(long itemId)
    {
        var list = new List<SegmentHit>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT s.id, i.path, i.kind, s.kind, s.t0, s.t1, s.text, s.vec " +
                          "FROM segments s JOIN items i ON i.id = s.item WHERE s.item = $i ORDER BY s.id";
        cmd.Parameters.AddWithValue("$i", itemId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SegmentHit(r.GetInt64(0), r.GetString(1), (ResultKind)r.GetInt32(2),
                                    r.GetInt32(3), r.GetDouble(4), r.GetDouble(5), r.GetString(6), r.GetInt64(7)));
        return list;
    }

    public List<SegmentHit> Fts(string query, int limit)
    {
        var list = new List<SegmentHit>();
        string q = FtsQuery(query);
        if (q.Length == 0) return list;
        using var cmd = _c.CreateCommand();
        cmd.CommandText = @"SELECT s.id, i.path, i.kind, s.kind, s.t0, s.t1, s.text, s.vec FROM fts f
                            JOIN segments s ON s.id=f.rowid JOIN items i ON i.id=s.item
                            WHERE fts MATCH $q ORDER BY bm25(fts) LIMIT $n";
        cmd.Parameters.AddWithValue("$q", q);
        cmd.Parameters.AddWithValue("$n", limit);
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new SegmentHit(r.GetInt64(0), r.GetString(1), (ResultKind)r.GetInt32(2), r.GetInt32(3), r.GetDouble(4), r.GetDouble(5), r.GetString(6), r.GetInt64(7)));
        }
        catch (SqliteException) { /* a query FTS5 cannot parse is no hits, not a fault */ }
        return list;
    }

    // Every word becomes a quoted prefix term, ANDed: FTS5 syntax characters in a file name or a
    // sentence must never reach the parser as operators.
    public static string FtsQuery(string query)
    {
        var parts = new List<string>();
        foreach (var tok in SearchQuery.Tokenize(query))
        {
            if (tok.Length >= 2 && tok[0] == '"' && tok[^1] == '"')
            {
                // a quoted phrase: the words together, in order
                string ph = tok[1..^1].Replace("\"", "").Trim();
                if (ph.Length >= 2) parts.Add("\"" + ph + "\"");
                continue;
            }
            string t = tok.Replace("\"", "");
            if (t.Length < 2) continue;
            parts.Add("\"" + t + "\"" + (t.Length >= 3 ? "*" : ""));
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>What the index holds for one path - the probe's view.</summary>
    public List<string> Describe(string path)
    {
        var rows = new List<string>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = @"SELECT i.state, i.error, s.kind, s.t0, s.t1, s.vec, substr(s.text,1,80) FROM items i
                            LEFT JOIN segments s ON s.item=i.id WHERE i.path=$p ORDER BY s.id";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        string name = System.IO.Path.GetFileName(path);
        while (r.Read())
        {
            int state = r.GetInt32(0);
            string st = state switch { 1 => "indexed", 2 => "FAILED", 3 => "skipped", _ => "queued" };
            if (r.IsDBNull(2)) { rows.Add($"{name}: {st}{(r.IsDBNull(1) ? "" : " - " + r.GetString(1))}"); continue; }
            int k = r.GetInt32(2);
            string kind = k switch { SegImage => "image", SegText => "text", SegSpeech => "speech", SegFrame => "frame", _ => k.ToString(CultureInfo.InvariantCulture) };
            double t0 = r.GetDouble(3);
            string text = r.GetString(6).Replace('\n', ' ');
            string at = t0 >= 0 ? " @ " + t0.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "";
            rows.Add($"{name}: {st} {kind} vec {r.GetInt64(5)}{at}{(text.Length > 0 ? "  \"" + text + "\"" : "")}");
        }
        if (rows.Count == 0) rows.Add($"{name}: not in the index");
        return rows;
    }

    /// <summary>Every segment that carries text, oldest first - the whole indexed corpus, read
    /// back without re-opening a single file.</summary>
    public List<(long SegId, string Path, int SegKind, string Text)> TextSegments()
    {
        var list = new List<(long, string, int, string)>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT s.id, i.path, s.kind, s.text FROM segments s JOIN items i ON i.id=s.item WHERE s.text != '' ORDER BY s.id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
        return list;
    }

    public void UpdateVec(long segId, long vec, SqliteTransaction? tx = null)
    {
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE segments SET vec=$v WHERE id=$i";
        cmd.Parameters.AddWithValue("$v", vec);
        cmd.Parameters.AddWithValue("$i", segId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Queue every item of the given kinds again, because something that can now read
    /// them arrived. Nothing is deleted here: the indexer replaces an item's segments when it
    /// gets to the row, so removing them up front would only blank the index for the length of
    /// the re-run. Returns how many rows were queued.
    ///
    /// <para><paramref name="reason"/> is not decoration. <see cref="Indexer"/> dequeues a row
    /// untouched unless the reason is <see cref="Indexer.Recheck"/>, the row is Skipped, or the
    /// file's bytes have moved - so a caller that invents a friendly sentence here queues
    /// thousands of files and re-reads none of them.</para>
    ///
    /// <para><paramref name="notBecause"/> filters the SKIPPED rows by the reason they were
    /// skipped for, and only those - an indexed row has no reason at all and must never be
    /// excluded by this. The recorded reason carries five different meanings ("no decoder for
    /// this kind", "no decoder for this format", "no text", "too large", "longer than the
    /// transcription limit") and a new model can do nothing about the middle three, so re-opening
    /// a 200 MB database dump on every install is work with a guaranteed outcome.</para>
    ///
    /// <para><paramref name="onlyBecause"/> is the mirror, and the narrow one: exactly the rows
    /// carrying one of these reasons, whatever their state. Raising the transcription limit uses
    /// it, and it has to reach an INDEXED video that was read for its frames and carries
    /// "longer than the transcription limit" as a note about the sound track nobody heard.</para>
    ///
    /// <para>The two filters are mutually exclusive and <paramref name="onlyBecause"/> wins,
    /// because a caller that passes both has not decided which set it means.</para>
    ///
    /// <para>An empty <paramref name="kinds"/> queues nothing and DOES NOT TOUCH THE DATABASE,
    /// which is the part that matters. The clause below is built by concatenation, so an empty
    /// array emits <c>IN ()</c> - and the bundled SQLite accepts that as an empty list rather
    /// than refusing it, so the danger is not the syntax. It is the transaction: without this
    /// return a caller already inside one gets a nested-transaction
    /// <see cref="InvalidOperationException"/> out of a flow that has no catch for it.</para>
    /// </summary>
    public int RequeueKinds(int[] kinds, string reason,
                            IReadOnlyList<string>? notBecause = null,
                            IReadOnlyList<string>? onlyBecause = null)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Length == 0) return 0;

        using var claim = Enter();
        int n = 0;
        using var tx = Begin();
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;

            // The kinds themselves stay concatenated - they are ints from an enum this code owns
            // - but every reason string is a parameter, because a skip reason is free text that
            // has come from an exception message.
            string filter = "";
            if (onlyBecause is { Count: > 0 })
            {
                // The narrow direction: exactly the rows carrying one of these reasons. Raising
                // the transcription limit uses it, because re-queueing everything Speech covers
                // would re-transcribe every recording already done.
                var named = onlyBecause.Select((_, i) => $"$o{i.ToString(CultureInfo.InvariantCulture)}");
                filter = $" AND error IN ({string.Join(",", named)})";
                for (int i = 0; i < onlyBecause.Count; i++)
                    cmd.Parameters.AddWithValue($"$o{i.ToString(CultureInfo.InvariantCulture)}", onlyBecause[i]);
            }
            else if (notBecause is { Count: > 0 })
            {
                // The `state <> StateSkipped OR error IS NULL` guard is load-bearing: written as
                // a bare `error NOT IN (...)`, every INDEXED row's NULL error would exclude it,
                // SQL three-valued logic being what it is - and a new model would then never
                // re-embed anything already read.
                var named = notBecause.Select((_, i) => $"$e{i.ToString(CultureInfo.InvariantCulture)}");
                filter = $" AND (state <> {StateSkipped.ToString(CultureInfo.InvariantCulture)} " +
                         $"OR error IS NULL OR error NOT IN ({string.Join(",", named)}))";
                for (int i = 0; i < notBecause.Count; i++)
                    cmd.Parameters.AddWithValue($"$e{i.ToString(CultureInfo.InvariantCulture)}", notBecause[i]);
            }

            // state IN (1, 3): indexed AND skipped. Every photo, video and audio file this build
            // meets ends at StateSkipped, because there is no decoder for it yet - so a clause
            // that saw only state=1 would find nothing to re-queue, and "a kind is skipped now,
            // the capability that arrives later picks up exactly those" would silently never
            // happen. StateFailed (2) stays out on purpose: a file the decoder genuinely could
            // not read has not changed because a capability arrived, and retrying it on every
            // install is a loop with no exit. Skipped means "not attempted yet"; failed means
            // "attempted, and it did not work".
            cmd.CommandText = $"SELECT vol, frn, path, kind FROM items WHERE state IN (1, 3) " +
                              $"AND kind IN ({string.Join(",", kinds)}){filter}";
            using var r = cmd.ExecuteReader();
            var rows = new List<(string, ulong, string, int)>();
            while (r.Read()) rows.Add((r.GetString(0), unchecked((ulong)r.GetInt64(1)), r.GetString(2), r.GetInt32(3)));
            r.Close();
            foreach (var (vol, frn, path, kind) in rows) { Enqueue(vol, frn, path, (ResultKind)kind, reason, tx); n++; }
        }
        tx.Commit();
        return n;
    }

    public (long Items, long Segments, long Failed) Stats()
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM items WHERE state=1), (SELECT COUNT(*) FROM segments), (SELECT COUNT(*) FROM items WHERE state=2)";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }
}
