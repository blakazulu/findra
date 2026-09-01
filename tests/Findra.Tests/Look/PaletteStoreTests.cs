using Findra;
using Xunit;

public class PaletteStoreTests
{
    [Fact]
    public void NoFileMeansTheSixBuiltIns()
    {
        Assert.Equal(6, PaletteStore.Load(null).Count);
        Assert.Equal(6, PaletteStore.Load("").Count);
    }

    [Fact]
    public void AUserEntryIsAdded()
    {
        var list = PaletteStore.Load("""
        [{ "name": "Ink", "accent": "#4FC3D9", "ink": "#DCE8EE", "ground": "#0F1418", "light": false }]
        """);

        Assert.Equal(7, list.Count);
        Palette ink = list.Single(p => p.Name == "Ink");
        Assert.Equal(0x4F, ink.Accent.Red);
        Assert.False(ink.Light);
    }

    [Fact]
    public void AUserEntryReplacesABuiltInOfTheSameName()
    {
        // Someone who dislikes Mond's orange should be able to fix Mond, not add "Mond 2".
        var list = PaletteStore.Load("""
        [{ "name": "mond", "accent": "#00FF00", "ink": "#EBDBC0", "ground": "#14141A", "light": false }]
        """);

        Assert.Equal(6, list.Count);
        Assert.Equal(0, list.Single(p => p.Name.Equals("mond", StringComparison.OrdinalIgnoreCase)).Accent.Red);
    }

    [Fact]
    public void AMalformedEntryIsSkippedAndTheRestSurvive()
    {
        // A hand-edited file with one typo must not cost someone their whole theme list.
        var list = PaletteStore.Load("""
        [
          { "name": "Good", "accent": "#112233", "ink": "#EEEEEE", "ground": "#101010", "light": false },
          { "name": "NoAccent", "ink": "#EEEEEE", "ground": "#101010", "light": false },
          { "name": "BadHex", "accent": "not-a-colour", "ink": "#EEEEEE", "ground": "#101010", "light": false },
          { "name": "", "accent": "#112233", "ink": "#EEEEEE", "ground": "#101010", "light": false }
        ]
        """);

        Assert.Equal(7, list.Count);
        Assert.Contains(list, p => p.Name == "Good");
        Assert.DoesNotContain(list, p => p.Name is "NoAccent" or "BadHex" or "");
    }

    [Fact]
    public void BrokenJsonFallsBackToTheBuiltInsRatherThanThrowing()
    {
        Assert.Equal(6, PaletteStore.Load("{ this is not json").Count);
    }

    [Fact]
    public void HexAcceptsBothFormsAndRejectsTheRest()
    {
        var list = PaletteStore.Load("""
        [{ "name": "Short", "accent": "#abc", "ink": "#EEEEEE", "ground": "#101010", "light": false }]
        """);
        Palette p = list.Single(x => x.Name == "Short");
        Assert.Equal(0xAA, p.Accent.Red);
        Assert.Equal(0xBB, p.Accent.Green);
        Assert.Equal(0xCC, p.Accent.Blue);
    }

    [Fact]
    public void TheDefaultFileParsesBackIntoExactlyTheSixBuiltIns()
    {
        // The shipped file is documentation as much as configuration: if it does not
        // round-trip, the example someone copies is wrong.
        var list = PaletteStore.Load(PaletteStore.DefaultJson);
        Assert.Equal(6, list.Count);
        foreach (Palette b in Palette.BuiltIn)
        {
            Palette got = list.Single(p => p.Name == b.Name);
            Assert.Equal(b.Accent, got.Accent);
            Assert.Equal(b.Ink, got.Ink);
            Assert.Equal(b.Ground, got.Ground);
            Assert.Equal(b.Light, got.Light);
        }
    }
}
