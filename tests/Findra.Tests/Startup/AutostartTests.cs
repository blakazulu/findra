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
        // others' capsules. This is a constant-pinning test: it fires when somebody moves the key
        // to HKLM "so it works for everyone", which is a plausible edit with a bad blast radius.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", Autostart.KeyPath);
        Assert.Equal("Findra", Autostart.ValueName);
    }
}
