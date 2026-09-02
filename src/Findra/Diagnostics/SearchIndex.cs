using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Findra.Diagnostics;

/// <summary>
/// A frozen picture of the content index, at the moment it was read. <see cref="SearchIndexReport"/>
/// renders it, and nothing else touches a database - which is what makes the twelve behaviours in
/// its report testable with no index on disk (spec §9).
/// </summary>
public sealed record IndexSnapshot(
    int Schema, bool WasRebuilt, string DbPath,
    IReadOnlyList<(string Name, long Bytes)> Stores,
    IReadOnlyList<(char Volume, ulong JournalId, long Usn)> Cursors,
    long Queued, long Indexed, long Failed, long Skipped,
    IReadOnlyDictionary<ResultKind, long> ByKind,
    string IndexerState, string IndexerCurrent, string IndexerRate, string IndexerPid, bool IndexerAlive,
    long JournalDropped, IReadOnlyList<(string Path, string Error)> Failures);

/// <summary>
/// `--searchindex`'s formatter: what is in the content index, and what is still queued (spec §9).
/// <see cref="Render"/> is a pure function of a snapshot, fixed-width in the same style as
/// `--searchprobe`, with every number through <see cref="CultureInfo.InvariantCulture"/> - this
/// text is pasted into bug reports from machines set to other locales.
/// </summary>
public static class SearchIndexReport
{
    public static string Render(IndexSnapshot s)
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');
        string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        Line("findra --searchindex");
        Line();

        // Checked first and printed before anything else: a small count after a rebuild needs
        // explaining before the reader starts wondering why the index looks nearly empty.
        if (s.WasRebuilt)
        {
            Line("  NOTE: this index was rebuilt - the previous file could not be opened and was moved aside");
            Line();
        }

        // Every file the index is actually made of, not just the database. The write-ahead log
        // and the shared-memory sidecar are real bytes on the user's disk and the WAL is routinely
        // the larger of the two while indexing is in flight - measured, 988 KB of -wal against a
        // 68 KB search.db - so sizing search.db alone answers "how big is my index" with a number
        // that can be twenty times short. --searchbench has sized all three since it was written.
        long total = 0;
        foreach ((string _, long bytes) in s.Stores) total += bytes;
        Line($"  index    : schema {s.Schema.ToString(CultureInfo.InvariantCulture)}, {s.DbPath} ({Bytes(total)})");
        // Only when there is something to break down: a checkpointed index is one file, and
        // printing "search.db 12.0 MB" under "12.0 MB" is the noise that stops people reading.
        if (s.Stores.Count > 1)
            // " + " and not a nicer separator: this report is printed to a Windows console whose
            // code page is whatever the machine was set up with, and it is pasted into bug
            // reports from those machines. It also happens to read as the sum it is.
            Line("             " + string.Join(" + ", s.Stores.Select(f => $"{f.Name} {Bytes(f.Bytes)}")));
        Line();

        Line("  volumes  :");
        if (s.Cursors.Count == 0)
        {
            // Zero is a real USN - printing it for "never consumed" would be indistinguishable
            // from a drive that has genuinely consumed nothing since position zero.
            Line("    no volume has a recorded position");
        }
        else
        {
            foreach ((char vol, ulong journalId, long usn) in s.Cursors)
                Line($"    {vol}: usn {usn.ToString(CultureInfo.InvariantCulture)} (journal {journalId.ToString("X", CultureInfo.InvariantCulture)})");
        }
        Line();

        Line($"  counts   : queued {N(s.Queued)}, indexed {N(s.Indexed)}, failed {N(s.Failed)}, skipped {N(s.Skipped)}");
        Line();

        // Every kind, including the ones sitting at zero - a kind that vanishes because it has no
        // rows is how "why are none of my photos indexed" becomes unanswerable.
        Line("  by kind  :");
        foreach (ResultKind k in Enum.GetValues<ResultKind>())
        {
            long n = s.ByKind.TryGetValue(k, out long v) ? v : 0;
            Line($"    {FileKinds.Label(k),-8} {N(n)}");
        }
        Line();

        // A heartbeat this report was told is stale must not be read back as live work - the
        // state, current file and rate are all dropped together, not just the rate.
        //
        // The live sentence is IndexStatus's, shared with --searchprobe, which reads the same
        // rows. Building it here meant an idle child - no current file, no rate, the ORDINARY
        // state on a finished machine - printed "idle -  ()", and that the pid was missing from
        // the one report whose whole job is to say which process is doing what.
        Line("  indexer  : " + (s.IndexerAlive
            ? IndexStatus.Running(s.IndexerPid, s.IndexerState, s.IndexerCurrent, s.IndexerRate)
            : "not running"));

        // Printed only when non-zero - a report that always says "dropped: 0" trains people to
        // stop reading it, and a dropped journal event is invisible in every other count here.
        if (s.JournalDropped != 0)
        {
            Line();
            Line($"  journal  : {N(s.JournalDropped)} event(s) dropped - a subscriber fell behind and a full pass is owed");
        }

        if (s.Failed > 0)
        {
            Line();
            // The literal header word "failures", alone on its own line: the report's own tests
            // locate this section by splitting on it, and a "recent failures" or "could not be
            // read" heading would silently widen that split into the whole document.
            Line("failures");
            foreach ((string path, string error) in s.Failures)
            {
                Line($"    {path}");
                Line($"      {error}");
            }
            // The remainder comes from the real total, NOT from the length of the sample handed
            // in - Failures is at most ten rows out of however many actually failed, and deriving
            // "and N more" from the ten visible ones caps a catastrophic run at "and 9 more".
            long remaining = s.Failed - s.Failures.Count;
            if (remaining > 0) Line($"  and {N(remaining)} more");
        }

        return sb.ToString();
    }

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    private static string Bytes(long bytes)
    {
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < ByteUnits.Length - 1) { v /= 1024; i++; }
        string num = i == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{num} {ByteUnits[i]}";
    }
}

// `findra --searchindex [file|folder|q:query]...`: what is indexed, and what is queued (spec §9).
// It never needs elevation - the content database is the interface process's own file, not the
// helper's. With no arguments it just reads and reports. Given files or folders it queues them,
// drains the queue in THIS process with Indexer's own code, then reports - which is what makes
// "it did not find my document" splittable into was it queued, did the decoder read it, and what
// does a query score against it. Given `q:<query>` it prints what ContentBranch finds.
public static class SearchIndex
{
    public static int Run(string[] args)
    {
        var files = new List<string>();
        var queries = new List<string>();
        foreach (string a in args.Skip(1))
        {
            if (a.StartsWith("q:", StringComparison.OrdinalIgnoreCase)) { queries.Add(a[2..]); continue; }
            if (Directory.Exists(a)) files.AddRange(Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).Take(200));
            else if (File.Exists(a)) files.Add(a);
            else Console.WriteLine($"no such file: {a}");
        }

        using ContentDb db = ContentDb.OpenOrRebuild();

        if (files.Count > 0)
        {
            int queuedCount = 0;
            using (var tx = db.Begin())
            {
                foreach (string f in files)
                {
                    ResultKind kind = FileKinds.Classify(Path.GetFileName(f), isDirectory: false);
                    if (!FileKinds.HasContent(kind)) { Console.WriteLine($"  skip (not a content kind): {f}"); continue; }
                    ulong frn;
                    try { frn = FrnOf(f); }
                    catch (IOException ex) { Console.WriteLine($"  skip ({ex.Message}): {f}"); continue; }
                    string? root = Path.GetPathRoot(f);
                    string vol = root is { Length: > 0 } ? root[..1] : "?";
                    db.Enqueue(vol, frn, f, kind, "searchindex probe", tx);
                    Console.WriteLine($"  queued {kind,-8} frn {frn.ToString("X", CultureInfo.InvariantCulture)}  {f}");
                    queuedCount++;
                }
                tx.Commit();
            }
            Console.WriteLine($"queued {queuedCount.ToString(CultureInfo.InvariantCulture)}, pending now {db.PendingCount().ToString("N0", CultureInfo.InvariantCulture)}");
            Console.WriteLine();
            Console.WriteLine("draining...");
            Indexer.DrainOnce(db, line => Console.WriteLine("  " + line));
            Console.WriteLine();
        }

        Console.WriteLine(SearchIndexReport.Render(Snapshot(db)));

        if (queries.Count > 0)
        {
            foreach (string q in queries)
            {
                SearchResults results = ContentBranch.Search(db, q, 20);
                Console.WriteLine($"'{q}': {results.Rows.Count.ToString(CultureInfo.InvariantCulture)} hit(s) in {results.ContentMs.ToString("0", CultureInfo.InvariantCulture)} ms");
                foreach (SearchResult r in results.Rows)
                    Console.WriteLine($"  {r.Score.ToString("0.00", CultureInfo.InvariantCulture),5}  {FileKinds.Label(r.Kind),-6} {r.Why,-20} {r.Name}  {r.Excerpt}");
                if (results.Note.Length > 0) Console.WriteLine($"  {results.Note}");
                Console.WriteLine();
            }
        }

        return 0;
    }

    /// <summary>
    /// Read everything <see cref="SearchIndexReport"/> needs off a live database. Two decisions
    /// worth writing down:
    ///
    /// <para>The indexer's "done" counter (<c>indexer:done</c>) is never used here as "indexed" -
    /// it is the child's own running total and counts a skipped file (no decoder yet) the same as
    /// an indexed one. Someone with eight thousand photos this build cannot decode would read
    /// "8,000 indexed" for files that were never opened. Indexed and Skipped instead come straight
    /// from <see cref="ContentDb.Counts"/>, which counts rows by their actual state.</para>
    ///
    /// <para><see cref="Indexer.DrainOnce"/> - used above when this mode is given files to queue -
    /// writes a fresh <c>indexer:beat</c> and <c>indexer:pid</c> when it finishes, the same status
    /// rows a real <c>--index</c> child writes while it runs. Read naively right after a drain,
    /// that heartbeat is fresh and this diagnostic's own one-shot drain reads back as a live
    /// indexer. The recorded pid tells the two apart, and the rule that reads the two rows
    /// together now lives in <see cref="IndexStatus.Alive(string?, string?, int, long)"/>, where
    /// the card, the capsule and <c>--searchprobe</c> read it as well.</para>
    ///
    /// <para>The rebuild notice comes from the index and not only from this connection.
    /// <c>ContentDb.WasRebuilt</c> is a fact about the OPEN that rebuilt the file, and this one
    /// never rebuilt anything - so an index the running interface threw away and rebuilt looked
    /// perfectly ordinary here while the card and the capsule were both saying so, off the
    /// <c>index:rebuilt</c> row the rebuilding session leaves behind.</para>
    /// </summary>
    public static IndexSnapshot Snapshot(ContentDb db)
    {
        (long queued, long indexed, long failed, long skipped) = db.Counts();

        var cursors = new List<(char, ulong, long)>();
        foreach (char v in db.KnownVolumes())
            if (db.UsnPosition(v) is { } pos) cursors.Add((v, pos.JournalId, pos.Usn));

        string pid = db.Get("indexer:pid") ?? "";
        bool alive = IndexStatus.Alive(db.Get("indexer:beat"), pid);

        // Written by QueueFeeder.NoteClientDrops - the running total of journal events this
        // install's own receive channel evicted before the queue ever saw them. An absent row is
        // "0, never shown", which is the right answer on a machine that has not lost any.
        long dropped = long.TryParse(db.Get("journal:dropped"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long jd) ? jd : 0;

        return new IndexSnapshot(
            Schema: ContentDb.SchemaVersion,
            WasRebuilt: db.WasRebuilt || db.Get("index:rebuilt") == "1",
            DbPath: db.Path, Stores: Stores(db.Path),
            Cursors: cursors, Queued: queued, Indexed: indexed, Failed: failed, Skipped: skipped,
            ByKind: db.CountsByKind(),
            IndexerState: db.Get("indexer:state") ?? "", IndexerCurrent: db.Get("indexer:current") ?? "",
            IndexerRate: db.Get("indexer:rate") ?? "", IndexerPid: pid, IndexerAlive: alive,
            JournalDropped: dropped, Failures: db.RecentFailures(10));
    }

    /// <summary>Every file the index is made of that exists right now - the database and its
    /// write-ahead log and shared-memory sidecars. The names are the file names, not the paths:
    /// the path is already on the line above, and this report is pasted into bug reports.</summary>
    private static IReadOnlyList<(string Name, long Bytes)> Stores(string dbPath)
    {
        var rows = new List<(string, long)>();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string p = dbPath + suffix;
            try
            {
                var fi = new FileInfo(p);
                if (fi.Exists) rows.Add((Path.GetFileName(p), fi.Length));
            }
            catch (IOException) { }
        }
        return rows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    /// <summary>The 64-bit file reference number the journal and the index agree on - the only way
    /// to hand-queue a file the same way the journal would have queued it.</summary>
    public static ulong FrnOf(string path)
    {
        using SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(h, out ByHandleFileInformation info))
            throw new IOException("GetFileInformationByHandle failed for " + path);
        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }
}
