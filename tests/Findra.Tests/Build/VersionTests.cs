using System.Xml.Linq;

using Findra;
using Xunit;

/// <summary>
/// One version, in one file, that parses. Spec 9b: the update check compares the running build
/// against the newest release tag, and "a check that gets that wrong is worse than none, because
/// it tells people they are current when they are not". Every failure mode below produces exactly
/// that sentence on somebody else's machine.
/// </summary>
public class VersionTests
{
    private static string Declared()
    {
        XDocument props = XDocument.Load(Repo.Path_("Directory.Build.props"));
        XElement? v = props.Descendants("Version").FirstOrDefault();
        Assert.True(v is not null, "Directory.Build.props declares no <Version>");
        return v!.Value.Trim();
    }

    [Fact]
    public void TheVersionIsDeclaredInDirectoryBuildPropsAndNowhereElse()
    {
        // MSBuild imports Directory.Build.props BEFORE the project body, so a <Version> left in a
        // csproj silently wins and the props file becomes decoration that a person still edits.
        // Two numbers that can differ is the entire thing this task exists to remove.
        Assert.Matches(@"^\d+\.\d+\.\d+$", Declared());

        foreach (string proj in Directory.GetFiles(Repo.Root, "*.csproj", SearchOption.AllDirectories))
        {
            if (proj.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                              StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(proj);
            foreach (string tag in new[] { "Version", "AssemblyVersion", "FileVersion", "InformationalVersion" })
                Assert.DoesNotContain($"<{tag}>", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRunningBuildReportsTheVersionThePropsFileDeclares()
    {
        // Read from the source tree, compared against the compiled assembly. They can only differ
        // if something between the two overrode the props file - which is the defect above, seen
        // from the other end.
        Assert.Equal(Declared(), BuildInfo.Version);
    }

    [Fact]
    public void TheVersionThisBuildReportsIsOneTheUpdateCheckCanActuallyCompare()
    {
        // The single most valuable assertion in this task. UpdateCheck.Compare returns 0 for
        // anything System.Version cannot parse, and CheckAsync routes >= 0 to "up to date". So a
        // version carrying the +sha suffix .NET adds to InformationalVersion by default does not
        // fail loudly - it reports every user as current, for ever, silently.
        Assert.True(UpdateCheck.Compare(BuildInfo.Version, "999.0.0") < 0,
            $"'{BuildInfo.Version}' does not compare as older than 999.0.0");
        Assert.True(UpdateCheck.Compare(BuildInfo.Version, "0.0.1") > 0,
            $"'{BuildInfo.Version}' does not compare as newer than 0.0.1");
    }

    [Theory]
    [InlineData("1.2.0+9f3c1a7", "1.2.0.0", "1.2.0")]
    [InlineData("1.10.0", "1.10.0.0", "1.10.0")]
    [InlineData(null, "0.1.0.0", "0.1.0")]
    [InlineData("", "0.4.2.0", "0.4.2")]
    public void BuildMetadataIsStrippedSoTheNumberStaysComparable(
        string? informational, string assemblyVersion, string want)
        => Assert.Equal(want, BuildInfo.Normalise(informational, assemblyVersion));

    [Fact]
    public void APreReleaseSuffixIsLeftInPlaceRatherThanQuietlyTurnedIntoTheRelease()
    {
        // Strip the '-' as well as the '+' and a release candidate reports itself as the release.
        // Leaving it makes Compare return 0 and the state Unknown, which is the honest answer:
        // "we cannot tell". No tag with a suffix can be released anyway (Check-Release.ps1), so
        // this only ever describes a local build.
        Assert.Equal("1.2.0-rc.1", BuildInfo.Normalise("1.2.0-rc.1+9f3c1a7", "1.2.0.0"));
    }

    [Fact]
    public void AVersionThatCouldNotBeReadIsNotReportedAsZero()
    {
        // "0.0.0" would compare as older than every release and put a permanent update banner in
        // the tray. An unknown version has to look unknown.
        Assert.Equal("?", BuildInfo.Normalise(null, null));
    }
}
