using Findra;
using Xunit;

/// <summary>
/// The query encoders cost about 1.4 GB on the processor, so they open only when the index holds
/// something they could match - and once it does, they open without a restart.
/// </summary>
public class QueryEncoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-enc-" + Guid.NewGuid().ToString("N"));

    public QueryEncoderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    private sealed class Owner : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private static float[] Unit(int i)
    {
        var v = new float[VectorStore.Dim];
        v[i] = 1f;
        return v;
    }

    // ---- what is wanted ----

    [Fact]
    public void AnInstalledModelWithNothingInTheIndexToMatchIsNotWanted()
    {
        // The case that cost 1.76 GB: a reinstall kept the models, "Just names" was chosen, and
        // the interface loaded both encoders for an index that held no vector at all.
        var wanted = Semantic.Wanted(Set(Capability.Meaning, Capability.Photos), (Words: false, Pictures: false));
        Assert.Equal((false, false), wanted);
    }

    [Fact]
    public void EachEncoderIsWantedOnlyForItsOwnKindOfVector()
    {
        var both = Set(Capability.Meaning, Capability.Photos);
        Assert.Equal((true, false), Semantic.Wanted(both, (Words: true, Pictures: false)));
        Assert.Equal((false, true), Semantic.Wanted(both, (Words: false, Pictures: true)));
        Assert.Equal((true, true), Semantic.Wanted(both, (Words: true, Pictures: true)));
    }

    [Fact]
    public void VectorsWithoutTheirModelWantNothing()
    {
        // A model deleted by hand leaves its vectors behind. There is nothing to open.
        Assert.Equal((false, false), Semantic.Wanted(Set(), (Words: true, Pictures: true)));
        Assert.Equal((false, true), Semantic.Wanted(Set(Capability.Photos), (Words: true, Pictures: true)));
    }

    // ---- what the index holds ----

    [Fact]
    public void WordsAreTextAndSpeechAndPicturesAreImagesAndFrames()
    {
        Assert.Equal((true, false), ContentBranch.Holds([(byte)ContentDb.SegText]));
        Assert.Equal((true, false), ContentBranch.Holds([(byte)ContentDb.SegSpeech]));
        Assert.Equal((false, true), ContentBranch.Holds([(byte)ContentDb.SegImage]));
        Assert.Equal((false, true), ContentBranch.Holds([(byte)ContentDb.SegFrame]));
        Assert.Equal((false, false), ContentBranch.Holds([]));
    }

    [Fact]
    public void ADiscardedRowHoldsNothing()
    {
        // A tombstoned row's kind byte is 255; a file that was read and then deleted is not a
        // reason to spend a gigabyte on an encoder.
        Assert.Equal((false, false), ContentBranch.Holds([255, 255]));
    }

    [Fact]
    public void TheKindsAreReadFromDiskWithoutOpeningTheStore()
    {
        string path = Path.Combine(_dir, "vectors.bin");
        Assert.Empty(VectorStore.KindsOnDisk(path));   // no file yet is an empty index, not an error

        using (var w = new VectorStore(path, writer: true))
        {
            w.Append(Unit(0), (byte)ContentDb.SegImage);
            w.Append(Unit(1), (byte)ContentDb.SegText);
            w.Flush();

            // Read while the indexer still holds its writer open, which is when it matters.
            Assert.Equal(new byte[] { (byte)ContentDb.SegImage, (byte)ContentDb.SegText },
                         VectorStore.KindsOnDisk(path));
        }
    }

    // ---- filling a slot later ----

    [Fact]
    public void AnEncoderSuppliedLaterIsSeenByWhoeverAlreadyHoldsTheSemantic()
    {
        // A card holds the Semantic it was built with; an encoder that opens while it is up has
        // to reach it without the card being rebuilt.
        using var store = new VectorStore(Path.Combine(_dir, "vectors.bin"));
        using var semantic = new Semantic(store, text: null, image: null);
        Semantic heldByACard = semantic;

        Assert.Null(heldByACard.Text);
        Assert.True(semantic.Supply(QueryEncoder.Words, _ => Unit(0), new Owner()));
        Assert.NotNull(heldByACard.Text);
        Assert.Null(heldByACard.Image);
    }

    [Fact]
    public void AnEncoderIsNeverReplacedUnderASearch()
    {
        // Replacing would dispose a session a card may be part-way through a query on.
        using var store = new VectorStore(Path.Combine(_dir, "vectors.bin"));
        using var semantic = new Semantic(store, text: null, image: null);
        Func<string, float[]> first = _ => Unit(0);
        var second = new Owner();

        Assert.True(semantic.Supply(QueryEncoder.Pictures, first, new Owner()));
        Assert.False(semantic.Supply(QueryEncoder.Pictures, _ => Unit(1), second));
        Assert.Same(first, semantic.Image);
        Assert.False(second.Disposed);   // refused, so still the caller's to dispose
    }

    [Fact]
    public void ASuppliedEncoderIsDisposedWithTheSemantic()
    {
        using var store = new VectorStore(Path.Combine(_dir, "vectors.bin"));
        var owner = new Owner();
        var semantic = new Semantic(store, text: null, image: null);
        semantic.Supply(QueryEncoder.Words, _ => Unit(0), owner);

        semantic.Dispose();

        Assert.True(owner.Disposed);
    }

    [Fact]
    public void NothingInstalledMeansNoSemanticAtAll()
    {
        Assert.Null(Semantic.For(Set(), Path.Combine(_dir, "vectors.bin")));
    }

    [Fact]
    public void AnInstalledCapabilityGetsASemanticWithNoEncoderYet()
    {
        using Semantic? semantic = Semantic.For(Set(Capability.Meaning), Path.Combine(_dir, "vectors.bin"));
        Assert.NotNull(semantic);
        Assert.Null(semantic.Text);
        Assert.Null(semantic.Image);
    }
}
