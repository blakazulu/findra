using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Findra.Pipe;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Findra.Startup;

namespace Findra;

// Findra has no main window. It lives in the tray with a capsule on the desktop, so the
// desktop lifetime's default ShutdownMode (OnLastWindowClose) is wrong here - it would quit
// the whole application the moment the search card, which is not the "main window", is
// dismissed. OnExplicitShutdown means the process only exits when something asks it to
// (tray "Quit", or a future Shutdown() call), never as a side effect of a window closing.
public sealed class App : Application
{
    private Shell? _shell;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Nothing here is allowed to throw out of framework initialisation: a failure inside
            // the shell has to leave a log line and a running process, not a launch that dies
            // before anything is on screen to explain itself.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _shell = new Shell(this, desktop);
                    _shell.Start();
                }
                catch (Exception ex) { Log.Error("startup", "the shell could not start", ex); }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Where the card lands when it is opened from the hotkey rather than from the capsule:
/// centred on the working area, about a third of the way down, which is where the eye already is.
/// Pure, so the arithmetic has a test even though the window it positions cannot have one.</summary>
public static class CardPlacement
{
    public const double FromTop = 0.28;

    /// <summary>The card at its worst case, in physical pixels: the width it always has, and the
    /// height it reaches once a full page of results lands. Placement has to reserve that height
    /// up front, because the card grows in place and is never moved again - clamping against the
    /// empty card puts the grown one off the bottom of a short screen.</summary>
    public static PixelSize GrownSize(double zoom, double scaling) => new(
        (int)Math.Round(SearchCardLayout.Width * zoom * scaling),
        (int)Math.Round(SearchCardLayout.Height(SearchCardLayout.MaxRows, true) * zoom * scaling));

    public static PixelPoint Centred(PixelRect workingArea, int width, int height)
    {
        int x = workingArea.X + (workingArea.Width - width) / 2;
        int y = workingArea.Y + (int)Math.Round(workingArea.Height * FromTop);
        return CapsulePlacement.Clamp(new PixelPoint(x, y), workingArea, width, height);
    }

    /// <summary>Where a card opened from the hotkey or the tray goes: centred, and kept inside the
    /// monitor at the size it will have with results in it, which is the same worst case
    /// <see cref="CardWindow.PlaceOver"/> already clamps the capsule-opened card against.</summary>
    public static PixelPoint CentredGrown(PixelRect workingArea, double zoom, double scaling)
    {
        PixelSize grown = GrownSize(zoom, scaling);
        return Centred(workingArea, grown.Width, grown.Height);
    }
}

/// <summary>
/// What Findra remembers about updates between launches, and the words that state wears.
///
/// A check runs at most once a day, so on the other twenty-three launches in twenty-four there is
/// no live answer to show. The tag the last successful check returned is kept in the config and
/// turned back into a state here - pure, so both the state and the menu wording have tests.
/// </summary>
public static class UpdateMemory
{
    /// <summary>The state to open with, from the tag the last successful check recorded.</summary>
    public static UpdateState Remembered(string running, string? latestKnown)
    {
        if (string.IsNullOrWhiteSpace(latestKnown)) return UpdateState.NotDue;

        // Compare returns zero both for "the same version" and for "one of these did not parse",
        // so an unparseable remembered tag would otherwise read as "up to date" - a claim made on
        // no information, which spec 9b calls worse than not checking at all.
        string trimmed = latestKnown.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V')) trimmed = trimmed[1..];
        if (!Version.TryParse(trimmed, out _)) return UpdateState.Unknown;

        return UpdateCheck.Compare(running, latestKnown) < 0 ? UpdateState.Available : UpdateState.Current;
    }

    /// <summary>What the tray's "Check for updates" item says once a check the user asked for has
    /// come back. A menu item that never changes leaves a click looking like it did nothing.</summary>
    public static string CheckedHeader(UpdateState state, string? latest) => state switch
    {
        UpdateState.Available when !string.IsNullOrWhiteSpace(latest) => $"Checked: {latest} available",
        UpdateState.Current => "Checked: up to date",
        UpdateState.Disabled => "Update checks are turned off",
        _ => "Checked: could not reach GitHub",
    };
}

/// <summary>
/// Everything that has to exist for Findra to be running: the settings, the palette in force, the
/// elevated name helper, the capsule, the global hotkey, the tray, and the once-a-day update check.
///
/// Every stage is wrapped on its own. A machine with no shell to hold a tray icon, or one where no
/// hotkey combination is free, is a degraded Findra that still starts and still says what it lost.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Shell : ISettingsHost
{
    /// <summary>The zoom the capsule and the card are painted at. This is NOT the monitor's DPI:
    /// Avalonia's drawing context already carries the render scaling, so multiplying by it here
    /// would apply it twice and draw a capsule half again too big. A user-facing zoom setting
    /// lands with the settings surface; until then it is one.</summary>
    private const double Zoom = 1.0;

    /// <summary>Long enough that the check never competes with the first frame. It is not on a
    /// keystroke, not on a query, and it blocks nothing whatever it returns.</summary>
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(2);

    // One client for the process. The only request Findra ever makes off this machine goes
    // through it (spec 9b).
    private static readonly HttpClient Http = UpdateCheck.CreateClient();

    /// <summary>Cancelled when Findra quits. An update check in flight at that moment is abandoned
    /// without stamping the day as checked, so the next launch still asks.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    private Config _config = Config.Default;
    private Palette _palette = Palette.DefaultDark;

    private CapsuleWindow? _capsule;
    private HotkeyHost? _hotkey;
    private TrayIcon? _tray;
    private NativeMenuItem? _showCapsuleItem;
    private NativeMenuItem? _checkForUpdatesItem;
    private CardWindow? _card;

    private UpdateState _update = UpdateState.NotDue;
    private string? _latest;

    // ---- what the settings window and the capsule menu are shown ----
    //
    // Three facts about the machine that are NOT settings, kept here because the only places that
    // can see them are the content loop and the pipe session - and both of those run off the
    // interface thread, where neither surface may reach. Written there, read here, and nothing
    // else depends on them, so a stale read costs one repaint of a status line.
    private volatile bool _everIndexed;
    private volatile bool _indexerAlive;
    private IReadOnlyList<string> _drives = [];

    // ---- the content index ----
    //
    // TWO CONNECTIONS, ONE FILE. ContentDb wraps a single SQLite connection, which is not safe for
    // concurrent use, and in this process the card reads the index on its own thread while the
    // feeder writes it from the background loop. Write-ahead logging gives one writer and many
    // readers ACROSS connections, so the split is the design's own answer: the feeder owns the
    // writer, the card gets a read-only connection of its own. A shared lock would have been the
    // other answer and the wrong one - it would stall the card behind a long indexing write, which
    // is the one moment the card most needs to answer.
    private ContentDb? _content;        // the writer. Opened FIRST: it creates the schema.
    private ContentDb? _cardStore;      // read-only, lent to every card, disposed by this shell

    // The query encoders, opened once for the process and lent to every card exactly as the
    // read-only store is - an encoder is a hundred milliseconds and a hundred megabytes, and a
    // card is opened dozens of times a day. Null is the ordinary state of a machine that took no
    // model, and it is what makes "just names" a working product rather than a broken one.
    private Semantic? _semantic;
    // What is installed, read from the disk ONCE. It is a fact about files, not a setting, and
    // re-reading it per keystroke would stat seven paths for every letter typed.
    private CapabilitySet _installed;
    private QueueFeeder? _feeder;
    private IndexerHost? _indexer;
    private Task? _contentLoop;

    // Work that has to run against the WRITER connection but is asked for from somewhere else -
    // a download continuation, a settings click. ContentDb.Claim is a thread-id detector rather
    // than a lock, so there is no safe way to touch the writer off the content loop; this hands
    // the work to the loop that owns it and waits for the answer. Drained once a second by the
    // same pump that keeps the indexer's control rows up to date.
    private readonly ConcurrentQueue<PostedWork> _contentWork = new();

    private sealed record PostedWork(Func<ContentDb, int> Work, TaskCompletionSource<int> Done);

    /// <summary>
    /// Run <paramref name="work"/> on the flow that owns the index's writer connection, and give
    /// back what it returned.
    ///
    /// <para>Answers 0 immediately when there is no loop to run on. That is a real state rather
    /// than a defensive one: the first-run screen is answered BEFORE the content index is opened,
    /// so a download that finished in the seconds between the two has nowhere to post to - and it
    /// costs nothing, because <see cref="OpenContentIndex"/> runs the same gate itself as it
    /// starts. A task that never completed would instead leave the download run waiting for ever
    /// on its own re-queue.</para>
    /// </summary>
    private Task<int> OnContentLoopAsync(Func<ContentDb, int> work)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_content is null || _contentLoop is null || _contentLoop.IsCompleted)
        {
            Log.Info("index", "the content index is not open on this flow yet; " +
                              "the re-queue is planned again the next time Findra starts");
            done.SetResult(0);
            return done.Task;
        }

        _contentWork.Enqueue(new PostedWork(work, done));
        // Bounded by the shutdown token as well as by the loop, so quitting mid-download does not
        // leave a continuation parked on a queue nobody will drain again.
        return done.Task.WaitAsync(_shutdown.Token);
    }

    /// <summary>Everything posted since the last pump, on the loop's own thread. A throw is the
    /// caller's answer rather than the loop's death: the queue, the index and every stamp survive,
    /// and the next start plans exactly the same work.</summary>
    private void DrainContentWork(ContentDb db)
    {
        while (_contentWork.TryDequeue(out PostedWork? posted))
        {
            try { posted.Done.TrySetResult(posted.Work(db)); }
            catch (Exception ex)
            {
                Log.Error("index", "work posted to the content loop failed", ex);
                posted.Done.TrySetException(ex);
            }
        }
    }

    public Shell(Application app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _app = app;
        _desktop = desktop;
    }

    /// <summary>True once the welcome screen is actually on the display. Everything else waits
    /// for it, so a screen that never appeared has to be told apart from one that is up: the
    /// first is a launch that must carry on, the second a launch that must not.</summary>
    private bool _firstRunIsUp;

    /// <summary>What was held back has been built. The answer can only arrive once - the screen
    /// guards that itself - but a second call here would be a second tray icon over one process.
    /// </summary>
    private bool _restIsBuilt;

    public void Start()
    {
        Stage("settings", () =>
        {
            _config = Config.LoadFromDisk();

            // Once, and then never again (spec §9b). The marker file can be lost - an antivirus
            // quarantine, a repair - and the truth about how this copy arrived cannot change, so a
            // recorded answer always wins over a fresh look.
            if (string.IsNullOrWhiteSpace(_config.InstallSource))
            {
                string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                _config = _config with { InstallSource = InstallSource.Detect(dir) };
                _config.Save();
                Log.Info("startup", $"install source recorded as {_config.InstallSource}");
            }

            IReadOnlyList<Palette> palettes = PaletteStore.LoadFromDisk();
            // Follow Windows is read once, here. Switching the palette live when Windows flips
            // between light and dark mid-session lands with the settings surface in a later plan.
            _palette = Theme.Resolve(_config, Theme.WindowsIsLight(), palettes);
            Log.Info("startup", $"palette '{_palette.Name}' ({(_palette.Light ? "light" : "dark")}), mode {_config.Mode}");

            // What the last successful check found, so a launch that is not due for one still says
            // whether an update is waiting instead of going quiet for the rest of the day.
            _latest = _config.LatestKnownVersion;
            _update = UpdateMemory.Remembered(Log.Version, _latest);
            if (_update is UpdateState.Available or UpdateState.Current)
                Log.Info("startup", _update is UpdateState.Available
                    ? $"the last check found {_latest}, which is newer than {Log.Version}; the tray says so"
                    : $"the last check found {_latest}: up to date");
        });

        bool firstRun = !_config.FirstRunDone;
        foreach (StartupStep step in StartupOrder.Immediate(firstRun)) Run(step);

        // The screen is up and owns the display. Everything else is built from the answer, in
        // OnFirstRunAnswered - because Show() does not block, and carrying on here is what used
        // to put a capsule, a tray icon and a global hotkey behind a window somebody was still
        // reading.
        if (firstRun && _firstRunIsUp) return;

        // The screen was wanted and is not there. Every stage is wrapped, so this is a real path
        // rather than a defensive one, and the answer that would have built the rest is never
        // coming: build it here instead of leaving a process with nothing on screen at all.
        if (firstRun)
        {
            Log.Warn("startup", "the first screen could not be shown; starting the rest of Findra without it");
            foreach (StartupStep step in StartupOrder.WhenTheScreenCouldNotBeShown()) Run(step);
        }
    }

    /// <summary>
    /// One stage. The default arm THROWS rather than returning quietly: a step added to
    /// <see cref="StartupStep"/> and forgotten here is a stage that silently stops happening, and
    /// on this list that is the tray icon, the hotkey or the content index simply not existing.
    /// </summary>
    private void Run(StartupStep step)
    {
        switch (step)
        {
            // Before the capsule, the tray and the content loop. It decides whether there is
            // anything for the content loop to do, and it is where the one elevated thing Findra
            // needs gets registered - which nothing in the tree did before it existed.
            case StartupStep.FirstRun:
                Stage("first run", ShowFirstRun);
                return;

            // EnsureRunning asks the scheduler to start the helper and then waits up to five
            // seconds for the pipe to answer. On the UI thread that is five seconds of nothing on
            // screen.
            case StartupStep.NamesHelper:
                Stage("names helper", () => _ = Task.Run(() =>
                {
                    bool up = HelperTask.EnsureRunning();
                    Log.Info("startup", up
                        ? "the names helper is answering"
                        : "the names helper is not answering; name search will be empty until it is registered");
                }));
                return;

            case StartupStep.Hotkey:
                Stage("hotkey", () =>
                {
                    var host = new HotkeyHost();
                    host.Pressed += () => Dispatcher.UIThread.Post(() => OpenCentred(fromClick: false));
                    // Owned before it is started: Start creates a real window, and a throw inside
                    // it would otherwise leave that window with nobody holding it and nobody to
                    // Dispose it.
                    _hotkey = host;
                    host.Start(HotkeyChain.Build(_config.Hotkey, Hotkey.DefaultChain));
                    UiStatus.Write(Environment.ProcessId, host.Landed);
                });
                return;

            case StartupStep.Capsule:
                Stage("capsule", () =>
                {
                    if (_config.ShowCapsule) CreateCapsule();
                    else Log.Info("app", "the capsule is turned off; the hotkey and the tray open the card");
                });
                return;

            case StartupStep.ContentIndex:
                Stage("content index", OpenContentIndex);
                return;

            case StartupStep.Tray:
                Stage("tray", CreateTray);
                return;

            case StartupStep.UpdateCheck:
                Stage("update check", () => _ = Task.Run(async () =>
                {
                    await Task.Delay(UpdateCheckDelay).ConfigureAwait(false);
                    await RunUpdateCheck(force: false).ConfigureAwait(false);
                }));
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(step), step, "no startup stage arm for this value");
        }
    }

    /// <summary>Build everything the welcome screen held back. Once: a second call would be a
    /// second tray icon and a second capsule over one process.</summary>
    private void StartTheRest()
    {
        if (_restIsBuilt) return;
        _restIsBuilt = true;
        foreach (StartupStep step in StartupOrder.AfterTheScreenIsAnswered()) Run(step);
    }

    private static void Stage(string what, Action body)
    {
        try { body(); }
        catch (Exception ex) { Log.Error("startup", $"the {what} stage failed", ex); }
    }

    // ---- the first screen ---------------------------------------------------------------------

    /// <summary>
    /// Spec §6's screen, shown once, before anything else has been built.
    ///
    /// <para><b>It owns the display until it is answered.</b> Not modal in code - nothing blocks a
    /// thread - but nothing else is built while it is up, and <see cref="OnFirstRunAnswered"/> is
    /// what carries the launch on. It used to be shown and not waited for, and since <c>Show()</c>
    /// does not block, the hotkey, the capsule and the tray were all built behind it: pressing
    /// "Get these" landed in a product that was already running, which made the download somebody
    /// had just asked to watch read as a window in the way of it.</para>
    ///
    /// <para>Closing the screen mid-download still leaves the download running and Findra in the
    /// tray - the screen says so itself. That stays true and is now a choice rather than the only
    /// behaviour.</para>
    /// </summary>
    private void ShowFirstRun()
    {
        var state = new FirstRunState
        {
            // Only where there is Hebrew. A 1.5 GB row is a decision, and a Thai machine should
            // not have to make it.
            HebrewOffered = Capabilities.HebrewIsOffered(Capabilities.SystemLanguages()),
            ContentOn = _config.IndexContent,
            // The limit as it stands, not the default: a reinstall over an existing config must
            // not show five minutes to somebody who chose two hours the last time.
            TranscribeMinutes = _config.TranscribeMinutes,
            CheckUpdates = _config.CheckForUpdates,
            // What is actually in the Run key, not a default: a reinstall over an existing entry
            // must not show the switch off while the entry is there.
            StartAtLogon = Autostart.IsSet(),
        };

        var window = new FirstRunWindow(state, _palette);
        window.Answered += answer => OnFirstRunAnswered(window, answer);
        window.Show();
        // AFTER Show, because this is what tells Start that the screen really is on the display
        // and the rest of the launch may wait for an answer. Set before it, a window that threw on
        // its way up would leave a process with nothing on screen and nothing to answer.
        _firstRunIsUp = true;
        Log.Info("startup", "the first-run screen is up and has the display; " +
                            "nothing is downloaded and nothing else is built until it is answered");
    }

    /// <summary>
    /// The answer, and everything that follows from it.
    ///
    /// <para>The registration is the part that matters most and the part that is easiest to read
    /// as optional: it happens whatever was chosen, INCLUDING "Not now", because searching by name
    /// is the half of Findra that is always on and it does not work at all without the scheduled
    /// task.</para>
    /// </summary>
    private void OnFirstRunAnswered(FirstRunWindow window, FirstRunState answer)
    {
        _config = FirstRun.Outcome(answer, _config);
        _config.Save();
        Log.Info("startup", "the first-run screen was answered: " +
                            (answer.Chosen.Count == 0
                                ? "no capabilities"
                                : string.Join(", ", answer.Chosen.Select(Capabilities.Title))) +
                            ", looking inside files " + (_config.IndexContent ? "on" : "off") +
                            ", update checks " + (_config.CheckForUpdates ? "on" : "off") +
                            ", transcribing up to " + TranscribeLimit.Describe(_config.TranscribeMinutes));

        string exe = Environment.ProcessPath ?? "";
        if (answer.StartAtLogon) Autostart.Set(exe); else Autostart.Clear();

        // The names helper first, and immediately: it is the one thing that does not wait for the
        // download, because searching by name is what works with nobody's models and nobody should
        // wait on a 1.5 GB file for their filenames.
        RegisterAndStartHelper(exe);

        // And now the rest of Findra - the hotkey, the capsule, the content index, the tray and
        // the update check - which have been waiting for this answer rather than appearing behind
        // the screen while it was being read.
        StartTheRest();

        IReadOnlyList<Model> wanted = FirstRun.Wanted(answer);
        // Always, even when it is empty: the bars are drawn from what is NOT being fetched as
        // much as from what is, so a selection already on disk shows full rather than "0 of 2".
        window.NoteFetching(wanted);
        if (wanted.Count == 0)
        {
            // Everything chosen is already here - a reinstall, or somebody who took the same
            // capabilities from `--models install` first. The startup gate has already planned
            // whatever they owe the index, so there is nothing to fetch and nothing to queue.
            Log.Info("models", "everything chosen is already on disk; nothing was fetched");
            window.NoteFinished("");
            return;
        }

        _ = FetchFirstRunAsync(window, wanted);
    }

    /// <summary>
    /// Register the scheduled task and START it, in this session.
    ///
    /// <para>The second half is the one nothing in the tree had. <c>Start</c>'s <c>names helper</c>
    /// stage already ran <c>EnsureRunning</c>, before this screen was answered, against a task that
    /// did not exist - so without this the task is created and the helper does not run until the
    /// next logon. Name search, the part that is always on and the thing that makes "Not now" a
    /// safe answer, would be dead for the whole of somebody's first session with Findra.</para>
    ///
    /// <para>Off the interface thread, because <c>HelperTask.Register</c> raises the UAC prompt
    /// itself (<c>UseShellExecute</c> with <c>runas</c>) and then waits for it. A refused prompt
    /// throws <c>Win32Exception 1223</c> into Register's own catch and returns false - a decision,
    /// not a fault, so it is logged and the recovery is left to Settings.</para>
    /// </summary>
    private static void RegisterAndStartHelper(string exe) => _ = Task.Run(() =>
    {
        bool registered = !FirstRun.NeedsHelperRegistration(HelperTask.Query().State)
                          || HelperTask.Register(exe);

        if (registered && HelperTask.EnsureRunning())
            Log.Info("startup", "the names helper is answering");
        else
            Log.Warn("startup", "the names helper is not answering; " +
                                "Settings > Opening it can register it again");
    });

    /// <summary>
    /// The first-run download, from the interface, with its progress on the screen that asked for
    /// it.
    ///
    /// <para>Awaited inside a task whose exceptions are CAUGHT here rather than left on an
    /// unobserved <c>Task</c>, which is the whole point: a dropped network raises an
    /// <c>HttpRequestException</c> that <c>ModelDownloader</c> does not catch, and on screen an
    /// unobserved throw is a progress bar that simply stops.</para>
    /// </summary>
    private async Task FetchFirstRunAsync(FirstRunWindow window, IReadOnlyList<Model> wanted)
    {
        string dir = ModelStore.Dir;
        Log.Info("models", $"first run: fetching {wanted.Count.ToString(CultureInfo.InvariantCulture)} file(s), " +
                           $"{Sizes.Human(ModelStore.TotalBytes(wanted))}, into {dir}");
        string problem = "";
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,   // a 1.5 GB file over a slow line is not a hung request
            };

            IReadOnlyList<DownloadOutcome> outcomes = await FirstRunDownloads.RunAsync(
                wanted, dir, ModelDownloader.Http(http),
                p => Dispatcher.UIThread.Post(() => window.NoteProgress(p)),
                RequeueWhatArrivedAsync, _shutdown.Token).ConfigureAwait(false);

            foreach (DownloadOutcome o in outcomes)
                if (!o.Complete)
                {
                    Log.Warn("models", $"{o.Model.File} did not finish - {o.Problem}; what arrived is kept");
                    // The FIRST failure is the one shown. A screen carrying seven of them has no
                    // room for anything else, and they are all the same fault seen seven times.
                    if (problem.Length == 0) problem = o.Problem ?? "the download did not finish";
                }
        }
        catch (OperationCanceledException)
        {
            // Findra quitting, not a fault: the .part files stay and the next run resumes.
            Log.Info("models", "the download stopped when Findra quit; what arrived is kept");
            return;
        }
        catch (Exception ex)
        {
            Log.Error("models", "the first-run download could not run", ex);
            problem = ex.Message;
        }

        Dispatcher.UIThread.Post(() => window.NoteFinished(problem));
    }

    /// <summary>
    /// Re-queue what a newly-arrived capability can now read, ON THE FLOW THAT OWNS THE WRITER.
    ///
    /// <para>This is why <c>FirstRunDownloads.RunAsync</c> takes a callback and no
    /// <c>ContentDb</c> at all.
    /// <c>ContentDb.Claim</c> is a thread-id detector rather than a lock, so the second flow to
    /// reach the writer connection gets an <c>InvalidOperationException</c> - and a download
    /// continuation calling <c>CapabilityGate.Apply</c> directly would be that second flow,
    /// throwing inside a handler nobody is watching. <see cref="OnContentLoopAsync"/> hands the
    /// work to the loop that holds the connection and waits for its answer.</para>
    /// </summary>
    private async Task RequeueWhatArrivedAsync()
    {
        CapabilitySet installed = CapabilitySet.Installed(ModelStore.Dir);
        _installed = installed;

        int requeued = await OnContentLoopAsync(db =>
            CapabilityGate.Apply(db, CapabilityGate.Plan(installed, CapabilityGate.StampsIn(db))))
            .ConfigureAwait(false);

        Log.Info("models", requeued > 0
            ? $"{requeued.ToString("N0", CultureInfo.InvariantCulture)} file(s) were queued for what Findra can now read"
            : "nothing new to queue - the files these models cover have already been read");

        CapabilitySet shown = installed;
        Dispatcher.UIThread.Post(() => SettingsWindow.Open?.UseInstalled(shown));
    }

    // ---- the content index ------------------------------------------------------------------------

    /// <summary>How long one journal batch is allowed to gather before it is written.</summary>
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(400);

    /// <summary>Or this many events, whichever comes first.</summary>
    private const int MaxEventsPerBatch = 500;

    /// <summary>How many files one enumerate frame carries. Large enough that a real disk is a few
    /// hundred frames rather than tens of thousands, small enough that no single frame is a
    /// megabyte of JSON.</summary>
    private const int EnumerateBatch = 1000;

    /// <summary>Between attempts to reach the helper, and the floor between two walks of the same
    /// volume. A walk that keeps failing must cost one attempt a minute, not a busy loop.</summary>
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WalkNoOftenThan = TimeSpan.FromSeconds(60);

    /// <summary>How often the indexer's control rows and the capsule's line are refreshed while
    /// this loop is waiting for a helper it cannot reach. One small read of a local file a second
    /// is what the card already costs itself; a capsule that only learns the queue moved every
    /// thirty seconds looks stuck for twenty-nine of them.</summary>
    private static readonly TimeSpan PumpEvery = TimeSpan.FromSeconds(1);

    /// <summary>How often a session that keeps failing is allowed to say so again. Every retry is
    /// a line every thirty seconds; once per process is the silence that hides a fault for a whole
    /// session. Five minutes is roughly ten attempts per line, which reads as "still broken"
    /// without burying anything else in the log.</summary>
    private static readonly TimeSpan SessionFailureEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Record that one attempt to feed the queue from the journal failed, and return the running
    /// total. Accumulated in the index, like <c>journal:dropped</c>, so it survives the process
    /// that could not reach the helper - <c>--searchindex</c> is usually run from a different
    /// terminal, after the fact, by somebody asking why nothing is being indexed.
    ///
    /// <para>Never fatal. This is the failure path; a store that cannot record it must not turn a
    /// retry into a crash.</para>
    /// </summary>
    private static long NoteSessionFailed(ContentDb db, Exception ex)
    {
        try
        {
            long before = long.TryParse(db.Get("index:sessionfailures"), NumberStyles.Integer,
                                        CultureInfo.InvariantCulture, out long had) ? had : 0;
            long now = before + 1;
            db.Set("index:sessionfailures", now.ToString(CultureInfo.InvariantCulture));
            db.Set("index:sessionfailure", ex.GetType().Name + ": " + ex.Message);
            return now;
        }
        catch { return 0; }
    }

    /// <summary>
    /// A repository is found by the one file every git checkout has in the same place. The
    /// enumerate call never returns directories - a folder whose name ends in a suffix is exactly
    /// what its own test forbids - so asking for ".git" would come back empty every time; asking
    /// for the marker file inside it and taking the grandparent finds the same roots and costs the
    /// same single pass.
    /// </summary>
    private static readonly string[] RepoMarkerSuffix = ["HEAD"];
    private const string RepoMarkerTail = @"\.git\HEAD";

    /// <summary>
    /// Open the process's stores and start the loop that decides what is indexed.
    ///
    /// ORDER MATTERS. The writer is opened first because it is what creates the file, the schema
    /// and any migration; a read-only open against a database that does not exist yet fails, and
    /// the card would then spend the session saying the index is not open.
    /// </summary>
    private void OpenContentIndex()
    {
        ContentDb writer = ContentDb.OpenOrRebuild();
        _content = writer;

        // Recorded in the index itself, not just on this instance. The card reads through its OWN
        // connection, and WasRebuilt is a fact about the open that rebuilt the file - it cannot
        // cross a connection boundary. Written on every launch, so the notice is about THIS
        // session and does not haunt every later one.
        try { writer.Set("index:rebuilt", writer.WasRebuilt ? "1" : "0"); } catch { }

        if (writer.WasRebuilt)
            Log.Error("index", $"the content index could not be read and was rebuilt from nothing at {writer.Path}; " +
                               "what was indexed before is gone and the disk is walked again");

        // The card's own read-only connection. Write-ahead logging is what makes a second
        // connection safe while the feeder writes; an in-memory store has no file to open twice,
        // and a read-only ":memory:" would be a different, empty database.
        if (!string.Equals(writer.Path, ":memory:", StringComparison.Ordinal))
        {
            try { _cardStore = new ContentDb(writer.Path, readOnly: true); }
            catch (Exception ex)
            {
                // A null store is a supported state: the card says the index is not open in this
                // session rather than showing an empty one.
                Log.Warn("index", "the card gets no read-only view of the index this session :: " + ex.Message);
            }
        }

        // The model-backed half of a content query. Read once, opened once, and both are cheap on
        // a machine that took nothing: Installed() stats seven paths and finds none, and Open()
        // returns null before it constructs a session. Only somebody who actually downloaded a
        // capability pays for the load, and they pay for it once for the whole session rather
        // than on the first query - which is where a lazy load would put it, in front of a person
        // who has already typed.
        _installed = CapabilitySet.Installed();
        _semantic = Semantic.Open(_installed);
        Log.Info("models", _semantic is null
            ? "no query encoder this session - content search answers with the words in your files"
            : "query encoders ready: " + (_semantic.Text is null ? "" : "meaning ") + (_semantic.Image is null ? "" : "pictures"));

        // Before the content loop, and this is not a stylistic choice. QueueFeeder holds the
        // writer across a whole ContentDb.Scope, and ContentDb.Claim is a thread-id detector
        // rather than a lock: whichever flow arrives second gets an InvalidOperationException.
        // Running the gates here means there is no second flow yet.
        //
        // Once at startup, and here rather than anywhere else. A capability installed by
        // `--models install` is applied by that process on its own connection; one that arrives
        // while Findra is open is applied by RequeueWhatArrivedAsync, on this same flow. Either
        // way the indexer child reads it: it looks at what is on disk before every file it opens.
        //
        // What this start still owes is anything an earlier one queued and nothing could read.
        // The stamp alone would say that debt was paid - StampsIn is where the index is asked
        // instead.
        try
        {
            int requeued = CapabilityGate.Apply(writer, CapabilityGate.Plan(
                _installed, CapabilityGate.StampsIn(writer)));
            requeued += CapabilityGate.ApplyLimit(writer, _config.TranscribeMinutes);
            if (requeued > 0)
                Log.Info("models", $"a change to what Findra can read queued {requeued.ToString("N0", CultureInfo.InvariantCulture)} file(s)");
        }
        catch (Exception ex)
        {
            // A backlog that could not be cleared is a capability that finds nothing until the
            // next launch, which is a bad session and not a broken install. The queue, the index
            // and every stamp survive, so the next start plans exactly the same work.
            Log.Error("models", "the re-queue for what Findra can now read did not run this session", ex);
        }

        // One bit, one row, written before anything reads it: the child, IndexStatus and
        // --searchindex all learn the switch from `index:paused`, and the card reads that row
        // rather than the config it has no access to.
        try
        {
            writer.Set("index:paused", _config.IndexContent ? "0" : "1");
            writer.Set(Indexer.TranscribeMinutesKey, _config.TranscribeMinutes.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) { Log.Warn("index", "the indexer's control rows could not be written :: " + ex.Message); }

        _feeder = new QueueFeeder(writer, () => _config);
        _indexer = new IndexerHost();
        _contentLoop = Task.Run(() => RunContentAsync(_shutdown.Token));

        Log.Info("index", $"the content index is open at {writer.Path}" +
                          (_cardStore is null ? " (writer only)" : " (one writer, one reader)"));
    }

    /// <summary>
    /// The loop that owns the queue. It reconciles what the rules now cover, keeps a subscription
    /// to the helper's journal, runs a first pass over any volume that owes one, and keeps the
    /// indexer child alive while there is work for it.
    ///
    /// Every failure here is a retry, never a throw: a machine with no helper registered still has
    /// a working queue, a running indexer and a card that can search what is already indexed.
    /// </summary>
    private async Task RunContentAsync(CancellationToken ct)
    {
        QueueFeeder? feeder = _feeder;
        ContentDb? db = _content;
        if (feeder is null || db is null) return;

        try
        {
            Log.Info("index", "the queue feeder is running; this process decides what is indexed");

            try { feeder.Reconcile(); }
            catch (Exception ex) { Log.Error("index", "the queue could not be reconciled at startup", ex); }

            while (!ct.IsCancellationRequested)
            {
                PumpIndexer(db);
                try { await OneSessionAsync(db, feeder, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // Recorded before it is logged, because a number in the index is the half a
                    // person can see without opening a log file: --searchindex prints it.
                    long failures = NoteSessionFailed(db, ex);

                    // NOT Log.Once. Keyed on the exception type, a session failing every thirty
                    // seconds for four hours reported itself in the first minute of the log and
                    // was silent for the rest of the process - which is exactly why this class of
                    // fault is hard to find. It says so again every few minutes, carrying what it
                    // held back, so "still broken" and "broken once" stop reading the same.
                    Log.Repeat("index|session", SessionFailureEvery, "WARN ", "index",
                        "the queue is not being fed from the journal (" +
                        failures.ToString("N0", CultureInfo.InvariantCulture) +
                        " failed session(s) so far) :: " + ex.GetType().Name + ": " + ex.Message);
                }
                // Pumped rather than slept through. With no helper registered there is no session
                // to run, so this is the whole of the loop - and the indexer child is still
                // draining whatever the queue holds. A capsule reporting a count from thirty
                // seconds ago reads as a stall, which is the exact impression spec §3 says the
                // widget must not give.
                bool cancelled = false;
                for (TimeSpan waited = TimeSpan.Zero; waited < RetryEvery; waited += PumpEvery)
                {
                    try { await Task.Delay(PumpEvery, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; break; }
                    PumpIndexer(db);
                }
                if (cancelled) break;
            }
        }
        finally
        {
            // This loop owns the disposal of both stores. Quit only cancels: it runs on the user
            // interface thread and cannot block there waiting for a walk to unwind, and a store
            // disposed under a write in flight is worse than one the process exit closes for us.
            // Every write here is a committed transaction, so nothing is lost either way.
            // Before the stores are disposed, and it is a cancel rather than a result: whoever
            // posted this is a download continuation, and telling it the re-queue ran when the
            // connection is about to close would be a lie it writes into the log.
            while (_contentWork.TryDequeue(out PostedWork? posted)) posted.Done.TrySetCanceled();

            try { _indexer?.Dispose(); } catch { }
            try { feeder.Dispose(); } catch { }
            try { _cardStore?.Dispose(); } catch { }
            try { _semantic?.Dispose(); } catch { }
            try { db.Dispose(); } catch { }
            Log.Info("index", "the content index is closed");
        }
    }

    /// <summary>One connection to the helper, from subscribe to the stream ending.</summary>
    private async Task OneSessionAsync(ContentDb db, QueueFeeder feeder, CancellationToken ct)
    {
        await using NameClient client = await NameClient.ConnectAsync(TimeSpan.FromSeconds(5), ct)
            .ConfigureAwait(false);

        // Beside the construction, because the counter belongs to the client and not to the
        // feeder: this one starts at zero, and a feeder still holding the last one's total would
        // wait for this session to lose MORE events than the last session ever did before it
        // recorded anything. It also hands the feeder the counter itself, so the decisions that
        // depend on it read it at the moment they are made rather than at the moment this loop
        // last thought to mention it.
        feeder.NoteSessionStarted(() => client.JournalDropped);

        StatusReply status = await client.StatusAsync(ct).ConfigureAwait(false);
        // Every volume the helper reports, not the subset this config chose: the settings window's
        // Drives row offers all of them and ticks the ones that are chosen, so a letter left out
        // here is a disk nobody can turn back on.
        _drives = [.. status.Volumes.Select(v => char.ToUpperInvariant(v.Letter).ToString())];
        IReadOnlyList<char> drives = ChosenDrives(_config, status);
        if (drives.Count == 0)
        {
            Log.Warn("index", "the helper reports no volumes to index");
            return;
        }

        // Everything below this line can run for minutes, and this is the flow that owns the
        // writer connection: the settings window's "Start reading now" posts work here, the
        // capsule's line is written here, and the indexer child is started here. All of that used
        // to happen only BETWEEN sessions, so a first pass over a fresh disk - which is the one
        // session everybody's first hour is spent inside - starved every one of them. Pressing
        // the button did nothing, said nothing and left no line in the log.
        //
        // Throttled here rather than in the walk, beside the interval the loop itself keeps, so
        // there is one answer to how often this flow comes round.
        long pumpedAt = 0;
        void Pump()
        {
            if (pumpedAt != 0 && Stopwatch.GetElapsedTime(pumpedAt) < PumpEvery) return;
            pumpedAt = Stopwatch.GetTimestamp();
            PumpIndexer(db);
        }

        // Learned before anything is judged, because a repository root changes what is eligible.
        // Reconciled again afterwards: the pass at startup ran before the helper was reachable and
        // therefore before any root was known.
        await LearnRepoRootsAsync(client, feeder, drives, Pump, ct).ConfigureAwait(false);
        feeder.Reconcile();

        // The pushed events go into this queue as they arrive, so nothing about the writing side
        // can stall the pipe. A stalled reader costs dropped events, which NoteClientDrops turns
        // into an owed walk; a stalled PUMP would cost every name query as well.
        //
        // Started BEFORE the first pass, not after it. A pass over a real disk takes long enough
        // that the client's bounded receive channel would otherwise fill and start evicting during
        // it - which owes a fresh walk, which is another pass, on a machine that is merely busy.
        // Nothing is subscribed yet, so this reads nothing until the pass below asks for it, and
        // the events it collects are consumed after the pass has stamped its position, which keeps
        // the position moving only forwards.
        var arrived = new ConcurrentQueue<JournalEvent>();
        var full = new SemaphoreSlim(0, 1);
        Task reader = Task.Run(async () =>
        {
            await foreach (JournalEvent e in client.JournalAsync(ct).ConfigureAwait(false))
            {
                arrived.Enqueue(e);
                if (arrived.Count >= MaxEventsPerBatch && full.CurrentCount == 0)
                    try { full.Release(); } catch (SemaphoreFullException) { }
            }
        }, CancellationToken.None);

        var walkedAt = new Dictionary<char, long>();
        await CatchUpAsync(client, feeder, drives, walkedAt, first: true, Pump, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested && !reader.IsCompleted)
        {
            try { await full.WaitAsync(FlushEvery, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var batch = new List<JournalEvent>();
            while (arrived.TryDequeue(out JournalEvent? e)) batch.Add(e);
            if (batch.Count > 0) feeder.Consume(batch);

            // A batch charges the counter itself, inside the transaction that moves the position
            // those events cover, so this call is the turn with NO events - a receive channel
            // that evicted everything it was handed leaves a hole and no batch to notice it.
            // After EVERY turn, not once at startup: a hole can open at any moment, from a reset
            // marker or from this process stalling under its own interface, and a debt inspected
            // only at launch is a debt nobody collects. It is one small read of the meta table.
            feeder.NoteClientDrops(client.JournalDropped);
            await CatchUpAsync(client, feeder, drives, walkedAt, first: false, Pump, ct).ConfigureAwait(false);

            PumpIndexer(db);
        }

        await reader.ConfigureAwait(false);
    }

    /// <summary>
    /// Walk every volume that owes a full pass. On the first turn that is whatever the subscribe
    /// reply says needs one; afterwards it is whatever has since come to owe one, which is checked
    /// cheaply and acted on rarely.
    /// </summary>
    private static async Task CatchUpAsync(NameClient client, QueueFeeder feeder, IReadOnlyList<char> drives,
                                           Dictionary<char, long> walkedAt, bool first, Action pump,
                                           CancellationToken ct)
    {
        var owed = new List<char>();
        foreach (char v in drives)
        {
            if (!first && !feeder.NeedsFreshWalk(v)) continue;
            // A walk that keeps failing leaves the debt standing, and this loop comes round every
            // 400 ms. One attempt a minute per volume, not two and a half a second.
            if (walkedAt.TryGetValue(v, out long last) &&
                System.Diagnostics.Stopwatch.GetElapsedTime(last) < WalkNoOftenThan) continue;
            owed.Add(v);
        }
        if (owed.Count == 0) return;

        // Re-subscribing gives the CURRENT journal id and position for every volume, which is what
        // a pass has to stamp. It also replaces this session's registration rather than doubling
        // it, and replays whatever gap the stored cursors left.
        SubscribeReply sub = await client.SubscribeJournalAsync(feeder.StoredCursors(), ct).ConfigureAwait(false);
        var resume = new Dictionary<char, VolumeResume>();
        foreach (VolumeResume r in sub.Volumes) resume[char.ToUpperInvariant(r.Volume)] = r;

        foreach (char v in owed)
        {
            if (!resume.TryGetValue(v, out VolumeResume? at)) continue;
            // On the first turn, a volume the helper can simply resume needs no pass: the session
            // has already replayed its gap, and those events arrive through the journal like any
            // other. On later turns the caller has already established that a walk is owed.
            if (first && !at.NeedsFullPass && !feeder.NeedsFreshWalk(v)) continue;

            walkedAt[v] = System.Diagnostics.Stopwatch.GetTimestamp();
            await FirstPass.WalkAsync(client, feeder, v, at, EnumerateBatch, pump, ct).ConfigureAwait(false);
        }
    }

    private static async Task LearnRepoRootsAsync(NameClient client, QueueFeeder feeder,
                                                  IReadOnlyList<char> drives, Action pump,
                                                  CancellationToken ct)
    {
        var roots = new List<string>();
        foreach (char v in drives)
        {
            await foreach (EnumeratedFile f in client
                               .EnumerateAsync(v, RepoMarkerSuffix, EnumerateBatch, ct)
                               .ConfigureAwait(false))
            {
                pump();
                if (!f.Path.EndsWith(RepoMarkerTail, StringComparison.OrdinalIgnoreCase)) continue;
                roots.Add(f.Path[..^RepoMarkerTail.Length]);
            }
        }
        feeder.SetRepoRoots(roots);
        Log.Info("index", string.Create(CultureInfo.InvariantCulture,
            $"{roots.Count} repository root(s) found; their names stay searchable and their contents are not read"));
    }

    /// <summary>Which drives are fed. An empty setting means every volume the helper reports,
    /// which is what a fresh install does and what almost everyone wants.</summary>
    private static IReadOnlyList<char> ChosenDrives(Config config, StatusReply status)
    {
        var live = new List<char>();
        foreach (VolumeStatus v in status.Volumes) live.Add(char.ToUpperInvariant(v.Letter));
        if (config.IndexDrives.Length == 0) return live;

        var wanted = new List<char>();
        foreach (string d in config.IndexDrives)
        {
            string t = d.Trim();
            if (t.Length == 0) continue;
            char letter = char.ToUpperInvariant(t[0]);
            if (live.Contains(letter) && !wanted.Contains(letter)) wanted.Add(letter);
        }
        return wanted;
    }

    // What the indexer child was last told, so a setting that has not moved is not a write per
    // turn of a loop that comes round every 400 ms.
    private string _indexerPaused = "";
    private string _indexerPower = "";
    private string _indexerMinutes = "";

    /// <summary>Keep the indexer child running while there is work and nobody has paused it, and
    /// keep its two control rows matching the settings.</summary>
    private void PumpIndexer(ContentDb db)
    {
        // First, and before the early return below: whoever posted work is waiting on it, and a
        // process with no indexer child yet still has a writer connection they can only reach
        // through here.
        DrainContentWork(db);

        IndexerHost? host = _indexer;
        if (host is null) return;

        Config cfg = _config;
        // One bit, one row. IndexContent false means the queue does not move, which is the same
        // mechanism the pause switch already used - so the child, IndexStatus and --searchindex
        // need no new concept.
        string paused = cfg.IndexContent ? "0" : "1";
        string power = cfg.IndexPower.ToString(CultureInfo.InvariantCulture);
        string minutes = cfg.TranscribeMinutes.ToString(CultureInfo.InvariantCulture);
        try
        {
            if (paused != _indexerPaused) { db.Set("index:paused", paused); _indexerPaused = paused; }
            if (power != _indexerPower) { db.Set("index:power", power); _indexerPower = power; }
            // Read by the child per file rather than captured, so a change to the limit reaches
            // the next recording instead of waiting for a restart.
            if (minutes != _indexerMinutes) { db.Set(Indexer.TranscribeMinutesKey, minutes); _indexerMinutes = minutes; }

            // The child is started, never stopped, by this: it watches this process's id and dies
            // with it. That is the whole of the "indexing stops when the app quits" rule, and
            // there is no other lifetime code anywhere.
            if (cfg.IndexContent && db.PendingCount() > 0) host.EnsureRunning();

            ShowOnCapsule(db);
        }
        catch (Exception ex)
        {
            Log.Once("index|pump|" + ex.GetType().Name, "WARN ", "index",
                "the indexer could not be kept up to date :: " + ex.Message);
        }
    }

    // What the capsule was last told. Posted only when it changes: the line moves every few
    // seconds and this loop comes round every second, so three of every four posts would repaint
    // the desktop widget to say exactly what it already says.
    private string _capsuleLine = "";
    private float _capsuleFraction = -1;

    /// <summary>
    /// Put the content index's one-line status under the capsule's bar.
    ///
    /// <para>This is what stops the widget looking broken at startup. A queue left over from a
    /// previous session reads "N waiting - indexing is paused while Findra is closed" until the
    /// child comes up, rather than showing a progress bar that does not move (spec §3); an index
    /// that had to be rebuilt says so instead (spec §2a). An empty line draws nothing at all -
    /// <see cref="CapsulePainter"/> skips the whole progress row - which is what keeps an idle
    /// widget from looking busy.</para>
    ///
    /// <para>Read on the content loop's thread, off the writer connection this loop owns, and
    /// posted to the interface thread. The capsule is a window; nothing about it may be touched
    /// from here.</para>
    /// </summary>
    private void ShowOnCapsule(ContentDb db)
    {
        long pending = db.PendingCount(), indexed = db.IndexedCount();
        // The pid is read with the heartbeat because IndexStatus.Alive needs both: the same rows
        // are written by a one-shot drain in some other process, and by this one, and only the
        // recorded pid tells a live child from the last thing a finished drain left behind.
        bool alive = IndexStatus.Alive(db.Get("indexer:beat"), db.Get("indexer:pid"));
        string line = IndexStatus.Line(_config.IndexContent, db.Get("indexer:state") ?? "off", pending, indexed,
                                       alive, db.WasRebuilt || db.Get("index:rebuilt") == "1");

        // Kept before the early return below. The settings window's Content sentence and the
        // capsule menu's "(not running)" both read these, and neither is asked at the moment the
        // line happens to change.
        _everIndexed = indexed > 0;
        _indexerAlive = alive;
        // Zero rather than a full bar when there is nothing waiting: "up to date" is a sentence,
        // not a completed job, and a bar sitting at 100% invites the reader to wait for something.
        float fraction = pending == 0 ? 0f : (float)(indexed / (double)(indexed + pending));

        if (line == _capsuleLine && Math.Abs(fraction - _capsuleFraction) < 0.001f) return;
        _capsuleLine = line;
        _capsuleFraction = fraction;

        Dispatcher.UIThread.Post(() =>
        {
            CapsuleWindow? capsule = _capsule;
            if (capsule is null) return;
            capsule.Progress = line;
            capsule.ProgressFraction = fraction;
        });
    }

    // ---- screens ---------------------------------------------------------------------------------

    // The hotkey host exists whether or not the capsule does, so it is the reliable monitor source.
    private Screens? ScreenSource() => _hotkey?.Screens ?? _capsule?.Screens;

    private static PixelRect Fallback => new(0, 0, 1920, 1080);

    // ---- the capsule -----------------------------------------------------------------------------

    private void CreateCapsule()
    {
        Screens? screens = ScreenSource();
        Screen? primary = screens?.Primary ?? screens?.All.FirstOrDefault();

        // Null means never dragged. (0,0) is a position like any other - the top-left corner of
        // the primary monitor - and has to be honoured rather than read as "no saved position".
        bool everPlaced = _config.CapsuleX.HasValue && _config.CapsuleY.HasValue;
        var saved = new PixelPoint(_config.CapsuleX ?? 0, _config.CapsuleY ?? 0);
        Screen? on = screens?.ScreenFromPoint(saved);
        double scaling = (on ?? primary)?.Scaling ?? 1.0;

        int w = (int)Math.Round(CapsuleLayout.Width * Zoom * scaling);
        int h = (int)Math.Round(CapsuleLayout.Height * Zoom * scaling);

        IReadOnlyList<PixelRect> all = screens?.All.Select(s => s.Bounds).ToArray() ?? Array.Empty<PixelRect>();

        PixelPoint at;
        if (everPlaced && all.Count > 0 && CapsulePlacement.IsOnAnyScreen(new PixelRect(saved.X, saved.Y, w, h), all))
        {
            at = saved;
        }
        else
        {
            at = CapsulePlacement.BottomCentre(primary?.WorkingArea ?? Fallback, w, h);
            Log.Info("app", everPlaced
                ? $"the saved capsule position ({saved.X},{saved.Y}) is not on any monitor; it opens at ({at.X},{at.Y}) on the primary screen"
                : $"the capsule has no saved position; it opens at ({at.X},{at.Y}) on the primary screen");
        }

        // Seeded from whatever the content loop last worked out, so a capsule created after the
        // index has already been read - the tray toggle, or a slow first paint - opens carrying
        // the current line instead of a blank one until the queue next moves.
        var capsule = new CapsuleWindow(_palette, Zoom)
        {
            Position = at,
            Progress = _capsuleLine,
            ProgressFraction = Math.Max(_capsuleFraction, 0f),
        };
        capsule.Clicked += OpenFromCapsule;
        capsule.Moved += SaveCapsulePosition;
        // Spec §7 surface 4: palette and content indexing live on the capsule so that most people
        // never open settings. Built at the moment of the click, from the config as it is then.
        capsule.MenuItems = () =>
            CapsuleMenu.Items(_config, PaletteStore.LoadFromDisk(), Theme.WindowsIsLight(), _indexerAlive);
        capsule.MenuCommand += OnCapsuleCommand;
        capsule.Show();
        capsule.Position = at;   // Show is entitled to place a window itself; this is the last word
        _capsule = capsule;
        Log.Info("app", $"the capsule is on the desktop at ({at.X},{at.Y})");
    }

    private void SaveCapsulePosition(PixelPoint at)
    {
        _config = _config with { CapsuleX = at.X, CapsuleY = at.Y };
        _config.Save();
    }

    // ---- the card --------------------------------------------------------------------------------

    private CardWindow NewCard()
    {
        // The card BORROWS the read-only store and never disposes it: the store outlives every
        // card, and null is a supported state the card already answers with a sentence.
        var card = new CardWindow(_palette, Zoom, _cardStore, _semantic, _installed);
        card.SettingsRequested += OpenSettings;
        // The card cannot write a setting - it reads the index through a read-only connection -
        // so this is where "turn reading back on" from the Content pill actually lands. Through
        // ApplyConfig, so an open settings window shows the switch move rather than keeping a
        // stale view that would write it back off on its owner's next click.
        card.ContentReadingRequested += () =>
        {
            if (_config.IndexContent) return;
            Log.Info("index", "the card asked for reading inside files to be turned back on");
            ApplyConfig(_config with { IndexContent = true });
        };
        card.Closed += (_, _) => { if (ReferenceEquals(_card, card)) _card = null; };
        _card = card;
        return card;
    }

    private void CloseCard()
    {
        CardWindow? card = _card;
        _card = null;
        try { card?.Close(); } catch (Exception ex) { Log.Warn("app", "the card would not close: " + ex.Message); }
    }

    /// <summary>Opening from the capsule dims the capsule's monitor, and the card unfolds so its
    /// field lands exactly where the capsule's bar was.</summary>
    private void OpenFromCapsule()
    {
        // A click on the capsule deactivated the card BEFORE this handler ran, so without this a
        // click that dismissed the card would immediately reopen it.
        if (CardWindow.JustClosed) return;
        if (_card is not null) { CloseCard(); return; }
        if (_capsule is null) return;

        try
        {
            Screens? screens = _capsule.Screens;
            Screen? s = screens?.ScreenFromWindow(_capsule) ?? screens?.Primary;
            CardWindow card = NewCard();
            if (s is not null) card.ShowDim(s.Bounds, s.Scaling);
            card.PlaceOver(_capsule.Position, Zoom, CapsuleWindow.BarRect, s?.Bounds ?? Fallback);
            card.Show();
        }
        catch (Exception ex) { Log.Error("app", "the card could not open from the capsule", ex); _card = null; }
    }

    /// <summary>The open path shared by the hotkey, the tray icon and the tray's Search item. It
    /// dims the monitor under the CURSOR, which is not necessarily the one the capsule rests on -
    /// two open paths, two dim behaviours.
    ///
    /// <paramref name="fromClick"/> is true for the two tray routes. A mouse click deactivates an
    /// open card, and the card closes and nulls itself out BEFORE the handler runs, so without the
    /// guard a click that dismissed the card would immediately open a fresh one. WM_HOTKEY moves
    /// no focus and closes nothing, so the hotkey does not take the guard and keeps its plain
    /// toggle: pressed while the card is open, it closes it.</summary>
    private void OpenCentred(bool fromClick)
    {
        if (fromClick && CardWindow.JustClosed) return;
        if (_card is not null) { CloseCard(); return; }

        try
        {
            Screens? screens = ScreenSource();
            Screen? s = screens?.ScreenFromPoint(HotkeyHost.CursorPosition()) ?? screens?.Primary;
            double scaling = s?.Scaling ?? 1.0;
            PixelRect work = s?.WorkingArea ?? Fallback;

            CardWindow card = NewCard();
            if (s is not null) card.ShowDim(s.Bounds, s.Scaling);
            // Against the card WITH results in it. It opens empty, but it grows in place the
            // moment the first results land and is never placed again.
            card.Position = CardPlacement.CentredGrown(work, Zoom, scaling);
            card.Show();
        }
        catch (Exception ex) { Log.Error("app", "the card could not open", ex); _card = null; }
    }

    // ---- the tray --------------------------------------------------------------------------------

    private void CreateTray()
    {
        var menu = new NativeMenu();

        var search = new NativeMenuItem("Search");
        search.Click += (_, _) => OpenCentred(fromClick: true);
        menu.Items.Add(search);

        _showCapsuleItem = new NativeMenuItem("Show capsule")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _config.ShowCapsule,
        };
        _showCapsuleItem.Click += (_, _) => ToggleCapsule();
        menu.Items.Add(_showCapsuleItem);

        var settings = new NativeMenuItem("Settings");
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);
        menu.Items.Add(new NativeMenuItemSeparator());

        var check = new NativeMenuItem("Check for updates");
        check.Click += (_, _) => _ = RunUpdateCheck(force: true);
        menu.Items.Add(check);
        _checkForUpdatesItem = check;

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(quit);

        var icon = new TrayIcon { Menu = menu, ToolTipText = Tooltip(), IsVisible = true };
        if (TrayIconFactory.Draw(_palette) is { } drawn) icon.Icon = drawn;
        icon.Clicked += (_, _) => OpenCentred(fromClick: true);

        TrayIcon.SetIcons(_app, new TrayIcons { icon });
        _tray = icon;
        Log.Info("app", "the tray icon is up");
    }

    private string Tooltip() => TrayText.Tooltip(Log.Version, _hotkey?.Landed, _update, _latest);

    private void RefreshTooltip()
    {
        if (_tray is not null) _tray.ToolTipText = Tooltip();
    }

    private void ToggleCapsule()
    {
        bool show = !_config.ShowCapsule;
        _config = _config with { ShowCapsule = show };
        _config.Save();
        if (_showCapsuleItem is not null) _showCapsuleItem.IsChecked = show;
        SettingsWindow.Open?.UseConfig(_config);

        if (show)
        {
            if (_capsule is null) Stage("capsule", CreateCapsule);
        }
        else
        {
            CloseCapsule();
            Log.Info("app", "the capsule is turned off; the hotkey and the tray open the card");
        }
    }

    private void CloseCapsule()
    {
        CapsuleWindow? capsule = _capsule;
        _capsule = null;
        try { capsule?.Close(); } catch (Exception ex) { Log.Warn("app", "the capsule would not close: " + ex.Message); }
    }

    private void Quit()
    {
        Log.Info("app", "quitting");
        try { _shutdown.Cancel(); } catch { }
        UiStatus.Clear();
        try { _hotkey?.Dispose(); } catch { }
        _hotkey = null;

        CloseCard();
        try { _capsule?.Close(); } catch { }
        _capsule = null;
        try { _tray?.Dispose(); } catch { }
        _tray = null;

        // The content loop owns both stores and the indexer child, and unwinds on the cancellation
        // above. If it never started, nothing else will close them.
        if (_contentLoop is null)
        {
            try { _indexer?.Dispose(); } catch { }
            try { _feeder?.Dispose(); } catch { }
            try { _cardStore?.Dispose(); } catch { }
            try { _semantic?.Dispose(); } catch { }
            try { _content?.Dispose(); } catch { }
        }

        Log.Info("app", Log.SessionSummary());
        Log.Flush();
        _desktop.Shutdown();
    }

    // ---- settings ----------------------------------------------------------------------------

    /// <summary>
    /// Open the settings window, or bring the one that is already open to the front. One instance:
    /// a second window is a second view of the same configuration and the two write over each
    /// other on every click.
    ///
    /// <para>The state is built from the config, what is on disk, and what the machine has told us
    /// so far - with one exception. The scheduled-task query shells out to <c>schtasks</c> and
    /// waits up to five seconds, so it is asked for AFTER the window is up and posted back into
    /// it; the row reads "Findra could not tell" for that moment, which is exactly what is true
    /// while nobody has asked.</para>
    /// </summary>
    private void OpenSettings() => OpenSettings(Section.Look);

    private void OpenSettings(Section section)
    {
        try
        {
            // Already open: bring it forward AND take it to the section that was asked for. A
            // window raised on whichever section it was left on answers the wrong question when
            // the card sent somebody here to find one particular row.
            if (SettingsWindow.Open is { } already) { already.ShowSection(section); already.Activate(); return; }

            var state = new SettingsState(_config)
            {
                Section = section,
                Palettes = PaletteStore.LoadFromDisk(),
                Installed = _installed,
                HebrewOffered = Capabilities.HebrewIsOffered(Capabilities.SystemLanguages()),
                StartsAtLogon = Autostart.IsSet(),
                EverIndexed = _everIndexed,
                IndexerAlive = _indexerAlive,
                Drives = _drives,
                WindowsIsLight = Theme.WindowsIsLight(),
                Version = BuildInfo.Version,
                Update = _update,
                Latest = _latest,
            };

            var window = new SettingsWindow(state, this, chord => _hotkey?.Rebind(chord) ?? false);
            window.Changed += OnSettingsChanged;
            window.PaletteChanged += OnPaletteChanged;
            window.Show();

            _ = Task.Run(() =>
            {
                HelperTaskState helper = HelperTask.Query().State;
                Dispatcher.UIThread.Post(() => SettingsWindow.Open?.UseHelperState(helper));
            });
        }
        catch (Exception ex) { Log.Error("settings", "the settings window could not open", ex); }
    }

    /// <summary>
    /// A setting moved. The window has already written config.json - this is the half of the
    /// answer that is not on disk: the palette everything else is painted in, the tray's own tick,
    /// and whether there is a capsule at all.
    /// </summary>
    private void OnSettingsChanged(Config c)
    {
        _config = c;
        if (_showCapsuleItem is not null) _showCapsuleItem.IsChecked = c.ShowCapsule;

        Palette resolved = Theme.Resolve(c, Theme.WindowsIsLight(), PaletteStore.LoadFromDisk());
        if (!string.Equals(resolved.Name, _palette.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Through the window when there is one: it repaints itself first and then hands the
            // palette back through PaletteChanged, which is what redraws everything that is NOT
            // it. The capsule's own menu can change the palette with no settings window open, and
            // that route has to reach the same handler rather than change nothing on screen.
            if (SettingsWindow.Open is { } window) window.UsePalette(resolved);
            else OnPaletteChanged(resolved);
        }

        if (c.ShowCapsule && _capsule is null) Stage("capsule", CreateCapsule);
        else if (!c.ShowCapsule && _capsule is not null) CloseCapsule();
    }

    /// <summary>Everything painted in the palette except the window that chose it: the tray icon,
    /// and the capsule, which takes its palette in its constructor and so is made again.</summary>
    private void OnPaletteChanged(Palette p)
    {
        _palette = p;
        if (_tray is not null && TrayIconFactory.Draw(p) is { } drawn) _tray.Icon = drawn;
        if (_capsule is not null) { CloseCapsule(); Stage("capsule", CreateCapsule); }
        Log.Info("look", $"the palette is now '{p.Name}' ({(p.Light ? "light" : "dark")})");
    }

    /// <summary>A config change made from somewhere other than the settings window. Saved,
    /// applied, and shown to the window when there is one - two views of one configuration must
    /// not be allowed to disagree, because the stale one wins on its owner's next click.</summary>
    private void ApplyConfig(Config next)
    {
        next.Save();
        // The window learns it FIRST. OnSettingsChanged repaints that window in the new palette,
        // and a window still holding the old configuration would draw the tick beside the palette
        // that has just been replaced until the next pointer move.
        SettingsWindow.Open?.UseConfig(next);
        OnSettingsChanged(next);
    }

    /// <summary>The capsule's right-click menu, answered. It switches on
    /// <see cref="MenuEntry.Command"/> and never on a display string.</summary>
    private void OnCapsuleCommand(string command)
    {
        const string palettePrefix = "palette:";
        if (command.StartsWith(palettePrefix, StringComparison.Ordinal))
        {
            string name = command[palettePrefix.Length..];
            // The side actually in use, which is the side the menu offered. Writing the other one
            // would save a palette and change nothing on screen.
            bool light = _config.Mode switch
            {
                ThemeMode.AlwaysDark => false,
                ThemeMode.AlwaysLight => true,
                _ => Theme.WindowsIsLight(),
            };
            ApplyConfig(light ? _config with { LightPalette = name } : _config with { DarkPalette = name });
            return;
        }

        switch (command)
        {
            case "content": ApplyConfig(_config with { IndexContent = !_config.IndexContent }); return;
            case "settings": OpenSettings(); return;
            case "quit": Quit(); return;
            default:
                Log.Warn("app", $"the capsule menu asked for '{command}', which the shell does not know");
                return;
        }
    }

    // ---- what a settings click asks the machine for ------------------------------------------
    //
    // The only place in Findra where a settings click reaches the operating system. Every one of
    // these is a call into Avalonia, the registry, schtasks or the network, so none of them has a
    // test - SettingsActionTests proves the click gets HERE, and the end-to-end checklist is what
    // proves each of these does the right thing once it has.

    void ISettingsHost.OpenPalettesFile(string path)
    {
        PaletteStore.EnsureOnDisk();           // it may never have been written
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("settings", "could not open palettes.json: " + ex.Message); }
    }

    void ISettingsHost.BeginChordCapture() { /* the window handles keys; nothing to do here */ }

    void ISettingsHost.SetAutostart(bool on)
    {
        string exe = Environment.ProcessPath ?? "";
        if (on) Autostart.Set(exe); else Autostart.Clear();
    }

    void ISettingsHost.RegisterHelper() => _ = Task.Run(() =>
    {
        // Register raises the one UAC prompt itself (runas), so this must not be on the UI thread.
        // A refused prompt throws Win32Exception 1223 into Register's own catch and returns false.
        bool ok = HelperTask.Register(Environment.ProcessPath ?? "");
        if (ok) HelperTask.EnsureRunning();     // and start it now, not at the next logon
        HelperTaskState state = HelperTask.Query().State;
        Dispatcher.UIThread.Post(() => SettingsWindow.Open?.UseHelperState(state));
    });

    void ISettingsHost.PickFolder() => _ = PickFolderAsync();

    void ISettingsHost.InstallCapability(Capability c) => _ = InstallAsync(c);

    void ISettingsHost.CheckNow() => _ = RunUpdateCheck(force: true);

    void ISettingsHost.RecentreCapsule()
    {
        // The config write has already happened in the model; this is the half the person sees.
        // Close the capsule and create it again: CreateCapsule reads _config, whose CapsuleX and
        // CapsuleY are now null, and CapsulePlacement puts a capsule with no saved position back
        // on the primary monitor. Without this the window stays exactly where it was - which, for
        // the one control whose entire purpose is recovering a capsule that cannot be seen, is
        // the same defect as not handling the click at all.
        CloseCapsule();
        if (_config.ShowCapsule)
        {
            Stage("capsule", CreateCapsule);
            Log.Info("app", "the capsule was brought back to the primary monitor");
        }
        else
        {
            Log.Info("app", "the capsule's saved position was cleared; it opens on the primary " +
                            "monitor when it is turned back on");
        }
    }

    /// <summary>
    /// Begin reading inside files NOW, rather than at the next turn of the content loop.
    ///
    /// <para>The model has already written <c>IndexContent</c>; this is the half in front of the
    /// person who pressed it. The child is started, never stopped, by this: it watches this
    /// process's id and dies with it, which is the whole of "indexing stops when Findra quits".
    /// </para>
    ///
    /// <para>Posted to the flow that owns the writer connection, because <c>ContentDb.Claim</c> is
    /// a thread-id detector rather than a lock and a settings click arrives on the interface
    /// thread. A session with no content loop yet answers 0 and logs it - the loop's own pump
    /// starts the child a second later off the configuration this click has already saved.</para>
    /// </summary>
    void ISettingsHost.StartIndexing()
    {
        Log.Info("index", "reading inside files was started from settings");
        Task<int> asked = OnContentLoopAsync(db =>
        {
            // The same two rows the pump writes, so nothing waits a second to agree with the
            // setting, and the same EnsureRunning - there is one way to start this child.
            db.Set("index:paused", "0");
            _indexerPaused = "0";

            IndexerHost? host = _indexer;
            if (host is null)
            {
                Log.Warn("index", "the content index is not open in this session, so nothing " +
                                  "will be read inside files until Findra is started again");
                return 0;
            }

            host.EnsureRunning();
            // The request and its outcome, in that order and both in the log. A button that
            // writes a setting and reports nothing about the thing it asked for is how a child
            // that never started went unnoticed through a whole session.
            Log.Info("index", host.Running
                ? "the indexer is running"
                : "the indexer did not start; it is waiting out the backoff after an earlier exit");
            return 1;
        });

        // Observed rather than discarded. This is posted to another flow, so a fault or a
        // shutdown mid-request is otherwise an unobserved task exception nobody ever sees.
        _ = asked.ContinueWith(static t =>
        {
            if (t.Exception is { } failed)
                Log.Error("index", "starting to read inside files did not reach the content index",
                          failed.GetBaseException());
            else if (t.IsCanceled)
                Log.Warn("index", "starting to read inside files was cut short by Findra closing");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The system folder dialog, deliberately: spec §12 accepts that these two surfaces
    /// are hand-drawn and mitigates it by calling the operating system's own picker where one
    /// exists.</summary>
    private async Task PickFolderAsync()
    {
        try
        {
            if (SettingsWindow.Open is not { } window) return;
            IReadOnlyList<IStorageFolder> picked = await window.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "A folder Findra will not look inside", AllowMultiple = false });
            if (picked.Count == 0) return;

            string? path = picked[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            window.AddExclusion(path);
            Log.Info("settings", "a folder was added to the list Findra will not open");
        }
        catch (Exception ex) { Log.Warn("settings", "the folder picker failed: " + ex.Message); }
    }

    /// <summary>How often a download says where it has got to. It is a log line, not a progress
    /// bar - the settings window has no room for one, and the row it came from turns into
    /// "installed" when the files land.</summary>
    private static readonly TimeSpan DownloadProgressEvery = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Fetch what a capability needs, and tell the window when it is there.
    ///
    /// <para>The same controller the first-run screen uses, deliberately. This was written
    /// against <c>ModelDownloader.GetAllAsync</c> before <c>FirstRunDownloads</c> existed, and the
    /// two behaved differently in the two ways that matter: a fetch that threw
    /// escaped as an unobserved exception rather than becoming an outcome, and nothing re-queued
    /// the files the new capability covers until the next launch - which reads, to the person who
    /// just waited for 630 MB, as a download that did not work. One path now, and one policy.
    /// </para>
    ///
    /// <para>The re-queue still cannot happen here: <c>ContentDb.Claim</c> is a thread-id detector
    /// and this runs on the pool while the content loop holds the writer.
    /// <see cref="RequeueWhatArrivedAsync"/> is what posts it onto the loop that owns it.</para>
    /// </summary>
    private async Task InstallAsync(Capability c)
    {
        string dir = ModelStore.Dir;
        IReadOnlyList<Model> need = Capabilities.ModelsFor([c]);
        IReadOnlyList<Model> missing = ModelStore.Missing(need, dir);
        if (missing.Count == 0)
        {
            Log.Info("models", $"{Capabilities.Title(c)} is already on disk; nothing was fetched");
            return;
        }

        Log.Info("models", $"{Capabilities.Title(c)}: fetching {missing.Count.ToString(CultureInfo.InvariantCulture)} " +
                           $"file(s), {Sizes.Human(ModelStore.TotalBytes(missing))}, into {dir}");
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,   // a 1.5 GB file over a slow line is not a hung request
            };
            IReadOnlyList<DownloadOutcome> outcomes = await FirstRunDownloads.RunAsync(
                need, dir, ModelDownloader.Http(http), NoteDownloadProgress,
                RequeueWhatArrivedAsync, _shutdown.Token).ConfigureAwait(false);

            foreach (DownloadOutcome o in outcomes)
                if (!o.Complete)
                    // What arrived is kept in the .part file, so pressing the row again resumes.
                    Log.Warn("models", $"{o.Model.File} did not finish - {o.Problem}; what arrived is kept");

            _installed = CapabilitySet.Installed(dir);
            Log.Info("models", _installed.Has(c)
                ? $"{Capabilities.Title(c)} is installed"
                : $"{Capabilities.Title(c)} is not complete yet; what arrived is kept and the row resumes it");
        }
        catch (OperationCanceledException) { Log.Info("models", "the download stopped when Findra quit; what arrived is kept"); }
        catch (Exception ex) { Log.Error("models", $"{Capabilities.Title(c)} could not be fetched", ex); }
        finally
        {
            CapabilitySet installed = _installed;
            Dispatcher.UIThread.Post(() => SettingsWindow.Open?.UseInstalled(installed));
        }
    }

    private static void NoteDownloadProgress(DownloadProgress p) =>
        Log.Repeat("models|fetch|" + p.File, DownloadProgressEvery, "INFO ", "models",
            $"{p.File}: {Sizes.Human(p.Got)} of {(p.Total > 0 ? Sizes.Human(p.Total) : "?")}");

    // ---- the update check ------------------------------------------------------------------------

    /// <summary>The tray's "Check for updates" forces the request past the 24 hour gate; startup
    /// does not. Either way the result only ever becomes a tooltip line and a log line - Findra
    /// downloads nothing and installs nothing.</summary>
    private async Task RunUpdateCheck(bool force)
    {
        try
        {
            UpdateResult result = await UpdateCheck.CheckAsync(
                _config,
                ct => UpdateCheck.FetchLatestTagAsync(Http, Log.Version, ct),
                DateTime.UtcNow, _shutdown.Token, force).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Only the timestamp and the tag are taken back, and both are merged onto the
                // CURRENT config on the UI thread. The config may have moved on since the request
                // started - a capsule dragged, the capsule toggled off - and writing the whole
                // record back would undo it.
                Config merged = _config;
                if (result.Config.LastUpdateCheck != merged.LastUpdateCheck)
                    merged = merged with { LastUpdateCheck = result.Config.LastUpdateCheck };

                bool answered = result.State is UpdateState.Current or UpdateState.Available;
                if (answered && !string.IsNullOrWhiteSpace(result.Latest) &&
                    result.Latest != merged.LatestKnownVersion)
                    merged = merged with { LatestKnownVersion = result.Latest };

                if (!ReferenceEquals(merged, _config))
                {
                    _config = merged;
                    _config.Save();
                }

                // A not-due or failed check carries no tag, and must not erase what the last
                // successful one found - remembering it is the whole point of writing it down.
                if (answered)
                {
                    _update = result.State;
                    _latest = result.Latest;
                }

                // A check the user asked for has to visibly answer. The header goes back to
                // "Check for updates" on the next launch, where the item is built fresh.
                if (force && _checkForUpdatesItem is not null)
                    _checkForUpdatesItem.Header = UpdateMemory.CheckedHeader(result.State, result.Latest);

                // And the same answer in About, which is the other place a person can ask for one.
                // Without this, "Check now" makes a request and changes nothing anybody can see.
                SettingsWindow.Open?.NoteUpdate(result.State, result.Latest);

                RefreshTooltip();
                if (result.Advice is { } advice) Log.Info("startup", advice);
            });
        }
        catch (Exception ex)
        {
            Log.Warn("startup", "the update check could not run: " + ex.Message);
        }
    }
}
