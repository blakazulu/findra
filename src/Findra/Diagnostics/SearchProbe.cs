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
