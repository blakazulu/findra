using Findra;
using Xunit;

[Collection("culture")]
public class TranscribeLimitTests
{
    [Theory]
    [InlineData(0, 1, false)]           // off means off, even for a one-second clip
    [InlineData(0, 0.5, false)]
    [InlineData(-1, 36_000, true)]      // negative means no limit
    [InlineData(-99, 36_000, true)]     // ANY negative, not just -1
    [InlineData(5, 299, true)]
    [InlineData(5, 300, true)]          // exactly at the limit is inside it
    [InlineData(5, 301, false)]
    [InlineData(120, 7_200, true)]
    public void TheRuleIsZeroIsOffNegativeIsNoLimitAndPositiveIsMinutes(int minutes, double seconds, bool covered)
    {
        // Three meanings in one int, and each has a wrong implementation that looks right:
        // treating 0 as "no limit" transcribes everything on a machine that asked for nothing;
        // treating negative as 0 transcribes nothing for somebody who asked for everything;
        // `<` instead of `<=` drops a recording that is exactly five minutes long, which is what
        // a voice memo app produces.
        Assert.Equal(covered, TranscribeLimit.Covers(minutes, seconds));
    }

    [Fact]
    public void ThePresetsAreTheOnesTheSpecNames()
    {
        Assert.Equal([TranscribeLimit.Off, 5, 30, 120, TranscribeLimit.NoLimit], TranscribeLimit.Presets);
        Assert.Equal(0, TranscribeLimit.Off);
        Assert.True(TranscribeLimit.NoLimit < 0);
        Assert.Equal(5, TranscribeLimit.Default);
    }

    [Fact]
    public void APresetAndATypedValueAreTheSameSetting()
    {
        // Spec §6: "the named choices are presets over that one number, so a typed value and a
        // preset cannot disagree". A second field for the preset name is what makes them able
        // to - this asserts the name is DERIVED from the number and nothing else.
        Assert.Equal("2 hours", TranscribeLimit.Named(120));
        Assert.Equal("2 hours", TranscribeLimit.Describe(120));
        Assert.Null(TranscribeLimit.Named(17));                  // not a preset
        Assert.Equal("17 minutes", TranscribeLimit.Describe(17)); // still readable
    }

    [Fact]
    public void EveryPresetHasAName()
    {
        foreach (int m in TranscribeLimit.Presets)
            Assert.False(string.IsNullOrEmpty(TranscribeLimit.Named(m)), $"{m} has no name");
    }

    [Theory]
    [InlineData("off", 0)]
    [InlineData("5", 5)]
    [InlineData("30 minutes", 30)]
    [InlineData("2 hours", 120)]
    [InlineData("no limit", -1)]
    [InlineData("nolimit", -1)]
    [InlineData("17", 17)]
    public void APresetNameAndABareNumberBothParse(string word, int minutes)
        => Assert.Equal(minutes, TranscribeLimit.Parse(word));

    [Theory]
    [InlineData("soon")]
    [InlineData("")]
    [InlineData("5 fortnights")]
    public void AWordThatIsNeitherIsRefusedRatherThanTreatedAsZero(string word)
    {
        // Zero is a real setting - "transcribe nothing" - so a parse that falls back to it
        // silently turns speech search off for somebody who mistyped a number.
        Assert.Null(TranscribeLimit.Parse(word));
    }

    [Fact]
    public void EverySettingReadsTheSameOnEveryMachine()
    {
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("2 hours", TranscribeLimit.Describe(120));
            Assert.Equal(1_500, TranscribeLimit.Parse("1500"));
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }
}
