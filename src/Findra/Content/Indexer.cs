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
// It drains the queue the interface fills (the `pending` table): asks its IDecoders whether this
// kind can be read at all, and if it can, writes the segments, the full-text rows and the vector
// rows those segments point at in one transaction. Everything expensive, and everything
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
    /// <summary>The queue reason that means "the bytes did not change, what Findra can do with them
    /// did". It reopens a file the index already holds as indexed; a skipped file is reopened
    /// whatever the reason says, because it was never opened in the first place.</summary>
    public const string Recheck = "recheck";

    /// <summary>The meta row the interface writes the transcription limit to. The child reads it
    /// per file, the same way it reads <c>index:power</c>, so raising the limit takes effect on
    /// the next recording rather than on the next restart.</summary>
    public const string TranscribeMinutesKey = "index:transcribeminutes";

    private readonly ContentDb _db;
    private readonly int _parentPid;
    private readonly IDecoders _decoders;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _done, _failed;
    private double _rateWindowStart;
    private long _rateWindowDone;
    private string _rate = "";

    private Indexer(ContentDb db, int parentPid, IDecoders decoders)
    {
        _db = db;
        _parentPid = parentPid;
        _decoders = decoders;
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
            // The only place in the product that opens a writer on the real vector store. It is
            // held for the life of the process and disposed last, and BOTH of the things that can
            // move while this process runs - the transcription limit, and which models are on
            // disk - are read through delegates rather than captured, so each of them reaches the
            // next file rather than the next launch.
            using IDecoders decoders = Decoders.ForThisMachine(() => TranscribeMinutes(db));
            Loop(db, parent, () => true, decoders);
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

    /// <summary>How long a recording is worth transcribing, off the same meta row the interface
    /// writes. Missing or unreadable is the default rather than "no limit": a machine that has
    /// never been asked must not spend hours on the first four-hour recording it meets.</summary>
    public static int TranscribeMinutes(ContentDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return int.TryParse(db.Get(TranscribeMinutesKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)
            ? m
            : TranscribeLimit.Default;
    }

    /// <summary>Run the queue to empty in THIS process and return - how a diagnostic exercises the
    /// same code the child runs, with the outcome of every file on a console. The database is the
    /// caller's: it opened it, it closes it, and taking it as a parameter is what lets a probe
    /// drain its own files against an index it is already holding.
    ///
    /// <para><paramref name="decoders"/> is the caller's too, and there is deliberately no overload
    /// that builds one: an overload that quietly called <see cref="Decoders.ForThisMachine"/>
    /// would hand a benchmark a writer on the user's real vector store and append rows for a
    /// synthetic corpus that the database referencing them is about to delete. Making that a
    /// compile error is worth more than a test.</para></summary>
    public static void DrainOnce(ContentDb db, Action<string> report, IDecoders decoders)
    {
        var ix = new Indexer(db, 0, decoders);
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

    // THE FALLBACK, not the mechanism. What actually stops this process when the interface goes is
    // the kill-on-close job object the interface put it in (see JobObject and IndexerHost): the
    // kernel terminates whatever is in the job when the last handle to it closes, which happens
    // however the interface dies. This poll exists for the environment that refused the
    // assignment, and IndexerHost logs which of the two is in force.
    //
    // It is deliberately kept, and it is deliberately not trusted on its own: Windows reuses
    // process ids, so a parent id reissued to something else between two polls reads as a live
    // parent forever. Any exception here - the pid is gone, or was recycled into something this
    // process cannot open - means the parent this child belongs to is not there any more.
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
    public static void Loop(ContentDb db, int parentPid, Func<bool> running, IDecoders decoders)
        => new Indexer(db, parentPid, decoders).Loop(running);

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
                // Delete hands back the vector rows its segments pointed at, and they are released
                // AFTER the transaction commits: a tombstone is destructive, and a rollback that
                // has already zeroed them leaves the surviving segments pointing at nothing. A
                // photo deleted a year ago still answering a query is what discarding this costs.
                List<long> gone;
                using (var tx = _db.Begin())
                {
                    gone = _db.Delete(item.Vol, item.Frn, tx);
                    _db.Dequeue(item.Id, tx);
                    tx.Commit();
                }
                _decoders.Release(gone);
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

            if (!_decoders.CanRead(item.Kind))
            {
                // Not an error and not a failure: a normal state this machine is in until the
                // capability that reads this kind arrives. The row stays exactly where
                // RequeueKinds can find it (spec §6).
                //
                // The return is captured here for the same reason as the other three, and the
                // case is real if rare: a machine that HAD a capability, indexed with it, and no
                // longer has the files - somebody cleared %LOCALAPPDATA%\Findra\models by hand.
                // The item drops back to Skipped, its segments go, and its vector rows have to go
                // with them or they answer queries for a file the index no longer describes.
                List<long> stale;
                using (var tx = _db.Begin())
                {
                    stale = _db.Upsert(item.Vol, item.Frn, item.Path, item.Kind, mtime, fi.Length,
                                       ContentDb.StateSkipped, Decoders.NoModel, [], tx);
                    _db.Dequeue(item.Id, tx);
                    tx.Commit();
                }
                _decoders.Release(stale);
                _done++;
                return "skipped";
            }

            KindResult decoded = _decoders.Decode(item.Kind, item.Path, fi.Length);

            _decoders.Flush();                     // before the commit that references the rows
            List<long> released;
            using (var tx = _db.Begin())
            {
                // The state follows Skip alone. Note goes into the same column and leaves the
                // row INDEXED: a long video whose frames were read is not a file Findra failed
                // to read, it is one it read incompletely, and the difference is visible in
                // every count --searchindex prints.
                int state = decoded.Skip is not null ? ContentDb.StateSkipped : ContentDb.StateIndexed;
                released = _db.Upsert(item.Vol, item.Frn, item.Path, item.Kind, mtime, fi.Length,
                                      state, decoded.Skip ?? decoded.Note, decoded.Segments, tx);
                _db.Dequeue(item.Id, tx);
                tx.Commit();
            }
            _decoders.Release(released);            // after it
            _done++;
            if (decoded.Skip is not null) return "skipped";

            Log.Once($"index|first|{item.Kind}", "INFO", "index",
                $"first {item.Kind.ToString().ToLowerInvariant()} indexed: {Path.GetFileName(item.Path)} -> {decoded.Segments.Count.ToString(CultureInfo.InvariantCulture)} segment(s) in {sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
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
                // A file that indexed once and later throws - a PDF replaced by a broken one -
                // would otherwise keep its old vector rows for ever, and nothing will ever
                // tombstone them because the item now says Failed.
                List<long> dead;
                using (var tx = _db.Begin())
                {
                    dead = _db.Upsert(item.Vol, item.Frn, item.Path, item.Kind, mtime, 0, ContentDb.StateFailed,
                                      $"{ex.GetType().Name}: {ex.Message}", Array.Empty<ContentDb.Segment>(), tx);
                    _db.Dequeue(item.Id, tx);
                    tx.Commit();
                }
                _decoders.Release(dead);
            }
            catch (Exception ex2) { Log.Once("index|fail|record", "ERROR", "index", $"could not record a failure :: {ex2.Message}"); }
            return "FAILED";
        }
    }
}
