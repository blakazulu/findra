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
        Assert.All(doc.Descendants(ns + "Enabled"), e => Assert.Equal("true", e.Value));
    }
}
