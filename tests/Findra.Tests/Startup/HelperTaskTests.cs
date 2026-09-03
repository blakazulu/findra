using System.Xml.Linq;
using Findra.Startup;
using Xunit;

public class HelperTaskTests
{
    [Fact]
    public void XmlRequestsHighestAvailable()
    {
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Program Files\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
    }

    [Fact]
    public void XmlTriggersOnLogon()
    {
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Single(doc.Descendants(ns + "LogonTrigger"));
    }

    [Fact]
    public void XmlPassesTheNamesArgument()
    {
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("--names", doc.Descendants(ns + "Arguments").Single().Value);
    }

    [Fact]
    public void XmlQuotesAnExePathContainingSpaces()
    {
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Program Files\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(@"""C:\Program Files\Findra\findra.exe""", doc.Descendants(ns + "Command").Single().Value);
    }

    [Fact]
    public void XmlDoesNotStopTheHelperOnBattery()
    {
        // a search index that dies when the laptop unplugs is a search index that is
        // always cold
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("false", doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", doc.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal("PT0S",  doc.Descendants(ns + "ExecutionTimeLimit").Single().Value);
    }

    [Fact]
    public void XmlNamesTheUserAndRunsHiddenAndEnabled()
    {
        // Nothing here can be exercised without elevation, so these assertions are the
        // only thing between a correct task and one that registers cleanly and then never
        // fires. A dropped UserId, LogonType, or an Enabled of false all produce exactly
        // that: schtasks accepts the XML, and the helper never starts.
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        string me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var userIds = doc.Descendants(ns + "UserId").ToList();
        Assert.Equal(2, userIds.Count);                       // the trigger and the principal
        Assert.All(userIds, u => Assert.Equal(me, u.Value));

        Assert.Equal("InteractiveToken", doc.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("IgnoreNew", doc.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("true", doc.Descendants(ns + "Hidden").Single().Value);
        // Counted first. Assert.All over a collection nothing populates asserts nothing: delete
        // both <Enabled> elements and the sweep passes while the trigger never fires, which is the
        // exact failure the paragraph above says these lines are the only defence against.
        var enabled = doc.Descendants(ns + "Enabled").ToList();
        Assert.Equal(2, enabled.Count);                       // the trigger and the settings block
        Assert.All(enabled, e => Assert.Equal("true", e.Value));
    }

    [Fact]
    public void XmlSurvivesAPathContainingAnAmpersand()
    {
        // `&` is legal in a Windows path. Unescaped it produces XML schtasks rejects,
        // so registration fails and the helper never starts - on someone else's machine.
        var doc = XDocument.Parse(HelperTask.BuildXml(@"D:\Tools & Utils\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(@"""D:\Tools & Utils\findra.exe""", doc.Descendants(ns + "Command").Single().Value);
    }

    // ---- taking it back off the machine ---------------------------------------------------------

    /// <summary>
    /// Everything <see cref="HelperTask.Unregister()"/> does to the machine, in the order it did
    /// it. Only BuildXml and the two argument strings were covered before, so replacing the whole
    /// body with <c>return true</c> - never running schtasks, never asking afterwards - left the
    /// suite green while the HighestAvailable task stayed registered.
    /// </summary>
    private static (bool Gone, List<string> Calls) Unregister(HelperTaskState after, int deleteCode = 0)
    {
        var calls = new List<string>();
        bool gone = HelperTask.Unregister(
            run: arguments => { calls.Add(arguments); return deleteCode; },
            query: () => { calls.Add("query"); return after; });
        return (gone, calls);
    }

    [Fact]
    public void TheTaskIsStoppedBeforeItIsDeleted()
    {
        // Deleting a task whose instance is running leaves the process behind - and that process
        // is the elevated one holding a volume handle on the system drive.
        (_, List<string> calls) = Unregister(HelperTaskState.NotRegistered);

        Assert.Equal(3, calls.Count);
        Assert.Contains("/end", calls[0], StringComparison.Ordinal);
        Assert.Contains("/delete", calls[1], StringComparison.Ordinal);
        Assert.Contains("\"Findra names helper\"", calls[0], StringComparison.Ordinal);
        Assert.Contains("\"Findra names helper\"", calls[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnswerComesFromAskingAgainAndNotFromSchtasksOwnExitCode()
    {
        // schtasks says "no such task" in the user's own language, so its non-zero exit cannot be
        // read. The query runs last, after both commands, and it is what decides.
        (bool gone, List<string> calls) = Unregister(HelperTaskState.NotRegistered, deleteCode: 1);

        Assert.Equal("query", calls[^1]);
        Assert.True(gone, "a task schtasks reported an error for, that is not there afterwards, is gone");
    }

    [Fact]
    public void ATaskStillRegisteredAfterwardsIsReportedAsNotRemoved()
    {
        // The one answer that must never be optimistic: it is an elevated logon task pointing at a
        // binary the uninstaller is about to delete, and this false is the uninstall's exit code.
        (bool gone, _) = Unregister(HelperTaskState.Registered);

        Assert.False(gone);
    }

    [Fact]
    public void ATaskThatWasNeverThereIsTheSameOutcomeAsOneThatWasRemoved()
    {
        // Findra installed from source on a machine where registration failed. Nothing is left
        // pointing at the binary, which is the only thing the caller is asking about.
        Assert.True(Unregister(HelperTaskState.NotRegistered, deleteCode: 1).Gone);
        // And a query that could not answer at all, which is deliberately not Registered: the
        // delete has already run, and refusing to say so would fail the uninstall over a
        // confirmation nobody can act on.
        Assert.True(Unregister(HelperTaskState.Unknown, deleteCode: -1).Gone);
    }
}
