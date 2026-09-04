using Findra;
using Findra.Startup;
using Xunit;

/// <summary>
/// One interface per index. Two of them share one models folder and one index, and the second's
/// indexer child cannot open the vector store the first holds - so it is restarted for ever at a
/// five-minute backoff, logging a death each time, while the second hotkey lands on a fallback
/// chord and a download in either writes into the other's part files.
/// </summary>
public class OnlyOneTests
{
    private static string SomeDir() =>
        Path.Combine(Path.GetTempPath(), "findra-one-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheSecondClaimOnOneIndexIsRefusedAndTheFirstKeepsIt()
    {
        string dir = SomeDir();

        Assert.True(OnlyOne.Take(out OnlyOne? first, dir));
        using (first)
        {
            Assert.NotNull(first);
            Assert.False(OnlyOne.Take(out OnlyOne? second, dir));
            Assert.Null(second);
        }
    }

    [Fact]
    public void ReleasingTheClaimLetsTheNextOneStart()
    {
        // A restart, an upgrade, or somebody quitting and reopening. A guard that outlived the
        // process it guarded would be worse than none: it would take a reboot to get back in.
        string dir = SomeDir();

        Assert.True(OnlyOne.Take(out OnlyOne? first, dir));
        first!.Dispose();

        Assert.True(OnlyOne.Take(out OnlyOne? again, dir));
        using (again) Assert.NotNull(again);
    }

    [Fact]
    public void TwoIndexesAreTwoClaims()
    {
        // Two users with two profiles are correctly two Findras: nothing is shared between them.
        string a = SomeDir(), b = SomeDir();

        Assert.True(OnlyOne.Take(out OnlyOne? one, a));
        using (one)
        {
            Assert.True(OnlyOne.Take(out OnlyOne? two, b));
            using (two) Assert.NotNull(two);
        }
    }

    [Fact]
    public void TheClaimIsAHandleRatherThanTheFileBeingThere()
    {
        // A file left behind by a kill must not lock anybody out of their own product. The handle
        // is the claim; the file is only where it lives.
        string dir = SomeDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(OnlyOne.PathIn(dir), "99999 left by a process that died");

        Assert.True(OnlyOne.Take(out OnlyOne? claim, dir));
        using (claim) Assert.NotNull(claim);
    }

    [Fact]
    public void ASecondClaimIsRefusedFromAnotherThreadToo()
    {
        // This is why it is not a named mutex. A mutex belongs to the thread that took it, so a
        // second wait on the SAME thread succeeds - a guard written on one is reentrant exactly
        // where a test tries to prove it works, which is how that version was caught. A file
        // handle is owned by the process.
        string dir = SomeDir();
        Assert.True(OnlyOne.Take(out OnlyOne? first, dir));
        using (first)
        {
            bool got = true;
            var t = new Thread(() =>
            {
                got = OnlyOne.Take(out OnlyOne? second, dir);
                second?.Dispose();
            });
            t.Start();
            t.Join();
            Assert.False(got, "a second claim was granted on another thread");
        }
    }

    [Fact]
    public void TheSecondLaunchSaysHowToReachTheOneAlreadyRunning()
    {
        // "Already running" with nothing visible - a welcome screen behind another window, a
        // capsule dragged onto a monitor that is now unplugged - is when this line is least
        // helpful and most likely to be read.
        string withHotkey = OnlyOne.AlreadyRunning(new UiStatus.Status(4321, "Alt+Space", DateTime.UtcNow));
        Assert.Contains("Alt+Space", withHotkey, StringComparison.Ordinal);
        Assert.Contains("4321", withHotkey, StringComparison.Ordinal);

        string none = OnlyOne.AlreadyRunning(new UiStatus.Status(99, null, DateTime.UtcNow));
        Assert.Contains("no hotkey", none, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("notification area", none, StringComparison.OrdinalIgnoreCase);

        // And it still says something useful when there is no status file at all.
        Assert.Contains("already running", OnlyOne.AlreadyRunning(null), StringComparison.OrdinalIgnoreCase);
    }
}
