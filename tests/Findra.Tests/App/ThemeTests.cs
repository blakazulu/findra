using Findra;
using Xunit;

public class ThemeTests
{
    private static readonly IReadOnlyList<Palette> All = Palette.BuiltIn;

    [Fact]
    public void FollowWindowsTakesTheDarkPickWhenWindowsIsDark()
    {
        Config c = Config.Default with { DarkPalette = "Verdigris", LightPalette = "Blueprint" };
        Assert.Equal("Verdigris", Theme.Resolve(c, windowsIsLight: false, All).Name);
        Assert.Equal("Blueprint", Theme.Resolve(c, windowsIsLight: true, All).Name);
    }

    [Fact]
    public void PinnedModesIgnoreWindowsEntirely()
    {
        Config dark = Config.Default with { Mode = ThemeMode.AlwaysDark, DarkPalette = "Brass" };
        Assert.Equal("Brass", Theme.Resolve(dark, windowsIsLight: true, All).Name);

        Config light = Config.Default with { Mode = ThemeMode.AlwaysLight, LightPalette = "Porcelain" };
        Assert.Equal("Porcelain", Theme.Resolve(light, windowsIsLight: false, All).Name);
    }

    [Fact]
    public void AUserPaletteIsResolvedByName()
    {
        var mine = new Palette("Mine", new SkiaSharp.SKColor(1, 2, 3),
            new SkiaSharp.SKColor(0xEE, 0xEE, 0xEE), new SkiaSharp.SKColor(0x10, 0x10, 0x10), false);
        Config c = Config.Default with { Mode = ThemeMode.AlwaysDark, DarkPalette = "Mine" };

        Assert.Equal("Mine", Theme.Resolve(c, false, [.. All, mine]).Name);
    }

    [Fact]
    public void ADeletedPaletteFallsBackToTheDefaultOfTheRightSide()
    {
        // Someone edits palettes.json and removes the palette their config names. Findra must
        // keep the right SIDE of the light/dark line rather than flipping the whole card.
        Config c = Config.Default with { Mode = ThemeMode.AlwaysDark, DarkPalette = "Gone" };
        Palette got = Theme.Resolve(c, windowsIsLight: false, All);
        Assert.False(got.Light);
        Assert.Equal(Palette.DefaultDark.Name, got.Name);

        Config l = Config.Default with { Mode = ThemeMode.AlwaysLight, LightPalette = "Gone" };
        Assert.True(Theme.Resolve(l, windowsIsLight: true, All).Light);
    }

    [Fact]
    public void APaletteOnTheWrongSideIsHonouredButNoted()
    {
        // Nothing stops someone naming a light palette as their dark pick. Honour it - it is
        // their choice - but the card must still be drawn from that palette's own values.
        Config c = Config.Default with { Mode = ThemeMode.AlwaysDark, DarkPalette = "Paper" };
        Assert.Equal("Paper", Theme.Resolve(c, false, All).Name);
    }

    [Fact]
    public void ASecondMissingPaletteFallsBackJustLikeTheFirst()
    {
        // The fallback used to be latched by a single process-wide flag, so only the first missing
        // palette was ever reported. Resolving two different missing names must give each its own
        // answer on its own side, however many have gone before.
        Config dark = Config.Default with { Mode = ThemeMode.AlwaysDark, DarkPalette = "GoneOne" };
        Config light = Config.Default with { Mode = ThemeMode.AlwaysLight, LightPalette = "GoneTwo" };

        Assert.Equal(Palette.DefaultDark.Name, Theme.Resolve(dark, false, All).Name);
        Assert.Equal(Palette.DefaultLight.Name, Theme.Resolve(light, true, All).Name);
        Assert.False(Theme.Resolve(dark, false, All).Light);
        Assert.True(Theme.Resolve(light, true, All).Light);
    }
}
