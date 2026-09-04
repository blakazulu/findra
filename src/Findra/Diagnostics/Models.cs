using System.Globalization;
using System.Net.Http;
using System.Text;

namespace Findra.Diagnostics;

/// <summary>
/// `findra --models`: what a capability would cost, and the only way to take one until the
/// first-run screen exists.
///
/// <para>This is not a stopgap. Content indexing is off until asked for and every model-backed
/// capability arrives by download, so without these two switches nothing in the capability path
/// - not even the free, model-free document text - can be reached at all on a machine with no
/// screen, in CI, or by somebody reporting a bug. It stays after the screen ships.</para>
///
/// <para><see cref="ParsePreset"/>, <see cref="ParseCapabilities"/> and <see cref="RenderList"/>
/// are the pure half and carry every rule; <see cref="RunAsync"/> is the impure half and does
/// the fetching. Every number goes through <see cref="Sizes.Human"/>, which is invariant - this
/// text is compared in tests and read on machines set to any locale.</para>
/// </summary>
public static class ModelsCommand
{
    /// <summary>The words that would have worked, printed back at somebody who typed something
    /// else. One list, so the refusal and the usage cannot drift apart.</summary>
    public const string PresetWords = "justnames | recommended | everything";

    public const string CapabilityWords = "photos | meaning | speech | hebrew";

    /// <summary>A preset by the name the first-run screen gives it, or null.
    ///
    /// <para><see cref="Preset.Custom"/> is deliberately unreachable: it is not something anybody
    /// asks for, it is what touching a row makes. Accepting the word would turn the one preset
    /// that is not a choice into one. Nothing falls back to a default either - a guessed preset
    /// downloads gigabytes nobody asked for.</para></summary>
    public static Preset? ParsePreset(string word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        "justnames" => Preset.JustNames,
        "recommended" => Preset.Recommended,
        "everything" => Preset.Everything,
        _ => null,
    };

    /// <summary>What a preset stands for. The same sets the screen will offer, read from
    /// <see cref="Presets"/> rather than rebuilt here.</summary>
    public static IReadOnlySet<Capability> CapabilitiesIn(Preset p) => p switch
    {
        Preset.JustNames => Presets.JustNames,
        Preset.Recommended => Presets.Recommended,
        Preset.Everything => Presets.Everything,
        _ => Presets.JustNames,
    };

    private static Capability? OneCapability(string word) => word.Trim().ToLowerInvariant() switch
    {
        "photos" => Capability.Photos,
        "meaning" => Capability.Meaning,
        "speech" => Capability.Speech,
        "hebrew" => Capability.Hebrew,
        _ => null,
    };

    /// <summary>
    /// A comma-separated list of capability names, taken TOGETHER and closed ONCE - or null if
    /// any one of them is not a name.
    ///
    /// <para>Closed once, over the whole list, because closing and installing each name in turn
    /// gets the download order wrong and leaves every intermediate state unusable. Refused rather
    /// than filtered, because dropping an unknown word means <c>--models install photos,speach</c>
    /// installs photos, reports success, and somebody waits for speech search that is never
    /// coming.</para>
    /// </summary>
    public static IReadOnlySet<Capability>? ParseCapabilities(string list)
    {
        if (string.IsNullOrWhiteSpace(list)) return null;
        var chosen = new HashSet<Capability>();
        foreach (string part in list.Split(','))
        {
            if (OneCapability(part) is not { } c) return null;
            chosen.Add(c);
        }
        return Capabilities.Close(chosen);
    }

    /// <summary>
    /// What is here, what each capability would add to it, and what the whole set costs.
    ///
    /// <para>Two things need no model at all and are printed FIRST and marked free, because a
    /// listing showing only the paid rows makes "just names" read as "no search": words in
    /// documents, and the words Windows reads out of a picture, which happens whenever the
    /// indexer opens a photo anyway and is not a capability - it has no download and appears in
    /// no graph. "Free" here means free of charge, not free of consent: both still wait for
    /// content indexing to be turned on.</para>
    ///
    /// <para>Every size is MARGINAL given <paramref name="installed"/> -
    /// <see cref="Capabilities.MarginalBytes"/>, never a fixed number. Speech is 818 MB on a bare
    /// machine and 547 MB beside documents' meaning, and a fixed table makes the total visibly
    /// fail to add up.</para>
    ///
    /// <para>Hebrew appears only when <paramref name="hebrewOffered"/>: it is 1.5 GB, and a row
    /// that size in front of somebody who reads no Hebrew is not an offer.</para>
    /// </summary>
    public static string RenderList(CapabilitySet installed, bool hebrewOffered)
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');
        IReadOnlySet<Capability> have = installed.Have ?? new HashSet<Capability>();

        // "installed" is written on installed rows and NOWHERE else - not "not installed", not
        // "everything installed would be". A listing that prints the word unconditionally makes
        // the test that an installed capability is shown as installed pass without ever reading
        // the set.
        string Cost(Capability c) => installed.Has(c)
            ? "installed"
            : Sizes.Human(Capabilities.MarginalBytes(c, installed));

        string Still(IReadOnlySet<Capability> want)
        {
            long marginal = Capabilities.TotalBytes(Capabilities.Close([.. want, .. have]))
                          - Capabilities.TotalBytes(have);
            return marginal <= 0 ? "nothing" : Sizes.Human(marginal);
        }

        Line("findra --models");
        Line();
        Line($"  models   : {ModelStore.Dir}");
        Line();

        Line("  free (no model, nothing to download - they run once content indexing is on):");
        Line("    words in documents          every word of every document that can be read");
        Line("    words inside pictures       the text Windows reads out of a photo, as it is opened anyway");
        Line();

        Line("  capabilities (what each would add, given what is already here):");
        Line($"    {Capabilities.Title(Capability.Photos),-26}{Cost(Capability.Photos),-12}findra --models install photos");
        Line($"    {Capabilities.Title(Capability.Meaning),-26}{Cost(Capability.Meaning),-12}findra --models install meaning");
        Line($"    {Capabilities.Title(Capability.Speech),-26}{Cost(Capability.Speech),-12}findra --models install speech");
        if (hebrewOffered)
            // Indented under Speech, because it is a second pass over what Speech already heard
            // and never an alternative to it.
            Line($"      {Capabilities.Title(Capability.Hebrew),-24}{Cost(Capability.Hebrew),-12}findra --models install hebrew");
        Line();

        Line("  presets (the same arithmetic, for a whole selection):");
        Line($"    {"just names",-26}{Still(Presets.JustNames),-12}findra --models install justnames");
        Line($"    {"recommended",-26}{Still(Presets.Recommended),-12}findra --models install recommended");
        Line($"    {"everything",-26}{Still(Presets.Everything),-12}findra --models install everything");
        Line();

        // The whole set, from the graph rather than from a sum of the rows above - the e5 pair is
        // shared by three capabilities and adding the printed numbers up counts it twice. This is
        // every capability Findra has, including any the list above did not offer here.
        Line($"  everything Findra can install is {Sizes.Human(Capabilities.TotalBytes(Presets.Everything))}, and it lives in {ModelStore.Dir}");
        Line("  nothing is read from inside a file until content indexing is on - findra --content on");

        return sb.ToString();
    }

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string verb = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "list";

        if (verb is "list")
        {
            Console.Write(RenderList(CapabilitySet.Installed(),
                                     Capabilities.HebrewIsOffered(Capabilities.SystemLanguages())));
            return 0;
        }

        if (verb is not "install") return Usage($"findra: --models does not know '{args[1]}'");

        string what = args.Length > 2 ? args[2] : "";
        IReadOnlySet<Capability>? chosen =
            ParsePreset(what) is { } p ? CapabilitiesIn(p) : ParseCapabilities(what);
        if (chosen is null)
            return Usage(what.Length == 0
                ? "findra: --models install needs a preset or a capability"
                : $"findra: '{what}' is not a preset or a capability");

        // Said before a byte is fetched: what is coming, and what it comes to. Anything already
        // on disk is named as already here rather than silently skipped - spec §2a's promise that
        // a finished file is never fetched again is only worth something if somebody can see it.
        string dir = ModelStore.Dir;
        IReadOnlyList<Model> need = Capabilities.ModelsFor(chosen);
        IReadOnlyList<Model> missing = ModelStore.Missing(need, dir);
        long toFetch = ModelStore.TotalBytes(missing);

        Console.WriteLine($"findra --models install {what}");
        Console.WriteLine();
        if (need.Count == 0)
        {
            Console.WriteLine("  nothing to download - names, and the words inside documents once content");
            Console.WriteLine("  indexing is on, need no model at all.");
        }
        else
        {
            foreach (Model m in need)
                Console.WriteLine(ModelStore.Present(m, dir)
                    ? $"    already here  {m.File,-24} {m.Purpose}"
                    : $"    to fetch      {m.File,-24} {Sizes.Human(m.Bytes),-9} {m.Purpose}");
            Console.WriteLine();
            Console.WriteLine(missing.Count == 0
                ? "  everything asked for is already on disk - nothing will be fetched."
                : $"  {missing.Count.ToString(CultureInfo.InvariantCulture)} file(s), {Sizes.Human(toFetch)} to fetch, into {dir}");
        }
        Console.WriteLine();

        IReadOnlyList<DownloadOutcome> outcomes = [];
        if (missing.Count > 0)
        {
            using var http = NewClient();
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            try
            {
                outcomes = await ModelDownloader.GetAllAsync(need, dir, ModelDownloader.Http(http), Progress, cts.Token)
                                                .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                EndProgressLine();
                Console.WriteLine("  stopped. What arrived is kept - run the same command again and it resumes.");
                return 2;
            }
            EndProgressLine();

            foreach (DownloadOutcome o in outcomes)
                if (!o.Complete)
                {
                    Console.Error.WriteLine($"findra: {o.Model.File} did not finish - {o.Problem}");
                    Console.Error.WriteLine("  what arrived is kept in the .part file; running this again resumes from it.");
                    return 2;
                }
        }

        // Every file is here, so the index owes the capability its backlog. This process owns its
        // own connection and runs no content loop, which is why the gate can be called here at
        // all - there is no second flow to take the writer from.
        try
        {
            using ContentDb db = ContentDb.OpenOrRebuild();
            int queued = CapabilityGate.Apply(db, CapabilityGate.Plan(CapabilitySet.Installed(dir), CapabilityGate.StampsIn(db)));
            Console.WriteLine($"{queued.ToString("N0", CultureInfo.InvariantCulture)} file(s) queued to be read again.");
        }
        catch (Exception ex)
        {
            // The download succeeded, and the interface runs this same gate at every startup, so
            // a busy or unreadable index costs nothing but a line. Reporting a failure here would
            // tell somebody their 900 MB did not arrive when it did.
            Log.Warn("models", $"the index could not be reconciled now :: {ex.Message}");
            Console.WriteLine("  the index was busy, so the files it owes will be queued the next time Findra starts.");
        }

        Console.WriteLine();
        // Conditional rather than flat. The sentence this replaces opened "Findra is running.",
        // which is simply false on the machine most likely to be running this command - a
        // terminal, with nothing else open - and a diagnostic that states something the reader
        // can see is untrue is not read any further.
        Console.WriteLine("If Findra is running, its indexer checks what is installed before every file it opens,");
        Console.WriteLine("so it begins reading these without a restart. The card loads its own half of a");
        Console.WriteLine("capability when Findra starts, so restart it before searching the new way.");
        return 0;
    }

    /// <summary>Redirects followed, because every model URL is a redirect to a content host, and
    /// no timeout, because a 1.5 GB file over a slow line is not a hung request. No cookie jar
    /// and no identifier of any kind - the model host sees the request a browser would make.</summary>
    private static HttpClient NewClient() => new(new SocketsHttpHandler { UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static bool _progressed;

    /// <summary>One line, rewritten. Padded to the width of the longest thing that has been on
    /// it, so a shorter line never leaves the tail of a longer one behind.</summary>
    private static void Progress(DownloadProgress p)
    {
        string pct = p.Total > 0
            ? (100.0 * p.Got / p.Total).ToString("0", CultureInfo.InvariantCulture) + "%"
            : "";
        string line = $"  {p.File,-24} {Sizes.Human(p.Got),-9} of {(p.Total > 0 ? Sizes.Human(p.Total) : "?"),-9} {pct}";
        Console.Write("\r" + line.PadRight(78));
        _progressed = true;
    }

    private static void EndProgressLine()
    {
        if (_progressed) Console.WriteLine();
        _progressed = false;
    }

    private static int Usage(string complaint)
    {
        Console.Error.WriteLine(complaint);
        Console.Error.WriteLine();
        Console.Error.WriteLine("  findra --models                              what is here, and what each capability would add");
        Console.Error.WriteLine("  findra --models list                         the same");
        Console.Error.WriteLine($"  findra --models install <preset>             {PresetWords}");
        Console.Error.WriteLine($"  findra --models install <cap>[,<cap>...]     {CapabilityWords}");
        return 1;
    }
}
