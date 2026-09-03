using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;

namespace Findra.Startup;

public enum Route { Proceed, Elevate, Fail }

public readonly record struct DataSize(string Label, string Path, long Bytes);
public readonly record struct Removal(string Label, string Path, bool Removed, string? Problem);
public readonly record struct Running(int? Interface, int? Helper, IReadOnlyList<int> Others);

public sealed record UninstallPlan(IReadOnlyList<DataSize> Deletes, IReadOnlyList<DataSize> Keeps)
{
    public long FreedBytes => Deletes.Sum(d => d.Bytes);
}

/// <summary>
/// What `findra --uninstall` does, and the line it will not cross. Spec §2a: leaving the
/// HighestAvailable scheduled task behind points an elevated logon task at a binary that no
/// longer exists, on somebody else's machine, and that is a defect rather than an inconvenience.
/// </summary>
public static class Uninstall
{
    private static readonly CultureInfo Fixed = CultureInfo.InvariantCulture;

    /// <summary>
    /// What an uninstall deletes and what it keeps. Purge decides that and nothing else: the
    /// scheduled task, the autostart entry and the three processes go either way, so they are not
    /// in the plan at all. Spec §2a - an uninstaller that misses the task is a defect rather than
    /// an inconvenience, and a plan that could express "keep the task" is one somebody eventually
    /// reads as an option.
    ///
    /// <para>The ORDER lives in <see cref="Run(string[])"/>, which is the only thing that performs
    /// it. It used to be listed here as a second enum nothing executed: both order tests then
    /// asserted a literal array against its own literals and stayed green with the task removal
    /// deleted from <c>Run</c> entirely. The order is now recorded as it happens, through the seam
    /// the second overload of <c>Run</c> takes.</para>
    /// </summary>
    public static UninstallPlan Plan(bool purge, IReadOnlyList<DataSize> measured)
    {
        ArgumentNullException.ThrowIfNull(measured);
        return purge ? new UninstallPlan(measured, []) : new UninstallPlan([], measured);
    }

    public static Route Decide(bool elevated, bool relaunched) =>
        elevated ? Route.Proceed : relaunched ? Route.Fail : Route.Elevate;

    /// <summary>
    /// Which process ids to stop, in order, skipping every id in <paramref name="spare"/>.
    ///
    /// <para><paramref name="spare"/> is this process AND the parent that relaunched it. On the
    /// elevation path the unelevated run starts an elevated copy and waits for its exit code, so
    /// the parent is a live findra process the child has no other reason to spare - and killing it
    /// mid-wait swallows the exit code and leaves the command looking as though it did nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> StopOrder(Running running, IReadOnlyList<int> spare)
    {
        ArgumentNullException.ThrowIfNull(spare);
        var order = new List<int>();
        void Add(int? pid) { if (pid is { } p && !spare.Contains(p) && !order.Contains(p)) order.Add(p); }

        // The interface first: the indexer sits in its kill-on-close job and dies with it, so
        // whatever is left in Others afterwards is a stray from a crash rather than a live child.
        Add(running.Interface);
        foreach (int p in running.Others ?? []) Add(p);
        // Last, WHEN IT IS KNOWN AT ALL, because it holds an elevated volume handle. Null is an
        // ordinary answer here rather than a missing one: an uninstall cannot name the helper (see
        // Discover), and it then arrives in Others and is stopped in there. Nothing depends on this
        // line for the helper to be stopped - only for where in the order it comes.
        Add(running.Helper);
        return order;
    }

    /// <summary>The two folders everything Findra writes lives under, and the only two anything is
    /// ever deleted from. No overrides: <see cref="Delete"/> takes its roots as an argument, which
    /// is the seam the containment tests drive, and a second injectable copy here was two
    /// parameters that could only ever be null.</summary>
    public static IReadOnlyList<string> Roots() =>
    [
        Path.GetDirectoryName(Paths.Models)!,
        Paths.Config,
    ];

    /// <summary>
    /// The four folders, measured. One derivation of these paths, not two: <see cref="Paths"/> is
    /// where the specification's §4 table lives in code, and a second copy here is one of them
    /// drifting later.
    ///
    /// <para>A folder that is not there is zero bytes rather than an exception - a machine that
    /// never turned content indexing on is the ordinary case, and an exception here would abort
    /// the uninstall before the scheduled task was removed.</para>
    /// </summary>
    public static IReadOnlyList<DataSize> Measure(string? localRoot = null, string? roamingRoot = null)
    {
        string models = localRoot is null ? Paths.Models : Path.Combine(localRoot, "models");
        string index = localRoot is null ? Paths.Index : Path.Combine(localRoot, "index");
        string logs = localRoot is null ? Paths.Logs : Path.Combine(localRoot, "logs");
        string settings = roamingRoot ?? Paths.Config;

        return
        [
            new DataSize("models", models, Bytes(models)),
            new DataSize("index", index, Bytes(index)),
            new DataSize("logs", logs, Bytes(logs)),
            new DataSize("settings", settings, Bytes(settings)),
        ];
    }

    private static long Bytes(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch (IOException) { }
            return total;
        }
        catch (Exception ex) { Log.Warn("uninstall", $"could not measure {dir}: {ex.Message}"); return 0; }
    }

    /// <summary>
    /// Delete what the plan says to delete, and nothing else, ever.
    ///
    /// <para>Every entry must sit strictly INSIDE one of <paramref name="roots"/>, or be one of
    /// them. A root's parent is refused - "starts with the root" is true of the parent's own
    /// prefix in a naive comparison, and deleting <c>%LOCALAPPDATA%</c> is the worst thing this
    /// codebase could do to somebody. An entry that fails the check is never handed to the
    /// deleter at all.</para>
    ///
    /// <para>One folder that will not go does not stop the rest. The dangerous half - the task and
    /// the autostart entry - is already done by the time this runs, so carrying on and reporting
    /// what survived beats aborting with a half-deleted profile and no account of it.</para>
    /// </summary>
    public static IReadOnlyList<Removal> Delete(
        IReadOnlyList<DataSize> deletes, IReadOnlyList<string> roots, Func<string, string?>? remove = null)
    {
        ArgumentNullException.ThrowIfNull(deletes);
        ArgumentNullException.ThrowIfNull(roots);
        remove ??= RemoveDirectory;

        var report = new List<Removal>(deletes.Count);
        foreach (DataSize d in deletes)
        {
            if (!Inside(d.Path, roots))
            {
                Log.Error("uninstall", $"refusing to delete '{d.Path}': it is outside Findra's own folders");
                report.Add(new Removal(d.Label, d.Path, false, "outside Findra's own folders"));
                continue;
            }

            string? problem = remove(d.Path);
            report.Add(new Removal(d.Label, d.Path, problem is null, problem));
            Log.Info("uninstall", problem is null
                ? $"removed {d.Label} ({Sizes.Human(d.Bytes)})"
                : $"could not remove {d.Label}: {problem}");
        }
        return report;
    }

    private static bool Inside(string path, IReadOnlyList<string> roots)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (string root in roots)
        {
            string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase)) return true;
            // The separator matters: without it "...\Findra" would also accept "...\FindraOther".
            if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? RemoveDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return null;   // already gone is done
            Directory.Delete(path, recursive: true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// The one line the log gets before anything is stopped or removed, and the only place the
    /// answer to the only question an uninstall asks is written down.
    ///
    /// <para>A run that kept 2.93 GB of models and a run that deleted them left identical logs -
    /// the task removal, the stopped processes, and nothing at all saying which of the two had
    /// been asked for. The size is the plan's own measurement and never a second walk of the
    /// disk: two numbers for one uninstall is how a prompt and a log come to disagree about what
    /// happened.</para>
    /// </summary>
    public static string Announce(UninstallPlan plan, bool purge)
    {
        ArgumentNullException.ThrowIfNull(plan);
        IReadOnlyList<DataSize> rows = purge ? plan.Deletes : plan.Keeps;
        return (purge ? "deleting " : "keeping ") + Listed(rows) +
               $" ({Sizes.Human(rows.Sum(r => r.Bytes))})";
    }

    private static string Listed(IReadOnlyList<DataSize> rows)
    {
        string[] names = [.. rows.Select(r => r.Label)];
        return names.Length switch
        {
            0 => "nothing",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }

    /// <summary>What the person is told before anything happens. Every size in it is measured, not
    /// declared (spec §2a).</summary>
    public static string Describe(UninstallPlan plan, string appFolder, bool purge)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sb = new StringBuilder();
        sb.AppendLine("Findra will stop the interface, the indexer and the name helper, then remove:");
        sb.AppendLine("  the scheduled task that starts the name helper at sign-in");
        sb.AppendLine("  the start-at-sign-in entry, if there is one");
        sb.AppendLine();

        void Rows(IReadOnlyList<DataSize> rows)
        {
            foreach (DataSize d in rows)
                sb.AppendLine($"  {d.Label,-9} {Sizes.Human(d.Bytes),9}  {d.Path}");
        }

        if (purge)
        {
            sb.AppendLine("It will also delete, because --purge was given:");
            Rows(plan.Deletes);
            sb.AppendLine();
            // Named rather than swept into "settings": palettes.json is somebody's own work.
            sb.AppendLine("That includes any palettes.json you wrote yourself.");
            sb.AppendLine($"It frees {Sizes.Human(plan.FreedBytes)}.");
        }
        else
        {
            sb.AppendLine("It will keep:");
            Rows(plan.Keeps);
            sb.AppendLine();
            sb.AppendLine("Run findra --uninstall --purge to delete those too - including any " +
                          "palettes.json you wrote yourself - freeing " +
                          $"{Sizes.Human(plan.Keeps.Sum(k => k.Bytes))}.");
        }

        sb.AppendLine();
        sb.AppendLine($"It cannot delete the folder it is running from: {appFolder}");
        sb.AppendLine("Remove that yourself, or let the installer's uninstaller do it.");
        return sb.ToString();
    }

    /// <summary>The report --dry-run leaves for the installer to read. Inno cannot capture a child
    /// process's standard output, and the alternative - the installer estimating the size itself -
    /// is exactly the vague warning spec §2a rejects.</summary>
    public static string ReportFile =>
        Path.Combine(Path.GetTempPath(), "findra-uninstall.txt");

    public static int Run(string[] args) =>
        Run(args, HelperTask.Unregister, Autostart.Clear,
            spare => StopAll(spare),
            deletes =>
            {
                IReadOnlyList<Removal> removed = Delete(deletes, Roots());
                SweepLooseFiles(Roots());
                return removed;
            },
            forgetTheWelcomeScreen: ForgetTheWelcomeScreen);

    /// <summary>
    /// The files sitting LOOSE in Findra's two data folders, beside the four directories the
    /// report prices. Purge only - a keep run keeps them, which is what keep means.
    ///
    /// <para><c>ui.json</c> is the one there is today. It is written into
    /// <c>%LOCALAPPDATA%\Findra</c> itself rather than into <c>models</c>, <c>index</c> or
    /// <c>logs</c>, so not one row of the plan covers it, and the only code that removes it is the
    /// interface's own shutdown - which an uninstall never reaches, because it KILLS the interface
    /// rather than asking it to close. So a purge that priced 2.99 GB and said it would take the
    /// settings with it left the folder standing, holding a stale pid and a hotkey. Same shape as
    /// <c>installed-by.txt</c> surviving the installer's uninstall: one small file nobody listed,
    /// keeping a whole directory alive.</para>
    ///
    /// <para>Files, and only at the top level. <c>logs</c> is a directory and it is in use - this
    /// very removal is being written into it - so anything reaching for the root itself would fail
    /// on every purge and report a clean uninstall as a failed one.</para>
    /// </summary>
    public static IReadOnlyList<Removal> SweepLooseFiles(
        IReadOnlyList<string> roots, Func<string, string?>? remove = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        remove ??= RemoveFile;

        var report = new List<Removal>();
        foreach (string root in roots)
        {
            string[] loose;
            try { loose = Directory.Exists(root) ? Directory.GetFiles(root) : []; }
            catch (Exception ex) { Log.Warn("uninstall", $"could not read {root}: {ex.Message}"); continue; }

            foreach (string file in loose)
            {
                string name = Path.GetFileName(file);
                string? problem = remove(file);
                report.Add(new Removal(name, file, problem is null, problem));
                Log.Info("uninstall", problem is null
                    ? $"removed {name}"
                    : $"could not remove {name}: {problem}");
            }
        }
        return report;
    }

    private static string? RemoveFile(string path)
    {
        try { File.Delete(path); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Put <c>FirstRunDone</c> back to false in the settings this uninstall is KEEPING.
    ///
    /// <para>The flag means "the welcome screen has been answered on this installation", and an
    /// uninstall is the end of an installation. It has to be cleared here rather than left to the
    /// next launch to notice, because what the screen produces is a state on the machine and the
    /// uninstall has just taken that state away: the <c>HighestAvailable</c> logon task is removed
    /// on every route through <see cref="Run(string[])"/>, and the welcome screen is the only
    /// surface that registers it. Nothing on an ordinary launch does.</para>
    ///
    /// <para>So a reinstall over a kept config used to come up with the screen skipped and the
    /// task gone, and that is not a cosmetic loss - it is half the product. Name search answers
    /// nothing, because the names live in the helper the task starts. The content queue is fed
    /// from the USN journal through that same helper, so the feeder times out, the queue stays
    /// empty, and pressing "Start now" starts an indexer that finds nothing to do and goes idle in
    /// a tenth of a second. Three unrelated-looking complaints, one missing task.</para>
    ///
    /// <para>A purge deletes the settings file outright, so this writes nothing on that route -
    /// <c>LoadFromDisk</c> answers a default <c>Config</c> for a file that is not there, and a
    /// default has the flag false already.</para>
    /// </summary>
    private static void ForgetTheWelcomeScreen()
    {
        Config c = Config.LoadFromDisk();
        if (!c.FirstRunDone) return;
        (c with { FirstRunDone = false }).Save();
    }

    /// <summary>
    /// The uninstall, with every effect it has on the machine passed in.
    ///
    /// <para>This overload exists because the version without it could not be run by a test at
    /// all - it stopped real processes, ran schtasks elevated, wrote the registry and deleted
    /// folders - so the two tests that covered the order read <c>Run</c>'s own SOURCE and asserted
    /// on where four names appeared in it. That is blind to what the code does with them:
    /// wrapping the whole removal block in <c>if (!quiet)</c> keeps every name in place and in
    /// order, and disables removal on exactly the route the installer takes, leaving the
    /// HighestAvailable task pointing at a binary that is about to be deleted.</para>
    ///
    /// <para>Public rather than internal only so the tests can reach it: this assembly grants no
    /// <c>InternalsVisibleTo</c>, and every other seam the tests reach is public too. It is a
    /// seam, not an API - nothing but <see cref="Run(string[])"/> should call it.</para>
    ///
    /// <para><paramref name="elevated"/> is the sixth thing a test cannot do for real. Left null
    /// it is the real check, which is what the shipped path uses.</para>
    /// </summary>
    public static int Run(string[] args, Func<bool> unregisterTask, Action clearAutostart,
                          Action<IReadOnlyList<int>> stop,
                          Func<IReadOnlyList<DataSize>, IReadOnlyList<Removal>> delete,
                          Action forgetTheWelcomeScreen,
                          Func<bool>? elevated = null, Action<string>? announce = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(unregisterTask);
        ArgumentNullException.ThrowIfNull(clearAutostart);
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(delete);
        ArgumentNullException.ThrowIfNull(forgetTheWelcomeScreen);
        elevated ??= IsElevated;
        announce ??= line => Log.Info("uninstall", line);

        bool purge = args.Contains("--purge", StringComparer.OrdinalIgnoreCase);
        bool quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
        bool dry = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        int parent = ParentPid(args);

        string appFolder = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        UninstallPlan plan = Plan(purge, Measure());
        string report = Describe(plan, appFolder, purge);

        if (dry)
        {
            // Reads four directories and writes one temp file. Nothing here needs elevation, so
            // Decide is deliberately not consulted: a dry run must never raise a UAC prompt.
            Console.Write(report);
            if (quiet) { try { File.WriteAllText(ReportFile, report); } catch (IOException) { } }
            return 0;
        }

        switch (Decide(elevated(), relaunched: parent > 0))
        {
            case Route.Elevate: return Relaunch(args);
            case Route.Fail:
                Console.Error.WriteLine("findra: this needs administrator rights to remove the scheduled task, " +
                                        "and the elevated run did not get them. Nothing was changed.");
                return 2;
        }

        // Before anything is stopped or removed, and before the report, which a quiet run does
        // not print at all: an uninstall that leaves no record of which way it went is one nobody
        // can answer questions about afterwards.
        announce(Announce(plan, purge));

        if (!quiet) Console.Write(report);

        // Stop everything first, sparing this process and the parent that is waiting on it.
        stop(parent > 0 ? [Environment.ProcessId, parent] : [Environment.ProcessId]);

        // Then the two that matter most, and the two that are done BEFORE any deletion, so a
        // failure in the delete loop still leaves a machine with no orphaned elevated task.
        // Unconditionally, and never behind a check of what Query said. Query is three-valued so
        // that a locked-down machine is distinguishable from a fresh one, but both still need the
        // task gone: treating Unknown as "nothing to do" is how the orphan survives on exactly the
        // machines least able to clear it by hand. Removing a task that was not there costs one
        // non-zero exit code nobody acts on, which is the cheaper of the two mistakes by a wide
        // margin. Only a real elevated uninstall proves it is really gone, which is why the
        // end-to-end checklist carries that step.
        bool taskGone = unregisterTask();
        clearAutostart();

        // And then the record of the welcome screen, on every route, purge or keep. The task has
        // just gone and the screen is the only thing that registers it, so a settings file that
        // still says the screen was answered is a reinstall with no name search and no content
        // queue. Before the deletes rather than after, so a folder that will not go does not take
        // this with it.
        forgetTheWelcomeScreen();

        // Then the data, only if asked, and only inside Findra's own folders.
        IReadOnlyList<Removal> removed = purge ? delete(plan.Deletes) : [];

        // Last, the one thing it cannot do.
        if (!quiet)
        {
            foreach (Removal r in removed.Where(r => !r.Removed))
                Console.Error.WriteLine($"findra: {r.Label} was not removed - {r.Problem}: {r.Path}");
            Console.WriteLine($"findra: delete {appFolder} yourself to finish removing it.");
        }

        Log.Flush();
        return taskGone && removed.All(r => r.Removed) ? 0 : 1;
    }

    /// <summary>`--relaunched &lt;pid&gt;`. The pid travels because the elevated child cannot find
    /// its own parent reliably once the parent is waiting on it, and killing that parent is the
    /// bug StopOrder's spare list exists to prevent.</summary>
    private static int ParentPid(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, "--relaunched", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, Fixed, out int pid) ? pid : 0;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) { Log.Warn("uninstall", "could not read the elevation state: " + ex.Message); return false; }
    }

    private static int Relaunch(string[] args)
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return 2;

        // Verb = runas is the one UAC prompt. A cancelled prompt throws Win32Exception 1223, which
        // is a decision rather than a fault: say so and change nothing.
        string arguments = string.Join(' ', args.Concat(["--relaunched", Environment.ProcessId.ToString(Fixed)]));
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(exe, arguments)
            { UseShellExecute = true, Verb = "runas", CreateNoWindow = true });
            if (p is null) return 2;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.Error.WriteLine("findra: administrator rights were declined, so nothing was removed. " +
                                    "The scheduled task is still registered.");
            return 2;
        }
        catch (Exception ex) { Log.Error("uninstall", "could not relaunch elevated", ex); return 2; }
    }

    /// <summary>`--stop`, and the first three steps of an uninstall. Also what the installer runs
    /// before it replaces files: Inno's own CloseApplications only closes windowed applications,
    /// and two of Findra's three processes have no window at all.</summary>
    public static int StopAll() => StopAll(spare: [Environment.ProcessId]);

    private static int StopAll(IReadOnlyList<int> spare)
    {
        foreach (int pid in StopOrder(Discover(), spare))
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                Log.Info("uninstall", $"stopped process {pid.ToString(Fixed)}");
            }
            catch (ArgumentException) { }        // already gone between Discover and here
            catch (Exception ex) { Log.Warn("uninstall", $"could not stop {pid.ToString(Fixed)}: {ex.Message}"); }
        }
        return 0;
    }

    /// <summary>
    /// Who is running, out of what an uninstall can actually see. The interface names itself in
    /// ui.json; everything else called findra is a process to stop.
    ///
    /// <para>THE HELPER IS NOT NAMED HERE, and that is the answer rather than a gap. It used to be
    /// asked for its own process id over the name pipe, and from an uninstall that ask can never
    /// succeed: the client connects with <c>PipeOptions.CurrentUserOnly</c>, which compares the
    /// pipe's OWNER against the connecting token's owner; the helper owns the pipe as the user SID
    /// so the normal-integrity interface can connect at all, and an elevated process's token owner
    /// is BUILTIN\Administrators. Every route that reaches an uninstall is elevated - the rest go
    /// through a UAC relaunch or are refused - so the round trip spent two seconds failing and left
    /// "the helper did not answer" in the log of every successful uninstall, above the line saying
    /// the helper had been stopped anyway.</para>
    ///
    /// <para>Nothing is given up with it. Knowing which process is the helper only decides where it
    /// comes in <see cref="StopOrder"/>; every findra process is stopped either way, which is the
    /// property that makes not knowing safe.</para>
    /// </summary>
    public static Running Discover(int? ui, IReadOnlyList<int> findra)
    {
        ArgumentNullException.ThrowIfNull(findra);
        return new Running(ui, Helper: null, Others: [.. findra.Where(p => p != ui)]);
    }

    private static Running Discover() => Discover(UiStatus.Read()?.Pid, FindraProcesses());

    private static IReadOnlyList<int> FindraProcesses()
    {
        var pids = new List<int>();
        try
        {
            foreach (Process p in Process.GetProcessesByName(UiStatus.ProcessName))
                using (p) pids.Add(p.Id);
        }
        catch (Exception ex) { Log.Warn("uninstall", "could not list processes: " + ex.Message); }
        return pids;
    }
}
