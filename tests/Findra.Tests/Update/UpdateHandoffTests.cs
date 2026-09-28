using System.ComponentModel;
using System.Diagnostics;

using Findra;
using Xunit;

public class UpdateHandoffTests
{
    private static RunProcess Returns(int code, string output, List<ProcessStartInfo>? started = null) =>
        (start, _) => { started?.Add(start); return Task.FromResult((code, output)); };

    private static RunProcess Throws(Exception ex) => (_, _) => throw ex;

    /// <summary>A downloaded installer on disk and the release entry it was checked against.</summary>
    private sealed class Downloaded : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"findra-handoff-{Guid.NewGuid():N}.exe");
        public ReleaseAsset Asset { get; }

        public Downloaded()
        {
            byte[] body = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i % 7))];
            File.WriteAllBytes(Path, body);
            Asset = new ReleaseAsset("findra-setup-x64.exe", "https://github.com/a.exe", body.Length,
                                     Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)));
        }

        public void Dispose() { try { File.Delete(Path); } catch (IOException) { } }
    }

    [Fact]
    public async Task TheInstallerIsStartedSilentlyWithAProgressWindow()
    {
        using var d = new Downloaded();
        var started = new List<ProcessStartInfo>();
        await UpdateHandoff.RunInstallerAsync(d.Path, d.Asset, Returns(5, "", started), default);

        ProcessStartInfo s = Assert.Single(started);
        Assert.Equal(d.Path, s.FileName);
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-", s.Arguments);
        Assert.True(s.UseShellExecute);
    }

    [Fact]
    public async Task AnInstallerThatReturnsWhileFindraRunsInstalledNothing()
    {
        using var d = new Downloaded();
        Handoff h = await UpdateHandoff.RunInstallerAsync(d.Path, d.Asset, Returns(5, ""), default);
        Assert.Equal(HandoffOutcome.DidNotRun, h.Outcome);
    }

    [Fact]
    public async Task AnInstallerThatExitsWithTwoWasRefusedPermission()
    {
        // Inno's installer elevates itself after it has started, so a No on the permission prompt
        // reaches Findra as the installer's own "cancelled before the installation started", 2.
        // Run silently with no opening question, nothing else produces it.
        using var d = new Downloaded();
        Handoff h = await UpdateHandoff.RunInstallerAsync(d.Path, d.Asset, Returns(2, ""), default);
        Assert.Equal(UpdateHandoff.PermissionRefused, h.Message);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task AnInstallerThatDidNotRunIsDeleted(int code)
    {
        // Try again downloads afresh, and a verified installer left on the disk is 80 MB that only
        // the next start would sweep - an uninstall before then kept it for good.
        using var d = new Downloaded();
        await UpdateHandoff.RunInstallerAsync(d.Path, d.Asset, Returns(code, ""), default);
        Assert.False(File.Exists(d.Path));
    }

    [Fact]
    public async Task AnInstallerThatChangedAfterItWasCheckedIsDeleted()
    {
        using var d = new Downloaded();
        File.AppendAllText(d.Path, "swapped");
        await UpdateHandoff.RunInstallerAsync(d.Path, d.Asset, Returns(0, ""), default);
        Assert.False(File.Exists(d.Path));
    }

    [Fact]
    public async Task RefusingThePermissionPromptIsSaidPlainly()
    {
        using var d = new Downloaded();
        Handoff h = await UpdateHandoff.RunInstallerAsync(d.Path, d.Asset, Throws(new Win32Exception(1223)), default);
        Assert.Equal(HandoffOutcome.DidNotRun, h.Outcome);
        Assert.Equal(UpdateHandoff.PermissionRefused, h.Message);
    }

    [Fact]
    public async Task AnInstallerChangedAfterItWasCheckedIsNeverRun()
    {
        using var d = new Downloaded();
        File.AppendAllText(d.Path, "swapped");
        var started = new List<ProcessStartInfo>();

        Handoff h = await UpdateHandoff.RunInstallerAsync(d.Path, d.Asset, Returns(0, "", started), default);

        Assert.Empty(started);
        Assert.Equal(HandoffOutcome.DidNotRun, h.Outcome);
        Assert.Equal(UpdateHandoff.ChangedSinceChecked, h.Message);
    }

    [Fact]
    public async Task NobodyCanChangeTheInstallerWhileItIsBeingStarted()
    {
        // Checked and started under one handle that denies writing and deleting, so the file that
        // runs is the file that was checked.
        using var d = new Downloaded();
        Exception? writing = null;
        RunProcess run = (_, _) =>
        {
            writing = Record.Exception(() => File.OpenWrite(d.Path).Dispose());
            return Task.FromResult((5, ""));
        };

        await UpdateHandoff.RunInstallerAsync(d.Path, d.Asset, run, default);

        Assert.IsAssignableFrom<IOException>(writing);
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
