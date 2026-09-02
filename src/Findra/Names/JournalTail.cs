using System.Globalization;
using System.Runtime.Versioning;
using Findra.Pipe;

namespace Findra;

/// <summary>
/// One volume as a session needs to see it. <see cref="NameIndex"/> knows its letter, its count
/// and its buffer size and nothing whatever about a journal, so the id and the position a
/// subscriber resumes from - and the cold-start enumeration time <c>--searchbench</c> publishes -
/// have no route into a session without this. The helper builds one per volume as it enumerates:
/// it already holds the <see cref="NtfsVolume"/>, and it already measures the walk.
/// </summary>
public sealed record VolumeView(NameIndex Index, ulong JournalId, long NextUsn, double EnumerateMs);

/// <summary>
/// One reader/writer lock PER VOLUME around the name indexes. Every session reads them
/// concurrently and the journal tail now writes them - the exact situation the pipe's own header
/// warned about while the index was still immutable.
///
/// The strongest reason for this lock is not a crash, it is a WRONG ANSWER. LongIntMap.Rehash
/// replaces its key and value arrays as two separate field writes, so an unlocked TryGet can pair
/// a new key array with an old value array and hand back the wrong record index - which is the
/// wrong path, on a real result row, that a person then clicks. Grow, Place and Trim have the same
/// shape via Array.Resize. Shared reads, exclusive writes.
///
/// Per volume, not global: the indexes are already a per-volume dictionary, and a journal batch on
/// D: has no business stalling a query that only touches C:.
/// </summary>
public sealed class IndexLock : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<char, ReaderWriterLockSlim> _byVolume = new();

    public IDisposable Read(char volume) => new Scope(For(volume), write: false);
    public IDisposable Write(char volume) => new Scope(For(volume), write: true);

    private ReaderWriterLockSlim For(char v)
        => _byVolume.GetOrAdd(char.ToUpperInvariant(v),
                              static _ => new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion));

    public void Dispose()
    {
        foreach (ReaderWriterLockSlim l in _byVolume.Values) l.Dispose();
        _byVolume.Clear();
    }

    private sealed class Scope : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;
        private readonly bool _write;
        private bool _released;

        public Scope(ReaderWriterLockSlim l, bool write)
        {
            _lock = l;
            _write = write;
            if (write) l.EnterWriteLock(); else l.EnterReadLock();
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (_write) _lock.ExitWriteLock(); else _lock.ExitReadLock();
        }
    }
}

/// <summary>
/// The helper's journal tail: read each volume's USN journal from where it left off, apply the
/// changes to that volume's in-RAM name index, and publish what it applied so subscribed sessions
/// can forward it. It decides nothing about eligibility - that is the UI's job at normal
/// integrity - and it opens no file.
/// </summary>
public static class JournalTail
{
    /// <summary>
    /// How many journal records are applied under one hold of a volume's write lock.
    ///
    /// <see cref="NtfsVolume.Read"/> drains the WHOLE journal in one call - its inner loop runs
    /// until the cursor stops moving. After a laptop resume or a long UI absence that single
    /// result can be hundreds of thousands of records, and holding the write lock across all of
    /// them stalls every name query on that drive for the duration. A journal batch is only
    /// "short" if you make it short.
    ///
    /// <see cref="NameServer.MaxOutbound"/> must be at least this, or one ordinary catch-up slice
    /// overflows a healthy client's outbound queue. <c>--searchtest</c> checks that.
    /// </summary>
    public const int MaxApplyBatch = 20_000;

    /// <summary>How long the tail sleeps between passes over every volume.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Where each volume resumes, given every volume's current journal id and position and the
    /// caller's stored cursors. Pure: no index, no handle, no disk - which is what makes the
    /// resume rules testable without elevation.
    ///
    /// Absent cursor, or one from a different journal, means a full pass is owed and the resume
    /// point is the volume's CURRENT position. A cursor that merely lags is not a full pass: it is
    /// replayed from its own position. Every answer carries the volume's current journal id, never
    /// the caller's stale one - what the feeder stores has to be what the next launch compares
    /// against.
    /// </summary>
    public static IReadOnlyList<VolumeResume> ResumeFrom(IReadOnlyDictionary<char, ulong> journalIds,
                                                         IReadOnlyDictionary<char, long> current,
                                                         IReadOnlyList<VolumeCursor> from)
    {
        // Built by hand rather than with ToDictionary: a caller that sends two cursors for one
        // volume would throw out of the elevated process, and the last one is a fine answer.
        var stored = new Dictionary<char, VolumeCursor>();
        foreach (VolumeCursor c in from) stored[char.ToUpperInvariant(c.Volume)] = c;

        var answers = new List<VolumeResume>(journalIds.Count);
        foreach ((char raw, ulong journalId) in journalIds)
        {
            char letter = char.ToUpperInvariant(raw);
            long now = current.TryGetValue(raw, out long n) ? n
                     : current.TryGetValue(letter, out long m) ? m : 0;

            if (!stored.TryGetValue(letter, out VolumeCursor? cursor))
            {
                answers.Add(new VolumeResume(letter, journalId, now, NeedsFullPass: true, Replayed: 0,
                    "no stored position for this volume - a full pass is owed"));
            }
            else if (cursor.JournalId != journalId)
            {
                answers.Add(new VolumeResume(letter, journalId, now, NeedsFullPass: true, Replayed: 0,
                    string.Create(CultureInfo.InvariantCulture,
                        $"the stored position came from journal {cursor.JournalId:x} and this volume " +
                        $"now runs {journalId:x} - a full pass is owed")));
            }
            else
            {
                answers.Add(new VolumeResume(letter, journalId, cursor.Usn, NeedsFullPass: false, Replayed: 0,
                    string.Create(CultureInfo.InvariantCulture,
                        $"resuming from usn {cursor.Usn} in the journal it was recorded against")));
            }
        }

        answers.Sort(static (a, b) => a.Volume.CompareTo(b.Volume));
        return answers;
    }

    /// <summary>
    /// Apply one journal record to a name index. Returns how many records it changed, so the tail
    /// can log a rate. The caller holds that volume's write lock.
    /// </summary>
    public static int Apply(NameIndex ix, NtfsVolume.Change c)
    {
        if ((c.Reason & NtfsVolume.ReasonFileDelete) != 0)
            return ix.Remove(c.Frn) ? 1 : 0;
        return ix.Upsert(c.Frn, c.ParentFrn, c.Attributes, c.Name) ? 1 : 0;
    }

    /// <summary>
    /// The one event that means "your position is worthless": the journal wrapped past it, or was
    /// recreated. Reason, name and path are all empty, and it carries the volume's real journal id
    /// like every other event. Nothing keys off that id today, because the feeder clears the
    /// position before it reads one - but a zero that works only by accident is not worth keeping,
    /// and one constructor means it cannot drift.
    /// </summary>
    public static JournalEvent ResetMarker(char volume, ulong journalId)
        => new(char.ToUpperInvariant(volume), journalId, Frn: 0, Parent: 0, Attributes: 0,
               Name: "", Path: "", Reason: 0, Usn: 0);

    /// <summary>
    /// The loop. One pass per second over every volume: read what the journal has, apply it in
    /// slices, publish each slice.
    ///
    /// Every exception is caught per volume. A failing journal on one drive must never stop the
    /// other drives or take the helper down - names on whatever can be read is the floor.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task RunAsync(IReadOnlyList<(NtfsVolume Volume, VolumeView View)> volumes,
                                      IndexLock gate, JournalBroadcast bus, CancellationToken ct)
    {
        var changes = new List<NtfsVolume.Change>();

        while (!ct.IsCancellationRequested)
        {
            foreach ((NtfsVolume vol, VolumeView view) in volumes)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    changes.Clear();
                    if (!vol.Read(vol.NextUsn, changes))
                    {
                        // Wrapped past the cursor, or recreated. Nothing that follows can be
                        // trusted against the position the subscriber holds, so say so once and
                        // let the UI owe itself a fresh walk.
                        Log.Warn("journal", $"{vol.Letter}: the journal no longer reaches this " +
                                            "position - subscribers are told to re-walk");
                        bus.Publish(ResetMarker(vol.Letter, view.JournalId));
                        continue;
                    }
                    if (changes.Count == 0) continue;

                    int applied = 0;
                    for (int off = 0; off < changes.Count; off += MaxApplyBatch)
                    {
                        int n = Math.Min(MaxApplyBatch, changes.Count - off);
                        var slice = new List<JournalEvent>(n);

                        using (gate.Write(vol.Letter))
                        {
                            for (int k = off; k < off + n; k++)
                            {
                                NtfsVolume.Change c = changes[k];
                                applied += Apply(view.Index, c);

                                // Resolve the path in the SAME locked section as the apply, so a
                                // create's path reflects the record that was just added. A delete
                                // resolves to "" - the record is gone, and the feeder keys deletes
                                // on (volume, frn) and never needs one.
                                string path = (c.Reason & NtfsVolume.ReasonFileDelete) != 0 ? ""
                                    : view.Index.TryIndexOf(c.Frn, out int rec)
                                        ? view.Index.PathOf(rec) ?? ""
                                        : "";

                                slice.Add(new JournalEvent(vol.Letter, view.JournalId, c.Frn,
                                    c.ParentFrn, c.Attributes, c.Name, path, c.Reason, c.Usn));
                            }
                        }

                        // The write lock is RELEASED before the publish, and that ordering is
                        // load-bearing rather than tidy. A subscriber registering takes the
                        // broadcast's lock and then, inside its backlog read, this volume's READ
                        // lock. Publishing from inside the write lock would make this path take
                        // them in the opposite order - volume write lock, then broadcast lock -
                        // which is a textbook lock-order inversion: the tail waits for the
                        // broadcast lock the subscriber holds while the subscriber waits for the
                        // volume lock the tail holds, and the elevated helper hangs with no name
                        // search on that drive and nothing in the log to say why.
                        foreach (JournalEvent e in slice) bus.Publish(e);
                    }

                    Log.Info("journal", string.Create(CultureInfo.InvariantCulture,
                        $"{vol.Letter}: {changes.Count} journal records, {applied} applied, " +
                        $"now at usn {vol.NextUsn}"));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Log.Once($"journal-tail-{vol.Letter}", "WARN ", "journal",
                        $"{vol.Letter}: the journal tail failed and that drive will stop tracking " +
                        $"changes; the others carry on :: {ex.GetType().Name}: {ex.Message}");
                }
            }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
