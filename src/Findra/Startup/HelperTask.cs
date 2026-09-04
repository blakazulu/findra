using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Principal;

namespace Findra.Startup;

/// <summary>What a scheduled-task query could establish - including that it could not.</summary>
public enum HelperTaskState { Registered, NotRegistered, Unknown }

/// <summary>
/// Registers the one elevated thing Findra needs: a logon task that starts
/// `findra.exe --names` at HighestAvailable. One UAC prompt, once, ever.
/// </summary>
public static class HelperTask
{
    public const string TaskName = "Findra names helper";

    /// <summary>
    /// The task definition. Both interpolated values are XML-escaped: `&amp;` is legal in a
    /// Windows path (`D:\Tools &amp; Utils\findra.exe`) and in an account name, and unescaped it
    /// produces XML schtasks rejects - registration fails and the helper never starts, on
    /// someone else's machine rather than this one.
    /// </summary>
    public static string BuildXml(string exePath)
    {
        string exe = SecurityElement.Escape(exePath) ?? "";
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name) ?? "";
        return
$"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Keeps the Findra file-name index live. Without it Findra still runs, with no file names to search.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>6</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"{exe}"</Command>
      <Arguments>--names</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    /// <summary>
    /// Query the XML form, never the CSV form: `schtasks /query /fo csv` column
    /// headings are localized, the XML is not.
    ///
    /// Three-valued on purpose. Collapsing "not registered" and "the query itself
    /// failed" into one `false` makes a locked-down machine look identical to a fresh
    /// one, and the probe exists precisely to tell a stranger which of those they have.
    /// `Detail` carries schtasks' own stderr when there is any - never parsed, only
    /// shown, because those messages are localized too.
    /// </summary>
    public static (HelperTaskState State, string Detail) Query()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks",
                $"/query /tn \"{TaskName}\" /xml ONE")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using Process? p = Process.Start(psi);
            if (p is null) return (HelperTaskState.Unknown, "schtasks could not be started");

            // Redirecting without draining can deadlock: the child blocks writing into a
            // full pipe buffer while we sit in WaitForExit. `/xml ONE` prints the entire
            // task definition on success, which is comfortably enough to fill it. Start
            // both reads before waiting.
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Log.Warn("startup", "schtasks /query did not return within 5s");
                return (HelperTaskState.Unknown, "schtasks did not return within 5s");
            }

            Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(1));
            if (p.ExitCode == 0) return (HelperTaskState.Registered, "");

            // A non-zero exit is almost always "no such task", but schtasks says so in the
            // user's own language, so do not try to read it - report the state and hand the
            // message through untouched. Guessing from localized text is the same mistake
            // as parsing localized CSV headings.
            string why = stderr.IsCompletedSuccessfully ? stderr.Result.Trim() : "";
            if (why.Length > 0) Log.Warn("startup", $"schtasks /query exited {p.ExitCode}: {why}");
            return (HelperTaskState.NotRegistered, why);
        }
        catch (Exception ex)
        {
            Log.Warn("startup", "task query failed: " + ex.Message);
            return (HelperTaskState.Unknown, ex.Message);
        }
    }

    public static bool Register(string exePath)
    {
        string xml = Path.Combine(Path.GetTempPath(), $"findra-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, BuildXml(exePath), new System.Text.UnicodeEncoding(false, true));
            var psi = new ProcessStartInfo("schtasks", $"/create /tn \"{TaskName}\" /xml \"{xml}\" /f")
            { UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using Process? p = Process.Start(psi);
            if (p is null) return false;

            // The wait's own answer, not a discarded bool. A UAC prompt nobody answers leaves
            // schtasks running for as long as the dialog is on screen, and reading ExitCode on a
            // live process throws - which lands in the catch below and is reported as a
            // registration failure with an unrelated exception attached.
            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Log.Error("startup", "schtasks /create did not return within 60s; the elevation prompt was probably left unanswered");
                return false;
            }

            if (p.ExitCode != 0) { Log.Error("startup", $"schtasks /create exited {p.ExitCode}"); return false; }
            Log.Info("startup", "names helper task registered");
            return true;
        }
        catch (Exception ex) { Log.Error("startup", "task registration failed", ex); return false; }
        finally { try { File.Delete(xml); } catch { } }
    }

    public static string EndArgs(string taskName) => $"/end /tn \"{taskName}\"";
    public static string DeleteArgs(string taskName) => $"/delete /tn \"{taskName}\" /f";

    /// <summary>
    /// Stop the task and remove it. True when the task is gone afterwards, INCLUDING the case
    /// where it was never there: "no such task" and "removed" are the same outcome from the
    /// caller's point of view, and the only thing that matters is that nothing is left pointing at
    /// a binary that is about to be deleted.
    ///
    /// <para><c>/end</c> first, because deleting a task whose instance is running leaves the
    /// process behind - and that process is the elevated one holding a volume handle.</para>
    /// </summary>
    public static bool Unregister() => Unregister(RunSchtasks, () => Query().State);

    /// <summary>
    /// <see cref="Unregister()"/> with the two things it cannot do in a test passed in: running
    /// schtasks, and asking afterwards whether the task is still there.
    ///
    /// <para>Public rather than internal only so the sequence can be tested: this assembly grants
    /// no <c>InternalsVisibleTo</c>, and every other seam the tests reach is public too. It is a
    /// seam, not an API - nothing but <see cref="Unregister()"/> should call it.</para>
    /// </summary>
    public static bool Unregister(Func<string, int> run, Func<HelperTaskState> query)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(query);

        run(EndArgs(TaskName));                       // may fail: it is not running. Not an error.
        int code = run(DeleteArgs(TaskName));

        // A non-zero exit is almost always "no such task", said in the user's own language - which
        // is why it is not parsed. Confirm by asking instead: Query reads the XML form, which is
        // the only answer that is not localized.
        bool gone = query() != HelperTaskState.Registered;

        Log.Info("uninstall", gone
            ? "the names helper task is removed"
            : $"the names helper task could NOT be removed (schtasks exited {code.ToString(CultureInfo.InvariantCulture)}); " +
              "it would start an elevated process pointing at a binary that is being deleted");
        return gone;
    }

    private static int RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using Process? p = Process.Start(psi);
            if (p is null) return -1;

            // Both reads started before the wait, and never ReadToEnd. Two redirected streams
            // drained one after the other deadlock the moment the child fills the one nobody is
            // reading: it blocks writing, this blocks reading the other, and the timeout below is
            // never reached at all - an unbounded hang inside an elevated uninstaller.
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Log.Warn("uninstall", $"schtasks {arguments} did not return within 10s");
                return -1;
            }

            Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(1));
            return p.ExitCode;
        }
        catch (Exception ex) { Log.Warn("uninstall", $"schtasks {arguments} failed: {ex.Message}"); return -1; }
    }

    /// <summary>
    /// Registration is the one thing that can fail on a stranger's machine in a way
    /// Findra cannot fix. Failing here is not fatal - the caller falls back to
    /// whatever can be read unelevated and says so.
    /// </summary>
    public static bool EnsureRunning()
    {
        // Ask the pipe, not the process list: the UI and the helper are both named
        // "findra", so a name check would always find this very process and conclude
        // the helper is up.
        if (IsHelperAnswering()) return true;
        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/run /tn \"{TaskName}\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using Process? p = Process.Start(psi);
            if (p is null) return false;
            // The wait's own answer, not a discarded bool - the rule Register and RunSchtasks are
            // both written under, and the one call site still breaking it. Reading ExitCode on a
            // process that has not exited THROWS, so a schtasks hung for ten seconds was reported
            // through the catch below as "could not start the helper" plus an InvalidOperation
            // message naming neither the timeout nor the task, and the schtasks process was left
            // running - alone among this file's callers.
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Log.Warn("startup", "schtasks /run did not answer within ten seconds; the helper may still be starting");
                return false;
            }
            if (p.ExitCode != 0) return false;
        }
        catch (Exception ex) { Log.Warn("startup", "could not start the helper: " + ex.Message); return false; }

        // schtasks returns as soon as it has asked; the helper still has to enumerate.
        for (int i = 0; i < 20; i++)
        {
            if (IsHelperAnswering()) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool IsHelperAnswering()
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", Pipe.NameServer.PipeName, System.IO.Pipes.PipeDirection.InOut);
            pipe.Connect(200);
            return true;
        }
        catch { return false; }
    }
}
