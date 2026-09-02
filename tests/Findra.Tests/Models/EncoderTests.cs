using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

using Findra;

using Xunit;

/// <summary>
/// The pure parts of the encoders - the only parts a test can reach without a 350 MB file on
/// disk, and the parts where being wrong is silent. A preprocessing layout that is subtly wrong
/// produces embeddings that look exactly like embeddings and rank exactly like noise, and no
/// integration test would catch it either.
/// </summary>
public class EncoderTests
{
    private static SKBitmap Solid(SKColor c, int w = 64, int h = 64)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(c);
        return bmp;
    }

    [Fact]
    public void APictureBecomesThreePlanesInTheOrderTheModelWasTrainedOn()
    {
        // One assertion that catches three separate wrong implementations at once:
        //   - an interleaved (H,W,C) layout, which the model reads as noise;
        //   - blue and red swapped, which is what happens when the pixel span is read as BGRA;
        //   - a 0..1 scaling instead of the -1..1 the model expects.
        // None of the three throws, and all three produce embeddings that look like embeddings.
        using SKBitmap red = Solid(new SKColor(255, 0, 0));
        float[] px = ClipImageEncoder.Preprocess(red);

        int plane = ClipImageEncoder.Size * ClipImageEncoder.Size;
        Assert.Equal(3 * plane, px.Length);
        Assert.All(px[..plane], v => Assert.True(Math.Abs(v - 1f) < 1e-3f, $"the red plane holds {v}"));
        Assert.All(px[plane..(2 * plane)], v => Assert.True(Math.Abs(v + 1f) < 1e-3f, $"the green plane holds {v}"));
        Assert.All(px[(2 * plane)..], v => Assert.True(Math.Abs(v + 1f) < 1e-3f, $"the blue plane holds {v}"));
    }

    [Fact]
    public void AMidGreyLandsInTheMiddleOfTheModelsRangeAndNotAtAHalf()
    {
        // The /127.5 - 1 scaling, asserted where the two candidate scalings differ most.
        using SKBitmap grey = Solid(new SKColor(128, 128, 128));
        float[] px = ClipImageEncoder.Preprocess(grey);
        Assert.All(px, v => Assert.True(Math.Abs(v) < 0.02f, $"mid grey mapped to {v}, not to about 0"));
    }

    [Fact]
    public void AWidePictureIsSquashedRatherThanCroppedSoItsEdgesSurvive()
    {
        // SigLIP was trained on squashed images, not centre-cropped ones, and a crop throws away
        // the edges of every wide photo silently - the output is the same size either way, so an
        // assertion on the LENGTH cannot tell the two apart and an earlier draft of this test
        // asserted nothing else.
        //
        // So: a 1024x256 picture whose left eighth is green and whose remainder is black. A plain
        // resize maps source column c to destination column c/4, so the green band survives at
        // destination columns 0..31. A 256x256 centre crop takes source columns 384..639, which
        // are all black, and the green is gone.
        var bmp = new SKBitmap(new SKImageInfo(1024, 256, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Black);
            using var green = new SKPaint { Color = new SKColor(0, 255, 0) };
            canvas.DrawRect(new SKRect(0, 0, 128, 256), green);
        }
        using (bmp)
        {
            float[] px = ClipImageEncoder.Preprocess(bmp);
            int plane = ClipImageEncoder.Size * ClipImageEncoder.Size;
            int greenPlane = plane;                       // planes are R, G, B
            int row = 128 * ClipImageEncoder.Size;        // halfway down, away from any edge

            Assert.Equal(3 * plane, px.Length);
            Assert.True(px[greenPlane + row + 5] > 0.5f,
                "the left of the picture is not in the output - it was cropped, not squashed");
            Assert.True(px[greenPlane + row + 200] < -0.5f,
                "the right of the picture is not in the output");
        }
    }

    [Fact]
    public void MeanPoolingIgnoresThePaddingItIsMaskedAgainst()
    {
        // Padding is attended over as zeros in the mask, and a pool that averages it anyway
        // drags every short passage towards whatever the padding embedding happens to be.
        var hidden = new DenseTensor<float>(new[] { 1, 3, 2 });
        hidden[0, 0, 0] = 2; hidden[0, 0, 1] = 4;
        hidden[0, 1, 0] = 4; hidden[0, 1, 1] = 8;
        hidden[0, 2, 0] = 1000; hidden[0, 2, 1] = 1000;     // padding, masked off

        float[] pooled = Onnx.MeanPool(hidden, [1, 1, 0], 2);

        Assert.Equal(3f, pooled[0], 3);
        Assert.Equal(6f, pooled[1], 3);
    }

    [Fact]
    public void MeanPoolingNothingIsZeroRatherThanADivideByZero()
    {
        var hidden = new DenseTensor<float>(new[] { 1, 2, 2 });
        float[] pooled = Onnx.MeanPool(hidden, [0, 0], 2);
        Assert.All(pooled, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void TheVocabularyShiftPutsEveryTokenWhereTheModelExpectsIt()
    {
        // XLM-R's ids are SentencePiece's shifted by one, to make room for <s>=0, <pad>=1 and
        // </s>=2, with SentencePiece's own <unk> (0) landing on 3. The tokenizer knows nothing
        // about that. Get it wrong and nothing throws: every embedding is off by one token id,
        // which is a model quietly reading a different sentence from the one that was typed.
        long[] ids = E5Encoder.ShiftIds([0, 5, 7], max: 512);

        Assert.Equal([0L, 3L, 6L, 8L, 2L], ids);
    }

    [Fact]
    public void APassageLongerThanTheModelIsCutButStillClosedProperly()
    {
        // The last token must be </s> whatever happens, or the model reads a truncated sentence
        // as an unfinished one.
        long[] ids = E5Encoder.ShiftIds(Enumerable.Repeat(9, 4000).ToList(), max: 16);

        Assert.Equal(16, ids.Length);
        Assert.Equal(0L, ids[0]);
        Assert.Equal(2L, ids[^1]);
    }

    [Fact]
    public void AChunkIsEmbeddedWithItsFileNameInFrontOfIt()
    {
        // "the lease agreement" has to find a Hebrew-named contract from a chunk that never
        // says the word lease. Separators become spaces so the name reads as words.
        string p = E5Encoder.Passage(@"C:\docs\rental-agreement_2026.pdf", "the tenant shall pay");

        Assert.StartsWith("rental agreement 2026", p, StringComparison.Ordinal);
        Assert.Contains("the tenant shall pay", p, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf", p, StringComparison.Ordinal);
    }
}
