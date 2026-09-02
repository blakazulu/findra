using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;

namespace Findra.Startup;

public enum UninstallStep { StopInterface, StopStrays, StopHelper, RemoveScheduledTask, RemoveAutostart, DeleteData, ReportAppFolder }
public enum Route { Proceed, Elevate, Fail }

public readonly record struct DataSize(string Label, string Path, long Bytes);
public readonly record struct Removal(string Label, string Path, bool Removed, string? Problem);
public readonly record struct Running(int? Interface, int? Helper, IReadOnlyList<int> Others);

public sealed record UninstallPlan(
    IReadOnlyList<UninstallStep> Steps, IReadOnlyList<DataSize> Deletes, IReadOnlyList<DataSize> Keeps)
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
    /// What an uninstall does, in order. The order is the point: the helper holds a volume handle
    /// and the indexer holds the index file, so both are stopped before anything is deleted, and
    /// the scheduled task goes before the files its target lives in.
    /// </summary>
    public static UninstallPlan Plan(bool purge, IReadOnlyList<DataSize> measured)
    {
        ArgumentNullException.ThrowIfNull(measured);
        UninstallStep[] steps =
        [
            UninstallStep.StopInterface,
            UninstallStep.StopStrays,
            UninstallStep.StopHelper,
            // Always, and never inside the purge branch. Spec §2a: an uninstaller that misses this
            // is a defect, not an inconvenience.
            UninstallStep.RemoveScheduledTask,
            UninstallStep.RemoveAutostart,
            UninstallStep.DeleteData,
            UninstallStep.ReportAppFolder,
        ];

        return purge ? new UninstallPlan(steps, measured, []) : new UninstallPlan(steps, [], measured);
    }

    /// <summary>Unknown counts as "remove it". Query is three-valued so a locked-down machine is
    /// distinguishable from a fresh one; both still need the task gone, and deleting a task that is
    /// not there costs one non-zero exit code nobody acts on.
    ///
    /// <para>Its test covers this function and not its caller: nothing asserts that <c>Run</c>
    /// consults it, so writing <c>if (state == Registered)</c> at the call site instead would pass.
    /// End-to-end checklist step 33 is what proves the task is really gone.</para></summary>
    public static bool ShouldRemoveTask(HelperTaskState state) => true;

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
        // Last, because it holds an elevated volume handle and because the scheduled task would
        // restart it if the task were removed first.
        Add(running.Helper);
        return order;
    }

    /// <summary>The two folders everything Findra writes lives under, and the only two anything is
    /// ever deleted from.</summary>
    public static IReadOnlyList<string> Roots(string? localRoot = null, string? roamingRoot = null) =>
    [
        localRoot ?? Path.GetDirectoryName(Paths.Models)!,
        roamingRoot ?? Paths.Config,
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

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
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

        switch (Decide(IsElevated(), relaunched: parent > 0))
        {
            case Route.Elevate: return Relaunch(args);
            case Route.Fail:
                Console.Error.WriteLine("findra: this needs administrator rights to remove the scheduled task, " +
                                        "and the elevated run did not get them. Nothing was changed.");
                return 2;
        }

        if (!quiet) Console.Write(report);

        // 1..3 - stop everything, sparing this process and the parent that is waiting on it.
        StopAll(spare: parent > 0 ? [Environment.ProcessId, parent] : [Environment.ProcessId]);

        // 4..5 - the two that matter most, and the two that are done BEFORE any deletion, so a
        // failure in the delete loop still leaves a machine with no orphaned elevated task.
        bool taskGone = HelperTask.Unregister();
        Autostart.Clear();

        // 6 - the data, only if asked, and only inside Findra's own folders.
        IReadOnlyList<Removal> removed = purge ? Delete(plan.Deletes, Roots()) : [];

        // 7 - the one thing it cannot do.
        if (!quiet)
        {
            foreach (Removal r in removed.Where(r => !r.Removed))
                Console.Error.WriteLine($"findra: {r.Label} was not removed - {r.Problem}");
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

    /// <summary>Who is running. The interface says so in ui.json; the helper's pid comes back on
    /// the pipe's status reply, which is the same number --searchprobe prints. Everything else
    /// called findra is a stray.</summary>
    private static Running Discover()
    {
        int? ui = UiStatus.Read()?.Pid;
        int? helper = HelperPid();
        var others = new List<int>();
        try
        {
            foreach (Process p in Process.GetProcessesByName(UiStatus.ProcessName))
            {
                using (p)
                    if (p.Id != ui && p.Id != helper) others.Add(p.Id);
            }
        }
        catch (Exception ex) { Log.Warn("uninstall", "could not list processes: " + ex.Message); }
        return new Running(ui, helper, others);
    }

    /// <summary>The helper is asked over the pipe rather than looked for by name: the interface and
    /// the helper are both called findra, so a name check cannot tell them apart. The reply's
    /// <see cref="Pipe.StatusReply.ProcessId"/> is the same number --searchprobe prints.</summary>
    private static int? HelperPid()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return AskHelperAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Log.Warn("uninstall", "the helper did not answer: " + ex.Message); return null; }
    }

    private static async Task<int?> AskHelperAsync(CancellationToken ct)
    {
        await using Pipe.NameClient client =
            await Pipe.NameClient.ConnectAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        Pipe.StatusReply status = await client.StatusAsync(ct).ConfigureAwait(false);
        return status.ProcessId;
    }
}
