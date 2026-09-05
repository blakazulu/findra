using Findra;
using Findra.Diagnostics;
using Xunit;

// Assigns CultureInfo.CurrentCulture (TheListingReadsTheSameOnEveryMachine), so it joins the
// collection that stops xUnit running it beside a class formatting a number on a pool thread.
[Collection("culture")]
public class ModelsCommandTests
{
    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    [Theory]
    [InlineData("justnames", Preset.JustNames)]
    [InlineData("recommended", Preset.Recommended)]
    [InlineData("Everything", Preset.Everything)]
    [InlineData("EVERYTHING", Preset.Everything)]
    public void APresetIsNamedTheWayTheFirstScreenNamesIt(string word, Preset want)
        => Assert.Equal(want, ModelsCommand.ParsePreset(word));

    [Theory]
    [InlineData("custom")]     // not something anybody can ask for - it is what touching a row makes
    [InlineData("all")]
    [InlineData("")]
    public void AWordThatIsNotAPresetIsRefusedRatherThanGuessedAt(string word)
        => Assert.Null(ModelsCommand.ParsePreset(word));

    [Fact]
    public void TheListingShowsEveryCapabilityAndWhatItWouldAddToWhatIsThere()
    {
        // Marginal, on the command line as everywhere else: on a machine that already has
        // documents' meaning, Speech is 547 MB and not 818.
        string bare = ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: false);
        string withDocs = ModelsCommand.RenderList(Set(Capability.Meaning), hebrewOffered: false);

        Assert.Contains("629 MB", bare, StringComparison.Ordinal);      // photos
        Assert.Contains("1.57 GB", bare, StringComparison.Ordinal);      // speech, from nothing
        Assert.Contains("547 MB", withDocs, StringComparison.Ordinal);  // speech, beside meaning
    }

    [Fact]
    public void AnInstalledCapabilityIsShownAsInstalledAndCostsNothingMore()
    {
        string text = ModelsCommand.RenderList(Set(Capability.Photos), hebrewOffered: false);

        Assert.Contains("Photos and video", text, StringComparison.Ordinal);
        Assert.Contains("installed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFreeCapabilitiesAreNamedSoNobodyThinksSearchIsOff()
    {
        // Spec §6 prints "free" on the documents row for a reason: somebody who takes nothing
        // still gets names and full-text search, and a listing that shows only the paid rows
        // makes "just names" read as "no search".
        string text = ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: false);

        Assert.Contains("words in documents", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("free", text, StringComparison.OrdinalIgnoreCase);
        // Reading words inside pictures needs no model either, and it is not a capability.
        Assert.Contains("words inside pictures", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HebrewIsListedOnlyWhereItIsWorthAGigabyteAndAHalf()
    {
        Assert.DoesNotContain("Hebrew", ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: false),
                              StringComparison.Ordinal);
        Assert.Contains("Hebrew", ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: true),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingSaysWhatEverythingWouldCostAltogether()
    {
        Assert.Contains("3.7 GB", ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: true),
                        StringComparison.Ordinal);
        // Added while mutation testing: on a BARE machine every column that could carry this
        // number carries it, so a total computed by adding the rows up - the mistake this test
        // names, which counts the shared e5 pair three times and renders 3.99 GB - was still
        // green off the preset column beside it. With photos already here, every marginal figure
        // in the listing has moved and the whole-set total is the only line that can still say
        // 2.93 GB.
        Assert.Contains("3.7 GB", ModelsCommand.RenderList(Set(Capability.Photos), hebrewOffered: true),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingReadsTheSameOnEveryMachine()
    {
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            string de = ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: true);
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: true), de);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }

    [Fact]
    public void AskingForHebrewAsksForEverythingItNeeds()
    {
        // The closure, at the one edge a person types at. Asking for the fine-tune alone
        // downloads 1.5 GB that cannot detect a language and therefore cannot be used.
        IReadOnlySet<Capability> chosen = ModelsCommand.ParseCapabilities("hebrew")!;

        Assert.Contains(Capability.Speech, chosen);
        Assert.Contains(Capability.Meaning, chosen);
        Assert.Contains(Capability.Hebrew, chosen);
    }

    [Fact]
    public void AListOfCapabilitiesIsTakenTogetherAndClosedOnce()
    {
        IReadOnlySet<Capability> chosen = ModelsCommand.ParseCapabilities("photos,speech")!;

        Assert.Equal([Capability.Photos, Capability.Meaning, Capability.Speech],
                     chosen.OrderBy(c => (int)c).ToArray());
    }

    [Fact]
    public void AnUnknownCapabilityNameIsRefusedRatherThanIgnored()
    {
        // Silently dropping a name means `--models install photos,speach` installs photos and
        // reports success, and the person waits for speech search that is never coming.
        Assert.Null(ModelsCommand.ParseCapabilities("photos,speach"));
        Assert.Null(ModelsCommand.ParseCapabilities(""));
    }
}
