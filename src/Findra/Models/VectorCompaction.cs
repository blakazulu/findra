using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Findra;

/// <summary>
/// Giving the vector file back its dead rows.
///
/// <para>The file only grows. A deleted or re-read file's rows are zeroed where they stand, and a
/// row can also be left with nothing pointing at it by a process that ended between committing a
/// replacement and zeroing what it replaced. Left alone, a disk that has moved a folder of photos
/// to another drive carries both copies' rows for good, and every search reads past them.</para>
///
/// <para>A file another process has memory-mapped - a card, a diagnostic - cannot be shrunk or
/// replaced, so nothing is edited in place. The rows still in use are copied, byte for byte, into
/// a file under a new name; <see cref="ContentDb.Renumber"/> points every segment at its new row
/// and names the new file in one transaction; readers look the name up before they search
/// (<see cref="Semantic.Follow"/>). Old files are deleted once nothing has them open.</para>
///
/// <para>Only the indexer runs it: it is the one writer, and it runs it between files, when its
/// queue is empty, so no segment can change between reading which rows are in use and
/// renumbering them.</para>
/// </summary>
public static class VectorCompaction
{
    /// <summary>At least this many dead rows (about 30 MB) before a copy is worth making.</summary>
    public const long MinDeadRows = 20_000;

    /// <summary>...and at least this share of the file. A big library with a few deletes is left
    /// alone; the copy costs the same whatever it saves.</summary>
    public const double MinDeadShare = 0.25;

    /// <summary>How often the indexer asks, at most.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);

    /// <summary>Whether a file of <paramref name="rows"/> rows, <paramref name="live"/> of them in
    /// use, is worth copying.</summary>
    public static bool Worth(long rows, long live, long minDead = MinDeadRows, double minShare = MinDeadShare)
    {
        long dead = rows - live;
        return dead >= minDead && dead >= rows * minShare;
    }

    /// <summary>Copy the rows in use into a new file and point the index at it. Returns the writer
    /// on the new file, or <paramref name="writer"/> itself when too little is dead; the old
    /// writer is the caller's to dispose either way.</summary>
    public static VectorStore Run(ContentDb db, VectorStore writer, long minDead = MinDeadRows,
                                  double minShare = MinDeadShare)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(writer);
        List<long> live = db.LiveVectorRows();
        long rows = writer.Count;
        if (!Worth(rows, live.Count, minDead, minShare)) return writer;

        var sw = Stopwatch.StartNew();
        string dir = Path.GetDirectoryName(Path.GetFullPath(writer.FilePath))!;
        string name = "vectors-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".bin";
        string to = Path.Combine(dir, name);
        long before = SizeOf(writer.FilePath);

        writer.CopyRowsTo(live, to);
        double copied = sw.Elapsed.TotalSeconds;
        db.Renumber(live, name);

        Log.Info("index", string.Create(CultureInfo.InvariantCulture,
            $"vectors compacted: {rows} row(s) to {live.Count}, {Sizes.Human(before)} to {Sizes.Human(SizeOf(to))}, " +
            $"in {sw.Elapsed.TotalSeconds:0.0} s (copy {copied:0.0} s, renumber {sw.Elapsed.TotalSeconds - copied:0.0} s)"));
        return new VectorStore(to, writer: true);
    }

    /// <summary>Delete every vector file beside the index that is not the current one: files a
    /// compaction replaced, once whoever had them mapped has let go, and a copy a process died
    /// before recording. One still open stays until the next time. Returns how many went.</summary>
    public static int DeleteStale(ContentDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        string current = db.VectorsPath();
        string dir = Path.GetDirectoryName(current)!;
        string keep = Path.GetFileName(current), keepKinds = keep + ".kinds";
        int gone = 0;
        string[] files;
        try { files = Directory.GetFiles(dir, "vectors*.bin*"); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        foreach (string f in files)
        {
            string name = Path.GetFileName(f);
            if (!name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".bin.kinds", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals(keep, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(keepKinds, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(f); gone++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (gone > 0)
            Log.Info("index", string.Create(CultureInfo.InvariantCulture, $"removed {gone} old vector file(s)"));
        return gone;
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length + new FileInfo(path + ".kinds").Length; }
        catch (IOException) { return 0; }
    }
}
