using System.IO;
using Avalonia;

namespace Findra;

public static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        if (TalksToWhoeverTypedIt(mode)) ParentConsole.Borrow();
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
    /// Whether this mode exists to print something to the person who typed it, and so should join
    /// the console they typed it at. Findra is a windows-subsystem binary and has no output of its
    /// own until it asks for one - see <see cref="ParentConsole"/> for why it has to be.
    ///
    /// <para>Every mode that begins with <c>--</c> qualifies, including a mistyped one, which owes
    /// the caller the list of real modes and exit 1. Three do not:</para>
    ///
    /// <para><c>--names</c> is started by the logon task and <c>--index</c> by the interface. Both
    /// are headless children nobody typed, both report through the log file, and neither has a
    /// terminal to join - so asking for one buys nothing and, on the one launch where the
    /// interface itself was started from a prompt, would put the indexer's noise in somebody's
    /// shell.</para>
    ///
    /// <para>The interface is the third, and it is a deliberate refusal rather than an oversight.
    /// A shell does not wait for a windows-subsystem process, so its prompt comes back at once and
    /// the widget then runs for hours behind it: anything Findra wrote would land in the middle of
    /// whatever was typed next, and a Ctrl+C aimed at that prompt would be aimed at Findra. The
    /// interface has a log file, a tray icon and a card to say things through, all of which
    /// outlive a shell. It says nothing to one.</para>
    /// </summary>
    private static bool TalksToWhoeverTypedIt(string mode) =>
        mode.StartsWith("--", StringComparison.Ordinal) && mode is not ("--names" or "--index");

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
    ///
    /// <para>It runs AFTER the console is borrowed, and that order is load-bearing: setting the
    /// encoding sets the console's output code page, and a process that has not joined a console
    /// yet has no code page to set. Reversed, every diagnostic would take the caught exception and
    /// print the separator as a replacement character.</para>
    /// </summary>
    private static void UseUtf8OnTheConsole()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }
    }

    private static int RunUi()
    {
        Log.Info("startup", $"findra {Log.Version} starting");

        // One interface per index, and the claim is held for the whole run. Two of them share one
        // models folder and one index: the second's indexer child cannot open the vector store the
        // first holds and is restarted for ever, the second hotkey lands on a fallback chord, and a
        // download in either writes into part files the other is writing. See OnlyOne.
        if (!Startup.OnlyOne.Take(out Startup.OnlyOne? claim))
        {
            string say = Startup.OnlyOne.AlreadyRunning(UiStatus.Read());
            Log.Info("startup", "another Findra already has this index; this one is not starting");
            Log.Flush();
            // The interface does not attach to a console, so this reaches somebody only when they
            // typed the name at a prompt - which is exactly when a silent exit is baffling. It
            // costs nothing on the four launches that have no console.
            ParentConsole.Borrow();
            Console.Error.WriteLine(say);
            return 0;   // not a fault: Findra IS running, which is what was asked for
        }

        using (claim)
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
