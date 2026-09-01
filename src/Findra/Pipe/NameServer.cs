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
        var rows = new List<NameRow>(Math.Min(req.Max, 512));
        char volume = '?';

        foreach ((char letter, NameIndex ix) in indexes)
        {
            hits.Clear();
            ix.Search(q, hits, req.Max);
            if (hits.Count > 0) volume = letter;
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

                rows.Add(new NameRow(ix.Frn(h.Record), name, path,
                                     ix.Attributes(h.Record), h.Score, h.Match));
                if (rows.Count >= req.Max) break;
            }
            if (rows.Count >= req.Max) break;
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
            var security = new PipeSecurity();
            var me = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));

            using var server = NamedPipeServerStreamAcl.Create(
                PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security);

            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            Log.Info("pipe", "client connected");
            await Serve(server, indexes, ct).ConfigureAwait(false);
            Log.Info("pipe", "client gone");
        }
    }
}
