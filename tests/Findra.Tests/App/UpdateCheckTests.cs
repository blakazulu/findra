using Findra;
using Xunit;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("1.9.0", "1.10.0", -1)]   // the one string ordering gets wrong
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("v1.2.3", "1.2.3", 0)]    // tags carry a leading v
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    public void ComparesNumbersNotStrings(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(UpdateCheck.Compare(a, b)));

    [Fact]
    public void AnUnparseableVersionIsNeverTreatedAsNewer()
    {
        // Telling someone they are current when they are not is worse than saying nothing,
        // so anything we cannot read loses.
        Assert.True(UpdateCheck.Compare("1.0.0", "not-a-version") >= 0);
        Assert.True(UpdateCheck.Compare("not-a-version", "1.0.0") <= 0);
    }

    [Theory]
    [InlineData("0.2.0-rc.1")]
    [InlineData("?")]
    [InlineData("")]
    public void ABuildWhoseOwnVersionCannotBeReadIsUnknownRatherThanUpToDate(string running)
    {
        // Compare answers 0 when EITHER side fails to parse, and 0 was routed straight into
        // "up to date" - a claim made on no information, which is the thing this check must never
        // do. Only the LATEST tag was guarded; the running version took the same route home.
        Assert.Equal(UpdateState.Unknown, UpdateMemory.Remembered(running, "1.2.0"));
    }

    [Fact]
    public void AReadableBuildStillComparesNormally()
    {
        Assert.Equal(UpdateState.Available, UpdateMemory.Remembered("1.9.0", "1.10.0"));
        Assert.Equal(UpdateState.Current, UpdateMemory.Remembered("1.10.0", "1.9.0"));
        Assert.Equal(UpdateState.Unknown, UpdateMemory.Remembered("1.0.0", "not-a-version"));
    }

    [Fact]
    public async Task OptingOutMeansNoRequestIsMade()
    {
        bool called = false;
        Config off = Config.Default with { CheckForUpdates = false };

        UpdateResult r = await UpdateCheck.CheckAsync(off,
            _ => { called = true; return Task.FromResult<string?>("9.9.9"); },
            DateTime.UtcNow, default);

        Assert.False(called);              // off means off
        Assert.Equal(UpdateState.Disabled, r.State);
    }

    [Fact]
    public async Task ItAsksAtMostOncePerDay()
    {
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        int calls = 0;
        Config recent = Config.Default with { LastUpdateCheck = now.AddHours(-3) };

        await UpdateCheck.CheckAsync(recent, _ => { calls++; return Task.FromResult<string?>("9.9.9"); }, now, default);
        Assert.Equal(0, calls);

        Config old = Config.Default with { LastUpdateCheck = now.AddHours(-25) };
        await UpdateCheck.CheckAsync(old, _ => { calls++; return Task.FromResult<string?>("9.9.9"); }, now, default);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AFailedRequestIsSilentNotAnError()
    {
        // A broken network is not something the user needs to acknowledge.
        UpdateResult r = await UpdateCheck.CheckAsync(Config.Default with { LastUpdateCheck = default },
            _ => throw new HttpRequestException("no network"), DateTime.UtcNow, default);

        Assert.Equal(UpdateState.Unknown, r.State);
    }

    [Fact]
    public void TheAdviceMatchesHowItWasInstalled()
    {
        Assert.Contains("winget upgrade", UpdateCheck.Advice("winget", "1.2.0"));
        Assert.DoesNotContain("winget upgrade", UpdateCheck.Advice("source", "1.2.0"));
        Assert.Contains("github.com/blakazulu/findra", UpdateCheck.Advice("source", "1.2.0"));
    }

    [Fact]
    public void ACopyPutHereByTheInstallerIsSentToTheInstallerAndNotToWinget()
    {
        // Three sources existed and the installer route is a fourth. Falling into the default arm
        // tells somebody who ran a downloaded .exe to run `winget upgrade` for a package winget
        // has never heard of on their machine, which fails with a message about no packages found.
        string advice = UpdateCheck.Advice("installer", "1.3.0");

        Assert.Contains("releases", advice, StringComparison.Ordinal);
        Assert.DoesNotContain("winget upgrade", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdviceMatchIsCaseInsensitiveOnInstallSource()
    {
        // Nothing writes Config.InstallSource yet, so the casing convention is still up for
        // grabs - "WinGet" must not silently fall through to the unknown-source branch.
        Assert.Contains("winget upgrade", UpdateCheck.Advice("WinGet", "1.2.0"));
    }

    [Fact]
    public async Task AnUnparseableLatestTagIsUnknownNeverCurrent()
    {
        // Spec 9b: telling someone they are current on no information is worse than not
        // checking at all. Compare("...", "nightly") returns 0 (unparseable never wins), and
        // routing that 0 straight into ">= 0 => Current" was exactly the bug: it must be
        // caught before the comparison, not after.
        UpdateResult r = await UpdateCheck.CheckAsync(Config.Default with { LastUpdateCheck = default },
            _ => Task.FromResult<string?>("nightly"), DateTime.UtcNow, default);

        Assert.Equal(UpdateState.Unknown, r.State);
    }

    [Fact]
    public void CompareIsNullSafe()
    {
        // Null is unparseable, same as any other string Version.TryParse rejects - it must
        // lose the comparison, not throw.
        Assert.Equal(0, UpdateCheck.Compare(null, "1.0.0"));
        Assert.Equal(0, UpdateCheck.Compare("1.0.0", null));
        Assert.Equal(0, UpdateCheck.Compare(null, null));
    }

    [Fact]
    public void CreateClientCapsResponseSize()
    {
        // UseCookies and AllowAutoRedirect live on the SocketsHttpHandler passed to the
        // HttpClient constructor; HttpClient does not re-expose either once wrapped, so this
        // asserts the one setting that is observable from the outside.
        using HttpClient client = UpdateCheck.CreateClient();
        Assert.Equal(64 * 1024, client.MaxResponseContentBufferSize);
    }

    [Fact]
    public async Task ForceBypassesTheDailyGate()
    {
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        int calls = 0;
        Config recent = Config.Default with { LastUpdateCheck = now.AddHours(-3) };

        UpdateResult r = await UpdateCheck.CheckAsync(recent,
            _ => { calls++; return Task.FromResult<string?>("9.9.9"); }, now, default, force: true);

        Assert.Equal(1, calls);
        Assert.NotEqual(UpdateState.NotDue, r.State);
    }

    [Fact]
    public async Task ForceDoesNotBypassCheckForUpdatesBeingOff()
    {
        // Off means off, even when a user forces a check from the tray.
        bool called = false;
        Config off = Config.Default with { CheckForUpdates = false };

        UpdateResult r = await UpdateCheck.CheckAsync(off,
            _ => { called = true; return Task.FromResult<string?>("9.9.9"); }, DateTime.UtcNow, default, force: true);

        Assert.False(called);
        Assert.Equal(UpdateState.Disabled, r.State);
    }

    [Fact]
    public async Task ShutdownDuringFetchDoesNotBurnTheDailyCheck()
    {
        // The caller's own token being cancelled means the app is quitting, not that the
        // network failed - it must not stamp LastUpdateCheck, or a quit during startup would
        // silently suppress tomorrow's check too.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Config config = Config.Default with { LastUpdateCheck = default };

        UpdateResult r = await UpdateCheck.CheckAsync(config,
            ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult<string?>("9.9.9"); },
            DateTime.UtcNow, cts.Token);

        Assert.Equal(UpdateState.Unknown, r.State);
        Assert.Equal(config.LastUpdateCheck, r.Config.LastUpdateCheck);
    }

    [Fact]
    public async Task NetworkFailureStillStampsLastUpdateCheck()
    {
        // Contrast with ShutdownDuringFetchDoesNotBurnTheDailyCheck: an ordinary failure (and
        // the internal 10s timeout, which throws the same way) is not a shutdown, so it still
        // counts against the daily budget.
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        UpdateResult r = await UpdateCheck.CheckAsync(Config.Default with { LastUpdateCheck = default },
            _ => throw new HttpRequestException("no network"), now, default);

        Assert.Equal(UpdateState.Unknown, r.State);
        Assert.Equal(now, r.Config.LastUpdateCheck);
    }

    [Fact]
    public async Task RunningVersionMatchingTheTaggedReleaseIsCurrent()
    {
        UpdateResult r = await UpdateCheck.CheckAsync(Config.Default with { LastUpdateCheck = default },
            _ => Task.FromResult<string?>("v" + Log.Version), DateTime.UtcNow, default);

        Assert.Equal(UpdateState.Current, r.State);
    }

    [Fact]
    public async Task NewerReleaseIsAvailableWithAdvice()
    {
        Version running = Version.Parse(Log.Version);
        string newer = $"{running.Major + 1}.0.0";

        UpdateResult r = await UpdateCheck.CheckAsync(
            Config.Default with { LastUpdateCheck = default, InstallSource = "source" },
            _ => Task.FromResult<string?>(newer), DateTime.UtcNow, default);

        Assert.Equal(UpdateState.Available, r.State);
        Assert.Equal(newer, r.Latest);
        Assert.False(string.IsNullOrWhiteSpace(r.Advice));
    }
}
