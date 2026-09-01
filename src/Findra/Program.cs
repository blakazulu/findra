namespace Findra;

public static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        return mode switch
        {
            "--names"       => RunNames(),
            "--searchprobe" => Diagnostics.SearchProbe.RunAsync(args).GetAwaiter().GetResult(),
            "--searchtest"  => Diagnostics.SelfTest.Run(),
            _               => Hello(),
        };
    }

    private static int Hello()
    {
        Log.Info("startup", $"findra {Log.Version} - no UI yet");
        Log.Flush();
        Console.WriteLine($"log: {Log.Dir}");
        return 0;
    }

    private static int RunNames()
    {
        Log.Info("names", "helper starting");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { Pipe.NameServer.RunAsync(cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        Log.Flush();
        return 0;
    }
}
