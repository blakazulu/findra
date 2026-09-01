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
}
