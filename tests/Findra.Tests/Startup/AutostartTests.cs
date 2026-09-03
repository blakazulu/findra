using System.Text.RegularExpressions;

using Findra.Startup;
using Xunit;

public class AutostartTests
{
    [Fact]
    public void APathWithASpaceInItIsQuoted()
    {
        // The classic. C:\Program Files\Findra\findra.exe unquoted makes Windows run C:\Program
        // with "Files\Findra\findra.exe" as an argument, at every sign-in, for ever, silently.
        Assert.Equal("\"C:\\Program Files\\Findra\\findra.exe\"",
                     Autostart.CommandFor(@"C:\Program Files\Findra\findra.exe"));
    }

    [Fact]
    public void AnAlreadyQuotedPathIsNotQuotedTwice()
    {
        Assert.Equal("\"C:\\Program Files\\Findra\\findra.exe\"",
                     Autostart.CommandFor("\"C:\\Program Files\\Findra\\findra.exe\""));
    }

    [Fact]
    public void TheEntryIsUnderTheCurrentUserAndNotTheWholeMachine()
    {
        // A machine-wide Run entry starts Findra for every account on the computer, including ones
        // that never installed it - and the uninstaller, running as one user, cannot remove the
        // others' capsules.
        //
        // The subkey path is the same under either hive, so pinning it said nothing about the one
        // thing this test is named for: the edit it claimed to catch is CurrentUser becoming
        // LocalMachine at the three call sites, and that left every assertion here green. The hive
        // is not a value this test can reach - Set, Clear and IsSet each open it themselves and
        // need a real registry - so it is read out of the source, which is the same shape
        // TypefaceTests uses for "only one place in the tree resolves a typeface".
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", Autostart.KeyPath);
        Assert.Equal("Findra", Autostart.ValueName);

        string source = Repo.Read("src/Findra/Startup/Autostart.cs");
        Assert.DoesNotContain("Registry.LocalMachine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HKEY_LOCAL_MACHINE", source, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, @"Registry\.CurrentUser").Count);
    }
}
