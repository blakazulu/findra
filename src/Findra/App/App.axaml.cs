using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
internal sealed class Shell
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
    private QueueFeeder? _feeder;
    private IndexerHost? _indexer;
    private Task? _contentLoop;

    public Shell(Application app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _app = app;
        _desktop = desktop;
    }

    public void Start()
    {
        Stage("settings", () =>
        {
            _config = Config.LoadFromDisk();
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

        // EnsureRunning asks the scheduler to start the helper and then waits up to five seconds
        // for the pipe to answer. On the UI thread that is five seconds of nothing on screen.
        Stage("names helper", () => _ = Task.Run(() =>
        {
            bool up = HelperTask.EnsureRunning();
            Log.Info("startup", up
                ? "the names helper is answering"
                : "the names helper is not answering; name search will be empty until it is registered");
        }));

        Stage("hotkey", () =>
        {
            var host = new HotkeyHost();
            host.Pressed += () => Dispatcher.UIThread.Post(() => OpenCentred(fromClick: false));
            // Owned before it is started: Start creates a real window, and a throw inside it would
            // otherwise leave that window with nobody holding it and nobody to Dispose it.
            _hotkey = host;
            host.Start(HotkeyChain.Build(_config.Hotkey, Hotkey.DefaultChain));
            UiStatus.Write(Environment.ProcessId, host.Landed);
        });

        Stage("capsule", () =>
        {
            if (_config.ShowCapsule) CreateCapsule();
            else Log.Info("app", "the capsule is turned off; the hotkey and the tray open the card");
        });

        Stage("content index", OpenContentIndex);

        Stage("tray", CreateTray);

        Stage("update check", () => _ = Task.Run(async () =>
        {
            await Task.Delay(UpdateCheckDelay).ConfigureAwait(false);
            await RunUpdateCheck(force: false).ConfigureAwait(false);
        }));
    }

    private static void Stage(string what, Action body)
    {
        try { body(); }
        catch (Exception ex) { Log.Error("startup", $"the {what} stage failed", ex); }
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
                    Log.Once("index|session|" + ex.GetType().Name, "WARN ", "index",
                        "the queue is not being fed from the journal :: " + ex.Message);
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
            try { _indexer?.Dispose(); } catch { }
            try { feeder.Dispose(); } catch { }
            try { _cardStore?.Dispose(); } catch { }
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
        IReadOnlyList<char> drives = ChosenDrives(_config, status);
        if (drives.Count == 0)
        {
            Log.Warn("index", "the helper reports no volumes to index");
            return;
        }

        // Learned before anything is judged, because a repository root changes what is eligible.
        // Reconciled again afterwards: the pass at startup ran before the helper was reachable and
        // therefore before any root was known.
        await LearnRepoRootsAsync(client, feeder, drives, ct).ConfigureAwait(false);
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
        await CatchUpAsync(client, feeder, drives, walkedAt, first: true, ct).ConfigureAwait(false);

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
            await CatchUpAsync(client, feeder, drives, walkedAt, first: false, ct).ConfigureAwait(false);

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
                                           Dictionary<char, long> walkedAt, bool first, CancellationToken ct)
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
            await WalkAsync(client, feeder, v, at, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One first pass: ask the helper for every file whose suffix this build cares about, and hand
    /// the whole answer to the feeder.
    ///
    /// The stream is collected before it is written, and that is a deliberate bound rather than an
    /// oversight: the suffix filter runs in the helper, so what comes back is the machine's
    /// documents, photos, audio and video, not its every file - a tenth to a twentieth of the
    /// rows, which is the whole reason enumerate takes a suffix list instead of streaming a
    /// snapshot. One transaction for the pass is also what makes the consumed position, the suffix
    /// stamp and the discharged debt land together or not at all.
    /// </summary>
    private static async Task WalkAsync(NameClient client, QueueFeeder feeder, char volume,
                                        VolumeResume at, CancellationToken ct)
    {
        Log.Info("index", string.Create(CultureInfo.InvariantCulture,
            $"{volume}: walking the disk for the first time this index has seen it ({at.Note})"));

        feeder.NoteWalkStarted(volume);
        var found = new List<EnumeratedFile>();
        await foreach (EnumeratedFile f in client
                           .EnumerateAsync(volume, QueueFeeder.ContentSuffixes(), EnumerateBatch, ct)
                           .ConfigureAwait(false))
            found.Add(f);

        // Only a stream that reached its Done frame gets here - EnumerateAsync throws otherwise -
        // so a truncated walk can never stamp a position or clear the debt it did not discharge.
        //
        // The walk above takes minutes on a real disk, and events lost while it ran are past the
        // position it is about to stamp. FillFrom re-reads this session's dropped-event counter
        // itself before it decides whether to discharge anything, which is why nothing has to be
        // reported here: a call on this line is a call the next refactor deletes without noticing.
        feeder.FillFrom(volume, at.JournalId, at.Usn, found);
    }

    private static async Task LearnRepoRootsAsync(NameClient client, QueueFeeder feeder,
                                                  IReadOnlyList<char> drives, CancellationToken ct)
    {
        var roots = new List<string>();
        foreach (char v in drives)
        {
            await foreach (EnumeratedFile f in client
                               .EnumerateAsync(v, RepoMarkerSuffix, EnumerateBatch, ct)
                               .ConfigureAwait(false))
            {
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

    /// <summary>Keep the indexer child running while there is work and nobody has paused it, and
    /// keep its two control rows matching the settings.</summary>
    private void PumpIndexer(ContentDb db)
    {
        IndexerHost? host = _indexer;
        if (host is null) return;

        Config cfg = _config;
        string paused = cfg.IndexPaused ? "1" : "0";
        string power = cfg.IndexPower.ToString(CultureInfo.InvariantCulture);
        try
        {
            if (paused != _indexerPaused) { db.Set("index:paused", paused); _indexerPaused = paused; }
            if (power != _indexerPower) { db.Set("index:power", power); _indexerPower = power; }

            // The child is started, never stopped, by this: it watches this process's id and dies
            // with it. That is the whole of the "indexing stops when the app quits" rule, and
            // there is no other lifetime code anywhere.
            if (!cfg.IndexPaused && db.PendingCount() > 0) host.EnsureRunning();

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
        string line = IndexStatus.Line(db.Get("indexer:state") ?? "off", pending, indexed,
                                       IndexStatus.Alive(db.Get("indexer:beat"), db.Get("indexer:pid")),
                                       db.WasRebuilt || db.Get("index:rebuilt") == "1");
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
        var card = new CardWindow(_palette, Zoom, _cardStore);
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

        // Present, so the shape of the menu is visible, and disabled, because the settings surface
        // is a later plan. A menu that grows later is worse than one that shows what is coming.
        menu.Items.Add(new NativeMenuItem("Settings") { IsEnabled = false });
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

        if (show)
        {
            if (_capsule is null) Stage("capsule", CreateCapsule);
        }
        else
        {
            CapsuleWindow? capsule = _capsule;
            _capsule = null;
            try { capsule?.Close(); } catch (Exception ex) { Log.Warn("app", "the capsule would not close: " + ex.Message); }
            Log.Info("app", "the capsule is turned off; the hotkey and the tray open the card");
        }
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
            try { _content?.Dispose(); } catch { }
        }

        Log.Info("app", Log.SessionSummary());
        Log.Flush();
        _desktop.Shutdown();
    }

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
