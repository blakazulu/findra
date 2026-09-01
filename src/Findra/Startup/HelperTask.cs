using System.Diagnostics;
using System.Security.Principal;

namespace Findra.Startup;

/// <summary>
/// Registers the one elevated thing Findra needs: a logon task that starts
/// `findra.exe --names` at HighestAvailable. One UAC prompt, once, ever.
/// </summary>
public static class HelperTask
{
    public const string TaskName = "Findra names helper";

    public static string BuildXml(string exePath) =>
$"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Keeps the Findra file-name index live. Findra does not work without it.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{WindowsIdentity.GetCurrent().Name}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{WindowsIdentity.GetCurrent().Name}</UserId>
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
      <Command>"{exePath}"</Command>
      <Arguments>--names</Arguments>
    </Exec>
  </Actions>
</Task>
""";

    /// <summary>
    /// Query the XML form, never the CSV form: `schtasks /query /fo csv` column
    /// headings are localized, the XML is not.
    /// </summary>
    public static bool IsRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks",
                $"/query /tn \"{TaskName}\" /xml ONE")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using Process? p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log.Warn("startup", "task query failed: " + ex.Message); return false; }
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
            p.WaitForExit(60_000);
            if (p.ExitCode != 0) { Log.Error("startup", $"schtasks /create exited {p.ExitCode}"); return false; }
            Log.Info("startup", "names helper task registered");
            return true;
        }
        catch (Exception ex) { Log.Error("startup", "task registration failed", ex); return false; }
        finally { try { File.Delete(xml); } catch { } }
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
            p?.WaitForExit(10_000);
            if (p?.ExitCode != 0) return false;
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
