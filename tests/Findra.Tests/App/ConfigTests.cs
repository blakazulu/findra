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
            LatestKnownVersion = "1.9.0", InstallSource = "winget",
        };

        Config back = Config.Load(c.ToJson());

        Assert.Equal(c, back);
    }

    [Fact]
    public void ACapsuleThatWasNeverDraggedHasNoSavedPosition()
    {
        // Null, not (0,0): the shell asks HasValue rather than guessing from the numbers.
        Assert.Null(Config.Default.CapsuleX);
        Assert.Null(Config.Default.CapsuleY);
    }

    [Fact]
    public void ACapsuleParkedAtTheOriginComesBackAsZeroAndNotAsNeverPlaced()
    {
        // The top-left corner of the primary monitor is somewhere a user is allowed to leave the
        // capsule, and it has to be found there again on the next launch.
        Config back = Config.Load((Config.Default with { CapsuleX = 0, CapsuleY = 0 }).ToJson());
        Assert.NotNull(back.CapsuleX);
        Assert.NotNull(back.CapsuleY);
        Assert.Equal(0, back.CapsuleX!.Value);
        Assert.Equal(0, back.CapsuleY!.Value);
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

    [Fact]
    public void AFieldOfTheWrongTypeCostsOnlyThatField()
    {
        // A hand-edited file that says mode:1 instead of mode:"AlwaysDark" must not throw away
        // every OTHER setting on its way past. Losing one field to a typo is forgivable; losing
        // the palette, the hotkey and the capsule position along with it is not.
        Config c = Config.Load("""{ "darkPalette": "Brass", "mode": 1, "hotkey": "Ctrl+Alt+F" }""");

        Assert.Equal(ThemeMode.FollowWindows, c.Mode);
        Assert.Equal("Brass", c.DarkPalette);
        Assert.Equal("Ctrl+Alt+F", c.Hotkey);
    }
}
