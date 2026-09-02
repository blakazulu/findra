using System.Globalization;
using System.Text;

namespace Findra.Diagnostics;

/// <summary>
/// `findra --content`: is Findra reading inside files at all, and how much of a recording is
/// worth listening to.
///
/// <para>Content indexing is off until asked for (spec §6) and the screen that would ask is not
/// written yet, so this is the only way to turn it on - and therefore the only way the free,
/// model-free document text ever runs on a fresh install. It is worth keeping afterwards: it is
/// how the setting is read and changed on a machine with no screen, in CI, and by anybody
/// reporting a bug.</para>
///
/// <para>Note that this is not <c>--index &lt;parentPid&gt;</c>. That is the indexer child, which
/// the interface starts and nobody runs by hand. This is a setting a person changes.</para>
/// </summary>
public static class ContentCommand
{
    /// <summary>The words the limit will take, printed back at somebody who typed something
    /// else. Never a fallback to zero: zero is a real setting - it turns transcription off - so
    /// guessing it for a mistyped number would silently switch speech off for them.</summary>
    public const string LimitWords = "off | 5 | 30 | 2 hours | no limit | any number of minutes";

    /// <summary>The sentence that ends every arm which changes something. The settings are saved
    /// whatever happens; what needs saying is that a running Findra read them at startup.</summary>
    private const string RestartNote =
        "If Findra is running, it reads these settings when it starts, so restart it for the\n" +
        "change to take effect on the queue. The setting is saved either way.";

    /// <summary>
    /// The four things somebody needs in order to know whether Findra is reading anything.
    ///
    /// <para>The state sentence comes from <see cref="IndexStatus.Line"/> - the same one the
    /// card's footer and the capsule draw - so the console and the interface cannot disagree
    /// about which of the states this machine is in. It matters most in the state this command
    /// exists for: a fresh install's queue is empty and nothing is running, which is byte for
    /// byte what a finished index looks like, and the counts alone would call a machine that has
    /// never read anything "up to date". The two OFF states are different sentences too, because
    /// telling somebody who turned it off that their index is empty is a lie.</para>
    ///
    /// <para><paramref name="installed"/> defaults to what is on disk. It is a parameter so the
    /// formatter can be driven from a set rather than from the machine; the contract's two-
    /// argument call is the ordinary one.</para>
    /// </summary>
    public static string RenderStatus(Config config, IndexSnapshot index, CapabilitySet? installed = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(index);
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');
        string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        string state = IndexStatus.Line(config.IndexContent, index.IndexerState, index.Queued, index.Indexed,
                                        index.IndexerAlive, index.WasRebuilt);
        // The one case that line has nothing to say: reading is on and the index is empty, which
        // it renders as "" so that an idle widget draws no bar at all. A diagnostic that printed
        // a blank there would be the "looks idle" failure this whole command exists to avoid.
        if (state.Length == 0) state = "reading inside files is on - nothing has been read yet";

        Line("findra --content");
        Line();
        Line($"  inside files : {state}");
        if (!config.IndexContent)
            Line("                 findra --content on");
        else
            Line($"                 {N(index.Indexed)} read, {N(index.Queued)} waiting, {N(index.Skipped)} passed over, {N(index.Failed)} failed");
        Line();

        Line($"  transcribe   : {TranscribeLimit.Describe(config.TranscribeMinutes)}");
        // The words this takes are NOT listed here, and that is deliberate rather than terse.
        // One of them is "off", and printing it in a hint on every status made the assertion
        // that a fresh install says reading is off pass off the hint instead of off the state
        // sentence above - found by mutating the state sentence and watching nothing fail.
        // `findra --content limit` with no length prints the list, which is where it belongs.
        Line("                 findra --content limit <length>");
        Line();

        IReadOnlySet<Capability> have = (installed ?? CapabilitySet.Installed()).Have ?? new HashSet<Capability>();
        Line($"  capabilities : {(have.Count == 0 ? "none yet" : string.Join(", ", Capabilities.All.Where(have.Contains).Select(Capabilities.Title)))}");
        Line("                 findra --models shows what each one would add");

        return sb.ToString();
    }

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string verb = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "status";

        switch (verb)
        {
            case "status":
            {
                using ContentDb db = ContentDb.OpenOrRebuild();
                Console.Write(RenderStatus(Config.LoadFromDisk(), SearchIndex.Snapshot(db)));
                return 0;
            }

            case "on":
            case "off":
            {
                bool on = verb == "on";
                Config config = Config.LoadFromDisk();
                (config with { IndexContent = on }).Save();
                if (on)
                {
                    Console.WriteLine("Findra will now read what is inside files as well as their names.");
                    Console.WriteLine("The first pass walks every drive it is allowed to and opens every document on it,");
                    Console.WriteLine("which on a large disk takes hours. Names keep working throughout.");
                }
                else
                {
                    Console.WriteLine("Findra will stop reading inside files. Nothing already read is thrown away -");
                    Console.WriteLine("every word already in the index stays searchable, and turning it back on picks up");
                    Console.WriteLine("where this left off.");
                }
                Console.WriteLine();
                Console.WriteLine(RestartNote);
                return 0;
            }

            case "limit":
            {
                string what = args.Length > 2 ? args[2] : "";
                if (TranscribeLimit.Parse(what) is not { } minutes)
                    return Usage(what.Length == 0
                        ? "findra: --content limit needs a length"
                        : $"findra: '{what}' is not a length this understands");

                (Config.LoadFromDisk() with { TranscribeMinutes = minutes }).Save();
                Console.WriteLine($"How long a recording is worth transcribing: {TranscribeLimit.Describe(minutes)}.");

                using (ContentDb db = ContentDb.OpenOrRebuild())
                {
                    int queued = CapabilityGate.ApplyLimit(db, minutes);
                    Console.WriteLine(queued > 0
                        ? $"{queued.ToString("N0", CultureInfo.InvariantCulture)} recording(s) queued to be heard."
                        : "Nothing new to hear at that limit.");
                }

                Console.WriteLine();
                Console.WriteLine(RestartNote);
                return 0;
            }

            default:
                return Usage($"findra: --content does not know '{args[1]}'");
        }
    }

    private static int Usage(string complaint)
    {
        Console.Error.WriteLine(complaint);
        Console.Error.WriteLine();
        Console.Error.WriteLine("  findra --content               is Findra reading inside files, and how much of a recording");
        Console.Error.WriteLine("  findra --content status        the same");
        Console.Error.WriteLine("  findra --content on            start reading inside files");
        Console.Error.WriteLine("  findra --content off           stop; nothing already read is thrown away");
        Console.Error.WriteLine($"  findra --content limit <what>  {LimitWords}");
        return 1;
    }
}
