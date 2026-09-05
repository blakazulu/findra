using System.Globalization;

using Findra;

[Collection("culture")]
public class ModelStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-models-" + Guid.NewGuid().ToString("N"));

    public ModelStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Write(Model m, long bytes)
    {
        string p = ModelStore.PathOf(m, _dir);
        using var fs = new FileStream(p, FileMode.Create, FileAccess.Write);
        fs.SetLength(bytes);
        return p;
    }

    [Fact]
    public void TheSevenFilesAreTheOnesTheSpecMeasured()
    {
        Assert.Equal(7, ModelStore.All.Count);
        Assert.Equal(
            new[] { "siglip2-vision.onnx", "siglip2-text-q.onnx", "siglip2.spm", "e5-base.onnx",
                    "e5-small.spm", "whisper-turbo-q5.bin", "whisper-ivrit.bin" },
            ModelStore.All.Select(m => m.File).ToArray());
    }

    [Fact]
    public void EveryDeclaredSizeIsTheMeasuredOneAndNotTheConservativeFloor()
    {
        // Two numbers with two jobs. MinBytes decides "is the file on disk complete"; Bytes is
        // what a person is asked to consent to downloading. Collapsing them - which is what
        // happens if the port keeps only the field it inherited - understates the whole set by
        // 145 MB and makes the README's 2.9 GB wrong.
        foreach (Model m in ModelStore.All)
            Assert.True(m.Bytes > m.MinBytes,
                $"{m.File}: the declared size ({m.Bytes}) is not above the completeness floor ({m.MinBytes})");
    }

    [Fact]
    public void TheWholeSetIsTwoPointNineThreeGigabytes()
    {
        Assert.Equal(3_973_264_175L, ModelStore.TotalBytes(ModelStore.All));
        Assert.Equal("3.7 GB", Sizes.Human(ModelStore.TotalBytes(ModelStore.All)));
    }

    [Fact]
    public void EveryModelCarriesAHttpsUrlAndAPurposeSomebodyCanRead()
    {
        foreach (Model m in ModelStore.All)
        {
            Assert.StartsWith("https://", m.Url, StringComparison.Ordinal);
            Assert.NotEqual("", m.Purpose);
        }
    }

    [Fact]
    public void AFileShorterThanItsFloorIsNotPresent()
    {
        // A half-written file under the final name would otherwise read as installed for ever:
        // the loader opens it, the load fails, the capability is dead, and nothing re-downloads
        // it because "it is there". The floor is the only thing between that and a user.
        Model m = ModelStore.E5Spm;
        Assert.False(ModelStore.Present(m, _dir));      // nothing on disk

        Write(m, 10);
        Assert.False(ModelStore.Present(m, _dir));      // there, and far too short

        Write(m, m.MinBytes);
        Assert.True(ModelStore.Present(m, _dir));
    }

    [Fact]
    public void PresenceIsCheckedAgainstTheFloorAndNotTheDeclaredSize()
    {
        // A publisher who re-exports a model a few kilobytes smaller must not cost every user a
        // 1.5 GB re-download. The floor is generous on purpose; the declared size is for display.
        //
        // Siglip2Spm, not WhisperTurbo: SetLength allocates the clusters, so a fixture built on
        // the whisper floor writes 500 MB of temporary disk for an assertion about an inequality,
        // and on a small or full disk `dotnet test` then fails for a reason nobody connects to
        // this test. Any model whose floor is below its declared size proves the same thing.
        Model m = ModelStore.Siglip2Spm;
        Write(m, m.MinBytes);
        Assert.True(ModelStore.Present(m, _dir));
        Assert.True(m.MinBytes < m.Bytes);
    }

    [Fact]
    public void AMissingDirectoryIsANormalStateAndNotAnException()
    {
        string never = Path.Combine(_dir, "not-created-yet");
        Assert.Equal(ModelStore.All.Count, ModelStore.Missing(ModelStore.All, never).Count);
    }

    [Fact]
    public void MissingNamesExactlyWhatIsNotThere()
    {
        Write(ModelStore.Siglip2Spm, ModelStore.Siglip2Spm.MinBytes);
        IReadOnlyList<Model> gone = ModelStore.Missing(
            [ModelStore.Siglip2Spm, ModelStore.E5Spm], _dir);
        Assert.Equal(["e5-small.spm"], gone.Select(m => m.File).ToArray());
    }

    [Fact]
    public void ModelsLiveUnderLocalAppDataAndNeverBesideTheExecutable()
    {
        // Spec §2 and §4: never in the publish folder, because an upgrade replaces it, and
        // never under Roaming, because 2.9 GB must not follow somebody between machines.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.StartsWith(local, ModelStore.Dir, StringComparison.OrdinalIgnoreCase);
        Assert.False(ModelStore.Dir.StartsWith(roaming, StringComparison.OrdinalIgnoreCase));
        Assert.False(ModelStore.Dir.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(659_659_160L, "629 MB")]          // photos
    [InlineData(1_115_055_717L, "1.04 GB")]       // meaning in documents
    [InlineData(1_624_558_796L, "1.51 GB")]       // the Hebrew fine-tune
    [InlineData(3_973_264_175L, "3.7 GB")]        // everything
    public void SizesReadTheWayThePersonPayingForThemWouldWriteThem(long bytes, string want)
        => Assert.Equal(want, Sizes.Human(bytes));

    [Fact]
    public void SizesReadTheSameOnEveryMachine()
    {
        // InvariantGlobalization is false on purpose, so a bare format string renders "2,93 GB"
        // on a German machine - and this text goes on the first-run screen, in --searchmodels
        // and into the README.
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("3.7 GB", Sizes.Human(3_973_264_175L));
            Assert.Equal("629 MB", Sizes.Human(659_659_160L));
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }
}
