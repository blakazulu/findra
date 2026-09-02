using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Findra.Startup;   // HelperTaskState

namespace Findra;

public enum FirstRunTarget { None, Preset, Row, Content, Updates, Autostart, NotNow, Go }
public enum FirstRunStage { Choosing, Downloading, Finished }

public readonly record struct FirstRunHit(FirstRunTarget Target, int Index);
public readonly record struct CapabilityProgress(Capability Capability, long Got, long Total);

/// <summary>One line of the list under the presets. <see cref="Capability"/> is null for the free
/// documents row, which is not a capability and has no download.</summary>
public readonly record struct FirstRunRow(
    Capability? Capability, string Title, string Size, string Note, bool Ticked, bool Indented, bool Free);

public sealed record FirstRunState
{
    public IReadOnlySet<Capability> Chosen { get; init; } = new HashSet<Capability>();
    public bool HebrewOffered { get; init; }
    public bool ContentOn { get; init; }
    public bool CheckUpdates { get; init; } = true;
    public bool StartAtLogon { get; init; } = true;
    public FirstRunStage Stage { get; init; } = FirstRunStage.Choosing;
    public IReadOnlyList<CapabilityProgress> Downloads { get; init; } = [];
    /// <summary>What went wrong with the download, shown on the screen. Set by the window from
    /// the outcomes <see cref="FirstRunDownloads"/> returns.</summary>
    public string Problem { get; init; } = "";

    /// <summary>Where the pointer is, so the painter can light a tile, a row or a switch. Part of
    /// the state rather than the window's own field, for the same reason the settings surface
    /// keeps it here: the painter is a pure function of this record.</summary>
    public FirstRunTarget HoverTarget { get; init; } = FirstRunTarget.None;
    public int HoverIndex { get; init; } = -1;
}

public static class FirstRun
{
    private static readonly CultureInfo Fixed = CultureInfo.InvariantCulture;

    public static IReadOnlyList<string> PresetTitles => ["Just names", "Recommended", "Everything"];

    public static string PresetSize(Preset p) => Sizes.Human(Capabilities.TotalBytes(p switch
    {
        Preset.Recommended => Presets.Recommended,
        Preset.Everything => Presets.Everything,
        _ => Presets.JustNames,
    }));

    /// <summary>
    /// The list under the presets. The free documents row comes first because it is what makes
    /// "Not now" safe rather than broken (spec §6), and Hebrew comes immediately after Speech and
    /// indented, because it is a second pass over the same files and not an alternative to it.
    /// </summary>
    public static IReadOnlyList<FirstRunRow> Rows(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var rows = new List<FirstRunRow>
        {
            new(null, "Words in documents", "free",
                "No download and no model. It still only runs once you let Findra look inside files.",
                Ticked: true, Indented: false, Free: true),
        };

        foreach (Capability c in Capabilities.All)
        {
            if (c == Capability.Hebrew && !s.HebrewOffered) continue;
            bool ticked = s.Chosen.Contains(c);
            rows.Add(new FirstRunRow(
                c, Capabilities.Title(c),
                // MARGINAL, given what is already ticked. Nothing more to pay for a row already on.
                Sizes.Human(ticked ? 0 : Capabilities.MarginalBytes(c, s.Chosen)),
                Note(c), ticked, Indented: c == Capability.Hebrew, Free: false));
        }

        return rows;
    }

    public static long TotalBytes(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        // The CLOSED set, summed once. Summing each capability's own total counts the e5 pair
        // twice for anybody who takes Speech and Meaning together.
        return Capabilities.TotalBytes(Capabilities.Close(s.Chosen));
    }

    public static FirstRunState Apply(FirstRunState s, FirstRunHit hit)
    {
        ArgumentNullException.ThrowIfNull(s);
        switch (hit.Target)
        {
            case FirstRunTarget.Preset:
                return s with
                {
                    Chosen = Capabilities.Close(hit.Index switch
                    {
                        1 => Presets.Recommended,
                        2 => Presets.Everything,
                        _ => Presets.JustNames,
                    }),
                    // ASSIGNED, not or-ed. Choosing "Just names" is the affirmative act of
                    // choosing nothing, and a latched switch would turn content indexing on for
                    // somebody who explored the presets and then declined - spec §6, PRIVACY.md.
                    ContentOn = hit.Index > 0,
                };

            case FirstRunTarget.Row:
            {
                IReadOnlyList<FirstRunRow> rows = Rows(s);
                if (hit.Index < 0 || hit.Index >= rows.Count) return s;
                if (rows[hit.Index].Capability is not { } c) return s;   // the free row is not a choice

                return rows[hit.Index].Ticked
                    // Drop, not Remove: untick Speech with Hebrew ticked and Hebrew has to go too.
                    ? s with { Chosen = Capabilities.Drop(s.Chosen, c) }
                    // Close, not Add: taking Speech takes the e5 pair a transcript is searched
                    // with. And ContentOn here IS one-way, unlike the preset arm: ticking a
                    // capability is an affirmative act with a download attached to it.
                    : s with { Chosen = Capabilities.Close([.. s.Chosen, c]), ContentOn = true };
            }

            case FirstRunTarget.Content: return s with { ContentOn = !s.ContentOn };
            case FirstRunTarget.Updates: return s with { CheckUpdates = !s.CheckUpdates };
            case FirstRunTarget.Autostart: return s with { StartAtLogon = !s.StartAtLogon };
            default: return s;
        }
    }

    /// <summary>Spec §9b's disclosure, in the one place the specification names for it.</summary>
    public const string Disclosure =
        "Findra asks GitHub for the newest release at most once every 24 hours. It is one " +
        "anonymous request with no query parameters, no machine or install identifier, and " +
        "nothing about your files or your searches. It never installs anything by itself, and " +
        "turning this off means the request is not made at all.";

    public static string Summary(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        // Both acts of the second half, not just the running one. A Finished run that fell
        // through to the arm below would go back to saying "900 MB to download" after the last
        // byte had landed.
        if (s.Stage != FirstRunStage.Choosing)
        {
            // Done is Got >= Total, so a file already on disk counts as done rather than as
            // nothing - which is what a resumed install looks like from its first second.
            int done = s.Downloads.Count(p => p.Total > 0 && p.Got >= p.Total);

            // Cast to the nullable BEFORE FirstOrDefault. Over a sequence of structs it returns
            // default(CapabilityProgress) when nothing matches, and assigning that to
            // CapabilityProgress? gives a non-null nullable - so the finished screen would name
            // whichever capability is enum value zero, for ever.
            CapabilityProgress? current = s.Downloads
                .Where(p => p.Total > 0 && p.Got < p.Total)
                .Cast<CapabilityProgress?>()
                .FirstOrDefault();

            string which = current is { } c ? " - " + Capabilities.Title(c.Capability) : "";
            // Joined with a dash rather than started as its own sentence: the problem is whatever
            // an exception said, so it arrives uncapitalised and unpunctuated, and " . " between
            // two sentences would put a lower-case word after a full stop on the screen.
            string trouble = s.Problem.Length > 0
                ? $" Findra kept what it already fetched - {s.Problem}."
                : "";

            // Only while it is still running. "It carries on in the tray" is a promise about work
            // that has already stopped once the run is over.
            string tail = s.Stage == FirstRunStage.Downloading
                ? " You can close this; it carries on in the tray."
                : "";

            return $"{done.ToString(Fixed)} of {s.Downloads.Count.ToString(Fixed)} done{which}.{trouble}{tail}";
        }

        if (s.Chosen.Count == 0)
            return "Names are searchable straight away. You can add any of this later in Settings.";

        return s.ContentOn
            ? $"{Sizes.Human(TotalBytes(s))} to download. Findra will read inside your files once it is there."
            : $"{Sizes.Human(TotalBytes(s))} to download, but nothing will be read until you turn " +
              "“Look inside my files” on.";
    }

    public static Config Outcome(FirstRunState s, Config config)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(config);
        // Answered when the person answers, not when the last byte lands. 2.9 GB outlives a laptop
        // lid, and a screen that came back would ask a settled question twice.
        return config with
        {
            FirstRunDone = true,
            IndexContent = s.ContentOn,
            CheckForUpdates = s.CheckUpdates,
        };
    }

    /// <summary>Unknown counts as "not there". HelperTask.Query is three-valued precisely so a
    /// locked-down machine is distinguishable from a fresh one, and registering a task that is
    /// already registered is harmless (schtasks /create /f), while skipping one that is missing
    /// leaves name search permanently empty.</summary>
    public static bool NeedsHelperRegistration(HelperTaskState state) => state != HelperTaskState.Registered;

    /// <summary>Every file the current selection still owes, in the order the rows are drawn. The
    /// window hands this to <see cref="FirstRunDownloads.RunAsync"/>.</summary>
    public static IReadOnlyList<Model> Wanted(FirstRunState s, string? dir = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return ModelStore.Missing(Capabilities.ModelsFor(s.Chosen), dir);
    }

    /// <summary>
    /// One bar per chosen capability, from what each file has moved so far.
    ///
    /// <para><paramref name="fetching"/> is the set this run is actually downloading, which is
    /// <see cref="Wanted"/> - everything else a capability needs was already on disk, and counts
    /// as done rather than as nothing. Without that, a machine that already has the e5 pair shows
    /// Meaning at zero for the whole of a Speech install and looks stuck.</para>
    ///
    /// <para>Totals are the declared sizes, which is what every other number on this screen is
    /// made of, so the bars and the summary cannot disagree. Bytes moved are clamped to them: a
    /// real file usually misses its declared size upward, and a bar past its own end is worse
    /// than one that stops at it.</para>
    /// </summary>
    public static IReadOnlyList<CapabilityProgress> Progress(
        FirstRunState s, IReadOnlyList<Model> fetching, IReadOnlyDictionary<string, long> moved)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(fetching);
        ArgumentNullException.ThrowIfNull(moved);

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Model m in fetching) wanted.Add(m.File);

        IReadOnlySet<Capability> closed = Capabilities.Close(s.Chosen);
        var bars = new List<CapabilityProgress>();
        foreach (Capability c in Capabilities.All)
        {
            if (!closed.Contains(c)) continue;
            long got = 0, total = 0;
            foreach (Model m in Capabilities.OwnModels(c))
            {
                total += m.Bytes;
                got += wanted.Contains(m.File)
                    ? Math.Min(moved.TryGetValue(m.File, out long n) ? n : 0, m.Bytes)
                    : m.Bytes;
            }
            if (total > 0) bars.Add(new CapabilityProgress(c, got, total));
        }
        return bars;
    }

    private static string Note(Capability c) => c switch
    {
        Capability.Photos => "Find a picture by what is in it, and a video by what a frame looks like.",
        Capability.Meaning => "Find a document by what it means, not only by the words it uses.",
        Capability.Speech => "Transcribe recordings and search what was said. Uses the document models too.",
        Capability.Hebrew => "A second pass over recordings detected as Hebrew, after the general model.",
        _ => "",
    };
}

/// <summary>
/// The first-run screen's download run.
///
/// <para>It takes <b>no <c>ContentDb</c></b>, and that is a design decision rather than an
/// omission. A capability that arrives has to be re-queued through <c>CapabilityGate</c>, and the
/// gate has to run on the flow that owns the writer connection: <c>ContentDb.Claim</c> is a
/// thread-id detector rather than a lock, so a call from this download's continuation would throw
/// an <see cref="InvalidOperationException"/> inside a handler nobody is watching. The shell
/// passes <c>afterInstall</c>, which posts onto the content loop.</para>
///
/// <para><b>This is a deliberate divergence from Plan 5 and it is written down here because both
/// halves have to be findable from each other.</b> <c>ModelDownloader.GetAllAsync</c> documents
/// itself as stopping at the first failure - "a set half fetched is resumable, and pressing on
/// after a network fault only turns one failed file into six" - which is right for
/// <c>findra --models install</c>, a command somebody is watching in a terminal. This screen is
/// not being watched; it is closable to the tray and expected to survive a dropped connection
/// (spec §6), and one bad mirror must not cost the other capabilities. Two surfaces, two
/// defensible policies, and the difference is stated rather than discovered.</para>
///
/// <para>It therefore loops <see cref="ModelDownloader.GetAsync"/> itself rather than calling
/// <c>GetAllAsync</c>, because it owns a policy Plan 5's downloader does not have: <b>a fetch that
/// throws becomes a reported outcome, and the set carries on</b>. Plan 5 catches
/// <c>RangeRefusedException</c> and <c>IOException</c> only, so a dropped network raises an
/// <see cref="System.Net.Http.HttpRequestException"/> that would escape the download, the screen,
/// and an unobserved background task - leaving a progress bar that simply stops. Spec §6 requires
/// this screen to survive a dropped connection.</para>
/// </summary>
public static class FirstRunDownloads
{
    public static async Task<IReadOnlyList<DownloadOutcome>> RunAsync(
        IReadOnlyList<Model> models, string dir, Fetch fetch,
        Action<DownloadProgress>? progress, Func<Task> afterInstall, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(afterInstall);

        var outcomes = new List<DownloadOutcome>(models.Count);
        foreach (Model m in models)
        {
            // Cancellation is the app quitting, not a problem to report: the .part files stay and
            // the next run resumes from them.
            ct.ThrowIfCancellationRequested();
            long got = 0;
            try
            {
                outcomes.Add(await ModelDownloader.GetAsync(
                    m, dir,
                    fetch,
                    p => { got = p.Got; progress?.Invoke(p); },
                    ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Whatever it was, it is a sentence on the screen and the rest of the set still
                // runs - one bad mirror must not cost the other capabilities.
                Log.Warn("models", $"{m.File} could not be fetched: {ex.Message}");
                outcomes.Add(new DownloadOutcome(m, Complete: false, Got: got, Problem: ex.Message));
            }
        }

        // Once, at the end, and only when something actually arrived. Per file would re-queue the
        // disk seven times for one install; never would leave a capability installed and idle
        // until the next launch, which reads as a download that did not work.
        if (outcomes.Any(o => o.Complete)) await afterInstall().ConfigureAwait(false);

        return outcomes;
    }
}
