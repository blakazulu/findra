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

    // ---- the round trip -------------------------------------------------------------------------

    /// <summary>
    /// A Run key that is not the machine's. Set, Clear and IsSet had no test at all before, which
    /// is what a test that must not write to HKCU costs if the code has no seam: a test that used
    /// the real key would decide whether Findra starts at sign-in on whatever machine ran the
    /// suite, and would leave it wrong when it failed halfway.
    /// </summary>
    private sealed class Fake : Autostart.IStore
    {
        public string? Value { get; set; }
        public int Removals { get; private set; }

        public string? Read() => Value;
        public void Write(string value) => Value = value;
        public void Remove() { Value = null; Removals++; }
    }

    [Fact]
    public void SettingItWritesTheQuotedCommandAndClearingItTakesTheEntryBackOut()
    {
        var key = new Fake();

        Autostart.Set(@"C:\Program Files\Findra\findra.exe", key);
        Assert.Equal(@"""C:\Program Files\Findra\findra.exe""", key.Value);
        Assert.True(Autostart.IsSet(key));

        Autostart.Clear(key);
        Assert.Null(key.Value);
        Assert.False(Autostart.IsSet(key));
    }

    [Fact]
    public void AnEntryThatIsNotThereAndAnEmptyOneBothReadAsNotSet()
    {
        // An empty value is what a half-written entry leaves behind, and Windows runs nothing for
        // it. Reporting it as set puts a tick in the settings window against a Findra that never
        // starts.
        Assert.False(Autostart.IsSet(new Fake { Value = null }));
        Assert.False(Autostart.IsSet(new Fake { Value = "" }));
        Assert.True(Autostart.IsSet(new Fake { Value = @"""C:\Findra\findra.exe""" }));
    }

    [Fact]
    public void ClearingWhenThereIsNothingToClearTouchesNothing()
    {
        // The ordinary uninstall on a machine that never turned it on. Deleting a value that is
        // not there is also how the log line comes to claim a removal that never happened.
        var key = new Fake();
        Autostart.Clear(key);

        Assert.Equal(0, key.Removals);
    }

    [Fact]
    public void AKeyThatCannotBeReachedIsALogLineRatherThanAThrow()
    {
        // Group policy, a locked-down hive, a roaming profile mid-sync. Set and IsSet run during
        // startup and from the settings window; Clear runs inside the uninstall, BEFORE the data
        // is deleted, so an exception escaping it would abort an uninstall halfway.
        var thrower = new Thrower();

        Assert.False(Autostart.IsSet(thrower));
        Autostart.Set(@"C:\Findra\findra.exe", thrower);
        Autostart.Clear(thrower);
    }

    private sealed class Thrower : Autostart.IStore
    {
        public string? Read() => throw new UnauthorizedAccessException("no");
        public void Write(string value) => throw new UnauthorizedAccessException("no");
        public void Remove() => throw new UnauthorizedAccessException("no");
    }
}
