using Findra;

using Xunit;

/// <summary>
/// The sequence and batch shapes an accelerator is asked to compile.
///
/// <para>DirectML compiles its kernels per input SHAPE. A document's chunks are whatever length
/// they happen to be and its last batch is whatever is left over, so an unbucketed indexer hands
/// the provider a shape it has never seen for nearly every batch and pays a fresh compile each
/// time - a cost several times the inference it is wrapped around. Rounding both dimensions up
/// collapses that to a handful of shapes for the life of the process.</para>
///
/// <para>The padding is free rather than merely cheap: the attention mask is zero over it and the
/// pooling counts only real tokens, so a bucketed vector is the same vector.</para>
/// </summary>
public class ShapeBucketTests
{
    [Theory]
    [InlineData(1, 32)]
    [InlineData(31, 32)]
    [InlineData(32, 32)]
    [InlineData(33, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 96)]
    [InlineData(480, 480)]
    [InlineData(481, 512)]
    public void ASequenceLengthRoundsUpToTheNextStep(int len, int want)
        => Assert.Equal(want, E5Encoder.Bucket(len));

    [Fact]
    public void TheBucketNeverExceedsWhatTheModelWasAskedFor()
    {
        // 512 is the cap the tokenizer already truncates to. A bucket above it would build a
        // tensor wider than any real input and, on an export with a fixed position table, throw.
        Assert.Equal(512, E5Encoder.Bucket(512));
        Assert.Equal(512, E5Encoder.Bucket(600));
    }

    [Fact]
    public void ABucketIsNeverShorterThanTheTextItHasToHold()
    {
        // The one property that would corrupt a vector rather than merely slow it down: a bucket
        // below the real token count would cut tokens off the end of the passage silently.
        for (int n = 1; n <= 512; n++) Assert.True(E5Encoder.Bucket(n) >= n, $"bucket({n}) lost tokens");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 8)]
    [InlineData(8, 8)]
    [InlineData(9, 16)]
    [InlineData(16, 16)]
    public void ABatchRoundsUpToAPowerOfTwo(int n, int want)
        => Assert.Equal(want, E5Encoder.BatchBucket(n));

    [Fact]
    public void APowerOfTwoBatchIsTheCompromiseAndTheReasonIsSize()
    {
        // Padding every batch to the full 16 would give exactly one shape and compute fifteen
        // wasted rows for a document with seventeen chunks. Powers of two cost at most twice the
        // rows and still leave only five batch shapes to compile.
        Assert.Equal(5, new[] { 1, 2, 4, 8, 16 }.Length);
        for (int n = 1; n <= 16; n++)
        {
            int b = E5Encoder.BatchBucket(n);
            Assert.True(b >= n, $"batch bucket({n}) would drop rows");
            Assert.True(b < n * 2 || n == 1, $"batch bucket({n}) wastes more than double");
        }
    }

    [Fact]
    public void TheWholeIndexerCollapsesToAHandfulOfShapes()
    {
        // The point of the change, stated as the number it moves. Every length up to the cap and
        // every batch up to sixteen, which unbucketed is thousands of distinct shapes.
        var shapes = new HashSet<(int, int)>();
        for (int len = 1; len <= 512; len++)
            for (int n = 1; n <= 16; n++)
                shapes.Add((E5Encoder.Bucket(len), E5Encoder.BatchBucket(n)));
        Assert.Equal(16 * 5, shapes.Count);
    }
}
