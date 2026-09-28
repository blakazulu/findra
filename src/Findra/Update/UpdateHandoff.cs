using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Findra;

/// <summary>What a hand-off came back with. There is no "succeeded": an install that worked stopped
/// Findra on its way through, so this process only ever hears about the ones that did not.</summary>
public enum HandoffOutcome { DidNotRun, WingetFailed, WingetNothingNewer, WingetMissing }

public sealed record Handoff(HandoffOutcome Outcome, string Message);

/// <summary>Start a process and wait for it: its exit code and everything it printed. Throws
/// <see cref="Win32Exception"/> when it cannot start and <see cref="TimeoutException"/> when it
/// outlived its limit and was killed.</summary>
public delegate Task<(int ExitCode, string Output)> RunProcess(ProcessStartInfo start, CancellationToken ct);

/// <summary>
/// Starting the upgrade: the installer, or winget. Findra replaces none of its own files. The
/// installer or winget does, and the installer stops Findra before it touches anything, so a
/// hand-off that returns at all is one that installed nothing.
/// </summary>
public static class UpdateHandoff
{
    /// <summary>A progress window and no wizard pages; no message boxes; no reboot.</summary>
    public const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-";

    /// <summary>No <c>--silent</c>: winget's default runs the installer with its progress window,
    /// which is what the person watches once Findra has closed.</summary>
    public const string WingetArguments =
        "upgrade --id blakazulu.Findra --exact --accept-source-agreements --accept-package-agreements --disable-interactivity";

    public static readonly TimeSpan WingetLimit = TimeSpan.FromMinutes(20);

    /// <summary>winget's own code for "no newer version of this package".</summary>
    public const int WingetNoUpgrade = unchecked((int)0x8A15002B);

    /// <summary>ERROR_CANCELLED: the permission prompt was answered No.</summary>
    private const int Cancelled = 1223;

    public const string PermissionRefused = "Windows was not given permission to install it. Nothing was installed.";
    public const string NothingNewer = "winget found nothing newer to install. The catalogue can take a few days to list a new release.";
    public const string WingetMissing = "winget is not installed on this computer, so it could not run the upgrade.";

    public static ProcessStartInfo InstallerStart(string installer) =>
        new(installer, InstallerArguments) { UseShellExecute = true };

    public static ProcessStartInfo WingetStart() =>
        new("winget", WingetArguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

    public static async Task<Handoff> RunInstallerAsync(string installer, RunProcess run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            (int code, _) = await run(InstallerStart(installer), ct).ConfigureAwait(false);
            return new Handoff(HandoffOutcome.DidNotRun,
                $"The installer stopped before installing anything (exit code {code}). Nothing was installed.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return new Handoff(HandoffOutcome.DidNotRun, PermissionRefused);
        }
        catch (Win32Exception ex)
        {
            return new Handoff(HandoffOutcome.DidNotRun, "The installer could not be started: " + ex.Message);
        }
    }

    public static async Task<Handoff> RunWingetAsync(RunProcess run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            (int code, string output) = await run(WingetStart(), ct).ConfigureAwait(false);
            if (code == WingetNoUpgrade || code == 0)
                return new Handoff(HandoffOutcome.WingetNothingNewer, NothingNewer);
            return new Handoff(HandoffOutcome.WingetFailed,
                LastLine(output) ?? $"winget stopped with code 0x{code:X8}.");
        }
        catch (Win32Exception)
        {
            return new Handoff(HandoffOutcome.WingetMissing, WingetMissing);
        }
        catch (TimeoutException)
        {
            return new Handoff(HandoffOutcome.WingetFailed, "winget did not finish within 20 minutes, so it was stopped.");
        }
    }

    /// <summary>The last line of <paramref name="output"/> a person can read. winget redraws its
    /// progress with carriage returns and spins with backspaces, so a line is what follows the last
    /// carriage return, with control characters dropped.</summary>
    public static string? LastLine(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (string raw in output.Split('\n').Reverse())
        {
            string line = raw;
            int cr = line.TrimEnd('\r').LastIndexOf('\r');
            if (cr >= 0) line = line[(cr + 1)..];
            var sb = new StringBuilder(line.Length);
            foreach (char c in line)
                if (!char.IsControl(c)) sb.Append(c);
            string clean = sb.ToString().Trim().TrimStart('-', '\\', '|', '/').Trim();
            if (clean.Length > 0) return clean;
        }
        return null;
    }

    /// <summary>The real runner. Both streams are read as they arrive, never one after the other,
    /// so a process that fills one pipe while this waits on the other cannot hang both; past
    /// <paramref name="limit"/> the process tree is killed.</summary>
    public static RunProcess Real(TimeSpan limit) => async (start, ct) =>
    {
        using Process p = Process.Start(start) ?? throw new Win32Exception("the process did not start");
        Task<string> stdout = start.RedirectStandardOutput ? p.StandardOutput.ReadToEndAsync(ct) : Task.FromResult("");
        Task<string> stderr = start.RedirectStandardError ? p.StandardError.ReadToEndAsync(ct) : Task.FromResult("");

        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
        within.CancelAfter(limit);
        try
        {
            await p.WaitForExitAsync(within.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{start.FileName} did not finish within {limit}");
        }

        return (p.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    };
}
