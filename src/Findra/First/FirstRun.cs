using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Findra.Startup;   // HelperTaskState

namespace Findra;

public enum FirstRunTarget { None, Preset, Row, Limit, Content, Updates, Autostart, NotNow, Go }
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

    /// <summary>How long a recording is worth transcribing, in minutes, on the terms
    /// <see cref="TranscribeLimit"/> sets. It is on this screen because ticking Speech is what
    /// signs somebody up for transcription, and five minutes - the default - passes over a
    /// lecture, an interview and most of a podcast without saying so.</summary>
    public int TranscribeMinutes { get; init; } = TranscribeLimit.Default;

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
                // This row's OWN files, and it does not move. A marginal figure - what this row
                // would add to what is already ticked - turns into "0 MB" the moment the row is
                // ticked, so the number somebody is weighing disappears exactly when they decide
                // on it. Own files also make the column add up: 629 + 270 + 547 + 1549 MB is the
                // 2.93 GB on the Everything tile, where the closed sets would total 4.08 GB.
                // What the whole selection costs is the summary's job, and it is closed there.
                Sizes.Human(ModelStore.TotalBytes(Capabilities.OwnModels(c))),
                Note(c), ticked, Indented: c == Capability.Hebrew, Free: false));
        }

        return rows;
    }

    /// <summary>The label beside the limit's five pills. Short because the column it sits in is
    /// what is left of the row after them - 98.3px of it in the shipped face, against 282px.
    /// </summary>
    public const string LimitLabel = "Transcribe up to";

    /// <summary>
    /// What the number actually decides, under the pills that set it.
    ///
    /// <para>The label says "Transcribe up to" and the row above it says Speech, so somebody
    /// reading this screen has no way to know the number also governs every video on the disk.
    /// The settings window has said so since it was written; the screen where a 547 MB download
    /// is agreed to said nothing.</para>
    ///
    /// <para>The second half is a statement about <c>Decoders.Video</c> rather than a
    /// reassurance: a video whose sound track is passed over for length is INDEXED with a note
    /// rather than skipped, because its frames were read - and the frames are read only where
    /// Photos is installed, which is why the sentence names that condition instead of promising
    /// them unconditionally.</para>
    /// </summary>
    public const string LimitNote =
        "One number for audio and video. A video past it is still found by its frames where " +
        "Photos is ticked; only its words are skipped.";

    /// <summary>
    /// What "Look inside my files" means, under the switch that turns it on.
    ///
    /// <para>It was a bare label while the update check under it carried four lines - the most
    /// consequential privacy choice on the screen explained by nothing and the least consequential
    /// one at length. Four facts, each checked against the code or against <c>PRIVACY.md</c>
    /// rather than written from the label: names are searchable either way, which is what makes
    /// "Not now" a complete answer; it walks the drives and reads inside what it finds, which is
    /// hours of work and not a switch that finishes; it happens only while Findra is open,
    /// because the indexer is a child of the interface and there is no service; and the text it
    /// reads is kept in an index in the user's own profile that is not encrypted, which is the
    /// privacy page's own straight answer and must not be softer here than it is there.</para>
    /// </summary>
    public const string ContentNote =
        "Names are searchable either way; this is only about what is inside files. Findra walks " +
        "your drives and reads them, which can take hours on a full disk and happens only while " +
        "Findra is open. The text it reads is kept in an index in your user profile, which is " +
        "not encrypted.";

    /// <summary>The label on the button that closes this window, in both halves of the second
    /// act. It says what it does: the answer has already been given and the download belongs to
    /// the shell, so there is nothing left to confirm.</summary>
    public const string CloseLabel = "Close";

    /// <summary>The label on the left-hand button, which exists only while the screen is still a
    /// question.</summary>
    public const string NotNowLabel = "Not now";

    /// <summary>The right-hand button's label. One function rather than four literals in the
    /// painter, so the test that measures every label into its pill measures what is drawn.
    /// </summary>
    public static string GoLabel(FirstRunStage stage) =>
        stage == FirstRunStage.Choosing ? "Get these" : CloseLabel;

    /// <summary>The same, for a screen that is asking the last question rather than reporting.
    /// </summary>
    public static string GoLabel(FirstRunState s) =>
        Asks(s) ? StartReadingLabel : GoLabel(s.Stage);

    /// <summary>The left-hand button when the last question is on the screen. It is about TIMING
    /// and not about the preference: "Look inside my files" was answered on the first act and is
    /// saved either way, so this declines to start in this session rather than turning anything
    /// off. "Not now" would read as the second.</summary>
    public const string LaterLabel = "Later";

    public const string StartReadingLabel = "Start reading";

    /// <summary>
    /// Is the finished screen asking whether to start reading inside files now?
    ///
    /// <para>Only at the end, and only when the switch on the first act said yes. The first act
    /// asks what Findra should be able to UNDERSTAND - the models - and there is no room on it for
    /// the one warning this question needs, which is that a first pass walks every drive and can
    /// run for hours. The last act has the room and is the moment: the download is done, nothing
    /// is competing for the disk, and the person is still here.</para>
    ///
    /// <para><b>Nothing reads until this is answered.</b> That is the whole point of asking, and it
    /// is also why the indexer no longer starts ten seconds after the first act while 2.9 GB is
    /// still coming down the wire - the two were reading and writing the same disk at once, and the
    /// indexing rate fell from 57 files a minute to 9 while the download ran.</para>
    /// </summary>
    public static bool Asks(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Stage == FirstRunStage.Finished && s.ContentOn;
    }

    /// <summary>The question itself, and the warning that is the reason for asking it at all. Here
    /// rather than in the painter because the layout has to measure them to know how tall the
    /// screen is, and a second copy in the painter is how the two come to disagree.</summary>
    public const string AskTitle = "Shall Findra start reading inside your files now?";

    public const string AskNote =
        "It walks every drive and reads what it finds, which can take a few hours the first time, " +
        "and it only reads while Findra is open. Settings, under Content, starts it whenever you like.";

    /// <summary>The five choices, in <see cref="TranscribeLimit.Presets"/> order, so an option
    /// index is an index into that list and there is nothing to keep in step.</summary>
    public static IReadOnlyList<string> LimitOptions { get; } =
        [.. TranscribeLimit.Presets.Select(TranscribeLimit.ShortName)];

    /// <summary>
    /// The row the transcription limit is drawn under, or -1 where it is not drawn at all.
    ///
    /// <para>It appears with Speech and goes with it, because it is Speech's setting and a
    /// control for a capability nobody took is a question with no subject. Under Speech and above
    /// the Hebrew row: Hebrew is a second pass over the same recordings, so a limit drawn below it
    /// would read as the fine-tune's alone.</para>
    /// </summary>
    public static int LimitRow(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.Chosen.Contains(Capability.Speech)) return -1;
        IReadOnlyList<FirstRunRow> rows = Rows(s);
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Capability == Capability.Speech) return i;
        return -1;
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

            case FirstRunTarget.Limit:
                // Bounds-checked, like the row arm above: a hit carries an index, an index can be
                // wrong, and indexing the presets with a wrong one is an unhandled exception on a
                // screen whose only other way out is the task manager.
                return hit.Index < 0 || hit.Index >= TranscribeLimit.Presets.Count
                    ? s
                    : s with { TranscribeMinutes = TranscribeLimit.Presets[hit.Index] };

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

            // "2 of 4 done" could be anything, and content indexing - a different, later and far
            // longer job - may be about to start on the same machine. The word that distinguishes
            // them goes in front of the count while the count is still moving.
            string doing = s.Stage == FirstRunStage.Downloading ? "Downloading model files. " : "";

            // And the end of the run says it has ended, and where the way out is. A finished run
            // that reported only its count left somebody looking at "1 of 1 done" with no reason
            // to believe anything more was going to happen.
            // "you can close this window" is right when closing is the only thing left, and wrong
            // when there is a question underneath it - a sentence telling somebody to leave, six
            // lines above the one thing on the screen still worth answering.
            string over = s.Stage == FirstRunStage.Finished && s.Problem.Length == 0 && current is null
                // Not "Findra is ready" - the title says that, two inches above, and a screen
                // that says the same thing twice in two registers reads as one thing said badly.
                ? Asks(s) ? " Everything you chose has arrived."
                          : " Everything you chose has arrived, and you can close this window."
                : "";

            return $"{doing}{done.ToString(Fixed)} of {s.Downloads.Count.ToString(Fixed)} " +
                   $"done{which}.{trouble}{over}{tail}";
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
            // Whether or not the row was on the screen. Somebody who took no speech keeps the
            // default they never saw, which is the same number they would have kept anyway.
            TranscribeMinutes = s.TranscribeMinutes,
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
    ///
    /// <para><b>A file finished is credited its whole declared size, not the bytes that moved.
    /// </b> The declared table is the specification's figure in megabytes to one decimal place,
    /// and two of the seven real files are SMALLER than it - the vision tower by 42,692 bytes and the
    /// Hebrew fine-tune by 3,521. Crediting what arrived leaves those two capabilities permanently
    /// 0.012% short, so a complete Everything install reads "2 of 4 done" with every bar
    /// visually full and nothing ever moving again. <see cref="ModelStore.SizeMatchesDeclared"/>
    /// is the existing answer to "is a file this long that file", asked here for the same reason
    /// the downloader and the report ask it; the clamp above stays for the four files that miss
    /// upward.</para>
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
                if (!wanted.Contains(m.File)) { got += m.Bytes; continue; }   // already on disk

                long n = moved.TryGetValue(m.File, out long moved_) ? moved_ : 0;
                got += ModelStore.SizeMatchesDeclared(m.Bytes, n) ? m.Bytes : Math.Min(n, m.Bytes);
            }
            if (total > 0) bars.Add(new CapabilityProgress(c, got, total));
        }
        return bars;
    }

    private static string Note(Capability c) => c switch
    {
        Capability.Photos => "Find a picture by what is in it, and a video by what a frame looks like.",
        Capability.Meaning => "Find a document by what it means, not only by the words it uses.",
        // "recordings" alone is what left somebody asking whether video was included, with a row
        // directly above called "Photos and video" making video look like somebody else's
        // business. This is the row that introduces transcription, so it is where video is named.
        Capability.Speech => "Transcribe what is said in recordings and videos. Uses the document models too.",
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
/// <para><b>This diverges from the model downloader deliberately, and it is written down here
/// because both halves have to be findable from each other.</b> <c>GetAllAsync</c> documents
/// itself as stopping at the first failure - "a set half fetched is resumable, and pressing on
/// after a network fault only turns one failed file into six" - which is right for
/// <c>findra --models install</c>, a command somebody is watching in a terminal. This screen is
/// not being watched; it is closable to the tray and expected to survive a dropped connection
/// (spec §6), and one bad mirror must not cost the other capabilities. Two surfaces, two
/// defensible policies, and the difference is stated rather than discovered.</para>
///
/// <para>It therefore loops <see cref="ModelDownloader.GetAsync"/> itself rather than calling
/// <c>GetAllAsync</c>, because it owns a policy the downloader does not have: <b>a fetch that
/// throws becomes a reported outcome, and the set carries on</b>. <c>GetAllAsync</c> catches
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
