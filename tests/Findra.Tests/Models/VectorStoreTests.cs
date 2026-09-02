using System.Text;

using Findra;

public class VectorStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-vec-" + Guid.NewGuid().ToString("N"));

    public VectorStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Store => Path.Combine(_dir, "vectors.bin");

    /// <summary>A unit vector with all its weight on one axis, so two of them are orthogonal and
    /// their dot product is exactly 0.</summary>
    private static float[] Axis(int i)
    {
        var v = new float[VectorStore.Dim];
        v[i] = 1f;
        return v;
    }

    [Fact]
    public void AVectorIsItsOwnBestMatch()
    {
        using (var w = new VectorStore(Store, writer: true))
        {
            w.Append(Axis(0), 0);
            w.Append(Axis(1), 0);
            w.Append(Axis(2), 0);
            w.Flush();
        }
        using var r = new VectorStore(Store);

        List<VectorStore.Match> top = r.Search(Axis(1), 3, []);

        Assert.Equal(1, top[0].Row);
        Assert.True(top[0].Score > 0.99f, $"a vector scored {top[0].Score} against itself");
        Assert.True(top[1].Score < 0.01f, "an orthogonal vector scored as a match");
    }

    [Fact]
    public void HalfPrecisionKeepsEnoughOfTheVectorToRankWithIt()
    {
        // Every row is stored as float16, which is the whole reason a million vectors fit in a
        // file worth memory-mapping. If the conversion is wrong the scores are not slightly off,
        // they are noise, and this catches that rather than a rounding change.
        var v = new float[VectorStore.Dim];
        for (int i = 0; i < v.Length; i++) v[i] = (i % 7) - 3;
        VectorStore.Normalise(v);

        using (var w = new VectorStore(Store, writer: true)) { w.Append(v, 1); w.Flush(); }
        using var r = new VectorStore(Store);

        Assert.True(r.Search(v, 1, [])[0].Score > 0.99f);
    }

    [Fact]
    public void ATombstonedRowCanNeverMatchAgain()
    {
        // How a deleted or replaced file stops being findable. A no-op here leaves a photo that
        // was deleted a year ago answering queries for ever.
        using (var w = new VectorStore(Store, writer: true))
        {
            w.Append(Axis(0), 0);
            w.Append(Axis(1), 0);
            w.Tombstone(1);
            w.Flush();
        }
        using var r = new VectorStore(Store);

        List<VectorStore.Match> top = r.Search(Axis(1), 5, []);
        Assert.DoesNotContain(top, m => m.Row == 1);
    }

    [Fact]
    public void AKindFilterAnswersOnlyWithTheKindsItWasAskedFor()
    {
        // Named for what it measures. The search reads every row and filters on the kind byte, so
        // this is a correctness claim about the ANSWER and not a claim about work avoided.
        using (var w = new VectorStore(Store, writer: true))
        {
            w.Append(Axis(0), ContentDb.SegImage);
            w.Append(Axis(0), ContentDb.SegText);      // the same vector, a different kind
            w.Flush();
        }
        using var r = new VectorStore(Store);

        List<VectorStore.Match> images = r.Search(Axis(0), 5, [ContentDb.SegImage]);
        Assert.Single(images);
        Assert.Equal(0, images[0].Row);
        Assert.Equal(2, r.Search(Axis(0), 5, []).Count);   // and no filter means both
    }

    [Fact]
    public void AStoreWrittenAtAnotherWidthIsStartedOverRatherThanRead()
    {
        // A vector file from a build whose model had a different hidden size is not this build's
        // file. Reading it produces scores that are not wrong-looking, only wrong.
        using (var fs = new FileStream(Store, FileMode.Create))
        {
            // The REAL magic, written as the bytes it has to be, so this fixture fails the WIDTH
            // check and not the magic check - otherwise the test passes for a reason its name
            // does not give.
            fs.Write("FVS1"u8.ToArray());
            fs.Write(BitConverter.GetBytes(512));           // not Dim
            fs.Write(BitConverter.GetBytes(99L));
        }
        using (var w = new VectorStore(Store, writer: true)) { w.Append(Axis(0), 0); w.Flush(); }
        using var r = new VectorStore(Store);

        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void AReaderSeesOnlyWhatTheWriterFlushed()
    {
        // The count lives in the header, so a reader that trusted the file LENGTH would read a
        // row the writer is halfway through appending.
        using var w = new VectorStore(Store, writer: true);
        w.Append(Axis(0), 0);
        w.Append(Axis(1), 0);

        using (var early = new VectorStore(Store)) Assert.Equal(0, early.Count);

        w.Flush();
        using var after = new VectorStore(Store);
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public void NormaliseLeavesAZeroVectorAloneRatherThanProducingNaN()
    {
        // One NaN in a stored row makes every comparison against it false and every top-k list
        // that touches it wrong, silently, for the life of the file.
        var v = new float[VectorStore.Dim];
        VectorStore.Normalise(v);
        Assert.All(v, f => Assert.Equal(0f, f));
    }

    [Fact]
    public void NormaliseMakesAVectorUnitLength()
    {
        var v = new float[VectorStore.Dim];
        for (int i = 0; i < 8; i++) v[i] = 3f;
        VectorStore.Normalise(v);
        float sum = 0;
        foreach (float f in v) sum += f * f;
        Assert.True(Math.Abs(sum - 1f) < 1e-4f, $"the squared length is {sum}");
    }

    [Fact]
    public void TheFileFormatCarriesFindrasOwnMagicAndNobodyElses()
    {
        // Four bytes in the header of every vector file this build writes, and no grep of the
        // source text can reach them, because in the source they are an integer. They are a
        // compatibility statement - a file that starts with these is Findra's - so they are
        // asserted on the bytes that land on disk and never on the literal.
        using (var w = new VectorStore(Store, writer: true)) { w.Append(Axis(0), 0); w.Flush(); }

        byte[] head = File.ReadAllBytes(Store)[..4];
        Assert.Equal("FVS1", Encoding.ASCII.GetString(head));
    }

    [Fact]
    public void TheStoreLivesBesideTheIndexAndNotBesideTheModels()
    {
        Assert.Equal(Path.Combine(Paths.Index, "vectors.bin"), VectorStore.DefaultPath);
    }
}
