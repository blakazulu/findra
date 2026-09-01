using Findra;
using Xunit;

public class ConfigTests
{
    [Fact]
    public void DefaultsAreMondAndPaperFollowingWindows()
    {
        Config c = Config.Default;
        Assert.Equal("Mond", c.DarkPalette);
        Assert.Equal("Paper", c.LightPalette);
        Assert.Equal(ThemeMode.FollowWindows, c.Mode);
        Assert.True(c.ShowCapsule);
        Assert.True(c.CheckForUpdates);
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        var c = Config.Default with
        {
            DarkPalette = "Verdigris", LightPalette = "Blueprint", Mode = ThemeMode.AlwaysDark,
            Hotkey = "Ctrl+Alt+F", CapsuleX = 120, CapsuleY = 900, ShowCapsule = false,
            CheckForUpdates = false, LastUpdateCheck = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            InstallSource = "winget",
        };

        Config back = Config.Load(c.ToJson());

        Assert.Equal(c, back);
    }

    [Fact]
    public void AMissingFileGivesTheDefaults()
    {
        Assert.Equal(Config.Default, Config.Load(null));
        Assert.Equal(Config.Default, Config.Load(""));
    }

    [Fact]
    public void BrokenJsonGivesTheDefaultsRatherThanThrowing()
    {
        // Someone's settings file must never be able to stop the app starting.
        Assert.Equal(Config.Default, Config.Load("{ not json"));
    }

    [Fact]
    public void AnUnknownFieldIsIgnoredAndTheRestSurvive()
    {
        // Forward compatibility: a newer Findra's file must not break an older one.
        Config c = Config.Load("""{ "darkPalette": "Brass", "somethingFromTheFuture": 42 }""");
        Assert.Equal("Brass", c.DarkPalette);
        Assert.Equal("Paper", c.LightPalette);
    }

    [Fact]
    public void AnUnknownModeFallsBackRatherThanThrowing()
    {
        Assert.Equal(ThemeMode.FollowWindows, Config.Load("""{ "mode": "Purple" }""").Mode);
    }
}
