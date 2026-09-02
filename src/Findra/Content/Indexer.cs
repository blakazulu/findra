using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Findra;

/// <summary>The two arguments of <c>findra.exe --index</c>, parsed. Pure, so the rules about what
/// counts as a parent pid are testable without starting a process.</summary>
public static class IndexerArgs
{
    /// <summary>A pid of 0 means "nobody to outlive" - the by-hand run, which must sit and work
    /// rather than exit immediately. Anything that is not a positive number is that case: a
    /// mistyped or missing argument must never be read as a live parent, and a negative number is
    /// not a pid at all.</summary>
    public static (int ParentPid, string DbPath) Parse(string[] args)
    {
        int pid = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p > 0 ? p : 0;
        string db = args.Length > 2 && args[2].Length > 0 ? args[2] : ContentDb.DefaultPath;
        return (pid, db);
    }
}

// `findra.exe --index <parentPid>`: the content indexer, in its own process.
//
// It drains the queue the interface fills (the `pending` table): decodes the file and writes its
// text segments and the full-text rows in one transaction. Everything expensive, and everything
// that can be taken down by a malformed file, happens HERE - so a bad PDF costs this process and
// not the card, and the interface starts a fresh one. It parses untrusted file content, which is
// exactly why it runs at normal integrity and the volume-reading helper does not (spec §3).
//
// It reads the filesystem only through the paths the queue hands it; the journal and the
// eligibility rules belong to the interface.
//
// It stays out of the way: BelowNormal priority, one file at a time with a rest between sized by
// the power setting, and a full stop while the pause switch is on. It exits when the parent dies -
// that check is the whole of its lifetime code, and it is why indexing stops when the app quits.
// Status goes back through the `meta` table, namespaced under `indexer:`.
public sealed class Indexer
{
    /// <summary>Past this a "document" is a corpus - a database dump, a concatenated log - and
    /// reading it whole costs minutes and finds nothing anyone was looking for.</summary>
    public const long MaxDocBytes = 200L << 20;

    /// <summary>Recorded against a file whose kind Findra cannot yet read INSIDE. It is a normal,
    /// re-queueable outcome and never a failure: the capability that arrives later picks up exactly
    /// the rows that carry it (spec §6, and <see cref="ContentDb.RequeueKinds"/>).</summary>
    private const string NoDecoder = "no decoder for this kind yet";

    /// <summary>Recorded against a file whose FORMAT this build has no reader for, as distinct from
    /// <see cref="NoDecoder"/>'s "no model for this kind". Reading those bytes as text would index
    /// zip structure and mojibake and call the file indexed; skipped with a reason of its own, a
    /// later plan that adds a real reader re-queues exactly these rows and nothing else.</summary>
    private const string NoFormatDecoder = "no decoder for this format yet";

    /// <summary>The queue reason that means "the bytes did not change, what Findra can do with them
    /// did". It reopens a file the index already holds as indexed; a skipped file is reopened
    /// whatever the reason says, because it was never opened in the first place.</summary>
    public const string Recheck = "recheck";

    private readonly ContentDb _db;
    private readonly int _parentPid;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _done, _failed;
    private double _rateWindowStart;
    private long _rateWindowDone;
    private string _rate = "";

    private Indexer(ContentDb db, int parentPid)
    {
        _db = db;
        _parentPid = parentPid;
    }

    public static int Run(string[] args)
    {
        (int parent, string dbPath) = IndexerArgs.Parse(args);
        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            // A priority the OS declines to lower is a slower indexer, not a broken one.
            Log.Warn("index", $"could not lower the indexer's priority :: {ex.Message}");
        }
        Log.Info("index", $"indexer up (pid {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}, parent {parent.ToString(CultureInfo.InvariantCulture)})");

        try
        {
            using ContentDb db = ContentDb.OpenOrRebuild(dbPath);
            Loop(db, parent, () => true);
            Log.Info("index", "indexer down (clean)");
            Log.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("index", "indexer died", ex);
            Log.Flush();
            return 1;
        }
    }

    /// <summary>Run the queue to empty in THIS process and return - how a diagnostic exercises the
    /// same code the child runs, with the outcome of every file on a console. The database is the
    /// caller's: it opened it, it closes it, and taking it as a parameter is what lets a probe
    /// drain its own files against an index it is already holding.</summary>
    public static void DrainOnce(ContentDb db, Action<string> report)
    {
        var ix = new Indexer(db, 0);
        long stuck = -1;
        string stuckOn = "";
        while (true)
        {
            ContentDb.Pending? next = db.TakeNext();
            if (next is null) break;
            ContentDb.Pending item = next.Value;
            if (item.Id == stuck)
            {
                // Handle failed even to record its own failure, so the row is still queued and the
                // next TakeNext hands back the same one. Looping on it forever would hang whoever
                // called this; stopping with the queue intact is the honest outcome.
                stuckOn = Path.GetFileName(item.Path);
                report($"stuck    {item.Kind,-8} {stuckOn} - the queue could not be advanced past it");
                break;
            }
            var t = Stopwatch.StartNew();
            string what = ix.Handle(item);
            stuck = item.Id;
            report($"{what,-8} {item.Kind,-8} {t.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),6} ms  {Path.GetFileName(item.Path)}");
        }
        // "idle" means the queue is empty. A drain that stopped because it could not get past one
        // row leaves the queue full, and recording that as idle is the status row telling the card
        // and --searchindex that there is nothing to do while the count sits there not moving. The
        // running loop already says "stuck" for exactly this; a one-shot drain owes the same word.
        if (stuckOn.Length > 0) ix.Status("stuck", stuckOn); else ix.Status("idle");
    }

    // The parent is polled rather than waited on: a handle would have to be inherited or opened by
    // pid, and both add a failure mode to a check that is allowed to be approximate. Any exception
    // here - the pid is gone, or was recycled into something this process cannot open - means the
    // parent this child belongs to is not there any more.
    private bool ParentGone()
    {
        if (_parentPid <= 0) return false;
        try { using var p = Process.GetProcessById(_parentPid); return p.HasExited; }
        catch { return true; }
    }

    // Namespaced: `state` and `pending` in a table that also holds `schema` and `usn:C` is a
    // collision waiting for a third writer. `indexer:` is what this process writes; `index:` is
    // what the interface writes and this process reads.
    private void Status(string state, string current = "")
    {
        try
        {
            _db.Set("indexer:state", state);
            _db.Set("indexer:current", current);
            _db.Set("indexer:done", _done.ToString(CultureInfo.InvariantCulture));
            _db.Set("indexer:failed", _failed.ToString(CultureInfo.InvariantCulture));
            _db.Set("indexer:rate", _rate);
            _db.Set("indexer:pending", _db.PendingCount().ToString(CultureInfo.InvariantCulture));
            _db.Set("indexer:beat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            _db.Set("indexer:pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) { Log.Once("index|status", "WARN", "index", $"indexer status write failed :: {ex.Message}"); }
    }

    /// <summary>The child's whole working life, driven by a caller that decides when it is over.
    /// <paramref name="running"/> is asked once per pass, which is what lets something other than
    /// the process entry point - a diagnostic, a test - put files through the loop the child
    /// actually runs rather than through a copy of it.</summary>
    public static void Loop(ContentDb db, int parentPid, Func<bool> running)
        => new Indexer(db, parentPid).Loop(running);

    private void Loop(Func<bool> running)
    {
        string lastState = "";
        long stuck = -1;
        var lastStatus = Stopwatch.StartNew();
        while (running())
        {
            if (ParentGone()) { Status("stopped"); return; }

            if (_db.Get("index:paused") == "1")
            {
                if (lastState != "paused") { Log.Info("index", "indexer paused"); lastState = "paused"; }
                Status("paused");
                Thread.Sleep(2000);
                continue;
            }

            ContentDb.Pending? next = _db.TakeNext();
            if (next is null)
            {
                if (lastState != "idle")
                {
                    Log.Info("index", $"indexer idle: queue drained ({_done.ToString(CultureInfo.InvariantCulture)} done, {_failed.ToString(CultureInfo.InvariantCulture)} failed this session)");
                    lastState = "idle";
                }
                Status("idle");
                Thread.Sleep(2000);
                continue;
            }
            ContentDb.Pending item = next.Value;
            if (item.Id == stuck)
            {
                // Handle failed even at recording its own failure, so the row was never dequeued
                // and TakeNext keeps handing back the same one. At the 30 ms working rest that is
                // thirty attempts a second for as long as the process lives, with Log.Once
                // swallowing every line after the first. Rest as if idle instead: the row is left
                // exactly where it was, a transient cause still clears on the next attempt, and a
                // permanent one costs the machine one attempt every two seconds rather than the
                // whole of a core.
                Log.Once($"index|stuck|{item.Id.ToString(CultureInfo.InvariantCulture)}", "WARN", "index",
                    $"the queue could not be advanced past {Path.GetFileName(item.Path)} - retrying it slowly");
                lastState = "stuck";
                Status("stuck", Path.GetFileName(item.Path));
                stuck = -1;
                Thread.Sleep(2000);
                continue;
            }
            if (lastState != "indexing") { Log.Info("index", "indexer working"); lastState = "indexing"; }

            var busy = Stopwatch.StartNew();
            _ = Handle(item);
            stuck = item.Id;
            busy.Stop();
            if (lastStatus.ElapsedMilliseconds > 1500) { Status("indexing", Path.GetFileName(item.Path)); lastStatus.Restart(); UpdateRate(); }
            // The power setting is a duty cycle: at 50% the indexer rests as long as it worked, at
            // 25% three times as long. The rest is between files, where nothing is running, so it
            // shapes every part of the machine equally.
            int power = int.TryParse(_db.Get("index:power"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pw) ? Math.Clamp(pw, 10, 100) : 100;
            int rest = power >= 100 ? 30 : (int)Math.Min(8000, busy.ElapsedMilliseconds * (100.0 - power) / power) + 30;
            Thread.Sleep(rest);
        }
    }

    private void UpdateRate()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _rateWindowStart >= 30)
        {
            double perMin = (_done - _rateWindowDone) / (now - _rateWindowStart) * 60;
            _rate = perMin.ToString("0", CultureInfo.InvariantCulture) + "/min";
            _rateWindowStart = now;
            _rateWindowDone = _done;
        }
    }

    /// <summary>Deal with one queued file and take it off the queue. Returns the word that
    /// describes what happened, which is what a drain prints beside the file name.</summary>
    private string Handle(ContentDb.Pending item)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (item.Reason == ContentDb.ReasonDelete)
            {
                using var tx = _db.Begin();
                // Delete hands back the vector rows its segments pointed at. Nothing in this build
                // owns a vector store, and every segment it writes carries Vec -1, so there is
                // nothing here to release; the store that fills that column tombstones them.
                _ = _db.Delete(item.Vol, item.Frn, tx);
                _db.Dequeue(item.Id, tx);
                tx.Commit();
                return "removed";
            }

            var fi = new FileInfo(item.Path);
            if (!fi.Exists)
            {
                // Moved or deleted between queuing and now. The journal will say which, and a file
                // that is simply gone is not a failure - recording one would fill the report with
                // rows nobody can act on.
                using var tx = _db.Begin();
                _db.Dequeue(item.Id, tx);
                tx.Commit();
                return "gone";
            }
            long mtime = fi.LastWriteTimeUtc.Ticks;
            // "Finished" here is not the freshness check on its own. A skipped file carries its
            // real modification time, so the bytes ARE current - and it was never opened, because
            // nothing could read it at the time. Re-queueing is how a capability picks those rows
            // up, and it arrives carrying whatever reason the caller wrote; deciding from that
            // string alone would dequeue every one of them untouched, move no counter and log
            // nothing. Recheck stays for reopening a file that genuinely was indexed.
            if (item.Reason != Recheck
                && _db.StateOf(item.Vol, item.Frn) != ContentDb.StateSkipped
                && _db.IsCurrent(item.Vol, item.Frn, mtime))
            {
                using var tx = _db.Begin();
                _db.Dequeue(item.Id, tx);
                tx.Commit();
                return "current";
            }

            (List<ContentDb.Segment> segments, string? skip) = item.Kind switch
            {
                ResultKind.Document => Document(item.Path, fi.Length),
                // Words in documents is free and always on; every other kind needs a model this
                // build does not carry. That is a normal state, recorded by name, and never an
                // error - the file is left exactly where a later capability can find it.
                ResultKind.Photo or ResultKind.Video or ResultKind.Audio => ([], NoDecoder),
                _ => ([], "not a content kind"),
            };

            using (var tx = _db.Begin())
            {
                int state = skip is not null ? ContentDb.StateSkipped : ContentDb.StateIndexed;
                _ = _db.Upsert(item.Vol, item.Frn, item.Path, item.Kind, mtime, fi.Length, state, skip, segments, tx);
                _db.Dequeue(item.Id, tx);
                tx.Commit();
            }
            _done++;
            if (skip is not null) return "skipped";

            Log.Once($"index|first|{item.Kind}", "INFO", "index",
                $"first {item.Kind.ToString().ToLowerInvariant()} indexed: {Path.GetFileName(item.Path)} -> {segments.Count.ToString(CultureInfo.InvariantCulture)} segment(s) in {sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
            if (sw.ElapsedMilliseconds > 120_000)
                Log.Warn("index", $"slow: {Path.GetFileName(item.Path)} took {sw.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s ({item.Kind}, {(fi.Length / 1048576).ToString(CultureInfo.InvariantCulture)} MB)");
            return "indexed";
        }
        catch (Exception ex)
        {
            _failed++;
            Log.Once($"index|fail|{item.Kind}|{ex.GetType().Name}|{Path.GetExtension(item.Path)}", "WARN", "index",
                $"cannot index {Path.GetFileName(item.Path)} ({item.Kind}) :: {ex.GetType().Name}: {ex.Message}");
            try
            {
                long mtime = File.Exists(item.Path) ? new FileInfo(item.Path).LastWriteTimeUtc.Ticks : 0;
                using var tx = _db.Begin();
                _ = _db.Upsert(item.Vol, item.Frn, item.Path, item.Kind, mtime, 0, ContentDb.StateFailed,
                               $"{ex.GetType().Name}: {ex.Message}", Array.Empty<ContentDb.Segment>(), tx);
                _db.Dequeue(item.Id, tx);
                tx.Commit();
            }
            catch (Exception ex2) { Log.Once("index|fail|record", "ERROR", "index", $"could not record a failure :: {ex2.Message}"); }
            return "FAILED";
        }
    }

    // ---- kinds ----

    // Text only. `Vec` is -1 on every segment: "no vector row", which is the whole of what a
    // model-free build can say about one.
    private static (List<ContentDb.Segment>, string?) Document(string path, long bytes)
    {
        if (bytes > MaxDocBytes) return ([], "too large");
        if (!DocText.CanExtract(path)) return ([], NoFormatDecoder);
        string text = DocText.Extract(path);
        if (text.Length < 40) return ([], "no text");
        var segs = new List<ContentDb.Segment>();
        foreach (string chunk in DocText.Chunk(text))
            segs.Add(new ContentDb.Segment(ContentDb.SegText, -1, -1, -1, chunk));
        return (segs, null);
    }
}
