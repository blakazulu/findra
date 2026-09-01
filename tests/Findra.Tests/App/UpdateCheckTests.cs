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
}
