using Findra;
using SkiaSharp;
using Xunit;

public class PaletteTests
{
    [Fact]
    public void SixShipThreeDarkThreeLight()
    {
        Assert.Equal(6, Palette.BuiltIn.Count);
        Assert.Equal(3, Palette.Darks.Count);
        Assert.Equal(3, Palette.Lights.Count);
    }

    [Fact]
    public void TheNamesAreTheOnesTheSpecPromises()
    {
        Assert.Equal(new[] { "Mond", "Brass", "Verdigris" }, Palette.Darks.Select(p => p.Name));
        Assert.Equal(new[] { "Paper", "Blueprint", "Porcelain" }, Palette.Lights.Select(p => p.Name));
    }

    [Fact]
    public void TheLightFlagMatchesTheGround()
    {
        // The flag is not decoration - the whole derivation branches on it, so a palette
        // whose flag disagrees with its own ground would paint itself unreadable.
        foreach (Palette p in Palette.BuiltIn)
            Assert.Equal(p.Light, Luminance(p.Ground) > 0.5);
    }

    [Fact]
    public void InkAlwaysContrastsWithItsGround()
    {
        foreach (Palette p in Palette.BuiltIn)
            Assert.True(Math.Abs(Luminance(p.Ink) - Luminance(p.Ground)) > 0.5,
                $"{p.Name}: ink and ground are too close to read");
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndMissesCleanly()
    {
        Assert.Equal("Mond", Palette.ByName("mond")!.Name);
        Assert.Null(Palette.ByName("nonesuch"));
    }

    [Fact]
    public void DefaultsAreMondAndPaper()
    {
        Assert.Equal("Mond", Palette.DefaultDark.Name);
        Assert.Equal("Paper", Palette.DefaultLight.Name);
    }

    internal static double Luminance(SKColor c) =>
        (0.2126 * Srgb(c.Red) + 0.7152 * Srgb(c.Green) + 0.0722 * Srgb(c.Blue));

    private static double Srgb(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
