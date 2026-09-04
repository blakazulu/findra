using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Findra;

/// <summary>One response body, and how many bytes the whole file is. <see cref="TotalBytes"/> is
/// the WHOLE file, not this leg of it, so a resumed download reports honest progress.</summary>
public sealed record Fetched(Stream Body, long TotalBytes, bool IsResume) : IDisposable
{
    public void Dispose() => Body.Dispose();
}

/// <summary>Fetch <paramref name="url"/> starting at byte <paramref name="from"/>. The one seam
/// between the downloader and the network, so every test in this file runs without one.</summary>
public delegate Task<Fetched> Fetch(string url, long from, CancellationToken ct);

/// <summary>The server would not serve from that offset - the file behind the URL changed, or
/// the partial file is longer than the whole. The downloader answers by starting over.</summary>
public sealed class RangeRefusedException(string url, long from)
    : Exception($"the server would not serve {url} from byte {from.ToString(CultureInfo.InvariantCulture)}");

public readonly record struct DownloadProgress(string File, long Got, long Total);

public readonly record struct DownloadOutcome(Model Model, bool Complete, long Got, string? Problem);

/// <summary>
/// Fetching model files, one at a time, resumably - and IN THE INTERFACE.
///
/// <para>Downloading from the indexer child would block its whole queue until all seven files
/// existed, and that is exactly what the spec forbids: consent lives on the first-run screen,
/// progress is shown there, and the child only ever asks whether a file is on disk. Nothing under
/// <c>src/Findra/Content/</c> may reference this type, and a test says so.</para>
///
/// <para>Progress is not written to the database. The <c>.part</c> file IS the durable progress -
/// it survives a reboot and a dropped connection by being on disk - and the index's single
/// writer connection belongs to the queue feeder.</para>
/// </summary>
public static class ModelDownloader
{
    /// <summary>Progress is reported at most this often, so a 1.5 GB file does not spend its
    /// time repainting a bar.</summary>
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(300);

    public static async Task<DownloadOutcome> GetAsync(Model m, string dir, Fetch fetch,
                                                       Action<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(fetch);
        Directory.CreateDirectory(dir);
        string final = ModelStore.PathOf(m, dir), part = final + ".part";

        // Already here and long enough: not one byte is requested. Spec §2a - models present and
        // the right size are kept, not re-downloaded.
        if (ModelStore.Present(m, dir)) return new DownloadOutcome(m, true, ModelStore.ActualBytes(m, dir), null);

        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        Fetched got;
        try
        {
            got = await fetch(m.Url, have, ct).ConfigureAwait(false);
        }
        catch (RangeRefusedException ex)
        {
            // Two very different situations arrive here as the same status code, and telling them
            // apart is worth 1.5 GB.
            //
            // The first is a .part that is ALREADY THE WHOLE FILE - the process was cancelled or
            // killed between the last write and the rename below. A range at or past the end is
            // refused, and discarding it throws away a complete file sitting on the disk, which
            // spec §2a calls the single most annoying thing this product could do to someone. So
            // try promoting it and asking whether it is the file. A part far LONGER than the
            // declared size can never be a prefix of the current file - the file behind the
            // URL was republished smaller - and treating that the same as "already complete"
            // would promote fifteen stale bytes under a nine-byte model's name. "Far" is the
            // whole difference: the declared size is the table's rounded megabytes and four of
            // the five files on a real install are larger than it, so an exact ceiling refuses
            // a part that IS the finished file and fetches 1.5 GB again. ModelStore owns the
            // width of that band, so neither mistake can come back on its own.
            if (ModelStore.CouldBeComplete(m, have))
            {
                try
                {
                    File.Move(part, final, overwrite: true);
                    if (ModelStore.Present(m, dir))
                    {
                        Log.Info("models", $"{m.File} was already complete on disk - nothing was fetched");
                        return new DownloadOutcome(m, true, ModelStore.ActualBytes(m, dir), null);
                    }
                    File.Move(final, part, overwrite: true);   // not the file; put it back
                }
                catch (IOException) { }
            }

            // The second is a stale .part against a file that has been re-published. Keeping it
            // would make every future run ask for a range that is refused, so the install could
            // never finish again on any run.
            Log.Warn("models", $"{m.File}: {ex.Message} - starting again from the beginning");
            try { File.Delete(part); } catch (IOException) { }
            have = 0;
            got = await fetch(m.Url, 0, ct).ConfigureAwait(false);
        }

        long done = got.IsResume ? have : 0;
        long total = got.TotalBytes;
        using (got)
        using (var fs = new FileStream(part, got.IsResume ? FileMode.Append : FileMode.Create,
                                       FileAccess.Write, FileShare.None))
        {
            Log.Info("models", $"fetching {m.File} ({m.Purpose})" +
                               (done > 0 ? $", resuming at {Sizes.Human(done)}" : ""));
            var buf = new byte[1 << 16];
            DateTime last = DateTime.UtcNow;
            int n;
            while ((n = await got.Body.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (DateTime.UtcNow - last > ProgressEvery)
                {
                    last = DateTime.UtcNow;
                    progress?.Invoke(new DownloadProgress(m.File, done, total));
                }
            }
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        progress?.Invoke(new DownloadProgress(m.File, done, total));

        // The completeness check, and it is the one that is easiest to leave out. A connection
        // that closes early leaves a short file, and promoting it puts something above its floor
        // under the final name for ever: every load fails and nothing re-fetches it, because it
        // is "there".
        // TWO conditions, because the first one is unreachable exactly when it is needed most.
        // `total` is ContentLength plus, on a resumed leg, the bytes already on disk - so a
        // response that carries no ContentLength gives total 0 on a fresh GET, skipping the check
        // entirely, and on a RESUMED leg gives total == the bytes we already had, which `done`
        // starts at, so `done < total` is false however little arrived. Chunked transfer encoding,
        // a re-encoding proxy, and a handler that strips the header after decompressing all
        // produce that. The floor does not need the length: a file below MinBytes cannot be this
        // model whatever the server did or did not say about its size.
        //
        // The floor ALONE is not enough, though, and that is the second half of the same lesson.
        // MinBytes is deliberately generous - it is a "this cannot be the file" line, not a "this
        // is the file" one - so with no length there is a window between the floor and the real
        // size where a truncated file passes: 124 MB wide on the Hebrew model, 74 on Whisper. A
        // file that lands in it is promoted under its real name, reads as installed on every
        // surface, and then fails every file that needs it. So when the server did not say how
        // long the file was, the declared size decides, on exactly the terms SizeMatchesDeclared
        // already sets - which is the only place in the tree allowed to call a file the wrong size.
        long floor = total > 0
            ? m.MinBytes
            : Math.Max(m.MinBytes, m.Bytes - ModelStore.SizeSlack(m.Bytes));

        if ((total > 0 && done < total) || done < floor)
        {
            string problem = total > 0
                ? $"the download ended at {done.ToString(CultureInfo.InvariantCulture)} of " +
                  $"{total.ToString(CultureInfo.InvariantCulture)} bytes"
                : $"the download ended at {done.ToString(CultureInfo.InvariantCulture)} bytes, " +
                  $"short of the {floor.ToString(CultureInfo.InvariantCulture)} this file must have";
            Log.Warn("models", $"{m.File}: {problem} - keeping what arrived so the next run resumes");
            return new DownloadOutcome(m, false, done, problem);
        }

        try
        {
            File.Move(part, final, overwrite: true);
        }
        catch (IOException ex)
        {
            // The one place the model directory is genuinely contended: if the indexer child has
            // the previous copy of this file open in an ONNX or whisper session, the rename is
            // refused. Everything fetched is in the .part, so the next run - after the child has
            // been restarted - resumes at zero cost. Letting this out of GetAllAsync would take
            // the first-run download down with an unhandled exception at the last byte.
            Log.Warn("models", $"{m.File}: downloaded, but could not be moved into place :: {ex.Message}");
            return new DownloadOutcome(m, false, done, ex.Message);
        }
        Log.Info("models", $"{m.File} is ready ({Sizes.Human(ModelStore.ActualBytes(m, dir))})");
        return new DownloadOutcome(m, true, done, null);
    }

    /// <summary>Every file in the set, in order, skipping the ones already there. Stops at the
    /// first failure - a set half fetched is resumable, and pressing on after a network fault
    /// only turns one failed file into six.</summary>
    public static async Task<IReadOnlyList<DownloadOutcome>> GetAllAsync(
        IEnumerable<Model> set, string dir, Fetch fetch, Action<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(set);
        var outcomes = new List<DownloadOutcome>();
        foreach (Model m in set)
        {
            DownloadOutcome o;
            try
            {
                o = await GetAsync(m, dir, fetch, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Findra is quitting. The .part is on the disk and the next run resumes from it;
                // an outcome would say the same thing less clearly than letting the caller see
                // its own cancellation.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                // A dropped connection mid-body, or a disk that filled while writing. Neither is
                // caught inside GetAsync - only the final Move is - so both used to leave this
                // method as an unhandled exception. On the first-run screen that is a progress bar
                // that simply stops; from `--models install` it is a stack trace and whatever exit
                // code the runtime picks, in place of the documented "what arrived is kept" and a
                // code a script can read. What arrived IS kept either way: the file streams are in
                // using blocks, so the .part survives and the next run resumes from it.
                Log.Warn("models", $"{m.File}: the download stopped :: {ex.Message} - keeping what arrived so the next run resumes");
                outcomes.Add(new DownloadOutcome(m, false, 0, ex.Message));
                break;
            }
            outcomes.Add(o);
            if (!o.Complete) break;
        }
        return outcomes;
    }

    /// <summary>The real fetch. One GET, a Range header when there is something to resume, and
    /// no header, parameter or identifier beyond a user agent - the model host sees the same
    /// request any browser would make.</summary>
    public static Fetch Http(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return async (url, from, ct) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (from > 0) req.Headers.Range = new RangeHeaderValue(from, null);
            HttpResponseMessage resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                                 .ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                resp.Dispose();
                throw new RangeRefusedException(url, from);
            }
            resp.EnsureSuccessStatusCode();
            bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
            long total = (resp.Content.Headers.ContentLength ?? 0) + (resumed ? from : 0);
            Stream body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new Fetched(body, total, resumed);
        };
    }
}
