using Findra;
using Xunit;

public class LogRepeatTests
{
    private static string Key() => "test|" + Guid.NewGuid().ToString("N");

    [Fact]
    public void ARepeatingFailureKeepsSayingSoAndSaysWhatItSwallowed()
    {
        // Log.Once is right for a condition that is true once and then either fixed or permanent.
        // It is exactly wrong for a retry loop: a session that fails every thirty seconds for four
        // hours reports itself in the first minute of the log and is then silent for the rest of
        // the process, which is the reason this class of bug is hard to find at all. A repeating
        // failure has to keep saying so - at an interval, not on every retry, and carrying the
        // count it held back so the reader can tell "still broken" from "broken twice".
        string key = Key();
        var t0 = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan every = TimeSpan.FromMinutes(5);

        Assert.True(Log.DueToRepeat(key, every, t0, out int held));
        Assert.Equal(0, held);                       // the first one is not "1 more since"

        Assert.False(Log.DueToRepeat(key, every, t0.AddSeconds(30), out _));
        Assert.False(Log.DueToRepeat(key, every, t0.AddMinutes(4), out _));

        Assert.True(Log.DueToRepeat(key, every, t0.AddMinutes(5), out held));
        Assert.Equal(2, held);                       // and it names the two it did not print

        Assert.False(Log.DueToRepeat(key, every, t0.AddMinutes(6), out _));
        Assert.True(Log.DueToRepeat(key, every, t0.AddMinutes(20), out held));
        Assert.Equal(1, held);                       // the counter resets each time it speaks
    }

    [Fact]
    public void TwoDifferentConditionsDoNotSilenceEachOther()
    {
        // The interval is per key. A shared clock would let a chatty failure hide a quiet one,
        // which is the same silence in a different place.
        string a = Key(), b = Key();
        var t0 = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan every = TimeSpan.FromMinutes(5);

        Assert.True(Log.DueToRepeat(a, every, t0, out _));
        Assert.True(Log.DueToRepeat(b, every, t0, out _));
        Assert.False(Log.DueToRepeat(a, every, t0.AddMinutes(1), out _));
        Assert.False(Log.DueToRepeat(b, every, t0.AddMinutes(1), out _));
    }

    [Fact]
    public void AClockThatWentBackwardsStillLetsTheFailureSpeak()
    {
        // A resync under a long-running process moves the wall clock. Comparing "now - last" and
        // waiting for it to reach the interval would go silent for however far the clock jumped.
        string key = Key();
        var t0 = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan every = TimeSpan.FromMinutes(5);

        Assert.True(Log.DueToRepeat(key, every, t0, out _));
        Assert.True(Log.DueToRepeat(key, every, t0.AddHours(-2), out int held));
        Assert.Equal(0, held);
    }
}
