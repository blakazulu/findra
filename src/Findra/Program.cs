using Avalonia;

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
            "--searchshot"  => Diagnostics.SearchShot.Render(
                args.Length > 1 ? args[1] : "searchshot.png",
                args.Length > 2 ? args[2] : "results",
                args.Length > 3 ? args[3] : null),
            "--version"     => Hello(),

            // A mistyped mode must not look like a success. `--searchprob` falling through to
            // the no-argument greeting exits 0, which is what a script checks, so the typo
            // reads as a passing diagnostic. Anything that starts with `--` is a mode the
            // caller meant; if it is not one of ours, say so and fail.
            _ when mode.StartsWith("--", StringComparison.Ordinal) => Unknown(mode),

            _               => RunUi(),
        };
    }

    private static int RunUi()
    {
        Log.Info("startup", $"findra {Log.Version} starting");
        try
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception ex) { Log.Error("startup", "the UI could not start", ex); Log.Flush(); return 1; }
    }

    private static int Unknown(string mode)
    {
        Console.Error.WriteLine($"findra: unknown mode '{mode}'");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  --names                  the elevated name-index helper");
        Console.Error.WriteLine("  --searchprobe [query]    the whole query path, end to end");
        Console.Error.WriteLine("  --searchtest             engine self-check");
        Console.Error.WriteLine("  --searchshot out.png <state> [palette]   render a surface, no screen required");
        Console.Error.WriteLine("                           palette defaults to the configured one, not a fixed built-in");
        Console.Error.WriteLine("  --version                print the version and log location, then exit");
        return 1;
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
