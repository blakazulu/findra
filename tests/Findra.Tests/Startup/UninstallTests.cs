using Findra;
using Findra.Startup;
using Xunit;

/// <summary>
/// Spec §2a, in tests. An uninstaller that leaves the HighestAvailable scheduled task behind
/// points an elevated logon task at a deleted binary on somebody else's machine, and the
/// specification calls that a defect rather than an inconvenience - so every way it could happen
/// gets a test.
/// </summary>
[Collection("culture")]
public class UninstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "findra-un-" + Guid.NewGuid().ToString("N"));

    public UninstallTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } GC.SuppressFinalize(this); }

    /// <summary>
    /// A machine with a gigabyte of models and four hundred megabytes of index.
    ///
    /// <para>The numbers matter and were got wrong once. Sizes.Human renders two decimals above a
    /// gigabyte, so 3,146,000,000 and the declared 2.93 GB total BOTH render "2.93 GB" - and the
    /// measured-size test below asserts the text contains one and not the other, which no
    /// implementation can satisfy. One gigabyte renders "1 GB" and cannot collide with it.</para>
    /// </summary>
    private static readonly DataSize[] Measured =
    [
        new("models", @"C:\Users\rae\AppData\Local\Findra\models", 1_073_741_824),   // 1 GB
        new("index", @"C:\Users\rae\AppData\Local\Findra\index", 419_430_400),       // 400 MB
        new("logs", @"C:\Users\rae\AppData\Local\Findra\logs", 1_234_567),
        new("settings", @"C:\Users\rae\AppData\Roaming\Findra", 4_096),
    ];

    // ---- what is always removed ------------------------------------------------------------

    [Fact]
    public void KeepingEveryByteOfDataChangesNothingButWhatIsDeleted()
    {
        // The plausible wrong shape is "purge removes the extra things", which puts the scheduled
        // task in the purge branch. Then the ordinary uninstall - the one nearly everybody runs -
        // leaves an elevated logon task pointing at a binary that no longer exists.
        //
        // The plan cannot express that any more: it carries deletes and keeps and nothing else, so
        // there is no field a task removal could be made conditional on. What purge decides is
        // asserted here; that the task goes regardless is asserted below, by running Run down each
        // of its routes and watching what it removes.
        UninstallPlan keep = Uninstall.Plan(purge: false, Measured);
        UninstallPlan purge = Uninstall.Plan(purge: true, Measured);

        Assert.Empty(keep.Deletes);
        Assert.Equal(Measured, keep.Keeps);
        Assert.Equal(Measured, purge.Deletes);
        Assert.Empty(purge.Keeps);
    }

    // ---- what Run actually does, in order -----------------------------------------------------

    /// <summary>
    /// Every effect <see cref="Uninstall.Run(string[])"/> has, recorded in the order it had them.
    ///
    /// <para>The two tests this replaced read <c>Run</c>'s own source and asserted where four
    /// names appeared in it. That is blind to what surrounds them: wrapping the whole removal
    /// block in <c>if (!quiet)</c> keeps every name in place and in source order, and the suite
    /// stayed green - while disabling the removal on the route the installer's own UninstallRun
    /// entries take, which is nearly every real uninstall. It was also brittle the other way,
    /// because it read to the end of the file: moving the removal into a private helper below
    /// <c>Run</c> broke it without changing a thing.</para>
    /// </summary>
    private sealed class Recorder
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<int>? Spared { get; private set; }
        public IReadOnlyList<DataSize>? Deleted { get; private set; }

        /// <summary>The one line the log gets before anything happens, or null if it never came.</summary>
        public string? Said { get; private set; }

        /// <summary>What HelperTask.Unregister answered - false is a task still registered.</summary>
        public bool TaskGone { get; init; } = true;

        /// <summary>What would not go, by label. Everything else is removed cleanly.</summary>
        public string? Stuck { get; init; }

        public int Run(params string[] args) => Uninstall.Run(
            args,
            unregisterTask: () => { Calls.Add("task"); return TaskGone; },
            clearAutostart: () => Calls.Add("autostart"),
            stop: spare => { Calls.Add("stop"); Spared = spare; },
            delete: deletes =>
            {
                Calls.Add("delete");
                Deleted = deletes;
                return [.. deletes.Select(d => d.Label == Stuck
                    ? new Removal(d.Label, d.Path, false, "the file is in use")
                    : new Removal(d.Label, d.Path, true, null))];
            },
            forgetTheWelcomeScreen: () => Calls.Add("welcome"),
            announce: line => { Calls.Add("say"); Said = line; },
            // Never the real check. Unelevated, Run would raise a UAC prompt and relaunch itself.
            elevated: () => true);
    }

    [Fact]
    public void TheOrderIsStopEverythingThenTheTaskThenAutostartThenAnyDeletion()
    {
        // A helper holding a volume handle and an indexer holding the index file are both live
        // while files are being deleted, if the order is wrong. And the task must be gone before
        // the delete loop, so a folder that will not go still leaves a machine with no orphaned
        // elevated task.
        var run = new Recorder();
        run.Run("--uninstall", "--purge", "--quiet");

        // The line saying which way it went comes before all five, because a run that fails half
        // way through still has to have said what it was doing.
        Assert.Equal(["say", "stop", "task", "autostart", "welcome", "delete"], run.Calls);
    }

    [Theory]
    [InlineData("--uninstall")]
    [InlineData("--uninstall", "--purge")]
    [InlineData("--uninstall", "--quiet")]
    [InlineData("--uninstall", "--purge", "--quiet")]
    [InlineData("--uninstall", "--relaunched", "4242")]
    public void TheWelcomeScreenIsUnansweredAgainOnEveryRouteThroughRun(params string[] args)
    {
        // FirstRunDone means "the welcome screen has been answered on this installation", and this
        // is the end of an installation. It matters on the KEEP route above all, which is the
        // default and keeps the settings file: the task the screen registers has just been removed
        // and nothing on an ordinary launch registers it, so a reinstall that skipped the screen
        // came up with no name search, a content queue nothing could feed, and a "Start now" that
        // started an indexer with an empty queue. Three complaints, one missing task.
        var run = new Recorder();
        run.Run(args);

        Assert.Contains("welcome", run.Calls);
    }

    [Fact]
    public void ADryRunAnswersTheQuestionAndChangesNothingAtAll()
    {
        // Including the settings file. --dry-run is what the installer's own dialog runs to get
        // the measured sizes it shows, seconds before the person has decided anything, and it runs
        // unelevated - so a write here would reset the welcome screen of somebody who then pressed
        // Cancel.
        var run = new Recorder();
        run.Run("--uninstall", "--purge", "--dry-run", "--quiet");

        Assert.Empty(run.Calls);
    }

    [Theory]
    [InlineData("--uninstall")]                             // the ordinary keep run
    [InlineData("--uninstall", "--purge")]                  // and its purge variant
    [InlineData("--uninstall", "--quiet")]                  // what the installer runs, on uninstall
    [InlineData("--uninstall", "--purge", "--quiet")]       // and what it runs when the box is ticked
    [InlineData("--uninstall", "--relaunched", "4242")]     // the elevated child of the UAC prompt
    public void TheScheduledTaskGoesOnEveryRouteThroughRun(params string[] args)
    {
        // The property the source-reading test could not see. --quiet is the installer's route:
        // CurUninstallStepChanged runs `--uninstall --quiet`, or `--uninstall --purge --quiet` if
        // the box was ticked, so a removal that only happens when somebody is reading the console
        // leaves a HighestAvailable logon task pointing at a binary Inno is about to delete -
        // spec 2a's defect, on the path nearly everybody takes.
        var run = new Recorder();
        run.Run(args);

        Assert.Contains("task", run.Calls);
        Assert.Contains("autostart", run.Calls);
    }

    [Fact]
    public void AKeepRunDeletesNothingAtAll()
    {
        var run = new Recorder();
        int code = run.Run("--uninstall", "--quiet");

        Assert.DoesNotContain("delete", run.Calls);
        Assert.Null(run.Deleted);
        Assert.Equal(0, code);
    }

    [Fact]
    public void APurgeRunDeletesAllFourOfFindrasOwnFolders()
    {
        // Pairs with the test above, so neither can be satisfied by a Run that never deletes -
        // which would be a perfectly green uninstaller that frees nothing --purge promised.
        var run = new Recorder();
        int code = run.Run("--uninstall", "--purge", "--quiet");

        Assert.Equal(["models", "index", "logs", "settings"], run.Deleted!.Select(d => d.Label));
        Assert.Equal(0, code);
    }

    [Fact]
    public void TheParentWaitingOnAnElevatedRelaunchIsSparedAlongWithThisProcess()
    {
        // StopOrder does the sparing, but only with the list Run hands it. Run building that list
        // out of this process alone puts the waiting parent back in range of the kill.
        var run = new Recorder();
        run.Run("--uninstall", "--quiet", "--relaunched", "4242");

        Assert.Contains(4242, run.Spared!);
        Assert.Contains(Environment.ProcessId, run.Spared!);
    }

    [Fact]
    public void ATaskThatWouldNotGoAwayIsANonZeroExit()
    {
        // Nobody reads an uninstaller's exit code by hand, but the installer does, and this is the
        // one failure that leaves an elevated logon task behind.
        var run = new Recorder { TaskGone = false };

        Assert.Equal(1, run.Run("--uninstall", "--quiet"));
    }

    [Fact]
    public void AFolderThatWouldNotGoIsANonZeroExitToo()
    {
        // The rest still went - Delete carries on past one failure - so the report is not empty
        // and the exit code is the only thing saying the purge was incomplete.
        var run = new Recorder { Stuck = "index" };

        Assert.Equal(1, run.Run("--uninstall", "--purge", "--quiet"));
    }

    // ---- what is kept, and what it says ------------------------------------------------------

    [Fact]
    public void KeepingTheModelsAndTheIndexIsTheDefault()
    {
        UninstallPlan plan = Uninstall.Plan(purge: false, Measured);

        Assert.Equal(["models", "index", "logs", "settings"], plan.Keeps.Select(k => k.Label));
        Assert.Equal(0, plan.FreedBytes);
    }

    [Fact]
    public void PurgeDeletesEveryFolderThePrivacyPageSaysItDoes()
    {
        // PRIVACY.md: "findra.exe --uninstall --purge removes everything", and it names
        // %LOCALAPPDATA%\Findra\ and %APPDATA%\Findra\ - which includes the logs. A purge that
        // leaves a logs folder behind has not removed everything.
        UninstallPlan plan = Uninstall.Plan(purge: true, Measured);

        Assert.Equal(["models", "index", "logs", "settings"], plan.Deletes.Select(d => d.Label));
        Assert.Empty(plan.Keeps);
    }

    [Fact]
    public void TheSizeInThePromptIsTheOneThatWasMeasured()
    {
        // Spec §2a: "The prompt states the MEASURED size it would free ... not a vague warning."
        // The fixture is a gigabyte of models, deliberately far from the 2.93 GB declared total,
        // so a sentence built from Capabilities.TotalBytes rather than from this disk fails here
        // instead of rendering the same string as the number it is being compared against.
        string text = Uninstall.Describe(Uninstall.Plan(purge: true, Measured), @"C:\Program Files\Findra", purge: true);

        Assert.Contains(Sizes.Human(1_073_741_824), text, StringComparison.Ordinal);
        Assert.Contains(Sizes.Human(419_430_400), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Sizes.Human(Capabilities.TotalBytes(Capabilities.All)), text, StringComparison.Ordinal);
        // And the total it would free, which is the number the person is actually deciding about.
        Assert.Contains(Sizes.Human(Measured.Sum(m => m.Bytes)), text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFolderIsStillListedRatherThanFilteredOut()
    {
        // A zero row is an answer. Filtering it makes "why did it not free anything" unanswerable,
        // and a machine that never downloaded a model is the ordinary case.
        DataSize[] nothing = [new("models", @"C:\x\models", 0), new("index", @"C:\x\index", 0),
                              new("logs", @"C:\x\logs", 0), new("settings", @"C:\y", 0)];
        string text = Uninstall.Describe(Uninstall.Plan(purge: false, nothing), @"C:\Program Files\Findra", purge: false);

        Assert.Contains("models", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePromptNamesTheFolderTheRunningProgramCannotDelete()
    {
        // A source build has no installer, and the running executable cannot remove its own
        // directory - which on that route is somebody's git working copy. Saying which folder is
        // left is the honest answer; deleting it quietly would be the wrong one.
        string text = Uninstall.Describe(Uninstall.Plan(purge: false, Measured), @"D:\src\findra\publish", purge: false);

        Assert.Contains(@"D:\src\findra\publish", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePurgePromptSaysThatHandWrittenPalettesGoWithIt()
    {
        // --purge deletes %APPDATA%\Findra, which holds palettes.json. That is correct against
        // both the specification and PRIVACY.md, and it is somebody's own work, so it is named
        // rather than swept into "settings".
        string text = Uninstall.Describe(Uninstall.Plan(purge: true, Measured), @"C:\Program Files\Findra", purge: true);
        Assert.Contains("palettes", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- which way it went ---------------------------------------------------------------------

    [Fact]
    public void TheLogSaysWhichWayTheUninstallWentBeforeAnythingIsStopped()
    {
        // A real uninstall kept 2.93 GB of models, an index and a settings folder, and the log
        // recorded the task removal and the stopped processes and nothing at all about the choice
        // that produced them. Ten minutes went on working out which of the two runs had happened.
        // First, because a run that dies half way through still has to have said what it was
        // trying to do.
        var run = new Recorder();
        run.Run("--uninstall", "--quiet");

        Assert.Equal("say", run.Calls[0]);
        Assert.StartsWith("keeping", run.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void APurgeSaysItIsDeletingRatherThanKeeping()
    {
        var run = new Recorder();
        run.Run("--uninstall", "--purge", "--quiet");

        Assert.Equal("say", run.Calls[0]);
        Assert.StartsWith("deleting", run.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void ADryRunSaysNothing()
    {
        // It changes nothing and it runs FIRST - the installer takes the measurement before it
        // asks the question. A line here would put "keeping" in the log ahead of the purge that
        // the person went on to ask for, which is the same confusion in a new place.
        var run = new Recorder();
        run.Run("--uninstall", "--dry-run", "--quiet");

        Assert.Empty(run.Calls);
        Assert.Null(run.Said);
    }

    [Fact]
    public void TheLineNamesEveryFolderAndTheSizeThatWasMeasured()
    {
        // The measurement is the plan's own. A second walk of the disk here would be a second
        // number, and the two would disagree the moment anything was being written.
        string line = Uninstall.Announce(Uninstall.Plan(purge: false, Measured), purge: false);

        Assert.Contains("models", line, StringComparison.Ordinal);
        Assert.Contains("index", line, StringComparison.Ordinal);
        Assert.Contains("logs", line, StringComparison.Ordinal);
        Assert.Contains("settings", line, StringComparison.Ordinal);
        Assert.Contains(Sizes.Human(Measured.Sum(m => m.Bytes)), line, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePurgeLineCarriesTheSameSizeItIsAboutToFree()
    {
        UninstallPlan plan = Uninstall.Plan(purge: true, Measured);
        string line = Uninstall.Announce(plan, purge: true);

        Assert.Contains(Sizes.Human(plan.FreedBytes), line, StringComparison.Ordinal);
        Assert.DoesNotContain("keeping", line, StringComparison.Ordinal);
    }

    // ---- the process list ---------------------------------------------------------------------

    [Fact]
    public void TheUninstallerNeverStopsItself()
    {
        // "Stop every process called findra" kills the uninstaller in the middle of its own run,
        // after it has stopped the helper and before it has removed the task - which is the exact
        // state the specification calls a defect, reached by the most obvious implementation.
        var running = new Running(Interface: 200, Helper: 300, Others: [400, 999]);
        Assert.DoesNotContain(999, Uninstall.StopOrder(running, spare: [999]));
    }

    [Fact]
    public void TheUninstallerNeverStopsTheParentThatElevatedIt()
    {
        // The one the first draft missed. On the elevation path the unelevated findra --uninstall
        // relaunches itself with runas and WAITS for the exit code, so the parent is a live findra
        // process with a different pid. It lands in Others and gets killed mid-wait: the exit code
        // never propagates and, from a console, the command returns silently having half finished.
        var running = new Running(Interface: null, Helper: 300, Others: [777, 999]);
        IReadOnlyList<int> order = Uninstall.StopOrder(running, spare: [999, 777]);

        Assert.DoesNotContain(777, order);
        Assert.DoesNotContain(999, order);
        Assert.Contains(300, order);
    }

    [Fact]
    public void TheInterfaceGoesFirstAndTheHelperLast()
    {
        var running = new Running(Interface: 200, Helper: 300, Others: [400]);
        IReadOnlyList<int> order = Uninstall.StopOrder(running, spare: [999]);

        Assert.Equal(200, order[0]);
        Assert.Equal(300, order[^1]);
    }

    [Fact]
    public void AStrayIndexerLeftByACrashIsStoppedToo()
    {
        // The indexer normally dies with its parent's job object. One left by a crash still holds
        // the index file open, and a purge that cannot delete the index reports a failure the
        // person cannot act on.
        var running = new Running(Interface: null, Helper: null, Others: [400]);
        Assert.Contains(400, Uninstall.StopOrder(running, spare: [999]));
    }

    [Fact]
    public void WorkingOutWhoIsRunningNeverAsksTheHelperOverThePipe()
    {
        // The uninstaller used to ask the helper for its own process id on the name pipe. It can
        // never get an answer: the client connects with CurrentUserOnly, which compares the pipe's
        // OWNER against the client's token owner, the helper owns the pipe as the user SID so the
        // normal-integrity interface can connect, and an elevated process's token owner is
        // BUILTIN\Administrators. The uninstaller is elevated on every route that reaches this
        // (Decide sends the rest through a UAC relaunch or refuses), so the ask cost two seconds
        // and left "the helper did not answer" in the log of every successful uninstall.
        Running running = Uninstall.Discover(ui: 200, findra: [200, 300, 400]);

        Assert.Equal(200, running.Interface);
        Assert.Null(running.Helper);
        Assert.Equal([300, 400], running.Others);
    }

    [Fact]
    public void AHelperNothingCouldNameIsStoppedAllTheSame()
    {
        // The property that makes the line above safe to remove. Nothing is stopped because it was
        // identified; everything called findra is stopped, and identifying the interface only
        // decides what goes first.
        IReadOnlyList<int> order = Uninstall.StopOrder(Uninstall.Discover(ui: 200, findra: [200, 300]), spare: [999]);

        Assert.Equal(200, order[0]);
        Assert.Contains(300, order);
    }

    // ---- elevation ------------------------------------------------------------------------------

    [Fact]
    public void AnUnelevatedRunAsksForElevationRatherThanTryingAndFailingQuietly()
    {
        // Deleting a HighestAvailable task and killing an elevated helper both need administrator
        // rights. An implementation that just tries, logs "access denied" and exits 0 leaves the
        // orphan behind and tells the person it worked.
        Assert.Equal(Route.Elevate, Uninstall.Decide(elevated: false, relaunched: false));
    }

    [Fact]
    public void AnAlreadyRelaunchedRunThatIsStillNotElevatedStopsRatherThanLooping()
    {
        // The relaunch is a UAC prompt. On a machine where elevation is available but ineffective,
        // relaunching again is an endless chain of prompts, each of which looks to the person like
        // Findra breaking in a new way.
        Assert.Equal(Route.Fail, Uninstall.Decide(elevated: false, relaunched: true));
    }

    [Fact]
    public void AnElevatedRunJustGetsOnWithIt()
    {
        Assert.Equal(Route.Proceed, Uninstall.Decide(elevated: true, relaunched: false));
        Assert.Equal(Route.Proceed, Uninstall.Decide(elevated: true, relaunched: true));
    }

    // ---- measurement ------------------------------------------------------------------------------

    [Fact]
    public void TheFourFoldersAreTheOnesPathsAlreadyNames()
    {
        // Two derivations of the same four paths is one of them drifting later. Paths is the
        // single place the specification's §4 table lives in code; Measure reads it rather than
        // rebuilding %LOCALAPPDATA%\Findra by hand.
        IReadOnlyList<DataSize> sizes = Uninstall.Measure();

        Assert.Equal(Paths.Models, sizes.Single(s => s.Label == "models").Path);
        Assert.Equal(Paths.Index, sizes.Single(s => s.Label == "index").Path);
        Assert.Equal(Paths.Logs, sizes.Single(s => s.Label == "logs").Path);
        Assert.Equal(Paths.Config, sizes.Single(s => s.Label == "settings").Path);
    }

    [Fact]
    public void AFolderThatIsNotThereMeasuresZeroRatherThanThrowing()
    {
        // The ordinary state of a machine that never turned content indexing on. An exception here
        // aborts the uninstall before the scheduled task is removed.
        IReadOnlyList<DataSize> sizes = Uninstall.Measure(
            Path.Combine(_root, "nope-local"), Path.Combine(_root, "nope-roaming"));

        Assert.Equal(4, sizes.Count);
        Assert.All(sizes, s => Assert.Equal(0, s.Bytes));
    }

    [Fact]
    public void WhatIsMeasuredIsWhatIsOnTheDisk()
    {
        string local = Path.Combine(_root, "L", "Findra");
        string roaming = Path.Combine(_root, "R", "Findra");
        Directory.CreateDirectory(Path.Combine(local, "index", "deeper"));
        File.WriteAllBytes(Path.Combine(local, "index", "content.db"), new byte[4096]);
        // A nested file, because a Measure that does not recurse under-reports an index by most
        // of its size and the prompt then offers to free a fraction of what it frees.
        File.WriteAllBytes(Path.Combine(local, "index", "deeper", "vectors.bin"), new byte[2048]);

        Assert.Equal(6144, Uninstall.Measure(local, roaming).Single(s => s.Label == "index").Bytes);
    }

    // ---- deletion, which is the half that can do real harm -------------------------------------

    [Fact]
    public void APathOutsideFindrasOwnFoldersIsRefusedAndNeverHandedToTheDeleter()
    {
        // The worst bug available in this plan and one character wide: a Path.Combine with an
        // empty segment names %LOCALAPPDATA% itself, and the purge list is then a list of every
        // application's data.
        //
        // The first draft asserted containment about the paths Measure had just built out of its
        // own arguments, which cannot fail. This hands Delete a poisoned entry and a deleter that
        // fails the test if it is ever called.
        DataSize[] poisoned =
        [
            new("models", @"C:\Windows", 0),
            new("index", @"C:\Users\rae\AppData\Local", 0),
        ];

        IReadOnlyList<Removal> report = Uninstall.Delete(
            poisoned,
            roots: [@"C:\Users\rae\AppData\Local\Findra", @"C:\Users\rae\AppData\Roaming\Findra"],
            remove: path => { Assert.Fail($"the deleter was asked to remove '{path}'"); return null; });

        Assert.All(report, r => Assert.False(r.Removed));
        Assert.All(report, r => Assert.Contains("outside", r.Problem!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFolderInsideThemIsDeleted()
    {
        // Pairs with the test above, so neither can be satisfied by a Delete that refuses
        // everything - which would be a perfectly green uninstaller that never frees a byte.
        var asked = new List<string>();
        DataSize[] real = [new("models", @"C:\Users\rae\AppData\Local\Findra\models", 10)];

        IReadOnlyList<Removal> report = Uninstall.Delete(
            real, roots: [@"C:\Users\rae\AppData\Local\Findra"], remove: p => { asked.Add(p); return null; });

        Assert.Equal([@"C:\Users\rae\AppData\Local\Findra\models"], asked);
        Assert.True(report.Single().Removed);
    }

    [Fact]
    public void ARootIsNeverItsOwnChild()
    {
        // "Starts with the root" is true of the root itself, and deleting %LOCALAPPDATA%\Findra
        // wholesale would also take the logs the running uninstaller is writing to. Each entry has
        // to be strictly inside a root, or be the roaming folder that IS one - which is why the
        // roaming root is passed as its own entry and the containment check is by full path
        // rather than by prefix.
        IReadOnlyList<Removal> report = Uninstall.Delete(
            [new("models", @"C:\Users\rae\AppData\Local", 0)],
            roots: [@"C:\Users\rae\AppData\Local\Findra"],
            remove: _ => { Assert.Fail("the parent of a root was handed to the deleter"); return null; });

        Assert.False(report.Single().Removed);
    }

    [Fact]
    public void OneFolderThatWillNotGoDoesNotStopTheRest()
    {
        // An antivirus handle on the index, or the log file the running uninstaller has open - the
        // logs folder is in the purge list and the uninstaller writes to it. The dangerous half is
        // already done by this point (the task and the autostart entry go first), so the honest
        // behaviour is to carry on and report what survived rather than abort halfway.
        DataSize[] four =
        [
            new("models", @"C:\R\models", 0), new("index", @"C:\R\index", 0),
            new("logs", @"C:\R\logs", 0), new("settings", @"C:\R\settings", 0),
        ];

        IReadOnlyList<Removal> report = Uninstall.Delete(
            four, roots: [@"C:\R"],
            remove: p => p.EndsWith("index", StringComparison.Ordinal) ? "the file is in use" : null);

        Assert.Equal(4, report.Count);
        Assert.Equal(3, report.Count(r => r.Removed));
        Assert.Contains("in use", report.Single(r => r.Label == "index").Problem!, StringComparison.Ordinal);
    }

    // ---- the scheduled-task arguments -------------------------------------------------------------

    [Fact]
    public void TheTaskNameIsQuotedOnTheWayOutJustAsItIsOnTheWayIn()
    {
        // "Findra names helper" has spaces in it. Unquoted, schtasks reads /tn Findra and deletes
        // nothing, exits non-zero, and the orphan stays - silently, because nobody reads the exit
        // code of an uninstaller.
        Assert.Contains("\"Findra names helper\"", HelperTask.DeleteArgs(HelperTask.TaskName), StringComparison.Ordinal);
        Assert.Contains("/f", HelperTask.DeleteArgs(HelperTask.TaskName), StringComparison.Ordinal);
        Assert.Contains("\"Findra names helper\"", HelperTask.EndArgs(HelperTask.TaskName), StringComparison.Ordinal);
    }
}
