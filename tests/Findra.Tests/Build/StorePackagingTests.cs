using System.Xml.Linq;

using Findra;

using SkiaSharp;

using Xunit;

/// <summary>
/// The Microsoft Store packaging, held together as files. The package cannot be built in this
/// suite - makeappx lives on a Windows runner - so what is checked here is everything the runner
/// will read: the manifest parses, its placeholders are exactly the ones the build script fills
/// or refuses, and every image it names exists at the size its name declares.
///
/// <para>The identity placeholders are asserted BOTH ways, the way the winget manifest's zero
/// hashes are: a committed placeholder is correct, and so is a real value once Partner Center
/// has minted one. What is never correct is a value that is neither - a half-typed Publisher
/// with no CN= would sail through a "not the placeholder" check and fail in Redmond instead.</para>
/// </summary>
public class StorePackagingTests
{
    private const string ManifestPath = "packaging/store/Package.appxmanifest";

    private static readonly XNamespace F = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Rescap =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    private static XDocument Manifest() => XDocument.Load(Repo.Path_(ManifestPath));

    /// <summary>Every image the manifest names, with the pixel size its own file name declares.</summary>
    private static readonly (string File, int W, int H)[] Tiles =
    [
        ("Square44x44Logo.png", 44, 44),
        ("Square71x71Logo.png", 71, 71),
        ("Square150x150Logo.png", 150, 150),
        ("Square310x310Logo.png", 310, 310),
        ("StoreLogo.png", 50, 50),
        ("Wide310x150Logo.png", 310, 150),
        ("SplashScreen.png", 620, 300),
    ];

    [Fact]
    public void TheManifestDeclaresTheTwoRestrictedCapabilitiesThePackageNeeds()
    {
        // runFullTrust is the desktop bridge itself; allowElevation is the one UAC prompt that
        // registers the names-helper task, and dropping it quietly is how the Store build loses
        // name search without anybody noticing. Both are reviewed at submission, which is why
        // they are restricted - and why this test names them.
        var capabilities = Manifest().Descendants(Rescap + "Capability")
                                     .Select(c => (string?)c.Attribute("Name"))
                                     .ToList();
        Assert.Contains("runFullTrust", capabilities);
        Assert.Contains("allowElevation", capabilities);
    }

    [Fact]
    public void TheVersionAndArchitectureArePlaceholdersTheBuildScriptFills()
    {
        // Directory.Build.props is the only place a version is written. The manifest's 0.0.0.0
        // is not a version, it is a marker that Make-Msix.ps1 replaces - and a committed real
        // number here would be a second version file, the exact defect VersionTests exists on.
        XElement identity = Manifest().Descendants(F + "Identity").Single();
        Assert.Equal("0.0.0.0", (string?)identity.Attribute("Version"));
        Assert.Equal("__ARCH__", (string?)identity.Attribute("ProcessorArchitecture"));

        string script = Repo.Read("build/Make-Msix.ps1");
        Assert.Contains("Directory.Build.props", script, StringComparison.Ordinal);
        Assert.Contains("ProcessorArchitecture", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdentityIsEitherThePlaceholdersOrTheValuesPartnerCenterMints()
    {
        XElement identity = Manifest().Descendants(F + "Identity").Single();
        string name = (string?)identity.Attribute("Name") ?? "";
        string publisher = (string?)identity.Attribute("Publisher") ?? "";
        string display = Manifest().Descendants(F + "PublisherDisplayName").Single().Value;

        Assert.True(name == "__PACKAGE_IDENTITY_NAME__" || name.Length > 0,
            "the identity Name is neither the placeholder nor a value");
        Assert.True(publisher == "__PACKAGE_PUBLISHER__" || publisher.StartsWith("CN=", StringComparison.Ordinal),
            "a real Publisher from Partner Center always begins with CN=");
        Assert.True(display == "__PUBLISHER_DISPLAY_NAME__" || display.Length > 0,
            "the PublisherDisplayName is neither the placeholder nor a value");

        // Each field is independently placeholder-or-real: Publisher and PublisherDisplayName
        // are account-level and were filled from the existing developer account on day one,
        // while Name exists only after the app name is reserved. The build script refuses any
        // surviving placeholder, so a partially filled file cannot pack - which is exactly the
        // guarantee the all-or-none rule was bought with, kept per-field instead.
    }

    [Fact]
    public void EveryImageTheManifestNamesExistsAtTheSizeItsNameDeclares()
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Manifest().Descendants(F + "Logo").Single().Value,
        };
        XElement visual = Manifest().Descendants(Uap + "VisualElements").Single();
        foreach (string attr in new[] { "Square150x150Logo", "Square44x44Logo" })
            named.Add((string?)visual.Attribute(attr) ?? "");
        XElement tile = visual.Descendants(Uap + "DefaultTile").Single();
        foreach (string attr in new[] { "Wide310x150Logo", "Square310x310Logo", "Square71x71Logo" })
            named.Add((string?)tile.Attribute(attr) ?? "");
        named.Add((string?)visual.Descendants(Uap + "SplashScreen").Single().Attribute("Image") ?? "");

        // Every name in the manifest is one of the seven tiles, and every tile is named in the
        // manifest. An asset nothing references is dead weight; a reference to nothing is a
        // packaging failure on the runner.
        Assert.Equal(Tiles.Length, named.Count);
        foreach ((string file, int w, int h) in Tiles)
        {
            Assert.Contains($"Assets\\{file}", named);
            using SKBitmap? bmp = SKBitmap.Decode(Repo.Path_($"packaging/store/Assets/{file}"));
            Assert.True(bmp is not null, $"packaging/store/Assets/{file} does not decode");
            Assert.Equal(w, bmp!.Width);
            Assert.Equal(h, bmp.Height);
        }
    }

    [Fact]
    public void TheTilesComeFromTheSameScriptAsEveryOtherCopyOfTheMark()
    {
        // Hand-editing a tile is how two versions of the logo start to exist. Make-Icon.mjs is
        // the one definition of the mark, so the tiles must be something IT writes - and this
        // is the sentence that notices if the emission block is ever deleted.
        Assert.Contains("packaging/store/Assets", Repo.Read("build/Make-Icon.mjs"), StringComparison.Ordinal);
    }
}
