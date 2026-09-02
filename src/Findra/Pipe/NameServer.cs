using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;

namespace Findra.Pipe;

/// <summary>
/// The elevated half. It owns the volume handles and the in-RAM name index, and it
/// parses nothing but query text - never file content.
/// </summary>
public static class NameServer
{
    public const string PipeName = "findra-names";
    private const int MaxRows = 4000;

    /// <summary>
    /// How many journal events one session's outbound queue holds before the oldest are evicted.
    ///
    /// This is not a tuning knob, it is a correctness bound: it must be at least
    /// <see cref="JournalTail.MaxApplyBatch"/>. The tail publishes a slice of up to that many
    /// events, Publish fills this channel synchronously because a DropOldest TryWrite never
    /// blocks, and the drain task writes one frame at a time over a named pipe. Any smaller and a
    /// single ordinary catch-up slice drops events on a PERFECTLY HEALTHY client, which turns the
    /// drop path from a pathological case into the normal one. <c>--searchtest</c> asserts the
    /// relationship so a later edit to either constant cannot quietly break it.
    ///
    /// Public because the back-pressure tests read it from the test assembly to publish past it.
    /// </summary>
    public const int MaxOutbound = 32_768;

    /// <summary>
    /// How a session catches a subscriber up from its stored position. The real one wraps
    /// <see cref="NtfsVolume.Read"/> on a private handle; a test supplies its own, which is the
    /// only reason the gap replay is testable without a disk and without elevation.
    /// A <c>Reachable</c> of false means the journal has wrapped past that position: the caller
    /// owes itself a full pass, and no partial replay is honest.
    /// </summary>
    public delegate (bool Reachable, IReadOnlyList<JournalEvent> Events) GapReader(
        char volume, ulong journalId, long fromUsn);

    /// <summary>Longest query text accepted off the wire. See AnswerQuery for why it is capped.</summary>
    private const int MaxRaw = 4096;

    /// <summary>
    /// How many pipe instances exist at once: one listening plus up to three being served.
    /// More than one is not a nicety - with a single instance `--searchprobe` cannot reach a
    /// healthy helper while the UI holds it, so the product's primary diagnostic reports its
    /// worst failure mode against a working system.
    /// </summary>
    private const int MaxPipeInstances = 4;

    /// <summary>Consecutive failed pipe creations before the helper gives up for good.</summary>
    private const int MaxCreateFailures = 5;

    /// <summary>Largest enumerate batch the helper will honour, whatever the frame asked for.</summary>
    private const int MaxEnumerateBatch = 2000;

    /// <summary>
    /// How many suffixes one enumerate request may name, and how long each may be. Never trust
    /// the frame: the suffix list is a per-record inner loop the CALLER controls inside the
    /// elevated process, so an unbounded list turns one small frame into hours of comparisons on
    /// a 1.5M-record volume.
    ///
    /// <para>128 rather than a tighter number because the bound is paid against a real list that
    /// grows. Every content extension this build knows about travels in one of these frames, and
    /// a clamp the shipped list can reach is a clamp that silently deletes the extensions sorting
    /// last from every first pass on every machine. The cost of the number itself is one ordinal
    /// EndsWith per suffix per record, each rejected on its final character: 128 of them over
    /// 1.5M records is about a second inside the elevated process, once per volume per walk, and
    /// is bounded whatever the frame asks for. <c>NameClient.EnumerateAsync</c> splits a longer
    /// list across requests rather than letting this trim it, and
    /// <c>EnumerateTests.TheContentSuffixListFitsInOneEnumerateRequest</c> is what says when this
    /// number has to move again.</para>
    /// </summary>
    public const int MaxSuffixes = 128;
    private const int MaxSuffixLength = 16;

    /// <summary>
    /// How many times a walk restarts because the journal moved under it before it gives up on
    /// batching and takes the volume's read lock for the whole pass.
    ///
    /// A restart is the cost of not stalling every query behind a multi-second walk, and it is
    /// normally paid at most once. On a volume under constant churn - a build server, a sync
    /// client mid-download - the epoch could in principle move during every batch, and a walk
    /// that restarts forever never terminates. After this many attempts the walk takes the whole
    /// hold: queries on that one volume wait for it, which is bad, and an enumeration that never
    /// finishes is worse, because the first pass never completes and the debt is never cleared.
    /// </summary>
    private const int MaxEnumerateRestarts = 3;

    /// <summary>
    /// The shape Plan 1 shipped, kept so its tests and any caller with nothing but indexes
    /// still compile and still mean what they meant. Each index is wrapped in a zeroed view: no
    /// journal, no push channel, no gap replay - exactly the behaviour those callers had.
    /// </summary>
    public static Task Serve(Stream transport, IReadOnlyDictionary<char, NameIndex> indexes,
                             CancellationToken ct)
        => Serve(transport,
                 indexes.ToDictionary(kv => kv.Key,
                                      kv => new VolumeView(kv.Value, JournalId: 0, NextUsn: 0, EnumerateMs: 0)),
                 gate: null, bus: null, gap: null, ct);

    /// <summary>
    /// One client's session: read frames, answer queries and status, push journal events once it
    /// has subscribed, until the stream ends or cancellation fires. The transport is assumed to
    /// arrive already protected - <see cref="RunAsync"/> owns the pipe's DACL, and any future
    /// caller that hands <c>Serve</c> a transport owns that guarantee too.
    ///
    /// TWO WRITERS NOW SHARE ONE TRANSPORT: the reply path and the push path. Every write goes
    /// through one per-session semaphore, because two writers interleaving their bytes into one
    /// stream leave a half-written frame, and the framing never recovers - every later read is
    /// garbage, with nothing in the log to say when it started.
    /// </summary>
    public static async Task Serve(Stream transport, IReadOnlyDictionary<char, VolumeView> views,
                                   IndexLock? gate, JournalBroadcast? bus, GapReader? gap,
                                   CancellationToken ct)
    {
        var hits = new List<NameIndex.Hit>();

        // Every write to the transport - a query reply, a status reply, the subscribe ack and
        // every pushed event - is serialised on this.
        var writeLock = new SemaphoreSlim(1, 1);

        // Per volume, because the status reply is per volume. A drop is charged to the volume
        // whose event was evicted, so "which drive lost events" is answerable.
        var dropped = new ConcurrentDictionary<char, long>();
        var resetOwed = new ConcurrentDictionary<char, byte>();

        Channel<JournalEvent>? outbound = null;
        IDisposable? registration = null;
        Task? drain = null;

        using var sessionEnd = CancellationTokenSource.CreateLinkedTokenSource(ct);

        long NextUsnOf(char volume)
        {
            foreach ((char letter, VolumeView view) in views)
                if (char.ToUpperInvariant(letter) == char.ToUpperInvariant(volume)) return view.NextUsn;
            return 0;
        }

        ulong JournalIdOf(char volume)
        {
            foreach ((char letter, VolumeView view) in views)
                if (char.ToUpperInvariant(letter) == char.ToUpperInvariant(volume)) return view.JournalId;
            return 0;
        }

        // Assumes the write lock is already held. Used by the subscribe handler, which writes its
        // ack under the same hold that registered the sink.
        async Task WriteFrameAsync<T>(string kind, T body, CancellationToken wct)
            => await Frame.WriteAsync(transport, Envelope.Pack(kind, body), wct).ConfigureAwait(false);

        async Task SendAsync<T>(string kind, T body, CancellationToken sct)
        {
            await writeLock.WaitAsync(sct).ConfigureAwait(false);
            try { await WriteFrameAsync(kind, body, sct).ConfigureAwait(false); }
            finally { writeLock.Release(); }
        }

        async Task DrainAsync(ChannelReader<JournalEvent> reader, CancellationToken dct)
        {
            try
            {
                await foreach (JournalEvent e in reader.ReadAllAsync(dct).ConfigureAwait(false))
                {
                    // A drop is a hole in the range the subscriber's cursor is about to claim, so
                    // it must be impossible to lose. Markers are coalesced per volume per drain
                    // pass; they are never suppressed across the session, because "once per
                    // session" makes every drop after the first silent and lets later events
                    // advance the cursor over holes nobody recorded.
                    foreach (char letter in resetOwed.Keys)
                        if (resetOwed.TryRemove(letter, out _))
                            await SendAsync(Envelope.KindJournal,
                                JournalTail.ResetMarker(letter, JournalIdOf(letter)), dct).ConfigureAwait(false);

                    await SendAsync(Envelope.KindJournal, e, dct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("pipe", "this session's journal drain ended: " + ex.Message); }
        }

        IReadOnlyList<JournalEvent> Backlog(List<VolumeResume> answers)
        {
            var all = new List<JournalEvent>();
            for (int i = 0; i < answers.Count; i++)
            {
                VolumeResume r = answers[i];
                if (r.NeedsFullPass) continue;

                bool reachable = false;
                IReadOnlyList<JournalEvent> events = [];
                if (gap is not null) (reachable, events) = gap(r.Volume, r.JournalId, r.Usn);

                if (!reachable)
                {
                    answers[i] = r with
                    {
                        NeedsFullPass = true,
                        Usn = NextUsnOf(r.Volume),
                        Replayed = 0,
                        Note = "the journal no longer reaches that position - a full pass is owed",
                    };
                    continue;
                }

                answers[i] = r with { Replayed = events.Count };
                all.AddRange(events);       // already in journal order, per volume
            }
            return all;
        }

        async Task AnswerSubscribeAsync(SubscribeRequest req, CancellationToken sct)
        {
            // EVERYTHING below happens under one hold of the write lock: the resume rules, the
            // registration with its backlog, and the ack. The client therefore cannot see a
            // journal frame before the ack whatever the order inside - the sink never touches
            // the transport, and the drain has to take the same semaphore this is holding.
            await writeLock.WaitAsync(sct).ConfigureAwait(false);
            try
            {
                var answers = JournalTail.ResumeFrom(
                    views.ToDictionary(kv => kv.Key, kv => kv.Value.JournalId),
                    views.ToDictionary(kv => kv.Key, kv => kv.Value.NextUsn),
                    req.From ?? []).ToList();

                if (bus is null)
                {
                    // Nothing to register with. Say so rather than leaving the caller waiting for
                    // a reply that never comes, and owe every volume a full pass, because this
                    // session will never tell it about a change.
                    for (int i = 0; i < answers.Count; i++)
                        answers[i] = answers[i] with
                        {
                            NeedsFullPass = true,
                            Usn = NextUsnOf(answers[i].Volume),
                            Replayed = 0,
                            Note = "this helper session pushes no journal events",
                        };
                    await WriteFrameAsync(Envelope.KindSubscribeReply, new SubscribeReply(answers), sct)
                        .ConfigureAwait(false);
                    return;
                }

                outbound ??= Channel.CreateBounded<JournalEvent>(
                    new BoundedChannelOptions(MaxOutbound)
                    {
                        // A client that stops reading must cost ITS OWN events, never the journal
                        // tail: a tail parked on one stuck socket lets the journal wrap, and then
                        // EVERY subscriber has lost data.
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = true,        // Publish holds the broadcast's lock
                    },
                    // DropOldest evicts SILENTLY and TryWrite still returns true, so this callback
                    // is the only place in the runtime where an eviction can be observed at all.
                    // Written as `if (!writer.TryWrite(e)) dropped++` the count stays at zero
                    // forever and the gap becomes the one nothing records.
                    evicted =>
                    {
                        dropped.AddOrUpdate(evicted.Volume, 1, static (_, n) => n + 1);
                        resetOwed[evicted.Volume] = 0;
                        Log.Once("pipe-outbound-drop", "WARN ", "pipe",
                            $"a session stopped reading and its outbound journal queue is evicting " +
                            $"events (bound {MaxOutbound.ToString(CultureInfo.InvariantCulture)}); " +
                            "it is being sent a reset marker and owes a fresh walk");
                    });

                Channel<JournalEvent> queue = outbound;
                drain ??= Task.Run(() => DrainAsync(queue.Reader, sessionEnd.Token), CancellationToken.None);

                // A second subscribe on one session replaces the first rather than doubling it.
                registration?.Dispose();

                // The gap read happens INSIDE the registration, never before it. Two separate
                // steps are wrong in both possible orders: register second and an event published
                // in between is lost; register first and enqueue the gap afterwards and live
                // events go out ahead of older replayed ones, which makes the feeder delete files
                // that exist. See JournalBroadcast.SubscribeWithBacklog.
                registration = bus.SubscribeWithBacklog(
                    backlog: () => Backlog(answers),
                    sink: e => queue.Writer.TryWrite(e));

                await WriteFrameAsync(Envelope.KindSubscribeReply, new SubscribeReply(answers), sct)
                    .ConfigureAwait(false);
            }
            finally { writeLock.Release(); }
        }

        // The first pass. The caller names the suffixes and the helper applies them mechanically:
        // it holds no table of what a document is, and every rule that decides whether a file is
        // worth opening runs at normal integrity on the rows this sends back.
        async Task AnswerEnumerateAsync(EnumerateRequest req, CancellationToken ect)
        {
            char letter = char.ToUpperInvariant(req.Volume);
            int batch = Math.Clamp(req.BatchSize, 1, MaxEnumerateBatch);

            var suffixes = new List<string>(MaxSuffixes);
            int unheard = 0;
            foreach (string s in req.Suffixes ?? [])
            {
                if (string.IsNullOrEmpty(s) || s.Length > MaxSuffixLength) continue;
                if (suffixes.Count == MaxSuffixes) { unheard++; continue; }
                suffixes.Add(s);
            }

            // Said out loud, every time. Trimming the tail of a caller's suffix list makes the
            // answer LOOK complete - the stream still ends in a Done frame - while the files whose
            // extensions sorted last are simply absent, and nothing downstream can tell that from
            // a disk that does not have them. The client splits an over-long list rather than
            // letting this happen, so a machine that logs this line has a caller that did not.
            if (unheard > 0)
                Log.Warn("names", string.Create(CultureInfo.InvariantCulture,
                    $"{letter}: an enumerate request named {suffixes.Count + unheard} suffixes and only " +
                    $"{MaxSuffixes} are honoured; {unheard} were not compared and files with those " +
                    $"extensions are missing from this answer"));

            NameIndex? ix = null;
            foreach ((char l, VolumeView v) in views)
                if (char.ToUpperInvariant(l) == letter) { ix = v.Index; break; }

            // A drive the helper does not hold, or a request that named nothing usable, still gets
            // an answer. A session that just stops replying looks exactly like a slow disk.
            if (ix is null || suffixes.Count == 0)
            {
                await SendAsync(Envelope.KindEnumerateReply,
                    new EnumerateReply(req.Id, letter, [], true), ect).ConfigureAwait(false);
                return;
            }

            for (int attempt = 0; ; attempt++)
            {
                // The last attempt gives up on batching and holds the read lock for the whole
                // walk, so the enumeration always terminates. See MaxEnumerateRestarts.
                bool holdThroughout = attempt >= MaxEnumerateRestarts;
                long epoch = gate?.Epoch(letter) ?? 0;
                bool moved = false;
                int record = 0;
                var buf = new List<EnumeratedFile>(batch);

                using (IDisposable? whole = holdThroughout ? gate?.Read(letter) : null)
                {
                    int capacity = int.MaxValue;
                    while (record < capacity)
                    {
                        buf.Clear();

                        // ONE BATCH PER HOLD. Walking 1.5M records under a single read lock is
                        // seconds of PathOf calls, and ReaderWriterLockSlim gives a waiting writer
                        // priority over new readers - so one journal batch queued behind the walk
                        // blocks every AnswerQuery after it and the card is dead for the length of
                        // the enumeration. That is the first pass, on every fresh install, which
                        // is exactly when somebody is watching.
                        using (holdThroughout ? null : gate?.Read(letter))
                        {
                            capacity = ix.Capacity;
                            while (record < capacity && buf.Count < batch)
                            {
                                int r = record++;
                                if (!ix.IsAlive(r) || ix.IsDirectory(r)) continue;
                                string name = ix.Name(r);
                                if (!EndsWithAny(name, suffixes)) continue;
                                string? path = ix.PathOf(r);
                                if (path is null) continue;
                                buf.Add(new EnumeratedFile(ix.Frn(r), path, LastWriteTicks(path)));
                            }
                        }

                        // Checked BEFORE the frame goes out, so nothing read across a rehash is
                        // ever sent. A restarted walk costs a second; a skipped or duplicated
                        // record costs a wrong index, and FillFrom's diff makes the duplicates a
                        // restart produces free.
                        if (!holdThroughout && gate is not null && gate.Epoch(letter) != epoch) { moved = true; break; }

                        if (buf.Count > 0)
                            await SendAsync(Envelope.KindEnumerateReply,
                                new EnumerateReply(req.Id, letter, buf.ToArray(), false), ect).ConfigureAwait(false);
                    }
                }

                if (moved)
                {
                    Log.Info("pipe", string.Create(CultureInfo.InvariantCulture,
                        $"{letter}: the journal moved during an enumeration; restarting it " +
                        $"(attempt {attempt + 2} of {MaxEnumerateRestarts + 1})"));
                    continue;
                }

                await SendAsync(Envelope.KindEnumerateReply,
                    new EnumerateReply(req.Id, letter, [], true), ect).ConfigureAwait(false);
                return;
            }
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? payload = await Frame.ReadAsync(transport, ct).ConfigureAwait(false);
                if (payload is null) return;

                Envelope e;
                try { e = Envelope.Unpack(payload); }
                catch (Exception ex) { Log.Warn("pipe", "undecodable frame: " + ex.Message); continue; }

                try
                {
                    switch (e.Kind)
                    {
                        case Envelope.KindQuery:
                            await SendAsync(Envelope.KindQueryReply,
                                AnswerQuery(e.Body<QueryRequest>(), views, gate, hits), ct).ConfigureAwait(false);
                            break;
                        case Envelope.KindStatus:
                            await SendAsync(Envelope.KindStatusReply,
                                AnswerStatus(views, gate, dropped), ct).ConfigureAwait(false);
                            break;
                        case Envelope.KindSubscribe:
                            await AnswerSubscribeAsync(e.Body<SubscribeRequest>(), ct).ConfigureAwait(false);
                            break;
                        case Envelope.KindEnumerate:
                            await AnswerEnumerateAsync(e.Body<EnumerateRequest>(), ct).ConfigureAwait(false);
                            break;
                        default:
                            Log.Info("pipe", $"ignoring unknown kind '{e.Kind}'");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // Not just JsonException. Body<T> throws InvalidDataException for a body
                    // that decodes to null - the literal `null` in the Json field - and a
                    // JsonException-only guard lets that one shape through to end the session.
                    // Match the Unpack guard above: nothing a peer can put in a frame may
                    // decide whether this loop keeps running.
                    Log.Warn("pipe", $"undecodable body for '{e.Kind}': {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("pipe", "serve loop ended", ex); }
        finally
        {
            // A session that ends stops receiving. Unregister first, so the tail stops handing
            // this queue events, then let the drain run itself out.
            registration?.Dispose();
            outbound?.Writer.TryComplete();
            await sessionEnd.CancelAsync().ConfigureAwait(false);
            if (drain is not null) { try { await drain.ConfigureAwait(false); } catch { } }

            // writeLock is deliberately not disposed, for the reason NameClient's own semaphore
            // is not: SemaphoreSlim.Dispose neither resumes nor faults a queued async waiter, so
            // disposing it under one hangs that caller silently and throws out of its finally.
        }
    }

    /// <summary>The whole of the helper's opinion about which files matter: none. It compares a
    /// name against strings that arrived in a frame.</summary>
    private static bool EndsWithAny(string name, List<string> suffixes)
    {
        foreach (string s in suffixes)
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Windows answers a missing or unreadable file with the FILETIME epoch rather than
    /// an error, and that is not a modification time - it is a large, constant, plausible-looking
    /// number that would compare equal on every walk forever.</summary>
    private static readonly long FiletimeEpochTicks = DateTime.FromFileTimeUtc(0).Ticks;

    /// <summary>
    /// The file's last-write time in UTC ticks, or 0 when it cannot be read.
    ///
    /// THIS IS A METADATA CALL AND IT MUST STAY ONE. <c>File.GetLastWriteTimeUtc</c> is
    /// <c>GetFileAttributesEx</c>: no file is opened, no byte of one is read, and no decoder of any
    /// kind sees this path. That is the whole reason it is allowed here. The rule this process
    /// lives under is that the elevated half never parses untrusted file CONTENT (spec §3, and
    /// CLAUDE.md), because decoders over arbitrary files are the most likely thing on this machine
    /// to be exploitable by a malformed input - and a timestamp is not content. Nothing in this
    /// method may grow into opening the file.
    ///
    /// It is the same clock the indexer stamps into <c>items.mtime</c>
    /// (<c>FileInfo.LastWriteTimeUtc.Ticks</c>), so the first pass can compare the two directly.
    /// Without it the pass sees only whether an FRN is new, is blind to a file that was modified
    /// while Findra was closed, and clears the walk debt anyway - see <see cref="EnumeratedFile"/>.
    /// </summary>
    private static long LastWriteTicks(string path)
    {
        try
        {
            long ticks = File.GetLastWriteTimeUtc(path).Ticks;
            return ticks == FiletimeEpochTicks ? 0 : ticks;
        }
        catch (ArgumentException) { return 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (NotSupportedException) { return 0; }
    }

    private static QueryReply AnswerQuery(QueryRequest req, IReadOnlyDictionary<char, VolumeView> views,
                                          IndexLock? gate, List<NameIndex.Hit> hits)
    {
        long started = Stopwatch.GetTimestamp();

        // Never trust the frame, and Max is not the only field that arrives from off-process.
        // Raw has no natural bound, and a `regex:` prefix hands it straight to the Regex
        // constructor INSIDE THE ELEVATED PROCESS - the one place a pathological pattern is
        // worth the most. No query a person types comes near this cap, so refuse rather than
        // truncate: a silently shortened query answers something the caller did not ask.
        if (req.Raw.Length > MaxRaw)
        {
            Log.Warn("pipe", $"query of {req.Raw.Length} chars refused (cap {MaxRaw})");
            return new QueryReply(req.Gen, Stopwatch.GetTimestamp() - started, []);
        }

        var q = new SearchQuery(req.Raw);

        // Never trust the frame. An unclamped Max lets one query collect every record on a
        // 1.5M-name volume and materialise a path for each, which is memory amplification
        // against the elevated process; a negative one throws out of List's constructor and
        // drops the connection.
        int max = Math.Clamp(req.Max, 1, MaxRows);

        // Search stops scanning once it has `max` CANDIDATES, and Allows then discards some
        // of them - so capping the scan at the row count answers `sunset ext:png` with
        // nothing while the .png files sit further down the volume. Over-fetch when the
        // query filters. The index's own filters-only branch defends against exactly this;
        // the word-scan path reaches it through here instead.
        int scan = q.HasFilters ? Math.Min(max * 20, MaxRows) : max;

        var candidates = new List<NameRow>(Math.Min(max, 512));

        foreach ((char letter, VolumeView view) in views)
        {
            NameIndex ix = view.Index;

            // The whole scan for one volume goes inside that volume's read lock. The journal tail
            // writes this index now, and an unlocked read can pair a rehashed key array with the
            // old value array and hand back the WRONG record - a real row, with someone else's
            // path on it, that a person then clicks.
            using IDisposable? held = gate?.Read(letter);

            hits.Clear();
            ix.Search(q, hits, scan);
            foreach (NameIndex.Hit h in hits)
            {
                string? path = ix.PathOf(h.Record);
                if (path is null) continue;

                // Search is a coarse candidate generator, not the whole query. Its
                // vectorised word-scan branch never consults q.Exts, q.Kinds, q.Under or
                // q.NotUnder - those are enforced here, by Allows. Skipping this call
                // makes `sunset ext:png` return every sunset on the disk.
                string name = ix.Name(h.Record);
                bool dir = ix.IsDirectory(h.Record);
                if (!q.Allows(name, path, FileKinds.Classify(name, dir))) continue;

                candidates.Add(new NameRow(letter, ix.Frn(h.Record), name, path,
                                           ix.Attributes(h.Record), h.Score, h.Match));
            }
        }

        // Every volume contributes, then the best `max` win. Stopping the volume walk the
        // moment the cap fills lets C: take every slot and D: never appear at all - and
        // NameIndex.Search appends in MFT order, so what survived was an arbitrary prefix of
        // one disk rather than the best matches on the machine. The sort is over the accepted
        // candidates, which the per-volume scan already bounds.
        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        List<NameRow> rows = candidates.Count > max ? candidates.GetRange(0, max) : candidates;

        return new QueryReply(req.Gen, Stopwatch.GetTimestamp() - started, rows);
    }

    private static StatusReply AnswerStatus(IReadOnlyDictionary<char, VolumeView> views, IndexLock? gate,
                                            ConcurrentDictionary<char, long> dropped)
    {
        var vols = new List<VolumeStatus>(views.Count);
        foreach ((char letter, VolumeView view) in views)
        {
            int count;
            long bytes;

            // Count and BufferBytes are two reads of an index the tail mutates; taken without the
            // lock they can straddle a rehash and disagree with each other.
            using (gate?.Read(letter))
            {
                count = view.Index.Count;
                bytes = view.Index.BufferBytes;
            }

            long lost = 0;
            foreach ((char v, long n) in dropped)
                if (char.ToUpperInvariant(v) == char.ToUpperInvariant(letter)) lost += n;

            vols.Add(new VolumeStatus(letter, count, bytes, Live: true,
                                      view.EnumerateMs, view.NextUsn, lost));
        }
        return new StatusReply(Environment.ProcessId, vols);
    }

    /// <summary>
    /// What `--names` runs: build the indexes, start the journal tail, then serve clients over
    /// the pipe.
    /// </summary>
    public static async Task RunAsync(CancellationToken ct)
    {
        var views = new Dictionary<char, VolumeView>();
        var volumes = new List<NtfsVolume>();
        var tailed = new List<(NtfsVolume Volume, VolumeView View)>();

        // A SECOND handle per volume, for catch-up reads. Never the tail's: NextUsn on that one
        // is live state the tail owns, and a gap read would move it, so the tail would skip
        // everything the replay had just consumed.
        var catchUp = new Dictionary<char, NtfsVolume>();
        var catchUpLocks = new Dictionary<char, object>();

        using var gate = new IndexLock();
        var bus = new JournalBroadcast();
        try
        {
            foreach ((char letter, _, _, bool fixedDisk) in NtfsVolume.Volumes())
            {
                if (!fixedDisk) continue;
                NtfsVolume? vol = null;
                try
                {
                    vol = new NtfsVolume(letter);

                    // Read the journal cursor BEFORE enumerating. Enumeration takes about a
                    // second per million names, and every create, rename and delete that lands
                    // inside that second is invisible to the MFT walk. The NextUsn recorded
                    // here is where journal streaming resumes, so that window gets replayed
                    // rather than lost. Nothing streams the journal yet - the cursor is simply
                    // unreconstructable afterwards, which is why it is taken now.
                    //
                    // Never fatal. QueryJournal returns false only for "this volume has no
                    // journal support"; every other failure throws - including the create
                    // path it takes when a volume has no active journal, which is the
                    // ordinary state of a secondary data drive and needs a privilege that
                    // may not be held. Letting that escape would abandon the whole volume
                    // and lose name search for that disk, trading working search for a
                    // cursor nothing consumes yet. Names must keep working on whatever can
                    // be read.
                    try
                    {
                        if (!vol.QueryJournal())
                            Log.Warn("names", $"{letter}: no USN journal - changes cannot be followed");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("names", $"{letter}: journal cursor unavailable ({ex.GetType().Name}: " +
                            $"{ex.Message}) - indexing anyway; changes cannot be followed");
                    }

                    var ix = new NameIndex(letter);
                    long started = Stopwatch.GetTimestamp();
                    foreach (NtfsVolume.Record r in vol.Enumerate())
                        ix.Upsert(r.Frn, r.ParentFrn, r.Attributes, r.Name);
                    ix.Trim();

                    // The enumeration time was measured here already and thrown away after one
                    // log line. It is what --searchbench publishes as the cold-start cost, and
                    // nothing at normal integrity can measure it, so it goes into the view.
                    double enumerateMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var view = new VolumeView(ix, vol.JournalId, vol.NextUsn, enumerateMs);
                    views[letter] = view;
                    volumes.Add(vol);
                    tailed.Add((vol, view));
                    Log.Info("names", $"{letter}: {ix.Count:N0} names in " +
                        $"{enumerateMs / 1000.0:F2}s, {ix.BufferBytes / 1048576} MB, " +
                        $"journal cursor {vol.NextUsn}");
                    vol = null;   // handed over to `volumes`, which owns it now

                    // Never fatal either. Without a catch-up handle this volume simply cannot
                    // replay a subscriber's gap, and every subscriber is told it owes a full
                    // pass - slower, and correct.
                    NtfsVolume? second = null;
                    try
                    {
                        second = new NtfsVolume(letter);
                        second.QueryJournal();          // Read needs the journal id on THIS handle
                        catchUp[letter] = second;
                        catchUpLocks[letter] = new object();
                        volumes.Add(second);
                        second = null;                  // handed over to `volumes`
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("names", $"{letter}: no catch-up handle ({ex.GetType().Name}: " +
                            $"{ex.Message}) - subscribers behind the tail will be asked to re-walk");
                    }
                    finally { second?.Dispose(); }
                }
                catch (Exception ex) { Log.Error("names", $"{letter}: enumeration failed", ex); }
                finally { vol?.Dispose(); }
            }

            if (views.Count == 0) { Log.Error("names", "no volume could be read - is this running elevated?"); return; }

            // The tail starts BEFORE the first accept, so a client that connects immediately is
            // subscribing to a journal already being read rather than to a silent bus.
            Task tail = Task.Run(() => JournalTail.RunAsync(tailed, gate, bus, ct), CancellationToken.None);

            await ListenAsync(views, gate, bus, ReadGap, ct).ConfigureAwait(false);
            try { await tail.ConfigureAwait(false); } catch (OperationCanceledException) { }

            (bool Reachable, IReadOnlyList<JournalEvent> Events) ReadGap(char volume, ulong journalId, long fromUsn)
            {
                char letter = char.ToUpperInvariant(volume);
                if (!catchUp.TryGetValue(letter, out NtfsVolume? second) ||
                    !views.TryGetValue(letter, out VolumeView? view))
                    return (false, []);

                var changes = new List<NtfsVolume.Change>();

                // One catch-up read at a time per volume: NtfsVolume reuses one buffer, so two
                // concurrent sessions replaying the same drive would read each other's bytes.
                lock (catchUpLocks[letter])
                    if (!second.Read(fromUsn, changes)) return (false, []);   // wrapped, or recreated

                var events = new List<JournalEvent>(changes.Count);
                using (gate.Read(letter))                                     // shared: this only READS
                    foreach (NtfsVolume.Change c in changes)
                    {
                        // Resolve against the index as it stands now. This does NOT apply the
                        // change - the tail owns every write, and applying here would double-apply
                        // what the tail already did. A record since deleted resolves to "", which
                        // is correct: the feeder keys deletes on (volume, frn).
                        string path = (c.Reason & NtfsVolume.ReasonFileDelete) != 0 ? ""
                            : view.Index.TryIndexOf(c.Frn, out int rec) ? view.Index.PathOf(rec) ?? ""
                            : "";

                        // Stamped from the VIEW, not from anywhere else. A replayed record with a
                        // zero id makes the feeder store (0, usn), and the whole resume story then
                        // dies on precisely the path this replay was added to fix.
                        events.Add(new JournalEvent(letter, view.JournalId, c.Frn, c.ParentFrn,
                                                    c.Attributes, c.Name, path, c.Reason, c.Usn));
                    }
                return (true, events);
            }
        }
        finally
        {
            // The volume handles stay open for the life of the helper rather than being
            // disposed once enumeration finishes: NextUsn on each is the cursor journal
            // streaming resumes from, and closing the handle throws it away.
            foreach (NtfsVolume v in volumes) v.Dispose();
        }
    }

    /// <summary>
    /// Accept clients until cancelled. Several instances exist at once and connections are
    /// accepted concurrently, so a second client - `--searchprobe`, typically - is served
    /// while the UI holds a session rather than queueing behind it.
    ///
    /// Concurrent <see cref="Serve"/> calls read ONE shared <see cref="NameIndex"/> per volume,
    /// and the journal tail now WRITES those indexes. That is what <paramref name="gate"/> is
    /// for - the serialisation this header used to say a future reader would owe.
    /// </summary>
    private static async Task ListenAsync(IReadOnlyDictionary<char, VolumeView> views, IndexLock gate,
                                          JournalBroadcast bus, GapReader gap, CancellationToken ct)
    {
        var sessions = new List<Task>();
        int live = 0;         // instances that exist right now: the listener plus every session
        int failures = 0;

        while (!ct.IsCancellationRequested)
        {
            sessions.RemoveAll(t => t.IsCompleted);
            if (sessions.Count >= MaxPipeInstances - 1)
            {
                // Every instance is spoken for. Wait for one to free up rather than asking
                // the OS for a slot it will refuse.
                await Task.WhenAny(sessions).ConfigureAwait(false);
                continue;
            }

            NamedPipeServerStream? server = null;
            try
            {
                // FirstPipeInstance is legal only on a creation that finds the name unheld,
                // and it is what asserts ownership of it - see Create. `live` is 0 exactly
                // then: at startup, and again if every instance has somehow closed. Keeping a
                // listener open at all times is the other half of that: without it the name
                // is unowned between disposing one instance and creating the next, and a
                // same-user process can take it in the gap.
                server = Create(live == 0);
                Interlocked.Increment(ref live);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                failures = 0;

                Log.Info("pipe", "client connected");
                NamedPipeServerStream connected = server;
                server = null;                      // ownership moves to the session task
                sessions.Add(Task.Run(async () =>
                {
                    try
                    {
                        using (connected)
                            await Serve(connected, views, gate, bus, gap, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref live);
                        Log.Info("pipe", "client gone");
                    }
                }, CancellationToken.None));
            }
            catch (OperationCanceledException)
            {
                if (server is not null) { Interlocked.Decrement(ref live); server.Dispose(); }
                break;
            }
            catch (Exception ex)
            {
                if (server is not null) { Interlocked.Decrement(ref live); server.Dispose(); }

                // A failed create is not necessarily a squatter. Serve exiting by exception
                // disposes only the SERVER end; the pipe object survives until the client's
                // handle closes, and FirstPipeInstance then fails the next create with
                // access-denied. Returning on the first failure kills the helper until the
                // next logon over that ordinary race, so back off and try again instead.
                failures++;
                if (failures >= MaxCreateFailures)
                {
                    // The one failure that would otherwise leave nothing behind. An unhandled
                    // exception here escapes Main, .NET terminates without running the exit
                    // hook, and a HighestAvailable scheduled task discards stderr - so the log
                    // would never be written for a process whose only diagnostic is the log.
                    Log.Error("pipe", $"cannot create pipe '{PipeName}' after {failures} " +
                                      "consecutive attempts - is the name already taken?", ex);
                    Log.Flush();
                    return;
                }
                Log.Warn("pipe", $"pipe create/accept attempt {failures} failed: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << (failures - 1))), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Let sessions still mid-frame finish rather than tearing the process down under
        // them; Serve returns on its own cancellation, so this does not wait long.
        try { await Task.WhenAll(sessions).ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// One pipe instance, owned by this user and reachable by nobody else.
    /// <paramref name="first"/> adds FirstPipeInstance, which belongs on the creation that
    /// claims the name and on no other.
    /// </summary>
    private static NamedPipeServerStream Create(bool first)
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // SetOwner is not decoration. The client connects with CurrentUserOnly, and
        // that flag compares the pipe's OWNER against the client's token owner - not
        // its user. This process is elevated, so its default token owner is
        // BUILTIN\Administrators, while the normal-integrity UI's owner is the user
        // SID. Without this line the two never match and every connect fails with
        // UnauthorizedAccessException. Nothing in the unit suite catches it, because
        // nothing in the unit suite connects a real pipe.
        security.SetOwner(me);

        // FirstPipeInstance: creating a pipe needs no privilege, so without it any
        // local process can squat this name before the helper starts and feed the UI
        // paths of its choosing - a click on a fabricated result then launches as
        // this user. With it, Create fails instead of joining someone else's pipe.
        // It is legal only on the instance that claims the name - a later instance
        // passing it would collide with the helper's own earlier one.
        PipeOptions options = PipeOptions.Asynchronous |
                              (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);

        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, MaxPipeInstances, PipeTransmissionMode.Byte,
            options, 0, 0, security);
    }
}
