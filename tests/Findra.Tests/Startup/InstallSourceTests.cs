using Findra;
using Findra.Startup;
using Xunit;

/// <summary>
/// Spec §9b: "The install source is recorded at first run, not guessed at every launch." What it
/// decides is which sentence a person is given when an update exists, and a wrong one is advice
/// they cannot act on.
/// </summary>
public class InstallSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-src-" + Guid.NewGuid().ToString("N"));

    public InstallSourceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } GC.SuppressFinalize(this); }

    private void Marker(string text) => File.WriteAllText(Path.Combine(_dir, InstallSource.MarkerFile), text);

    [Fact]
    public void AMarkerWrittenByTheInstallerIsReadThroughItsLineEndingAndItsCase()
    {
        // Every way of writing a one-word file adds something: a trailing newline, a BOM, a
        // capital. A raw string comparison matches none of them and every installed copy reports
        // itself as unknown.
        Marker("winget\r\n");
        Assert.Equal("winget", InstallSource.Detect(_dir));

        Marker("  Installer  ");
        Assert.Equal("installer", InstallSource.Detect(_dir));
    }

    [Fact]
    public void NoMarkerAtAllMeansSomebodyBuiltItThemselves()
    {
        // The `git clone && dotnet publish` route has no installer to write one. Reporting unknown
        // here would tell a source builder to run winget upgrade for a package winget has never
        // heard of on their machine.
        Assert.Equal("source", InstallSource.Detect(_dir));
    }

    [Fact]
    public void AMarkerSayingSomethingElseIsUnknownRatherThanTrusted()
    {
        // Whatever is in that file is printed in the About section and switched on for advice. An
        // unrecognised word has to become "unknown", which has a sentence of its own, rather than
        // appearing verbatim as "Installed via carrier-pigeon".
        Marker("carrier-pigeon");
        Assert.Equal("unknown", InstallSource.Detect(_dir));
    }

    [Fact]
    public void ADirectoryThatIsNotThereIsNotAnException()
    {
        Assert.Equal("source", InstallSource.Detect(Path.Combine(_dir, "gone")));
    }

    [Fact]
    public void ASourceAlreadyRecordedIsNeverGuessedAgain()
    {
        // The whole of spec §9b's "recorded at first run, not guessed at every launch". A winget
        // copy whose marker file is lost - an antivirus quarantine, a partial repair - must keep
        // saying winget rather than silently becoming a source build.
        Config recorded = Config.Default with { InstallSource = "winget" };
        Assert.Equal("winget", InstallSource.Resolve(recorded, _dir));
    }

    [Fact]
    public void ACopyWithNothingRecordedYetGetsAnAnswerNow()
    {
        Marker("winget");
        Assert.Equal("winget", InstallSource.Resolve(Config.Default, _dir));
    }
}
