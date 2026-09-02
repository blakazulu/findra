using System.Diagnostics;
using System.Text;

using Xunit;

/// <summary>
/// The release gate, exercised the way the workflow exercises it: by running the script.
///
/// <para>A tag with no changelog section must not become a release, and a tag that disagrees with
/// the declared version must not become one either - because Findra compares its own version
/// against the newest tag, and a release whose binary reports a different number tells every
/// installed copy it is current for ever. Both rules live in one script so there is one place to
/// read them, and this class is what stops that script from being wrong in silence.</para>
/// </summary>
public class ReleaseCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-rel-" + Guid.NewGuid().ToString("N"));

    public ReleaseCheckTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A repository-shaped fixture: a props file declaring <paramref name="version"/> and
    /// a changelog holding exactly <paramref name="changelog"/>.</summary>
    private void Fixture(string version, string changelog)
    {
        File.WriteAllText(Path.Combine(_dir, "Directory.Build.props"),
            $"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_dir, "CHANGELOG.md"), changelog);
    }

    private (int Code, string Out, string Err) Run(string tag)
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Repo.Path_("build/Check-Release.ps1"));
        psi.ArgumentList.Add("-Tag");
        psi.ArgumentList.Add(tag);
        psi.ArgumentList.Add("-Root");
        psi.ArgumentList.Add(_dir);

        Process? p;
        // Process.Start THROWS when the executable is not on PATH rather than returning null, and
        // a Win32Exception out of a test reads as a broken test rather than as a missing tool.
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Fail("pwsh could not be started - PowerShell 7 is required to run the build " +
                        $"scripts, and the release gate is one of them: {ex.Message}");
            throw;
        }
        Assert.True(p is not null, "pwsh could not be started");
        using Process proc = p!;
        Task<string> o = proc.StandardOutput.ReadToEndAsync();
        Task<string> e = proc.StandardError.ReadToEndAsync();
        Assert.True(proc.WaitForExit(30_000), "Check-Release.ps1 did not return within 30 seconds");
        Task.WaitAll([o, e], TimeSpan.FromSeconds(5));
        return (proc.ExitCode, o.Result, e.Result);
    }

    private const string TwoSections = """
        # Changelog

        ## [Unreleased]

        ## [1.2.0] - 2026-09-10

        ### Added

        - Something a person reading release notes would care about.

        ## [1.1.0] - 2026-08-01

        ### Fixed

        - An older thing nobody is releasing today.

        [1.2.0]: https://github.com/blakazulu/findra/releases/tag/v1.2.0
        [1.1.0]: https://github.com/blakazulu/findra/releases/tag/v1.1.0
        """;

    [Fact]
    public void AMatchingTagPrintsItsOwnSectionAndStopsAtTheNext()
    {
        // The obvious wrong implementation prints from the heading to the end of the file, which
        // puts the previous release's notes - and the link definitions - into this release's body.
        Fixture("1.2.0", TwoSections);
        (int code, string text, _) = Run("v1.2.0");

        Assert.Equal(0, code);
        Assert.Contains("Something a person reading release notes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("An older thing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[1.1.0]:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLinkDefinitionsAtTheBottomAreNotPartOfTheOldestSectionsNotes()
    {
        // The oldest section runs to the end of the file, so "stop at the next ## heading" is not
        // enough on its own: without dropping reference definitions, releasing the last section
        // publishes a body ending in two bare URLs.
        Fixture("1.1.0", TwoSections);
        (int code, string text, _) = Run("v1.1.0");

        Assert.Equal(0, code);
        Assert.Contains("An older thing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://github.com/blakazulu/findra/releases/tag", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATagThatDisagreesWithTheDeclaredVersionIsRefusedAndBothNumbersAreNamed()
    {
        Fixture("1.2.0", TwoSections);
        (int code, _, string err) = Run("v1.3.0");

        Assert.Equal(4, code);
        Assert.Contains("1.2.0", err, StringComparison.Ordinal);
        Assert.Contains("1.3.0", err, StringComparison.Ordinal);
    }

    [Fact]
    public void APreReleaseTagIsRefusedAndSaysWhichCodeCannotHandleIt()
    {
        // An unanchored ^v\d+\.\d+\.\d+ matches "v1.2.0-rc.1" happily. The consequence is not
        // cosmetic: GitHub's releases/latest skips pre-releases, so the build would exist and no
        // installed copy would ever hear about it.
        Fixture("1.2.0", TwoSections);
        (int code, _, string err) = Run("v1.2.0-rc.1");

        Assert.Equal(3, code);
        Assert.Contains("pre-release", err, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("v1.2")]
    [InlineData("release-1.2.0")]
    public void ATagThatIsNotAVersionTagIsRefused(string tag)
    {
        Fixture("1.2.0", TwoSections);
        Assert.Equal(2, Run(tag).Code);
    }

    [Fact]
    public void ATagWithNoSectionOfItsOwnIsRefused()
    {
        Fixture("1.4.0", TwoSections);
        (int code, _, string err) = Run("v1.4.0");

        Assert.Equal(5, code);
        Assert.Contains("1.4.0", err, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySectionIsNotReleaseNotes()
    {
        // The plausible wrong implementation is a single Select-String for the heading. A heading
        // with nothing under it passes that and publishes a release with an empty body.
        Fixture("1.2.0", """
            # Changelog

            ## [1.2.0] - 2026-09-10

            ## [1.1.0] - 2026-08-01

            - Something.
            """);

        Assert.Equal(5, Run("v1.2.0").Code);
    }

    [Fact]
    public void TheUnreleasedSectionIsNeverUsedAsTheNotesForATag()
    {
        // A heading matcher loose enough to accept "## [Unreleased]" would release every tag with
        // whatever happened to be sitting at the top of the file.
        Fixture("1.2.0", """
            # Changelog

            ## [Unreleased]

            ### Added

            - Work in progress that has not been released.
            """);

        Assert.Equal(5, Run("v1.2.0").Code);
    }
}
