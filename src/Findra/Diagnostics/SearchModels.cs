using System.Globalization;
using System.Text;
using SkiaSharp;
using Whisper.net;

namespace Findra.Diagnostics;

/// <summary>One model file, as reported: what the table declares, what is really on disk, and
/// whether it is there at all. <see cref="Actual"/> is 0 for a file that is not present, not a
/// sentinel - <see cref="Present"/> is what a reader checks.</summary>
public readonly record struct ModelRow(string File, string Purpose, bool Present, long Declared, long Actual);

/// <summary>One capability, as reported: whether its whole closed model set is on disk, how many
/// of that set are present out of how many, and what turning it on would cost given what is
/// already installed (<see cref="Capabilities.MarginalBytes"/>).</summary>
public readonly record struct CapabilityRow(Capability Capability, bool Installed, int Have, int Needs, long MarginalBytes);

/// <summary>
/// A frozen picture of what this machine's models say, at the moment it was read. Nothing in
/// <see cref="ModelsReport.Render"/> touches a file or a model, which is what makes the eleven
/// behaviours in the report testable with no model on disk at all - the ordinary state before a
/// first run (spec §6).
/// </summary>
public sealed record ModelsSnapshot(
    string Dir,
    IReadOnlyList<ModelRow> Models,
    IReadOnlyList<CapabilityRow> Capabilities,
    IReadOnlyList<ProviderTry> Onnx,
    IReadOnlyList<ProviderTry> Whisper,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// What the encoders answered while they were open, one line each, or empty when no probe ran.
    ///
    /// <para>It is carried here rather than written straight to the console because the console is
    /// read top to bottom: the probe runs while the models are loaded, which is before the report
    /// can be rendered, so writing as it went put a block of vector norms above the title that
    /// says what the block is.</para>
    /// </summary>
    public IReadOnlyList<string> Probe { get; init; } = [];
}

/// <summary>
/// `--searchmodels`'s formatter: which files are on disk against what the table declares, what
/// each capability would cost to turn on, and which execution provider each runtime chose and
/// every one it rejected, with the reason (spec §6). <see cref="Render"/> is a pure function of a
/// snapshot, fixed-width in the style of `--searchindex`, every number through
/// <see cref="CultureInfo.InvariantCulture"/> - this text is pasted into bug reports from
/// machines set to any locale.
/// </summary>
public static class ModelsReport
{
    /// <summary>The two markers a provider line carries. Constants rather than literals inside
    /// the formatter, because a test counts them - a test that re-types the marker only tests its
    /// own copy of it.</summary>
    public const string Chosen = " : chosen";
    public const string Rejected = " : rejected - ";

    public static string Render(ModelsSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');
        string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
        string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

        Line("findra --searchmodels");
        Line();
        Line($"  models   : {s.Dir}");
        Line();

        // Every declared file, including the ones not on disk - "why are no photos indexed" is
        // unanswerable if the absent rows are filtered out (spec §6).
        Line("  files    :");
        foreach (ModelRow m in s.Models)
        {
            if (!m.Present)
                Line($"    --  {m.File,-24} not on disk, {Mb(m.Declared)} declared             {m.Purpose}");
            else if (!ModelStore.SizeMatchesDeclared(m.Declared, m.Actual))
                // A present file at the wrong size reads as installed unless the two numbers are
                // compared and printed together - a truncated download must never look finished.
                // Compared to within ModelStore.SizeSlack and never for equality: the declared
                // figure is the table's rounded megabytes, so a correctly downloaded file misses
                // it by tens of kilobytes, and an exact test flags the healthy state as broken in
                // the one mode the README's real sizes are supposed to come from.
                Line($"    ??  {m.File,-24} WRONG SIZE: {N(m.Actual)} bytes on disk, expected {Mb(m.Declared)} ({N(m.Declared)} bytes)   {m.Purpose}");
            else
                Line($"    ok  {m.File,-24} {Mb(m.Actual)} on disk (matches the {Mb(m.Declared)} declared)   {m.Purpose}");
        }
        Line();

        // Named beside the paid rows so a machine with nothing installed reads as a machine with
        // no MODELS, not a machine with no search (spec §6). "free" here means free of charge, not
        // free of consent - both still wait for content indexing to be turned on.
        Line("  free (no model, no download - once content indexing is turned on):");
        Line("    words in documents");
        Line("    words inside pictures (OCR)");
        Line();

        Line("  capabilities:");
        foreach (CapabilityRow c in s.Capabilities)
        {
            string title = Capabilities.Title(c.Capability);
            if (c.Installed)
                Line($"    {title} : ready");
            else
                Line($"    {title} : off - have {c.Have} of {c.Needs} file(s), {Sizes.Human(c.MarginalBytes)} to turn on");
        }
        Line($"    everything installed would be {Sizes.Human(ModelStore.TotalBytes(ModelStore.All))}");
        Line();

        // One row per ProviderTry that actually happened, never one per declared chain entry - a
        // chain that stopped at its first rung (Vulkan chosen, CPU never tried) must produce one
        // row, not two.
        Line("  onnx execution provider (SigLIP-2, e5):");
        if (s.Onnx.Count == 0) Line("    not tried - no model is on disk to open");
        else foreach (ProviderTry t in s.Onnx) Line($"    {t.Name}{(t.Chosen ? Chosen : Rejected + t.Reason)}");
        Line();

        Line("  whisper execution provider (speech):");
        if (s.Whisper.Count == 0) Line("    not tried - no model is on disk to open");
        else foreach (ProviderTry t in s.Whisper) Line($"    {t.Name}{(t.Chosen ? Chosen : Rejected + t.Reason)}");

        // After the report, under a heading of its own. A run with nothing on disk probed
        // nothing, and a heading over no lines would read as a probe that found nothing rather
        // than one that never ran.
        if (s.Probe.Count > 0)
        {
            Line();
            Line("  probe:");
            foreach (string line in s.Probe) Line($"    {line}");
        }

        if (s.Notes.Count > 0)
        {
            Line();
            Line("  notes:");
            foreach (string note in s.Notes) Line($"    {note}");
        }

        return sb.ToString();
    }
}

// `findra --searchmodels`: are the models present, do they load, does what loaded agree with
// itself (spec §6, §9). It never needs elevation - every file it reads is the interface's own,
// under %LOCALAPPDATA%\Findra\models. With no model on disk at all - the state of every machine
// before first run - it still prints a complete report and exits 0: a missing model is a normal
// state, not an error, and a non-zero exit here would make a script treat the ordinary case as a
// failure. It exits 2 only when a model that IS present would not load, which is a broken file
// and a real fault.
public static class SearchModels
{
    public static int Run(string[] args)
    {
        string dir = ModelStore.Dir;

        var modelRows = new List<ModelRow>();
        foreach (Model m in ModelStore.All)
            modelRows.Add(new ModelRow(m.File, m.Purpose, ModelStore.Present(m, dir), m.Bytes, ModelStore.ActualBytes(m, dir)));

        CapabilitySet installed = CapabilitySet.Installed(dir);
        var capRows = new List<CapabilityRow>();
        foreach (Capability c in Capabilities.All)
        {
            IReadOnlyList<Model> need = Capabilities.ModelsFor([c]);
            int have = need.Count(m => ModelStore.Present(m, dir));
            long marginal = Capabilities.MarginalBytes(c, installed);
            capRows.Add(new CapabilityRow(c, installed.Has(c), have, need.Count, marginal));
        }

        var notes = new List<string>();
        IReadOnlyList<string> probe = [];
        IReadOnlyList<ProviderTry> onnxTried = [];
        IReadOnlyList<ProviderTry> whisperTried = [];
        bool aPresentModelFailedToLoad = false;

        // Providers.First needs an initialiser: with nothing on disk there is nothing to build,
        // so a machine that took the "just names" preset gets an honest empty chain and a note,
        // never a fabricated one (spec §6).
        bool anyPresent = ModelStore.All.Any(m => ModelStore.Present(m, dir));
        if (!anyPresent)
        {
            notes.Add("no execution provider was tried: no model is on disk yet to open");
        }
        else
        {
            bool visionReady = ModelStore.Present(ModelStore.Siglip2Vision, dir);
            bool textReady = ModelStore.Present(ModelStore.Siglip2Text, dir) && ModelStore.Present(ModelStore.Siglip2Spm, dir);
            bool e5Ready = ModelStore.Present(ModelStore.E5Base, dir) && ModelStore.Present(ModelStore.E5Spm, dir);
            bool whisperReady = ModelStore.Present(ModelStore.WhisperTurbo, dir);

            ClipImageEncoder? vision = null;
            ClipTextEncoder? clipText = null;
            E5Encoder? e5 = null;
            try
            {
                if (visionReady) { vision = new ClipImageEncoder(wantAccelerator: true, dir); onnxTried = vision.Tried; }
                if (textReady) { clipText = new ClipTextEncoder(wantAccelerator: false, dir); if (onnxTried.Count == 0) onnxTried = clipText.Tried; }
                if (e5Ready) { e5 = new E5Encoder(wantAccelerator: false, dir); if (onnxTried.Count == 0) onnxTried = e5.Tried; }

                // Splits "the models are missing" from "DirectML is not being used" from "the
                // tokenizer is producing garbage" from "the scores are simply low", before any
                // of the rest of the pipeline is suspected.
                if (clipText is not null || e5 is not null) probe = Probe(clipText, e5, vision);
            }
            catch (Exception ex)
            {
                notes.Add($"a present onnx model is on disk but would not load: {ex.GetType().Name}: {ex.Message}");
                aPresentModelFailedToLoad = true;
            }
            finally { vision?.Dispose(); clipText?.Dispose(); e5?.Dispose(); }

            if (whisperReady)
            {
                try
                {
                    Chosen<WhisperFactory> w = Media.OpenWhisper(ModelStore.PathOf(ModelStore.WhisperTurbo, dir));
                    whisperTried = w.Tried;
                    w.Value.Dispose();
                }
                catch (Exception ex)
                {
                    notes.Add($"{ModelStore.WhisperTurbo.File} is on disk but would not load: {ex.GetType().Name}: {ex.Message}");
                    aPresentModelFailedToLoad = true;
                }
            }
        }

        var snapshot = new ModelsSnapshot(dir, modelRows, capRows, onnxTried, whisperTried, notes)
        {
            Probe = probe,
        };
        Console.WriteLine(ModelsReport.Render(snapshot));

        return aPresentModelFailedToLoad ? 2 : 0;
    }

    /// <summary>Encode a few sentences and one picture and REPORT the similarities, so a nothing-
    /// found result can be split into "the models are missing", "the accelerator is not being
    /// used", "the tokenizer is producing garbage" and "the scores are simply low" before the rest
    /// of the pipeline is suspected.
    ///
    /// <para>It hands its lines back rather than writing them, and that is the whole ordering: it
    /// runs while the encoders are open, which is before the report exists, so anything printed
    /// here would land above the title that explains it.</para></summary>
    private static IReadOnlyList<string> Probe(ClipTextEncoder? clipText, E5Encoder? e5, ClipImageEncoder? vision)
    {
        string[] texts =
        [
            "a sunset over the sea", "a cat sitting on a sofa",
            "שקיעה מעל הים", "a spreadsheet of quarterly revenue",
        ];
        var lines = new List<string>();
        var clipVecs = new List<float[]>();
        foreach (string t in texts)
        {
            string line = $"'{t}'";
            if (clipText is not null) { float[] v = clipText.Encode(t); clipVecs.Add(v); line += $"  clip |v|={Norm(v):0.000}"; }
            if (e5 is not null) line += $"  e5 |v|={Norm(e5.EncodeQuery(t)):0.000}";
            lines.Add(line);
        }

        if (vision is null) return lines;
        string? image = FindAnyPhoto();
        if (image is null) { lines.Add("no image found under Pictures to probe against"); return lines; }
        using SKBitmap? bmp = SKBitmap.Decode(image);
        if (bmp is null) { lines.Add($"could not decode {image}"); return lines; }

        float[] iv = vision.Encode([ClipImageEncoder.Preprocess(bmp)])[0];
        lines.Add($"image {Path.GetFileName(image)} ({bmp.Width}x{bmp.Height}) via {vision.Provider}");
        if (clipVecs.Count == texts.Length)
        {
            lines.Add("image-text similarity:");
            for (int i = 0; i < texts.Length; i++)
                lines.Add($"  {Dot(iv, clipVecs[i]):0.000}  {texts[i]}");
        }
        return lines;
    }

    private static string? FindAnyPhoto()
    {
        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories))
                    if (new FileInfo(f).Length > 50_000) return f;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static float Dot(float[] a, float[] b) { float s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }
    private static float Norm(float[] a) => MathF.Sqrt(Dot(a, a));
}
