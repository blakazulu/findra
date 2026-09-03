using System.IO;
using Avalonia;

namespace Findra;

public static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        UseUtf8OnTheConsole();
        return mode switch
        {
            "--names"       => RunNames(),
            "--index"       => Indexer.Run(args),
            "--searchprobe" => Diagnostics.SearchProbe.RunAsync(args).GetAwaiter().GetResult(),
            "--searchtest"  => Diagnostics.SelfTest.Run(),
            "--searchindex" => Diagnostics.SearchIndex.Run(args),
            "--searchbench" => Diagnostics.SearchBench.RunAsync(args).GetAwaiter().GetResult(),
            "--searchmodels" => Diagnostics.SearchModels.Run(args),
            // The plan's second GetAwaiter().GetResult(), and like the first it is at a switch
            // arm where there is no async context to await into. There is no third: --content
            // downloads nothing and is synchronous throughout.
            "--models"      => Diagnostics.ModelsCommand.RunAsync(args).GetAwaiter().GetResult(),
            "--content"     => Diagnostics.ContentCommand.Run(args),
            "--searchshot"  => Diagnostics.SearchShot.Render(
                args.Length > 1 ? args[1] : "searchshot.png",
                args.Length > 2 ? args[2] : "results",
                args.Length > 3 ? args[3] : null),
            "--version"     => Version(),
            "--uninstall"   => Startup.Uninstall.Run(args),
            "--stop"        => Startup.Uninstall.StopAll(),

            // A mistyped mode must not look like a success. `--searchprob` falling through to
            // the no-argument greeting exits 0, which is what a script checks, so the typo
            // reads as a passing diagnostic. Anything that starts with `--` is a mode the
            // caller meant; if it is not one of ours, say so and fail.
            _ when mode.StartsWith("--", StringComparison.Ordinal) => Unknown(mode),

            _               => RunUi(),
        };
    }

    /// <summary>
    /// Several of the sentences the diagnostics print are the card's own, and the card's
    /// separator is a middle dot. A console left on the machine's OEM code page renders that as
    /// a replacement character, and so does every Hebrew string the probes carry. Asking for
    /// UTF-8 once, here, is cheaper than forking a sentence per command, and it is exactly the
    /// sentence being shared that makes the console and the card impossible to disagree.
    ///
    /// <para>It is done for every mode rather than for the console ones, because the failure is
    /// silent and per-command: a mode that forgets the line prints question marks and nothing
    /// anywhere says so. Some hosts refuse the change, and a
    /// window with no console attached refuses it too - a mangled separator is not worth failing
    /// a diagnostic over, and it is certainly not worth failing to start.</para>
    /// </summary>
    private static void UseUtf8OnTheConsole()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }
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
        Console.Error.WriteLine("  --index <parentPid>      the content indexer, started by the UI - not run by hand");
        Console.Error.WriteLine("  --models [list|install <preset|cap,cap>]   what a capability costs, and how to take it");
        Console.Error.WriteLine("  --content [status|on|off|limit <what>]     whether Findra reads inside files at all -");
        Console.Error.WriteLine("                           a setting you change, unlike --index above");
        Console.Error.WriteLine("  --searchprobe [query]    the whole query path, end to end");
        Console.Error.WriteLine("  --searchtest             engine self-check");
        Console.Error.WriteLine("  --searchmodels           are the models present, do they load, and on which provider");
        Console.Error.WriteLine("  --searchindex [file|folder|q:query]...   what is indexed, and what is queued");
        Console.Error.WriteLine("  --searchbench [out.md] [corpus]     measure it, and print numbers fit to publish");
        Console.Error.WriteLine("  --searchshot out.png <state> [palette]   render a surface, no screen required");
        Console.Error.WriteLine("                           palette defaults to the configured one, not a fixed built-in");
        Console.Error.WriteLine("  --version                print the version and log location, then exit");
        Console.Error.WriteLine("  --uninstall [--purge]    remove the scheduled task, the autostart entry and Findra");
        Console.Error.WriteLine("                           --purge also deletes the models, the index and the settings");
        Console.Error.WriteLine("                           --dry-run prints what it would do and changes nothing");
        Console.Error.WriteLine("  --stop                   stop the interface, the indexer and the name helper");
        return 1;
    }

    // --version answers the question a bug report starts with: which build is this, and where
    // are its logs. It writes nothing to the log itself - asking a version should not add a line
    // to the file the answer points at.
    private static int Version()
    {
        Console.WriteLine($"findra {Log.Version}");
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
