using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Findra.Pipe;
using Microsoft.Data.Sqlite;

namespace Findra;

/// <summary>
/// The interface's policy about what gets indexed, and the only thing that ever writes the queue.
///
/// Two sources feed it. Journal events arrive from the helper and are classified here - a create,
/// a rename, a change, a delete - and a first pass over everything already on the disk arrives as
/// an <see cref="EnumeratedFile"/> stream from the same helper. Neither source decides anything:
/// the helper forwards records and resolves paths, and every rule about whether a file is worth
/// opening is applied in THIS process, at normal integrity, because the elevated half must never
/// grow an opinion about the files it can read.
///
/// It also owns where journal consumption got to. That position is written in the same transaction
/// as the work it came from, and it always carries the journal's id: a USN is a coordinate inside
/// one journal, and a number stored without its id is compared on the next launch against a
/// journal that may have been recreated since, which answers "re-walk the whole disk" every single
/// start.
///
/// One thread. The interface drives it from a single background loop; nothing here is written to
/// be called from two at once, and the store it holds is the process's single WRITER connection.
/// </summary>
public sealed class QueueFeeder : IDisposable
{
    /// <summary>What a file queued by the first pass is queued for.</summary>
    private const string ReasonNew = "new";

    /// <summary>A file whose bytes changed, or that arrived somewhere new.</summary>
    private const string ReasonChange = "change";

    private readonly ContentDb _db;
    private readonly Func<Config> _config;

    private IReadOnlyList<string> _repoRoots = [];

    /// <summary>The last total THIS SESSION'S channel was seen at, so an unchanged count is not
    /// read as a fresh drop. Reset by <see cref="NoteSessionStarted"/>, because the counter it
    /// tracks belongs to one connection and starts again at zero on the next one. See
    /// <see cref="NoteClientDrops"/>.</summary>
    private long _drops;

    /// <summary>This session's dropped-event counter, read at the moment a decision depends on it
    /// rather than whenever the caller last thought to mention it. See
    /// <see cref="NoteSessionStarted"/>.</summary>
    private Func<long>? _dropSource;

    /// <summary>Volumes whose first full pass is in flight, and the drop total each of them
    /// started against. A pass cannot discharge a debt that opened while it was running - see
    /// <see cref="NoteWalkStarted"/>.</summary>
    private readonly Dictionary<char, long> _walking = [];

    private bool _disposed;

    public QueueFeeder(ContentDb db, Func<Config> config)
    {
        _db = db;
        _config = config;
    }

    // The feeder owns no unmanaged handle of its own: the store belongs to whoever opened it and
    // outlives every feeder, and the queue is a table, not a buffer that needs flushing. Dispose
    // exists so the lifetime is written down at the call site and so a feeder that has been let go
    // stops touching a store somebody else may be closing.
    public void Dispose() => _disposed = true;

    // ---- eligibility -------------------------------------------------------------------------

    /// <summary>Worth opening: a kind with content, outside every exclusion, outside every
    /// repository. A repository's fixtures, sample media and vendored documents are not the
    /// user's library; their NAMES stay searchable, their contents are not read.
    ///
    /// Static and pure, taking the rules as parameters rather than reading fields, because this is
    /// the decision most likely to be argued about later and it has to be answerable one path at a
    /// time with no database, no config file and no helper.</summary>
    public static bool Eligible(string path, ResultKind kind,
                                IReadOnlyList<string> exclusions, IReadOnlyList<string> repoRoots)
        => FileKinds.HasContent(kind)
           && !FileKinds.Excluded(path, exclusions)
           && !UnderAnyRoot(path, repoRoots);

    /// <summary>Under a root, not merely starting with its characters. Without the separator
    /// test, a root of C:\Code\findra silently swallows C:\Code\findra-notes as well.</summary>
    private static bool UnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        foreach (string root in roots)
            if (path.Length > root.Length && path[root.Length] == '\\' &&
                path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The repository roots to keep out of the content index. Sorted once, ordinally and
    /// case-insensitively, because it is walked per file.</summary>
    public void SetRepoRoots(IReadOnlyList<string> roots)
    {
        var sorted = new List<string>(roots.Count);
        foreach (string r in roots) sorted.Add(r.TrimEnd('\\'));
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        _repoRoots = sorted;
    }

    /// <summary>
    /// The suffixes the helper is asked to enumerate: every extension that classifies to a kind
    /// with content, lower case, with the dot, deduplicated and sorted.
    ///
    /// Built from <see cref="FileKinds.ContentExtensions"/> rather than written out, so the list
    /// the helper is given and the list this process will accept when the rows come back are the
    /// same list. Stamped into the index by <see cref="FillFrom"/>, which is how a later build
    /// that adds an extension gets an already-finished disk walked again.
    /// </summary>
    public static IReadOnlyList<string> ContentSuffixes() => Suffixes;

    private static readonly IReadOnlyList<string> Suffixes = BuildSuffixes();

    private static IReadOnlyList<string> BuildSuffixes()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string ext in FileKinds.ContentExtensions())
        {
            string e = ext.Trim().TrimStart('.').ToLowerInvariant();
            if (e.Length > 0) set.Add("." + e);
        }
        return [.. set];
    }

    // ---- journal events ----------------------------------------------------------------------

    /// <summary>
    /// Take one batch of journal events and queue whatever they imply. Returns how many rows were
    /// queued.
    ///
    /// The batch is one transaction, and the consumed position is written inside it. Recording the
    /// position separately means a crash between the two either loses work or repeats it, which is
    /// the same bug from either side.
    /// </summary>
    public int Consume(IReadOnlyList<JournalEvent> events)
    {
        if (_disposed || events.Count == 0) return 0;

        Config cfg = _config();
        IReadOnlyList<string> exclusions = cfg.SearchExclusions;
        IReadOnlyList<string> roots = _repoRoots;

        var positions = new Dictionary<char, Position>();
        int queued = 0;

        using SqliteTransaction tx = _db.Begin();

        foreach (JournalEvent e in events)
        {
            char letter = char.ToUpperInvariant(e.Volume);
            string vol = letter.ToString();
            if (!positions.TryGetValue(letter, out Position? at)) positions[letter] = at = new Position();

            // The tail's reset marker: reason 0, no name. The journal wrapped past our place, so
            // the position is worthless AND a hole is owed. Clearing the position alone is not
            // enough - the very next batch writes a fresh one from later events, and within
            // milliseconds the index is back to claiming it is caught up over a range it never
            // saw. The debt has to outlive the clear, which is why it is a row and not a field.
            if (e.Reason == 0 && e.Name.Length == 0)
            {
                at.Cleared = true;
                at.Max = -1;
                _db.SetWalkOwed(letter, tx);
                Log.Warn("index", $"{letter}: the journal lost our place - a fresh walk of that drive is owed");
                continue;
            }

            if (at.Journal != e.JournalId) { at.Journal = e.JournalId; at.Max = -1; }
            if (at.Max < 0 || e.Usn > at.Max) at.Max = e.Usn;

            bool deleted = (e.Reason & NtfsVolume.ReasonFileDelete) != 0;
            if (deleted)
            {
                // Every deletion on the volume arrives here - browser caches, build outputs, tens
                // of thousands an hour - and all but a handful name files this index never held.
                // Queuing them all buries the real work behind no-op deletes.
                if (_db.HasItem(vol, e.Frn))
                {
                    _db.Enqueue(vol, e.Frn, e.Path, FileKinds.Classify(e.Name, false), ContentDb.ReasonDelete, tx);
                    queued++;
                }
                continue;
            }

            // A create, a rename or a write. Without a path there is nothing to judge: the helper
            // resolves one for every live record, so an empty one means the record was already
            // gone by the time the tail looked, and a later event will say so.
            if (e.Path.Length == 0) continue;
            if ((e.Attributes & NtfsVolume.FileAttributeDirectory) != 0) continue;

            ResultKind kind = FileKinds.Classify(e.Name, false);
            if (Eligible(e.Path, kind, exclusions, roots))
            {
                _db.Enqueue(vol, e.Frn, e.Path, kind,
                            (e.Reason & NtfsVolume.ReasonFileCreate) != 0 ? ReasonNew : ReasonChange, tx);
                queued++;
            }
            else if (_db.HasItem(vol, e.Frn))
            {
                // It was indexed and it is not eligible any more: moved into an excluded folder,
                // or into a repository this build now skips. Leaving the row behind would keep
                // answering searches with content from a place the rules say is not read.
                _db.Enqueue(vol, e.Frn, e.Path, kind, ContentDb.ReasonDelete, tx);
                queued++;
            }
        }

        foreach ((char letter, Position at) in positions) WritePosition(letter, at, tx);

        // In the SAME transaction as the positions those events just moved, and after them.
        //
        // After, because the charge is spread over the volumes the index has a position for, and
        // a volume this batch is the first to give a position to is not one of them until the
        // lines above run - it would take a position covering a range the channel ate and owe
        // nobody a walk for it. In the same transaction, because a debt written afterwards is a
        // debt a crash between the two commits can take while leaving the position behind, which
        // is exactly the state the walk debt exists to make impossible.
        if (_dropSource is { } current) NoteClientDrops(current(), tx);

        tx.Commit();
        return queued;
    }

    private void WritePosition(char letter, Position at, SqliteTransaction tx)
    {
        if (at.Max < 0)
        {
            if (at.Cleared) _db.ClearUsnPosition(letter, tx);
            return;
        }

        // A USN is a coordinate inside ONE journal. Store the id with it, or the next launch
        // compares this number against a journal that has a different id, concludes the position
        // is meaningless, and re-walks the entire disk - at every single start.
        //
        // Forwards WITHIN the same journal, and unconditionally when the journal changed. "Only
        // forwards" alone is wrong: a recreated journal restarts its numbering near zero, so a
        // strictly-increasing rule would pin the position inside a journal that no longer exists
        // and this feeder would never record anything again.
        (ulong Journal, long Usn)? had = at.Cleared ? null : _db.UsnPosition(letter);
        if (had is null || had.Value.Journal != at.Journal || at.Max > had.Value.Usn)
            _db.SetUsnPosition(letter, at.Journal, at.Max, tx);
    }

    private sealed class Position
    {
        public ulong Journal;
        public long Max = -1;
        public bool Cleared;
    }

    // ---- the first pass ----------------------------------------------------------------------

    /// <summary>
    /// Tell the feeder a full pass over this volume has begun, and against which drop total.
    ///
    /// It exists to close one narrow window. A drop that lands WHILE the first pass is in flight
    /// records a debt against a volume that has no stored position yet, and the pass that is
    /// already running would then clear that debt on its way out - discharging a hole it never
    /// covered, because the events it lost are past the position the pass is about to stamp.
    /// A pass whose drop total moved under it leaves the debt standing and the next turn of the
    /// loop walks again.
    ///
    /// The other end of this is in <see cref="FillFrom"/>, which reads the counter for itself
    /// rather than trusting whatever the caller last reported. That is what makes the check
    /// falsifiable: the total recorded here and the total read there have to be able to differ,
    /// and if only the caller could move the number they never would.
    /// </summary>
    public void NoteWalkStarted(char volume) => _walking[char.ToUpperInvariant(volume)] = _drops;

    /// <summary>
    /// Queue everything on this volume that the index does not already hold, and record where the
    /// pass finished.
    ///
    /// <paramref name="throughUsn"/> is the volume's position read BEFORE the enumeration started,
    /// which the subscribe reply carries. Taking it afterwards would silently drop every change
    /// made during the walk; taking it before means those changes are replayed, and the queue's
    /// upsert on (volume, frn) makes the overlap free.
    ///
    /// Returns the number of DISTINCT files it queued, not the number of enqueue calls it made:
    /// the walk restarts from record zero when the journal moves under it, so one file can
    /// legitimately arrive twice in one stream, and a count of calls would not match the queue.
    ///
    /// It reads the session's dropped-event counter ITSELF, here, before it decides whether the
    /// pass discharges anything. Comparing a field the caller happens to have updated cannot
    /// work: the interface reports drops and awaits the walk on the same single thread, so
    /// nothing can move that field between <see cref="NoteWalkStarted"/> and this line and the
    /// check is true by construction. A pass that reads the counter at the moment it commits
    /// closes the window without a second walk, and can be made to fail.
    /// </summary>
    public int FillFrom(char volume, ulong journalId, long throughUsn, IEnumerable<EnumeratedFile> files)
    {
        if (_disposed) return 0;

        // Before the transaction below opens. A hole that opened during the walk is charged to
        // every volume the index knows about - including any OTHER volume this pass is not
        // about - and it has to be on the books whatever this transaction goes on to decide.
        if (_dropSource is { } current) NoteClientDrops(current());

        char letter = char.ToUpperInvariant(volume);
        string vol = letter.ToString();
        Config cfg = _config();
        IReadOnlyList<string> exclusions = cfg.SearchExclusions;
        IReadOnlyList<string> roots = _repoRoots;

        // One read of the item table rather than a lookup per file. A finished disk is a million
        // rows and a million single-row queries; this is one scan and a dictionary. Indexed,
        // Failed and Skipped all count as finished, which is what stops a photo with no decoder
        // being queued again on every launch. The value is the modification time recorded when
        // that file was last dealt with, which is what the enumerated record is compared against.
        Dictionary<(string, ulong), long> known = _db.KnownItems();
        var queued = new HashSet<ulong>();

        using SqliteTransaction tx = _db.Begin();

        foreach (EnumeratedFile f in files)
        {
            // Seen before is not the same as unchanged. Skipping on presence alone made this pass
            // able to see a file that was CREATED while Findra was closed and blind to one that
            // was MODIFIED - and this pass is the only fallback once the journal has wrapped, so
            // that file's stale words stayed searchable indefinitely while the stamps below
            // recorded the range as reconciled. The journal path has always compared an mtime
            // (ContentDb.IsCurrent); this is the same comparison on the other path.
            //
            // Mtime 0 is "the helper could not read one", not a timestamp - so it cannot prove the
            // file is unchanged and the file is queued. The indexer re-reads the mtime itself and
            // dequeues the row untouched when the bytes really did not move, which makes the safe
            // direction cost one no-op rather than one re-extraction.
            bool held = known.TryGetValue((vol, f.Frn), out long had);
            if (held && f.Mtime != 0 && had == f.Mtime) continue;
            ResultKind kind = FileKinds.Classify(System.IO.Path.GetFileName(f.Path), false);
            if (!Eligible(f.Path, kind, exclusions, roots)) continue;
            if (!queued.Add(f.Frn)) continue;
            _db.Enqueue(vol, f.Frn, f.Path, kind, held ? ReasonChange : ReasonNew, tx);
        }

        // All three stamps go in the SAME transaction as the enqueues. A pass that does not
        // record where it finished is a pass that happens again; a pass that does not clear the
        // debt is a pass that happens forever.
        _db.SetUsnPosition(letter, journalId, throughUsn, tx);
        _db.SetSuffixVersion(letter, ContentSuffixes(), tx);

        bool tainted = _walking.TryGetValue(letter, out long since) && since != _drops;
        if (!tainted) _db.ClearWalkOwed(letter, tx);
        _walking.Remove(letter);

        tx.Commit();

        if (tainted)
            Log.Warn("index", $"{letter}: events were lost while this pass was running, so it " +
                              "does not discharge the walk it owes; another one follows");

        Log.Info("index", string.Create(CultureInfo.InvariantCulture,
            $"{letter}: first pass queued {queued.Count} file(s), consumed through usn " +
            $"{throughUsn} in journal {journalId:x}"));

        return queued.Count;
    }

    // ---- what the interface hands back to the helper -------------------------------------------

    /// <summary>
    /// Every stored position, in the shape the subscribe request wants.
    ///
    /// It exists so the value this feeder WROTE and the value handed to the helper are provably
    /// one round trip rather than two pieces of code that happen to agree today.
    /// </summary>
    public IReadOnlyList<VolumeCursor> StoredCursors()
    {
        var cursors = new List<VolumeCursor>();
        foreach (char v in _db.KnownVolumes())
            if (_db.UsnPosition(v) is { } at) cursors.Add(new VolumeCursor(v, at.JournalId, at.Usn));
        return cursors;
    }

    /// <summary>
    /// Does this volume owe a full pass?
    ///
    /// Two halves. The suffix half fires when a LATER build added an extension: every existing
    /// install has only ever asked the helper for the old list, so those files were never
    /// enumerated and no journal event will ever mention the ones already on disk. The debt half
    /// is the drop path - a hole somebody recorded, which only a completed pass discharges.
    ///
    /// Asked every time round the interface's loop, not once at startup. A hole opened at 11am is
    /// no less a hole for the app having launched at 9.
    ///
    /// Both halves are per volume, including the suffix one. A single stamp for the whole index
    /// is discharged by whichever drive happens to be walked first, and every other drive is then
    /// told the extension list is current - which it is, and which it will stay, so on a machine
    /// with two indexed drives only one of them ever sees a new extension.
    /// </summary>
    public bool NeedsFreshWalk(char volume)
        => _db.SuffixSetOutOfDate(volume, ContentSuffixes()) || _db.WalkOwed(volume);

    /// <summary>
    /// Report the client's own dropped-event total. Any increase owes a fresh walk.
    ///
    /// The helper marks ITS drops with a reset marker, which arrives as an event like any other.
    /// A drop in the receive channel of this process has nothing upstream that knows it happened,
    /// so this counter is the only trace it leaves anywhere, and reporting it is the caller's job.
    ///
    /// The debt is charged to every volume the index has a position for, plus any whose first pass
    /// is still in flight - the channel is not per-volume, so it cannot be attributed more
    /// precisely, and over-walking is the safe direction. An unchanged total is not a new drop:
    /// re-reporting the same number must not re-owe a walk that a pass has since discharged.
    ///
    /// A LOWER total is not a correction either - it is a new connection, whose counter starts at
    /// zero again. Reading it as "nothing new" would let every drop in the second session hide
    /// behind the first session's larger number, which is the one thing this path exists to stop.
    ///
    /// Neither reading is enough on its own, which is why <see cref="NoteSessionStarted"/> tells
    /// this feeder when the channel changed: two connections that each lose the same number report
    /// the same number twice, and an equal total is indistinguishable from no news.
    ///
    /// What this index has lost, accumulated across every connection, is written to
    /// <c>journal:dropped</c>, which is where <c>--searchindex</c> reads it. Without that row the
    /// report says zero however many events were lost, and a dropped event is invisible in every
    /// other count it prints.
    /// </summary>
    /// <summary>
    /// A new connection to the helper, and the counter this session will be judged against.
    ///
    /// The dropped-event counter belongs to ONE client: the interface builds a new one every time
    /// the helper is unreachable or the pipe drops, and the new one starts at zero while this
    /// feeder lives for the whole process. Carrying the old session's total forward means the
    /// second session must lose MORE events than the first one ever did before any of its losses
    /// are recorded at all - and a drop in this process's own receive channel has nothing
    /// upstream that knows it happened, so the events it hides are hidden from everything.
    /// </summary>
    public void NoteSessionStarted(Func<long> journalDropped)
    {
        _dropSource = journalDropped;
        _drops = 0;

        // A walk that was in flight when the connection died never reached FillFrom, so its entry
        // would sit here forever, charged on every drop and compared against a total from a
        // channel that no longer exists. The pass itself did not survive the session either: the
        // debt it was going to discharge is still a row, and the next turn walks again.
        _walking.Clear();
    }

    public void NoteClientDrops(long journalDropped) => NoteClientDrops(journalDropped, null);

    /// <summary>The same charge, joined to a transaction that is already open, so the debt and
    /// the position it covers commit together or not at all.</summary>
    private void NoteClientDrops(long journalDropped, SqliteTransaction? tx)
    {
        if (_disposed || journalDropped == _drops) return;

        // Forwards WITHIN one session's channel; a LOWER total is a restart of the counter, and
        // a channel that restarted has ALREADY lost this many. Neither reading is a correction.
        long lost = journalDropped > _drops ? journalDropped - _drops : journalDropped;
        _drops = journalDropped;
        if (lost == 0) return;

        var charged = new SortedSet<char>(_db.KnownVolumes());
        foreach (char v in _walking.Keys) charged.Add(v);
        foreach (char v in charged) _db.SetWalkOwed(v, tx);

        // ACCUMULATED, not overwritten. --searchindex prints what this index has lost, and the
        // number this method is handed belongs to one connection: storing it raw would make the
        // report shrink every time the helper was restarted, which is the moment the reader most
        // needs it to grow.
        long before = long.TryParse(_db.Get("journal:dropped"), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out long had) ? had : 0;
        _db.Set("journal:dropped", (before + lost).ToString(CultureInfo.InvariantCulture), tx);

        Log.Warn("index", string.Create(CultureInfo.InvariantCulture,
            $"{lost} journal event(s) were dropped before they reached the queue; " +
            $"{charged.Count} volume(s) owe a fresh walk"));
    }

    // ---- reconcile -----------------------------------------------------------------------------

    /// <summary>
    /// Bring the queue and the index back in line with rules that changed between launches.
    ///
    /// Exclusions are config and repository roots are discovered, so both move. What they now
    /// exclude has to leave the queue, and what is already indexed under them has to be queued for
    /// removal. Returns how many rows it touched.
    ///
    /// It does NOT clear a walk debt. Nothing but a completed pass may.
    /// </summary>
    public int Reconcile()
    {
        if (_disposed) return 0;

        Config cfg = _config();
        IReadOnlyList<string> exclusions = cfg.SearchExclusions;
        IReadOnlyList<string> roots = _repoRoots;
        int touched = 0;

        using SqliteTransaction tx = _db.Begin();

        foreach (ContentDb.Pending p in _db.PendingRows())
        {
            // A queued delete is kept whatever the rules now say about its path. Dropping it
            // would strand the indexed row it was going to remove, permanently.
            if (p.Reason == ContentDb.ReasonDelete) continue;
            if (Eligible(p.Path, p.Kind, exclusions, roots)) continue;
            _db.Dequeue(p.Id, tx);
            touched++;
        }

        foreach ((string vol, ulong frn, string path) in _db.ItemPaths())
        {
            ResultKind kind = FileKinds.Classify(System.IO.Path.GetFileName(path), false);
            if (Eligible(path, kind, exclusions, roots)) continue;
            _db.Enqueue(vol, frn, path, kind, ContentDb.ReasonDelete, tx);
            touched++;
        }

        tx.Commit();

        if (touched > 0)
            Log.Info("index", string.Create(CultureInfo.InvariantCulture,
                $"the indexing rules changed: {touched} queued or indexed file(s) were reconciled"));

        return touched;
    }
}
