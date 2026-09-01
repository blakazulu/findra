using System.Diagnostics;
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

        Console.WriteLine($"  helper task registered : {(HelperTask.IsRegistered() ? "yes" : "NO")}");

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
            Console.WriteLine($"  rows                   : {reply.Rows.Count}");
            Console.WriteLine();
            foreach (NameRow r in reply.Rows.Take(20))
                Console.WriteLine($"    {r.Score,5:F2}  {r.Path}");

            return reply.Rows.Count > 0 ? 0 : 2;
        }
        finally { await client.DisposeAsync(); }
    }
}
