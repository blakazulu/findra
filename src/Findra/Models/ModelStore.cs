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

    public static readonly Model E5Base = new("e5-base-q.onnx",
        "https://huggingface.co/Xenova/multilingual-e5-base/resolve/main/onnx/model_quantized.onnx",
        250_000_000, Mib(265.7), "the meaning of documents, and of transcripts");

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
