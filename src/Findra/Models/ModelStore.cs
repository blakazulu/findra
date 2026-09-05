using System;
using System.Collections.Generic;
using System.IO;

namespace Findra;

/// <summary>
/// One model file: where it comes from, how big it really is, and the size below which a file on
/// disk is not it.
///
/// <para><see cref="Bytes"/> is the measured size of the real file (spec §6). It is what a person
/// is asked to consent to downloading, what the first-run screen adds up, and what the README
/// quotes. <see cref="MinBytes"/> is a deliberately generous completeness floor: a publisher who
/// re-exports a model a few kilobytes smaller must not cost every user a 1.5 GB re-download, and
/// a truncated file must never read as installed. They are different numbers with different jobs
/// and neither may be used for the other's.</para>
///
/// <para><see cref="ModelStore.SizeSlack"/> is the third, and it is the one that says how far a
/// real file may sit from <see cref="Bytes"/> and still be that file. See its own note.</para>
/// </summary>
public sealed record Model(string File, string Url, long MinBytes, long Bytes, string Purpose);

/// <summary>
/// Where the models live and which of them are actually there. Nothing here touches the network -
/// the download is the interface's own concern, run from a later task, and the indexer child asks
/// this type only whether a file exists.
/// </summary>
public static class ModelStore
{
    /// <summary>Measured sizes come from the spec's table in MB, and MB there means 1024 KB.
    /// Declaring them this way keeps the arithmetic in one place and makes a drift between the
    /// table and the code a one-line change rather than seven.</summary>
    private static long Mib(double mb) => (long)(mb * 1024 * 1024);

    public static string Dir => Paths.Models;

    public static readonly Model Siglip2Vision = new("siglip2-vision.onnx",
        "https://huggingface.co/onnx-community/siglip2-base-patch16-256-ONNX/resolve/main/onnx/vision_model.onnx",
        350_000_000, Mib(354.8), "photos and video frames");

    public static readonly Model Siglip2Text = new("siglip2-text-q.onnx",
        "https://huggingface.co/onnx-community/siglip2-base-patch16-256-ONNX/resolve/main/onnx/text_model_quantized.onnx",
        250_000_000, Mib(270.3), "what you type, when you are looking for a picture");

    public static readonly Model Siglip2Spm = new("siglip2.spm",
        "https://huggingface.co/onnx-community/siglip2-base-patch16-256-ONNX/resolve/main/tokenizer.model",
        3_000_000, Mib(4.0), "its vocabulary");

    /// <summary>
    /// SigLIP-2's own calibration, for the checkpoint above and no other.
    ///
    /// <para>SigLIP's score is not a cosine. It is
    /// <c>sigmoid(exp(logit_scale) * cos + logit_bias)</c>, and those two scalars are LEARNED
    /// parameters of the trained model. They live on the combined model rather than on either
    /// tower, so the moment the export is split into a vision file and a text file - which Findra
    /// needs, because one runs over the library and the other runs once per query - the
    /// calibration is not in the files any more, and nothing anywhere warns you. That is a trap
    /// the export format sets rather than an oversight anybody would catch in review.</para>
    ///
    /// <para><b>Recorded beside the file it belongs to, and never hardcoded blind</b>, for the
    /// reason <see cref="SizeSlack"/> exists: these are properties of one checkpoint. SigLIP v1
    /// base carries a bias of -12.93 against v2's -16.77, and that gap alone is a forty-six-fold
    /// difference in probability at the same cosine. A checkpoint swapped without moving these
    /// would silently move every threshold in the product and nothing would fail loudly.</para>
    ///
    /// <para>Read from <c>google/siglip2-base-patch16-256</c>'s own <c>model.safetensors</c> by a
    /// ranged byte read of the header, not quoted from a write-up.</para>
    /// </summary>
    public const float Siglip2Scale = 112.90f, Siglip2Bias = -16.771803f;

    /// <summary>What SigLIP-2 itself would call this cosine: a probability, not a rank. Nothing
    /// in the search path uses it, because the sigmoid is MONOTONE in the cosine and so cannot
    /// change any ordering - it is here so that a threshold can be argued about in the units the
    /// model was trained in, where 0.05 and 0.09 stop looking like neighbours.</summary>
    public static double Siglip2Probability(double cosine)
        => 1.0 / (1.0 + Math.Exp(-(Siglip2Scale * cosine + Siglip2Bias)));

    /// <summary>
    /// Full precision, and the size is the point rather than a regret.
    ///
    /// <para>This was <c>model_quantized.onnx</c>, 265.7 MiB. A quantised model does not mean the
    /// same thing on the two execution providers: measured against DirectML on one desktop it came
    /// back at 0.970 cosine, with elements 0.8 apart, where the processor against itself is exactly
    /// 1 and no graph optimisation setting closes the gap. Findra embeds documents on the
    /// accelerator and embeds the query on the processor, and compares the two, so a model that
    /// answers differently depending on which silicon ran it is a systematic error injected into
    /// every score - and one that moves when somebody's driver changes.</para>
    ///
    /// <para>fp32 agrees to 1.000000. It is also FASTER than the fp16 export on the processor -
    /// 10.9 ms against 27.7 for one query - because processors do fp32 natively and emulate fp16,
    /// and search runs on the processor. <c>ProviderAgreementTests</c> holds this to that.</para>
    /// </summary>
    public static readonly Model E5Base = new("e5-base.onnx",
        "https://huggingface.co/Xenova/multilingual-e5-base/resolve/main/onnx/model.onnx",
        900_000_000, Mib(1058.6), "the meaning of documents, and of transcripts");

    public static readonly Model E5Spm = new("e5-small.spm",
        "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/sentencepiece.bpe.model",
        4_000_000, Mib(4.8), "their vocabulary");

    public static readonly Model WhisperTurbo = new("whisper-turbo-q5.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
        500_000_000, Mib(547.4), "speech, in every language");

    public static readonly Model WhisperHebrew = new("whisper-ivrit.bin",
        "https://huggingface.co/ivrit-ai/whisper-large-v3-turbo-ggml/resolve/main/ggml-model.bin",
        1_500_000_000, Mib(1549.3), "speech, in Hebrew");

    public static readonly IReadOnlyList<Model> All =
        [Siglip2Vision, Siglip2Text, Siglip2Spm, E5Base, E5Spm, WhisperTurbo, WhisperHebrew];

    /// <summary>
    /// How far a real file may sit from its <see cref="Model.Bytes"/> and still be that file.
    ///
    /// <para>The declared size is the specification's table, quoted in megabytes to one decimal
    /// place, so it was never going to equal a byte count and it is not wrong that it does not:
    /// on a real install five of the seven files differ from it, four of them upward, by up to
    /// forty-seven kilobytes. Code that compares the two exactly calls a correct download the
    /// wrong size; code with no upper bound at all promotes a stale part under a finished file's
    /// name. This is the width between those two mistakes - one part in fifty, which is wider
    /// than the rounding of every file in the table and far narrower than any truncation.</para>
    ///
    /// <para>Proportional rather than a flat number of bytes on purpose: it has to be a sensible
    /// answer for a four-megabyte vocabulary and for a 1.5 GB fine-tune, and a constant that
    /// suits one of them is nonsense for the other.</para>
    /// </summary>
    public static long SizeSlack(long declared) => declared / 50;

    /// <summary>Is a file this long the size the table declares, to within the rounding of the
    /// table itself? What <c>--searchmodels</c> asks before it says "matches the declared", and
    /// the only comparison anywhere that may call a file the wrong size.</summary>
    public static bool SizeMatchesDeclared(long declared, long actual)
        => Math.Abs(actual - declared) <= SizeSlack(declared);

    /// <summary>
    /// Could a file this long be a complete copy of this model?
    ///
    /// <para>Asked by the downloader of a leftover <c>.part</c> the server has refused to serve a
    /// range from, where the two answers are "this is the finished file, promote it" and "this is
    /// stale, throw it away and start again" - and the second costs up to 1.5 GB. The floor is
    /// <see cref="Model.MinBytes"/>, generous by design; the ceiling is the declared size plus
    /// <see cref="SizeSlack"/>, because a part longer than the file can possibly be is not a
    /// prefix of it.</para>
    /// </summary>
    public static bool CouldBeComplete(Model m, long bytes)
    {
        ArgumentNullException.ThrowIfNull(m);
        return bytes >= m.MinBytes && bytes <= m.Bytes + SizeSlack(m.Bytes);
    }

    public static string PathOf(Model m, string? dir = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        return System.IO.Path.Combine(dir ?? Dir, m.File);
    }

    /// <summary>Is this model on disk and long enough to be itself? Never throws: an absent
    /// directory, an unreadable one and a locked file are all "not present", which is a normal
    /// state on a machine that has taken nothing.
    ///
    /// <para>The floor and nothing above it, deliberately. This is the question "can this be
    /// opened", and a file being longer than the table says answers nothing about that: a
    /// republished model a megabyte larger loads perfectly, and an upper bound here would report
    /// it absent, silently switch off a capability somebody has already paid the download for,
    /// and offer them the same file again. Whether a file is the size the table declares is a
    /// different question, asked by <see cref="SizeMatchesDeclared"/> and answered in a
    /// report.</para></summary>
    public static bool Present(Model m, string? dir = null)
    {
        try
        {
            var fi = new FileInfo(PathOf(m, dir));
            return fi.Exists && fi.Length >= m.MinBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static IReadOnlyList<Model> Missing(IEnumerable<Model> set, string? dir = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        var gone = new List<Model>();
        foreach (Model m in set) if (!Present(m, dir)) gone.Add(m);
        return gone;
    }

    /// <summary>The declared total of a set, de-duplicated by file - a set built from two
    /// capabilities that share the e5 pair must not count it twice.</summary>
    public static long TotalBytes(IEnumerable<Model> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (Model m in set) if (seen.Add(m.File)) total += m.Bytes;
        return total;
    }

    /// <summary>The size on disk of a model that is present, or 0. Printed beside
    /// <see cref="Model.Bytes"/> by <c>--searchmodels</c>, because the README's sizes have to
    /// come from real files rather than from this table (spec §9a).</summary>
    public static long ActualBytes(Model m, string? dir = null)
    {
        try { var fi = new FileInfo(PathOf(m, dir)); return fi.Exists ? fi.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
