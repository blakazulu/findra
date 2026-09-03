using System.Diagnostics;
using System.Globalization;
using Findra.Pipe;
using Findra.Startup;

namespace Findra.Diagnostics;

public static class SearchProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string query = args.Length > 1 ? string.Join(' ', args[1..]) : "findra";
        Console.WriteLine($"findra --searchprobe  query: \"{query}\"");
        Console.WriteLine();

        (HelperTaskState state, string detail) = HelperTask.Query();
        Console.WriteLine($"  helper task registered : {state switch
        {
            HelperTaskState.Registered    => "yes",
            HelperTaskState.NotRegistered => "NO",
            _                             => "UNKNOWN - the query itself failed",
        }}");
        if (detail.Length > 0) Console.WriteLine($"                           {detail}");

        UiStatus.Status? ui = UiStatus.Read();
        Console.WriteLine(ui is { } u
            ? $"  ui                     : running (pid {u.Pid}, hotkey {u.Hotkey ?? "none"})"
            : "  ui                     : not running");

        Console.WriteLine(Indexer());
        Console.WriteLine(CapsulePill());

        NameClient client;
        try
        {
            client = await NameClient.ConnectAsync(TimeSpan.FromSeconds(5), default);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  pipe                   : UNREACHABLE ({ex.GetType().Name}: {ex.Message})");
            Console.WriteLine();
            Console.WriteLine("  The helper is not running. Start it from an elevated terminal with");
            Console.WriteLine("  `findra --names`, or register the logon task from Settings.");
            return 1;
        }

        // Both outcomes say "pipe", and that symmetry is load-bearing rather than tidy. The line
        // below used to exist only on the UNREACHABLE branch, so build/Check-Diagnostics.ps1's
        // requirement that the probe mention the pipe was satisfied only when the pipe had FAILED.
        // On a runner, where no elevated helper exists, that is always true; on a developer machine
        // with the helper answering it is always false, so the check would have started failing the
        // first time somebody ran it where the product works. A phrase that only appears on the
        // error path is not evidence the path was taken.
        Console.WriteLine("  pipe                   : ok");

        try
        {
            StatusReply status = await client.StatusAsync(default);
            Console.WriteLine($"  answered by            : pid {status.ProcessId}" +
                              $"{(status.ProcessId == Environment.ProcessId ? "  (THIS process - wrong!)" : "  (the helper)")}");
            foreach (VolumeStatus v in status.Volumes)
                Console.WriteLine($"  volume {v.Letter}               : {v.Count:N0} names, {v.BufferBytes / 1048576} MB");

            long started = Stopwatch.GetTimestamp();
            QueryReply? reply = await client.SearchAsync(query, 20, default);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            Console.WriteLine($"  generation             : {client.CurrentGeneration}");
            if (reply is null) { Console.WriteLine("  result                 : STALE - dropped by the generation gate"); return 1; }

            Console.WriteLine($"  reply generation       : {reply.Gen}" +
                              $"{(reply.Gen == client.CurrentGeneration ? "" : "  (MISMATCH)")}");
            Console.WriteLine($"  round trip             : {elapsed.TotalMilliseconds:F1} ms");
            // Only when it moved. A dropped journal event is a file the queue feeder never heard
            // about, and it leaves no other trace anywhere: the index simply looks finished. A
            // line that always said "dropped: 0" would train the reader to skip it.
            if (client.JournalDropped != 0)
                Console.WriteLine($"  journal dropped        : {client.JournalDropped.ToString("N0", CultureInfo.InvariantCulture)}" +
                                  "  (events this session never saw - a full pass is owed)");
            Console.WriteLine($"  rows                   : {reply.Rows.Count}");
            Console.WriteLine();
            foreach (NameRow r in reply.Rows.Take(20))
                Console.WriteLine($"    {r.Score,5:F2}  {r.Volume}:  {r.Path}");

            return reply.Rows.Count > 0 ? 0 : 2;
        }
        finally { await client.DisposeAsync(); }
    }

    private const string Label = "  indexer                : ";
    private const string PillLabel = "  capsule pill           : ";

    /// <summary>
    /// What the capsule's progress pill would draw right now, composed the way the shell composes
    /// it rather than described.
    ///
    /// <para>A surface with no diagnostic is a surface nobody can be asked a question about. "I
    /// cannot see the progress pill" had no answer that did not involve reading source and
    /// guessing: the geometry checked out, the render was correct, the index said work was in
    /// hand, and there was no way to ask the running product what it thought it was drawing. This
    /// prints exactly that, from a second process over the same rows.</para>
    ///
    /// <para><c>indexer:kind</c> is printed beside it because an index written by an older build
    /// does not have that row, and "indexing" without its noun is the visible symptom of exactly
    /// that - worth telling apart from a pill that is not being drawn at all.</para>
    /// </summary>
    private static string CapsulePill()
    {
        try
        {
            if (!File.Exists(ContentDb.DefaultPath)) return PillLabel + "no content index yet";

            using var db = new ContentDb(ContentDb.DefaultPath, readOnly: true);
            // The switch as the INDEX records it. The shell reads its own config; this process has
            // none, and index:paused is the one copy of that bit both of them go by.
            string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
            bool reading = db.Get("index:paused") != "1";
            string kind = db.Get("indexer:kind") ?? "";
            IndexProgress pill = IndexStatus.Pill(
                reading, kind, db.PendingCount(), db.IndexedCount(),
                IndexStatus.Alive(db.Get("indexer:beat"), db.Get("indexer:pid")));

            if (pill.Show)
                return PillLabel + $"\"{pill.Label}\"  [{pill.Fraction * 100:F0}%]  \"{pill.Count}\"" +
                       (kind.Length == 0 ? "   (no indexer:kind row - an index from an older build)" : "");

            // THE reason, in the order Pill decides them, and one of them. The first version of
            // this listed every condition that happened to be unmet, so a finished index reported
            // "nothing drawn (no indexer:kind row)" - which is true, irrelevant, and sent somebody
            // reinstalling twice looking for a fault in a pill that was correctly drawing nothing.
            // A diagnostic that names a reason which is not the reason is worse than none.
            long pending = db.PendingCount();
            return PillLabel + "nothing drawn - " + (
                !reading ? "reading inside files is off"
                : !IndexStatus.Alive(db.Get("indexer:beat"), db.Get("indexer:pid"))
                    ? $"no indexer is running ({N(pending)} file(s) waiting)"
                : pending == 0 ? $"nothing is queued; the index is up to date at {N(db.IndexedCount())} file(s)"
                : "the pill said no with every condition met, which should not happen");
        }
        catch (Exception ex)
        {
            return PillLabel + $"the index could not be read ({ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>
    /// What the content indexer is doing, read from the <c>indexer:*</c> meta rows through a
    /// read-only connection.
    ///
    /// <para>Guarded on <see cref="File.Exists"/> rather than on catching the open: a read-only
    /// SQLite connection over a path that is not there throws, and a fresh machine that has never
    /// started the interface is exactly when somebody runs this. The existence check is also why
    /// <see cref="ContentDb.DefaultPath"/> does not call <c>Paths.Ensure</c> - a property getter
    /// that creates a directory would have this probe bring the index folder into being just by
    /// asking whether anything was in it.</para>
    ///
    /// <para>A stale heartbeat is not a running child. The queue is still reported, because "not
    /// running - 42 file(s) waiting" is the whole answer to "why is nothing being indexed".</para>
    /// </summary>
    private static string Indexer()
    {
        try
        {
            if (!File.Exists(ContentDb.DefaultPath)) return Label + "no content index yet";

            using var db = new ContentDb(ContentDb.DefaultPath, readOnly: true);
            string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

            // Alive takes the pid as well as the heartbeat: a one-shot drain writes both rows, so
            // the recorded pid is what separates a live child from the last thing a finished
            // drain left behind. The rule is IndexStatus's, and so is the sentence below - this
            // probe and --searchindex describe the same rows and must not word them differently.
            string pid = db.Get("indexer:pid") ?? "";
            if (!IndexStatus.Alive(db.Get("indexer:beat"), pid))
                return Label + $"not running - {N(db.PendingCount())} file(s) waiting";

            return Label + IndexStatus.Running(pid, db.Get("indexer:state"),
                                               db.Get("indexer:current"), db.Get("indexer:rate"));
        }
        catch (Exception ex)
        {
            // Never fatal. The rest of this probe is about the name path, which does not touch
            // this file at all, and an index nobody can read is itself worth printing.
            return Label + $"the index could not be read ({ex.GetType().Name}: {ex.Message})";
        }
    }
}
