using System;
using System.Collections.Generic;
using System.Globalization;

namespace Findra;

/// <summary>One capability's worth of re-queueing: which kinds, the stamp to write once it is
/// done, and a sentence for the log. <c>Why</c> is NOT the queue's reason - the queue's reason is
/// always <see cref="Indexer.Recheck"/>, because that is the only string the indexer's freshness
/// check will reopen an already-indexed file for.</summary>
public readonly record struct Requeue(Capability Capability, int[] Kinds, string Stamp, string Why);

/// <summary>
/// What a newly-installed capability owes the index, and the record that stops it being owed
/// twice.
///
/// <para>Spec §6: enabling a capability later re-queues ONLY the files it covers, and nothing
/// already indexed is redone.</para>
///
/// <para>The record is keyed per CAPABILITY and its value carries the model family's version.
/// Keying it on the family alone conflates two facts - which embedding space is on disk, and
/// whose backlog has been cleared - and the ordinary path breaks on it: Speech, Meaning and
/// Hebrew all embed with e5, so somebody who takes Recommended and later adds Speech finds the
/// family already stamped, gets an empty plan, and every audio file stays skipped for ever.</para>
///
/// <para>This runs in the INTERFACE, on the writer connection the queue feeder owns, ONCE at
/// startup and before the content loop begins. It is deliberately not the child's: the child
/// would have to write a fourth namespace into the meta table and would race the feeder for the
/// writer.</para>
/// </summary>
public static class CapabilityGate
{
    /// <summary>This gate's own meta prefix. `indexer:` is the child's, `index:` the content
    /// loop's, and the bare keys the queue feeder's; reusing any of them is a collision nothing
    /// would report. The key is `models:cap:photos`, `models:cap:meaning`, and so on.</summary>
    public const string StampPrefix = "models:cap:";

    /// <summary>The version of each embedding space. Bumped when the MODEL changes, so every
    /// vector already stored points somewhere that no longer exists - not when this code
    /// changes. Nothing has bumped either yet.</summary>
    public static string CurrentVersion(string family) => family switch
    {
        "siglip" => "1",
        "e5" => "1",
        _ => "0",
    };

    /// <summary>Which family's space a capability's vectors live in. Speech and Hebrew embed
    /// their transcripts with the same text model documents use, which is why they share - and
    /// why the stamp cannot be keyed on this.</summary>
    public static string Family(Capability c) => c switch
    {
        Capability.Photos => "siglip",
        _ => "e5",
    };

    /// <summary>What a cleared backlog looks like for one capability: its family and that
    /// family's current version. A bump changes the value for every capability in the family,
    /// which clears all of their backlogs and none of the other family's.</summary>
    public static string StampFor(Capability c) => Family(c) + "@" + CurrentVersion(Family(c));

    private static string Key(Capability c) => StampPrefix + c.ToString().ToLowerInvariant();

    /// <summary>What is owed, given what is installed and what has already been done. One entry
    /// per capability, in <see cref="Capabilities.All"/> order.</summary>
    public static IReadOnlyList<Requeue> Plan(CapabilitySet installed, IReadOnlyDictionary<Capability, string> stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        var owed = new List<Requeue>();
        foreach (Capability c in Capabilities.All)
        {
            if (!installed.Has(c)) continue;
            string want = StampFor(c);
            if (stamps.TryGetValue(c, out string? at) && at == want) continue;
            int[] kinds = Capabilities.KindsCovered(c);
            if (kinds.Length == 0) continue;
            owed.Add(new Requeue(c, kinds, want,
                                 $"{Capabilities.Title(c).ToLowerInvariant()} is now installed"));
        }
        return owed;
    }

    /// <summary>Queue what is owed and record that it was, so a launch that owes nothing does
    /// nothing. Returns how many files were queued in total. What "owed" means is
    /// <see cref="StampsIn"/>'s: the record, and the index agreeing with it.</summary>
    public static int Apply(ContentDb db, IReadOnlyList<Requeue> plan)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(plan);
        // Counted as a DELTA rather than as a sum of the per-capability returns. Speech and
        // Hebrew cover the same two kinds, so both re-queue the same rows and the queue's
        // UNIQUE(vol, frn) makes the second pass an upsert - summing the returns reports six
        // files where three moved. Each capability's own line below is still its own true
        // count; this is the total, and it has to be the number of rows that actually changed.
        long before = db.PendingCount();
        foreach (Requeue r in plan)
        {
            // Indexer.Recheck, and nothing else. A friendlier sentence here is dequeued untouched
            // by Indexer.cs:298-300 for every row that is already indexed, which is every
            // document already extracted and every transcript Speech wrote - the log would say twelve
            // thousand files queued and not one embedding would be written. r.Why is for the log,
            // where a sentence belongs.
            //
            // The exclusions: a new model can read a kind it had no decoder for, and a format the
            // old reader could not open. It cannot make a file smaller or put words into an empty
            // one.
            //
            // Wide the first time, narrow the second. A stamp that already stands means this is
            // not a capability arriving - it is one whose backlog was queued, drained by
            // something that could not read it, and left stranded (see StampsIn). Re-running the
            // wide re-queue there would re-read every file the capability has ALREADY been
            // through: Speech covers video as well as sound, so recovering one unheard voice memo
            // would re-embed every frame of every film on the disk. Exactly the rows that were
            // passed over for want of a model, and nothing else.
            bool stranded = db.Get(Key(r.Capability)) == r.Stamp;
            int n = stranded
                ? db.RequeueKinds(r.Kinds, Indexer.Recheck, onlyBecause: [Decoders.NoModel])
                : db.RequeueKinds(r.Kinds, Indexer.Recheck, [Decoders.TooLarge, Decoders.NoText]);
            db.Set(Key(r.Capability), r.Stamp);
            Log.Info("models", $"{r.Why}: {n.ToString("N0", CultureInfo.InvariantCulture)} file(s) queued to be read"
                               + (stranded ? " (they were queued once before and nothing could read them)" : " again"));
        }
        return (int)(db.PendingCount() - before);
    }

    /// <summary>
    /// The stamps that stand up, for <see cref="Plan"/>.
    ///
    /// <para>A stamp is written when the backlog is QUEUED, which is the only moment this code can
    /// see - and the queue is drained by an indexer child that read what was installed when it
    /// started. Install a capability while Findra is open and that child records every file just
    /// queued for it skipped again, for want of a model that is sitting on the disk. The child
    /// looking again is what stops that happening (Decoders.Refresh); this is what makes it
    /// recoverable when it happened anyway - an older build, a model deleted by hand, a machine
    /// where the download landed after the last file was drained.</para>
    ///
    /// <para>So the stamp is not taken on its own. A capability whose files are sitting there
    /// unread with no queue entry to bring them back has not had its backlog cleared, whatever
    /// the record says: done is a fact the index holds, not a note somebody left. Those rows and
    /// nothing else are what <see cref="Apply"/> then queues.</para>
    /// </summary>
    public static IReadOnlyDictionary<Capability, string> StampsIn(ContentDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var at = new Dictionary<Capability, string>();
        foreach (Capability c in Capabilities.All)
            // The stamp first, and the count only if there is one: a machine that took no
            // download has no stamps at all, and asking the index four questions about capability
            // backlogs it has never had is a scan of every item for a known answer.
            if (db.Get(Key(c)) is { } v
                && db.CountSkippedAndNotQueued(Capabilities.KindsCovered(c), Decoders.NoModel) == 0)
                at[c] = v;
        return at;
    }

    // ---- the transcription limit ----

    /// <summary>The last limit this index was reconciled against. A recorded value, not a guess:
    /// without it the re-queue below runs on every launch, and on a machine with a large archive
    /// that is a re-transcription every time Findra opens.</summary>
    public const string LimitKey = "models:limit:transcribe";

    /// <summary>
    /// What a change to the transcription limit owes the index - which is either nothing, or one
    /// very narrow re-queue.
    ///
    /// <para>Only a MORE permissive limit owes anything, and "more permissive" is not
    /// <c>now &gt; was</c>: a negative value means no limit, so -1 is the most permissive setting
    /// there is and a plain numeric comparison reads it as the least. Rank the two on a scale
    /// where off is 0, no limit is infinity, and a positive number is itself.</para>
    ///
    /// <para>Lowering it owes nothing on purpose. Deleting transcripts somebody already paid for
    /// because they moved a slider down is worse than keeping them; the new limit applies to
    /// what has not been read yet.</para>
    /// </summary>
    public static Requeue? PlanForLimit(int wasMinutes, int nowMinutes)
    {
        static double Rank(int m) => m < 0 ? double.PositiveInfinity : m;
        if (Rank(nowMinutes) <= Rank(wasMinutes)) return null;
        return new Requeue(Capability.Speech,
                           [(int)ResultKind.Audio, (int)ResultKind.Video],
                           Stamp: nowMinutes.ToString(CultureInfo.InvariantCulture),
                           Why: $"the transcription limit is now {TranscribeLimit.Describe(nowMinutes)}");
    }

    /// <summary>
    /// Reconcile the index against the current limit, and record it. Returns how many recordings
    /// were queued.
    ///
    /// <para>Telling the indexer the limit is part of reconciling and not a separate errand. The
    /// child reads <see cref="Indexer.TranscribeMinutesKey"/> before each recording it opens, so
    /// a limit raised from a terminal while Findra is running - <c>--content limit</c>, whose
    /// settings file the running interface will not read again - reaches exactly the recordings
    /// queued below. Without it the child passes over every one of them at the old length,
    /// records them too long a second time, and the value written here then says there is nothing
    /// left to hear, for good.</para>
    /// </summary>
    public static int ApplyLimit(ContentDb db, int nowMinutes)
    {
        ArgumentNullException.ThrowIfNull(db);
        int was = int.TryParse(db.Get(LimitKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                  ? w : TranscribeLimit.Default;
        Requeue? owed = PlanForLimit(was, nowMinutes);
        string minutes = nowMinutes.ToString(CultureInfo.InvariantCulture);
        // Before the re-queue, not after: the child may take the first row off the queue while
        // this is still running, and it must not meet it holding the old length.
        db.Set(Indexer.TranscribeMinutesKey, minutes);
        db.Set(LimitKey, minutes);
        if (owed is null) return 0;

        // onlyBecause, not notBecause: EXACTLY the recordings that were passed over for being
        // longer than the old limit, and nothing else. It reads the recorded reason rather than
        // the state, so it also reaches a long video that was indexed for its frames alone and
        // carries TooLong as a note about the sound track nobody heard.
        int n = db.RequeueKinds(owed.Value.Kinds, Indexer.Recheck, onlyBecause: [Decoders.TooLong]);
        Log.Info("models", $"{owed.Value.Why}: {n.ToString("N0", CultureInfo.InvariantCulture)} recording(s) queued to be heard");
        return n;
    }
}
