using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
// Segment kinds: 0 = a photo (one vector for the whole image), 1 = a text chunk of a document,
// 2 = a transcript segment (audio or video speech), 3 = a video frame at t0.
public sealed class ContentDb : IDisposable
{
    public const int SegImage = 0, SegText = 1, SegSpeech = 2, SegFrame = 3;
    public const int StateQueued = 0, StateIndexed = 1, StateFailed = 2, StateSkipped = 3;

    /// <summary>The relational shape of this database. Bumped only when a change makes rows
    /// already on disk mean something different.</summary>
    public const int SchemaVersion = 1;

    /// <summary>One schema step. <c>InvalidatedKinds</c> is what that step made stale - and
    /// nothing else is re-queued. Re-indexing a finished disk because an upgrade did not look
    /// first is the worst thing this product can do to someone (spec §2a).</summary>
    public readonly record struct Migration(int To, int[] InvalidatedKinds, string Reason);

    /// <summary>Empty at version 1: there is nothing before it to migrate from. Plan 5 appends
    /// the step that invalidates what its models change.</summary>
    public static readonly IReadOnlyList<Migration> Migrations = [];

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
        Paths.Ensure(System.IO.Path.GetDirectoryName(Path)!);
        _c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 15,
        }.ToString());
        _c.Open();
        try
        {
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
            // connection, and an undisposed handle keeps the file locked on Windows, which is
            // exactly the file OpenOrRebuild then has to move aside.
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
    }

    // ---- the schema stamp, and the migrations it gates ----

    /// <summary>Read the recorded schema version, run whatever steps stand between it and this
    /// build, and stamp the result. A step re-queues only the kinds it invalidated; everything
    /// else on disk is left exactly as it was found.</summary>
    private void OpenSchema(IReadOnlyList<Migration> steps)
    {
        string? stamped = Get("schema");
        int from;
        if (int.TryParse(stamped, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) from = v;
        else from = HasAnyItem() ? 0 : SchemaVersion;   // never stamped + never written = brand new

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
            int n = RequeueKinds(m.InvalidatedKinds, m.Reason);
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
            MoveAside(p);
            return new ContentDb(p) { WasRebuilt = true };
        }
    }

    // The write-ahead log and shared-memory files belong to the database they sit beside; leaving
    // them behind hands the fresh database somebody else's journal.
    private static void MoveAside(string path)
    {
        foreach (string tail in new[] { "", "-wal", "-shm" })
        {
            string from = path + tail;
            if (!File.Exists(from)) continue;
            try { File.Move(from, path + ".corrupt" + tail, overwrite: true); }
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

    public SqliteTransaction Begin() => _c.BeginTransaction();

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

    // ---- the suffix stamp: which extension set this index was walked with ----

    /// <summary>Record the extension set the disk was walked with. FileKinds' tables are code: a
    /// later version that adds an extension would otherwise keep asking for the OLD suffix list
    /// forever, and files with the new extension would never be enumerated on any existing
    /// install.</summary>
    public void SetSuffixVersion(IReadOnlyList<string> suffixes) => Set("suffixes", SuffixHash(suffixes));

    /// <summary>Compare the stamp against a suffix set without writing anything.</summary>
    public bool SuffixesChanged(IReadOnlyList<string> suffixes) => Get("suffixes") != SuffixHash(suffixes);

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

    public readonly record struct Pending(long Id, string Vol, ulong Frn, string Path, ResultKind Kind, string Reason);

    /// <summary>Queue a file. Re-queuing a file already waiting just refreshes its path and reason.</summary>
    public void Enqueue(string vol, ulong frn, string path, ResultKind kind, string reason, SqliteTransaction? tx = null)
    {
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
        using var cmd = _c.CreateCommand();
        // deletes first (they free vectors), then oldest first
        cmd.CommandText = "SELECT id, vol, frn, path, kind, reason FROM pending ORDER BY (reason='delete') DESC, id LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Pending(r.GetInt64(0), r.GetString(1), unchecked((ulong)r.GetInt64(2)), r.GetString(3), (ResultKind)r.GetInt32(4), r.GetString(5));
    }

    public void Dequeue(long id, SqliteTransaction? tx = null)
    {
        using var cmd = _c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM pending WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    public void ClearQueue()
    {
        Exec("DELETE FROM pending");
    }

    // ---- items and segments (the indexer writes) ----

    public readonly record struct Segment(int Kind, double T0, double T1, long Vec, string Text);

    /// <summary>Replace an item's segments with a fresh set. Returns the vector rows that are now
    /// dead (the previous segments' rows), for the caller to tombstone.</summary>
    public List<long> Upsert(string vol, ulong frn, string path, ResultKind kind, long mtime, long size,
        int state, string? error, IReadOnlyList<Segment> segments, SqliteTransaction tx)
    {
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

    /// <summary>Drop an item and everything under it. Returns the dead vector rows.</summary>
    public List<long> Delete(string vol, ulong frn, SqliteTransaction tx)
    {
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

    /// <summary>The segments behind a set of vector rows.</summary>
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

    /// <summary>Every segment that carries text - what a model migration re-embeds in place.</summary>
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
        using var cmd = _c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE segments SET vec=$v WHERE id=$i";
        cmd.Parameters.AddWithValue("$v", vec);
        cmd.Parameters.AddWithValue("$i", segId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Queue every item of the given kinds again (a capability arrived under them).
    /// Their textless segments are dropped so nothing points at the old space.</summary>
    public int RequeueKinds(int[] kinds, string reason)
    {
        int n = 0;
        using var tx = Begin();
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            // state IN (1, 3): indexed AND skipped. Every photo, video and audio file this build
            // meets ends at StateSkipped, because there is no decoder for it yet - so a clause
            // that saw only state=1 would find nothing to re-queue, and "a kind is skipped now,
            // the capability that arrives later picks up exactly those" would silently never
            // happen. StateFailed (2) stays out on purpose: a file the decoder genuinely could
            // not read has not changed because a capability arrived, and retrying it on every
            // install is a loop with no exit. Skipped means "not attempted yet"; failed means
            // "attempted, and it did not work".
            cmd.CommandText = $"SELECT vol, frn, path, kind FROM items WHERE state IN (1, 3) AND kind IN ({string.Join(",", kinds)})";
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
