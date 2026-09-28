using System.ComponentModel;
using System.Diagnostics;

using Findra;
using Xunit;

public class UpdateHandoffTests
{
    private static RunProcess Returns(int code, string output, List<ProcessStartInfo>? started = null) =>
        (start, _) => { started?.Add(start); return Task.FromResult((code, output)); };

    private static RunProcess Throws(Exception ex) => (_, _) => throw ex;

    [Fact]
    public async Task TheInstallerIsStartedSilentlyWithAProgressWindow()
    {
        var started = new List<ProcessStartInfo>();
        await UpdateHandoff.RunInstallerAsync(@"C:\u\findra-setup-x64.exe", Returns(5, "", started), default);

        ProcessStartInfo s = Assert.Single(started);
        Assert.Equal(@"C:\u\findra-setup-x64.exe", s.FileName);
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-", s.Arguments);
        Assert.True(s.UseShellExecute);
    }

    [Fact]
    public async Task AnInstallerThatReturnsWhileFindraRunsInstalledNothing()
    {
        Handoff h = await UpdateHandoff.RunInstallerAsync("x.exe", Returns(5, ""), default);
        Assert.Equal(HandoffOutcome.DidNotRun, h.Outcome);
    }

    [Fact]
    public async Task RefusingThePermissionPromptIsSaidPlainly()
    {
        Handoff h = await UpdateHandoff.RunInstallerAsync("x.exe", Throws(new Win32Exception(1223)), default);
        Assert.Equal(HandoffOutcome.DidNotRun, h.Outcome);
        Assert.Equal(UpdateHandoff.PermissionRefused, h.Message);
    }

    [Fact]
    public async Task TheWingetCommandIsExactlyThisAndHasNoWindow()
    {
        var started = new List<ProcessStartInfo>();
        await UpdateHandoff.RunWingetAsync(Returns(1, "", started), default);

        ProcessStartInfo s = Assert.Single(started);
        Assert.Equal("winget", s.FileName);
        Assert.Equal("upgrade --id blakazulu.Findra --exact --accept-source-agreements " +
                     "--accept-package-agreements --disable-interactivity", s.Arguments);
        Assert.False(s.UseShellExecute);
        Assert.True(s.CreateNoWindow);
        Assert.True(s.RedirectStandardOutput);
        Assert.True(s.RedirectStandardError);
    }

    [Fact]
    public async Task WingetWithNothingNewerSaysSo()
    {
        Handoff h = await UpdateHandoff.RunWingetAsync(
            Returns(UpdateHandoff.WingetNoUpgrade, "No available upgrade found.\r\n"), default);
        Assert.Equal(HandoffOutcome.WingetNothingNewer, h.Outcome);
        Assert.Equal(UpdateHandoff.NothingNewer, h.Message);
    }

    [Fact]
    public async Task AWingetFailureShowsItsOwnLastLine()
    {
        Handoff h = await UpdateHandoff.RunWingetAsync(
            Returns(-1978335226, "Found Findra [blakazulu.Findra]\r\nInstaller failed with exit code: 1\r\n"), default);
        Assert.Equal(HandoffOutcome.WingetFailed, h.Outcome);
        Assert.Equal("Installer failed with exit code: 1", h.Message);
    }

    [Fact]
    public async Task NoWingetOnTheMachineIsSaid()
    {
        Handoff h = await UpdateHandoff.RunWingetAsync(Throws(new Win32Exception(2)), default);
        Assert.Equal(HandoffOutcome.WingetMissing, h.Outcome);
        Assert.Equal(UpdateHandoff.WingetMissing, h.Message);
    }

    [Fact]
    public async Task AHungWingetIsStoppedAtTheLimit()
    {
        Handoff h = await UpdateHandoff.RunWingetAsync(Throws(new TimeoutException()), default);
        Assert.Equal(HandoffOutcome.WingetFailed, h.Outcome);
        Assert.Contains("20 minutes", h.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastLineIsWhatFollowsTheLastCarriageReturn()
    {
        // winget draws its progress bar by rewriting one line with carriage returns and backspaces.
        string output = "Downloading https://x\r\n  \u2588\u2588\u2592\u2592  1.00 MB / 82.0 MB\r  \u2588\u2588\u2588\u2588  82.0 MB / 82.0 MB\r\n" +
                        "Starting package install...\r\n\b\b-\b\\\b|Installer failed with exit code: 5\r\n\r\n";
        Assert.Equal("Installer failed with exit code: 5", UpdateHandoff.LastLine(output));
    }

    [Fact]
    public void NothingReadableIsNoLine() => Assert.Null(UpdateHandoff.LastLine("\r\n \b \r\n"));

    [Fact]
    public async Task TheRealRunnerReadsBothStreamsAndTheExitCode()
    {
        var start = new ProcessStartInfo("cmd.exe", "/c echo out & echo err 1>&2 & exit 3")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };

        (int code, string output) = await UpdateHandoff.Real(TimeSpan.FromSeconds(30))(start, default);

        Assert.Equal(3, code);
        Assert.Contains("out", output, StringComparison.Ordinal);
        Assert.Contains("err", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRealRunnerKillsWhatOutlivesItsLimit()
    {
        var start = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => UpdateHandoff.Real(TimeSpan.FromMilliseconds(300))(start, default));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the process was waited on rather than killed");
    }
}
