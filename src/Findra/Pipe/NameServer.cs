using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Findra.Pipe;

/// <summary>
/// The elevated half. It owns the volume handles and the in-RAM name index, and it
/// parses nothing but query text - never file content.
/// </summary>
public static class NameServer
{
    public const string PipeName = "findra-names";
    private const int MaxRows = 4000;

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

    /// <summary>
    /// One client's session: read frames, answer queries and status, until the stream ends
    /// or cancellation fires. The transport is assumed to arrive already protected -
    /// <see cref="RunAsync"/> owns the pipe's DACL, and any future caller that hands
    /// <c>Serve</c> a transport owns that guarantee too.
    /// </summary>
    public static async Task Serve(Stream transport, IReadOnlyDictionary<char, NameIndex> indexes,
                                   CancellationToken ct)
    {
        var hits = new List<NameIndex.Hit>();
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
                            await AnswerQuery(transport, e.Body<QueryRequest>(), indexes, hits, ct).ConfigureAwait(false);
                            break;
                        case Envelope.KindStatus:
                            await AnswerStatus(transport, indexes, ct).ConfigureAwait(false);
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
    }

    private static async Task AnswerQuery(Stream transport, QueryRequest req,
                                          IReadOnlyDictionary<char, NameIndex> indexes,
                                          List<NameIndex.Hit> hits, CancellationToken ct)
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
            await Frame.WriteAsync(transport, Envelope.Pack(Envelope.KindQueryReply,
                new QueryReply(req.Gen, Stopwatch.GetTimestamp() - started, [])), ct).ConfigureAwait(false);
            return;
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

        foreach ((char letter, NameIndex ix) in indexes)
        {
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

        var reply = new QueryReply(req.Gen, Stopwatch.GetTimestamp() - started, rows);
        await Frame.WriteAsync(transport, Envelope.Pack(Envelope.KindQueryReply, reply), ct).ConfigureAwait(false);
    }

    private static async Task AnswerStatus(Stream transport, IReadOnlyDictionary<char, NameIndex> indexes,
                                           CancellationToken ct)
    {
        var vols = indexes.Select(kv => new VolumeStatus(kv.Key, kv.Value.Count, kv.Value.BufferBytes, true)).ToList();
        var reply = new StatusReply(Environment.ProcessId, vols);
        await Frame.WriteAsync(transport, Envelope.Pack(Envelope.KindStatusReply, reply), ct).ConfigureAwait(false);
    }

    /// <summary>What `--names` runs: build the indexes, then serve clients over the pipe.</summary>
    public static async Task RunAsync(CancellationToken ct)
    {
        var indexes = new Dictionary<char, NameIndex>();
        var volumes = new List<NtfsVolume>();
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
                    if (!vol.QueryJournal())
                        Log.Warn("names", $"{letter}: no USN journal - changes cannot be followed");

                    var ix = new NameIndex(letter);
                    long started = Stopwatch.GetTimestamp();
                    foreach (NtfsVolume.Record r in vol.Enumerate())
                        ix.Upsert(r.Frn, r.ParentFrn, r.Attributes, r.Name);
                    ix.Trim();
                    indexes[letter] = ix;
                    volumes.Add(vol);
                    Log.Info("names", $"{letter}: {ix.Count:N0} names in " +
                        $"{Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s, {ix.BufferBytes / 1048576} MB, " +
                        $"journal cursor {vol.NextUsn}");
                    vol = null;   // handed over to `volumes`, which owns it now
                }
                catch (Exception ex) { Log.Error("names", $"{letter}: enumeration failed", ex); }
                finally { vol?.Dispose(); }
            }

            if (indexes.Count == 0) { Log.Error("names", "no volume could be read - is this running elevated?"); return; }

            await ListenAsync(indexes, ct).ConfigureAwait(false);
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
    /// Concurrent <see cref="Serve"/> calls read ONE shared <see cref="NameIndex"/> per
    /// volume. That is safe only while the index is immutable, which it is here: every index
    /// is built before the first accept and never written again. When journal streaming lands
    /// and the index starts mutating, these readers must be serialised against the writer -
    /// one lock around replay and search, as NameIndex's own header says.
    /// </summary>
    private static async Task ListenAsync(IReadOnlyDictionary<char, NameIndex> indexes, CancellationToken ct)
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
                            await Serve(connected, indexes, ct).ConfigureAwait(false);
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
