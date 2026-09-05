using Findra;

using Xunit;

/// <summary>
/// The two providers must agree about what a document means.
///
/// <para>Findra embeds documents on the accelerator and embeds the query on the processor, and
/// those two vectors are compared to each other. That only works if the same model gives the same
/// answer on both - and for a QUANTISED model it does not. Measured on one desktop, the shipped
/// <c>model_quantized.onnx</c> came back at 0.970 cosine between the processor and DirectML, with
/// individual elements 0.8 apart, while the processor against itself is exactly 1. DirectML does
/// not execute quantised operators the way the processor does, and no graph optimisation setting
/// changes it - the gap survives <c>ORT_DISABLE_ALL</c>.</para>
///
/// <para>A 3 percent systematic error between stored vectors and query vectors is not a crash.
/// It is a slow rot: every similarity score shifts against thresholds argued in different units,
/// and a driver change moves an existing index between dialects with nothing reporting anything
/// wrong. This test is what stops a future model swap reintroducing it - the cheapest possible
/// check for the most expensive possible mistake.</para>
///
/// <para>It needs the real file, so it says nothing on a machine that has not downloaded one.
/// That is the same bargain every model-backed check here makes, and it is why the assertion is
/// worth keeping cheap enough to run whenever the file happens to be present.</para>
/// </summary>
public class ProviderAgreementTests
{
    /// <summary>How close is close enough. The measured figure for a full-precision file is
    /// 1.000000 and for fp16 is 0.999991, so this floor passes both and fails every quantised
    /// export tried. It is deliberately far below the good answers and far above the bad one:
    /// there is nothing in between to argue about.</summary>
    private const double Floor = 0.9999;

    private static bool Present(Model m) => File.Exists(ModelStore.PathOf(m));

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    [Fact]
    public void E5MeansTheSameThingOnTheAcceleratorAndOnTheProcessor()
    {
        if (!Present(ModelStore.E5Base) || !Present(ModelStore.E5Spm)) return;

        // Prose rather than lorem: a quantised model's error is not uniform across inputs, and
        // the worst of these passages was half a point worse than the best.
        string[] passages =
        [
            E5Encoder.Passage("lease agreement.pdf",
                "The tenant shall pay the rent monthly in advance on the first day of each month."),
            E5Encoder.Passage("quarterly report.docx",
                "Revenue grew by eleven percent against the same quarter last year, driven by renewals."),
            E5Encoder.Passage("holiday photos.txt",
                "We walked up the hill behind the village and watched the sun go down over the sea."),
            E5Encoder.Passage("meeting notes.md",
                "Agreed to postpone the migration until the second half, and to write the plan first."),
        ];

        // One session at a time. This model is a gigabyte, and holding two open while the rest of
        // the suite renders bitmaps in parallel is a memory spike nobody needs - encode, dispose,
        // then open the other.
        string onProcessor, onAccelerator;
        float[][] a, b;
        using (var e = new E5Encoder(wantAccelerator: false)) { onProcessor = e.Provider; a = e.EncodePassages(passages); }
        using (var e = new E5Encoder(wantAccelerator: true)) { onAccelerator = e.Provider; b = e.EncodePassages(passages); }

        // A machine with no usable accelerator opens the processor twice, which is not a
        // comparison. Say nothing rather than passing on nothing.
        if (onAccelerator == onProcessor) return;

        for (int i = 0; i < passages.Length; i++)
        {
            double cos = Cosine(a[i], b[i]);
            Assert.True(cos >= Floor,
                $"{onProcessor} and {onAccelerator} disagree about passage {i}: " +
                $"cosine {cos:F6}, which is below {Floor}. A quantised export does this. " +
                $"Documents are embedded on the accelerator and queries on the processor, so the " +
                $"two must produce the same vector for the same text.");
        }
    }
}
