using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

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
                catch (JsonException ex)
                {
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

        var rows = new List<NameRow>(Math.Min(max, 512));
        char volume = '?';

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

                volume = letter;   // the volume that ANSWERED, not merely one with candidates
                rows.Add(new NameRow(ix.Frn(h.Record), name, path,
                                     ix.Attributes(h.Record), h.Score, h.Match));
                if (rows.Count >= max) break;
            }
            if (rows.Count >= max) break;
        }

        var reply = new QueryReply(req.Gen, volume, Stopwatch.GetTimestamp() - started, rows);
        await Frame.WriteAsync(transport, Envelope.Pack(Envelope.KindQueryReply, reply), ct).ConfigureAwait(false);
    }

    private static async Task AnswerStatus(Stream transport, IReadOnlyDictionary<char, NameIndex> indexes,
                                           CancellationToken ct)
    {
        var vols = indexes.Select(kv => new VolumeStatus(kv.Key, kv.Value.Count, kv.Value.BufferBytes, true)).ToList();
        var reply = new StatusReply(Environment.ProcessId, vols);
        await Frame.WriteAsync(transport, Envelope.Pack(Envelope.KindStatusReply, reply), ct).ConfigureAwait(false);
    }

    /// <summary>What `--names` runs: build the indexes, then listen for one client at a time.</summary>
    public static async Task RunAsync(CancellationToken ct)
    {
        var indexes = new Dictionary<char, NameIndex>();
        foreach ((char letter, _, _, bool fixedDisk) in NtfsVolume.Volumes())
        {
            if (!fixedDisk) continue;
            try
            {
                using var vol = new NtfsVolume(letter);
                var ix = new NameIndex(letter);
                long started = Stopwatch.GetTimestamp();
                foreach (NtfsVolume.Record r in vol.Enumerate())
                    ix.Upsert(r.Frn, r.ParentFrn, r.Attributes, r.Name);
                ix.Trim();
                indexes[letter] = ix;
                Log.Info("names", $"{letter}: {ix.Count:N0} names in " +
                    $"{Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s, {ix.BufferBytes / 1048576} MB");
            }
            catch (Exception ex) { Log.Error("names", $"{letter}: enumeration failed", ex); }
        }

        if (indexes.Count == 0) { Log.Error("names", "no volume could be read - is this running elevated?"); return; }

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                var security = new PipeSecurity();
                var me = WindowsIdentity.GetCurrent().User!;
                security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));

                // FirstPipeInstance: creating a pipe needs no privilege, so without it any
                // local process can squat this name before the helper starts and feed the UI
                // paths of its choosing - a click on a fabricated result then launches as
                // this user. With it, Create fails instead of joining someone else's pipe.
                server = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
            }
            catch (Exception ex)
            {
                // The one failure that would otherwise leave nothing behind. An unhandled
                // exception here escapes Main, .NET terminates without running the exit
                // hook, and a HighestAvailable scheduled task discards stderr - so the log
                // would never be written for a process whose only diagnostic is the log.
                Log.Error("pipe", $"cannot create pipe '{PipeName}' - is the name already taken?", ex);
                Log.Flush();
                return;
            }

            using (server)
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                Log.Info("pipe", "client connected");
                await Serve(server, indexes, ct).ConfigureAwait(false);
                Log.Info("pipe", "client gone");
            }
        }
    }
}
