using System.Diagnostics;

namespace Findra.Startup;

/// <summary>
/// Whether a name helper that has just started stays. The task starts it at every sign-in, and
/// Findra starts it through the same task every time it opens, so the helper runs while Findra
/// does: it stays when Findra is open, when Findra starts at sign-in too, or when somebody typed
/// <c>findra --names</c> at a prompt. Anything else is a sign-in that does not start Findra, and
/// an elevated findra.exe on its own there is a process nobody asked for.
/// </summary>
public static class HelperStart
{
    /// <summary>Asked in this order, and no further than the first yes.</summary>
    public static bool ShouldStay(Func<bool> interfaceRunning, Func<bool> startsAtSignIn, Func<bool> fromATerminal)
    {
        ArgumentNullException.ThrowIfNull(interfaceRunning);
        ArgumentNullException.ThrowIfNull(startsAtSignIn);
        ArgumentNullException.ThrowIfNull(fromATerminal);
        return interfaceRunning() || startsAtSignIn() || fromATerminal();
    }

    /// <summary>
    /// Another findra.exe in this Windows session: the interface, which is what runs the task from
    /// inside Findra, or its indexer child. A copy in another session is somebody else's Findra.
    /// </summary>
    public static bool AnotherFindraIn(int session, int self, IEnumerable<(int Pid, int Session)> findras)
    {
        ArgumentNullException.ThrowIfNull(findras);
        return findras.Any(p => p.Pid != self && p.Session == session);
    }

    public static bool AnotherFindraIsRunning()
    {
        using Process me = Process.GetCurrentProcess();
        var findras = new List<(int Pid, int Session)>();
        foreach (Process p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                try { findras.Add((p.Id, p.SessionId)); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return AnotherFindraIn(me.SessionId, me.Id, findras);
    }
}
