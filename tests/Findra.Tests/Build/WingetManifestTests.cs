using System.Text.RegularExpressions;

using Findra;
using Xunit;

/// <summary>
/// The winget manifests. What is wrong with one of these is discovered by somebody else, on their
/// machine, after `winget install` - so the parts that decide what happens there are asserted
/// against the code they have to agree with.
/// </summary>
public class WingetManifestTests
{
    private static readonly string Version = Yaml("packaging/winget/blakazulu.Findra.yaml");
    private static readonly string Installer = Yaml("packaging/winget/blakazulu.Findra.installer.yaml");
    private static readonly string Locale = Yaml("packaging/winget/blakazulu.Findra.locale.en-US.yaml");
    private static readonly string Workflow = Yaml(".github/workflows/winget.yml");

    /// <summary>
    /// A YAML file with its whole-line comments taken out, which is what every assertion in this
    /// class reads. The manifests and the workflow both carry comments that say what the line
    /// below them is for - "the /INSTALLSOURCE switch", "keeps packaging/winget the source of
    /// truth" - and a whole-file search finds the explanation just as happily as the thing it
    /// explains. Three tests in this suite's sibling class were kept green that way before
    /// anybody noticed, so the comments come out first here.
    /// </summary>
    private static string Yaml(string relative) =>
        string.Join("\n", Repo.Read(relative)
                              .Replace("\r\n", "\n", StringComparison.Ordinal)
                              .Split('\n')
                              .Where(l => !l.TrimStart().StartsWith('#')));

    [Fact]
    public void TheManifestsThisRepositoryKeepsAreTheOnesThatGetPublished()
    {
        // Otherwise every assertion in this class is about a file nobody publishes. `wingetcreate
        // update` bumps the manifests already in the catalogue, which cannot make a first
        // submission and leaves the description and the install switch living in somebody else's
        // repository afterwards - with these twelve tests still green on a copy that has drifted.
        Assert.Contains("packaging/winget", Workflow, StringComparison.Ordinal);
        Assert.Contains("wingetcreate.exe submit", Workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("wingetcreate.exe update", Workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPlaceholderHashCanReachTheCatalogue()
    {
        // The repository copy carries sixty-four zeros rather than plausible-looking hex, so a
        // manifest submitted by hand without running the workflow fails validation immediately
        // instead of installing something nobody verified. The workflow substitutes both and
        // throws if either survives.
        //
        // Every hash in the file, not "the file contains sixty-four zeros somewhere". Pasting a
        // real x64 hash in by hand and leaving arm64 alone is the likely version of this mistake,
        // and the plan's whole-file Contains was answered by the entry that had not been touched.
        string zeros = new('0', 64);
        string[] hashes = [.. Regex.Matches(Installer, @"(?m)^\s*InstallerSha256:\s*(\S+)")
                                   .Select(m => m.Groups[1].Value)];

        Assert.Equal(2, hashes.Length);
        Assert.All(hashes, h => Assert.Equal(zeros, h));
        Assert.Contains("a placeholder hash survived", Workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThreeManifestsAgreeAboutWhichVersionTheyDescribe()
    {
        // Three files, one version, and winget rejects a set that disagrees - after the pull
        // request is open, in somebody else's repository, which is the worst place to find out.
        string[] versions = [.. new[] { Version, Installer, Locale }
            .Select(m => Regex.Match(m, @"(?m)^PackageVersion:\s*(\S+)").Groups[1].Value)];

        Assert.All(versions, v => Assert.False(string.IsNullOrEmpty(v)));
        Assert.Single(versions.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePackageIdentifierIsTheOneFindraTellsPeopleToType()
    {
        // UpdateCheck.Advice prints `winget upgrade blakazulu.Findra`. If the manifest is filed
        // under any other identifier, that command fails with "no installed package found" for
        // every person who follows Findra's own advice.
        Assert.Contains("blakazulu.Findra", UpdateCheck.Advice("winget", "1.0.0"), StringComparison.Ordinal);

        foreach (string manifest in new[] { Version, Installer, Locale })
            Assert.Contains("PackageIdentifier: blakazulu.Findra", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void OneVersionCarriesBothArchitectures()
    {
        // One manifest, two installers (spec 6's "never assume x64"). Two package versions would
        // mean a person on ARM is offered an x64 build or nothing at all.
        MatchCollection arch = Regex.Matches(Installer, @"(?m)^\s*-?\s*Architecture:\s*(\S+)");
        string[] found = [.. arch.Select(m => m.Groups[1].Value)];

        Assert.Equal(2, found.Length);
        Assert.Contains("x64", found);
        Assert.Contains("arm64", found);
        Assert.Single(Regex.Matches(Installer, @"(?m)^PackageVersion:"));
    }

    [Fact]
    public void TheInstallerTypeIsTheOneThatIsActuallyBuilt()
    {
        // Task 9 builds an Inno Setup installer. Declaring it as anything else makes winget pass
        // the wrong silent switches, and a silent install that opens a wizard hangs somebody's
        // unattended machine.
        Assert.Contains("InstallerType: inno", Installer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManifestTellsFindraItCameFromWinget()
    {
        // Without this switch, every winget install records itself as "installer", and every
        // update tells the person to download a file by hand instead of running one command
        // (spec 9b, UpdateCheck.Advice).
        Assert.Contains("/INSTALLSOURCE=winget", Installer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingCarriesTheModelSizeAsTextBecauseThereIsNoInstallTimeUi()
    {
        // Spec 2: "winget shows a package size, not a first-run download ... The winget manifest
        // and README carry the size as text; the app carries the actual consent." The number is
        // read from the same constants every surface reads, so it cannot drift into being the
        // conservative floor total.
        Assert.Contains(Sizes.Human(Capabilities.TotalBytes(Capabilities.All)), Locale, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingSaysTheOptionalDownloadsAreOptional()
    {
        // A size with no sentence around it reads as the download size of the package itself.
        Assert.Contains("optional", Locale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheListingNamesNoCompetitor()
    {
        // The same rule the README follows (spec 9a): Findra cannot benchmark them fairly and a
        // claim it cannot defend is worse than no claim. A store listing is the most tempting
        // place in the project to break it.
        foreach (string name in Repo.Competitors)
            Assert.DoesNotContain(name, Locale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheListingDoesNotClaimTheInstallersAreSigned()
    {
        Assert.DoesNotContain("signed", Locale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheLicenceAndTheAttributionAreDeclared()
    {
        // Apache-2.0 with a NOTICE, and the propagating attribution that is the reason for
        // choosing it (spec 11).
        // Keyed to the fields, not to the URL. `github.com/blakazulu/findra` is also the support
        // URL and the licence URL, so a bare search for it was green with the attribution deleted.
        Assert.Contains("License: Apache-2.0", Locale, StringComparison.Ordinal);
        Assert.Contains("Publisher: blakazulu", Locale, StringComparison.Ordinal);
        Assert.Contains("PackageUrl: https://github.com/blakazulu/findra", Locale, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionTheCatalogueWouldPublishIsTheVersionThisBuildIs()
    {
        // The three manifests agreeing with each other is not enough: they can agree on a number
        // the product stopped being. Bumping Directory.Build.props without touching these files
        // was caught only by the release workflow's own stale-version throw, which is after a tag
        // has been pushed. Directory.Build.props is the one place the version lives, so it is the
        // one this is measured against.
        string props = Repo.Read("Directory.Build.props");
        string version = Regex.Match(props, @"<Version>\s*(\S+?)\s*</Version>").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(version), "Directory.Build.props declares no version");

        foreach (string manifest in new[] { Version, Installer, Locale })
            Assert.Equal(version, Regex.Match(manifest, @"(?m)^PackageVersion:\s*(\S+)").Groups[1].Value);
    }
}
