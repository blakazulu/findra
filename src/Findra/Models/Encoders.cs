using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SkiaSharp;

namespace Findra;

/// <summary>
/// The ONNX session factory.
///
/// <para>The accelerator decision is not taken here. Every session is opened through
/// <see cref="Providers.First"/>, so the chain, the choice and every rejection are one shared
/// record rather than a per-encoder try/catch that logs a warning and forgets - and
/// <c>--searchmodels</c> can print one answer about what this machine chose and why.</para>
/// </summary>
public static class Onnx
{
    /// <summary>Open a session on the first provider that will have it. The chain, the choice and
    /// every rejection come back on the result, because --searchmodels prints all three: "it is
    /// slow on my laptop" is unanswerable, and "DirectML did not initialise, so this is the CPU"
    /// is a bug report (spec §6).</summary>
    public static Chosen<InferenceSession> Open(string path, bool wantAccelerator)
    {
        var chain = new List<(string, Func<InferenceSession>)>();
        if (wantAccelerator)
            chain.Add(("DirectML", () =>
            {
                var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                o.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
                // DirectML needs sequential execution and no memory-pattern planning.
                o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                o.EnableMemoryPattern = false;
                o.AppendExecutionProvider_DML(0);
                return new InferenceSession(path, o);
            }));
        chain.Add(("CPU", () =>
        {
            var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            o.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
            // Half the cores: this runs beside a queue that is already using the machine, and
            // it must not be the reason somebody's laptop is warm.
            o.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            return new InferenceSession(path, o);
        }));
        return Providers.First(chain);
    }

    public static string Describe(InferenceSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return "in [" + string.Join(", ", s.InputMetadata.Select(kv => $"{kv.Key}:{string.Join("x", kv.Value.Dimensions)}")) + "] out [" +
               string.Join(", ", s.OutputMetadata.Select(kv => $"{kv.Key}:{string.Join("x", kv.Value.Dimensions)}")) + "]";
    }

    /// <summary>Mean-pool a [1,T,H] hidden state over the attention mask. Padding is masked off
    /// rather than averaged in: a pool that averages it anyway drags every short passage towards
    /// whatever the padding embedding happens to be, and nothing about the result looks wrong.
    /// Nothing attended to at all is a zero vector, not a divide by zero.</summary>
    public static float[] MeanPool(Tensor<float> hidden, long[] mask, int hiddenSize)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(mask);
        int T = mask.Length;
        var pooled = new float[hiddenSize];
        int n = 0;
        for (int t = 0; t < T; t++)
        {
            if (mask[t] == 0) continue;
            n++;
            for (int h = 0; h < hiddenSize; h++) pooled[h] += hidden[0, t, h];
        }
        if (n > 0) for (int h = 0; h < hiddenSize; h++) pooled[h] /= n;
        return pooled;
    }

    public static Tensor<float> Hidden(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        // the export names differ (last_hidden_state / token_embeddings / hidden_states); the one
        // with three dims is the sequence output
        foreach (DisposableNamedOnnxValue r in results)
            if (r.Value is Tensor<float> t && t.Dimensions.Length == 3) return t;
        throw new InvalidOperationException("no [1,T,H] output in the model");
    }
}

/// <summary>SigLIP-2's image side: a picture in, a unit vector in the shared space out. The
/// preprocessing is SigLIP's own: a plain resize to 256x256 (no centre crop - the model was
/// trained on squashed images and cropping throws away the edges), mean and std 0.5.
///
/// <para>This is the one encoder that asks for the accelerator, because this is where the batches
/// are: a first content index runs the vision tower over every picture on the disk.</para>
/// </summary>
public sealed class ClipImageEncoder : IDisposable
{
    public const int Size = 256;

    private readonly InferenceSession _s;
    private readonly string _in, _out;
    public string Provider { get; }
    public IReadOnlyList<ProviderTry> Tried { get; }

    public ClipImageEncoder(bool wantAccelerator, string? dir = null)
    {
        Chosen<InferenceSession> open = Onnx.Open(ModelStore.PathOf(ModelStore.Siglip2Vision, dir), wantAccelerator);
        _s = open.Value;
        Provider = open.Provider;
        Tried = open.Tried;
        _in = _s.InputMetadata.Keys.First();
        _out = PickPooled(_s);
        Log.Info("models", $"siglip2 vision: {Provider}, {Onnx.Describe(_s)} (using '{_out}')");
    }

    // the export carries the sequence output too; the embedding is the 2-D one
    internal static string PickPooled(InferenceSession s)
        => s.OutputMetadata.FirstOrDefault(kv => kv.Value.Dimensions.Length == 2).Key ?? s.OutputMetadata.Keys.First();

    public void Dispose() => _s.Dispose();

    public static float[] Preprocess(SKBitmap src)
    {
        ArgumentNullException.ThrowIfNull(src);
        using SKBitmap resized = src.Resize(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKCubicResampler.CatmullRom));
        if (resized is null) throw new InvalidOperationException("resize failed");
        var px = new float[3 * Size * Size];
        ReadOnlySpan<byte> span = resized.GetPixelSpan();
        int stride = resized.RowBytes;
        for (int y = 0; y < Size; y++)
        {
            int row = y * stride;
            for (int x = 0; x < Size; x++)
            {
                int o = row + x * 4;
                px[(0 * Size * Size) + (y * Size) + x] = (span[o] / 127.5f) - 1f;
                px[(1 * Size * Size) + (y * Size) + x] = (span[o + 1] / 127.5f) - 1f;
                px[(2 * Size * Size) + (y * Size) + x] = (span[o + 2] / 127.5f) - 1f;
            }
        }
        return px;
    }

    /// <summary>Encode a batch of preprocessed images; each result is L2-normalised.</summary>
    public float[][] Encode(IReadOnlyList<float[]> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        int n = batch.Count;
        var input = new DenseTensor<float>(new[] { n, 3, Size, Size });
        int per = 3 * Size * Size;
        for (int i = 0; i < n; i++) batch[i].AsSpan().CopyTo(input.Buffer.Span.Slice(i * per, per));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _s.Run(new[] { NamedOnnxValue.CreateFromTensor(_in, input) });
        Tensor<float> emb = results.First(r => r.Name == _out).AsTensor<float>();
        int dim = emb.Dimensions[1];
        var outv = new float[n][];
        for (int i = 0; i < n; i++)
        {
            var v = new float[VectorStore.Dim];
            for (int d = 0; d < Math.Min(dim, VectorStore.Dim); d++) v[d] = emb[i, d];
            VectorStore.Normalise(v);
            outv[i] = v;
        }
        return outv;
    }
}

/// <summary>SigLIP-2's text side: a sentence in any of its languages (Hebrew included) to the same
/// space the images live in. Gemma's SentencePiece vocabulary, lower-cased, padded to the fixed
/// 64 tokens the model was trained on.
///
/// <para>Query side, so it defaults to the processor: one short string takes about ten
/// milliseconds either way, and standing an accelerator up costs more memory and more startup than
/// it saves over a whole session's keystrokes.</para>
/// </summary>
public sealed class ClipTextEncoder : IDisposable
{
    private const int MaxTokens = 64;
    private readonly InferenceSession _s;
    private readonly SentencePieceTokenizer _tok;
    private readonly string _out;
    private readonly bool _wantsMask;
    public string Provider { get; }
    public IReadOnlyList<ProviderTry> Tried { get; }

    public ClipTextEncoder(bool wantAccelerator = false, string? dir = null)
    {
        Chosen<InferenceSession> open = Onnx.Open(ModelStore.PathOf(ModelStore.Siglip2Text, dir), wantAccelerator);
        _s = open.Value;
        Provider = open.Provider;
        Tried = open.Tried;
        using (FileStream spm = File.OpenRead(ModelStore.PathOf(ModelStore.Siglip2Spm, dir)))
            _tok = SentencePieceTokenizer.Create(spm, addBeginningOfSentence: false, addEndOfSentence: false);
        _out = ClipImageEncoder.PickPooled(_s);
        _wantsMask = _s.InputMetadata.ContainsKey("attention_mask");
        Log.Info("models", $"siglip2 text: {Provider}, {Onnx.Describe(_s)} (using '{_out}')");
    }

    public void Dispose() => _s.Dispose();

    public float[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // SigLIP canonicalises to lower case; the tokenizer's ids ARE the model's (no offset)
        IReadOnlyList<int> raw = _tok.EncodeToIds(text.ToLowerInvariant(), addBeginningOfSentence: false, addEndOfSentence: false);
        var input = new DenseTensor<long>(new[] { 1, MaxTokens });
        var mask = new DenseTensor<long>(new[] { 1, MaxTokens });
        int n = Math.Min(raw.Count, MaxTokens - 1);
        for (int i = 0; i < n; i++) { input[0, i] = raw[i]; }
        input[0, n] = 1;   // <eos>
        // the model was trained attending over its padding, so the mask - when the export asks for
        // one at all - is all ones
        for (int i = 0; i < MaxTokens; i++) mask[0, i] = 1;
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", input) };
        if (_wantsMask) inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", mask));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _s.Run(inputs);
        Tensor<float> emb = results.First(r => r.Name == _out).AsTensor<float>();
        var v = new float[VectorStore.Dim];
        for (int d = 0; d < Math.Min(emb.Dimensions[1], VectorStore.Dim); d++) v[d] = emb[0, d];
        VectorStore.Normalise(v);
        return v;
    }
}

/// <summary>multilingual-e5-base: text to the store's 768-d space. Queries carry the "query: "
/// prefix and passages "passage: ", which is how the model was trained.
///
/// <para>The weights are the same file wherever this runs, so the index and the queries share one
/// embedding space whichever provider answered. The default is the processor, which is what the
/// query side wants; the indexer asks for the accelerator, because a first index embeds hundreds
/// of thousands of chunks and that is the one place the difference is hours rather than
/// milliseconds.</para>
/// </summary>
public sealed class E5Encoder : IDisposable
{
    public const int Hidden = 768;
    private readonly InferenceSession _s;
    private readonly SentencePieceTokenizer _tok;
    private readonly bool _wantsTypeIds;
    public string Provider { get; }
    public IReadOnlyList<ProviderTry> Tried { get; }

    public E5Encoder(bool wantAccelerator = false, string? dir = null)
    {
        Chosen<InferenceSession> open = Onnx.Open(ModelStore.PathOf(ModelStore.E5Base, dir), wantAccelerator);
        _s = open.Value;
        Provider = open.Provider;
        Tried = open.Tried;
        using (FileStream spm = File.OpenRead(ModelStore.PathOf(ModelStore.E5Spm, dir)))
            _tok = SentencePieceTokenizer.Create(spm, addBeginningOfSentence: false, addEndOfSentence: false);
        _wantsTypeIds = _s.InputMetadata.ContainsKey("token_type_ids");
        Log.Info("models", $"e5: {Provider}, {Onnx.Describe(_s)}");
    }

    public void Dispose() => _s.Dispose();

    /// <summary>SentencePiece's ids as the model numbers them: shifted by one to leave room for
    /// &lt;s&gt;=0, &lt;pad&gt;=1 and &lt;/s&gt;=2, with SentencePiece's own &lt;unk&gt; (0) landing
    /// on 3. Opened with &lt;s&gt; and always closed with &lt;/s&gt;, even when the text was cut.
    /// The tokenizer knows nothing about any of this; this does.</summary>
    public static long[] ShiftIds(IReadOnlyList<int> sentencePieceIds, int max)
    {
        ArgumentNullException.ThrowIfNull(sentencePieceIds);
        var ids = new List<long>(Math.Min(sentencePieceIds.Count + 2, max)) { 0 };
        foreach (int id in sentencePieceIds)
        {
            if (ids.Count >= max - 1) break;
            ids.Add(id == 0 ? 3 : id + 1);
        }
        ids.Add(2);
        return [.. ids];
    }

    /// <summary>What a chunk is embedded AS: the file's own name in front of its words, so that
    /// "the lease agreement" reaches a contract whose name says so in Hebrew from a chunk that
    /// never says the word lease. Public and shared, because a passage stored under one wording
    /// and queried under another is a match nobody can see failing.</summary>
    public static string Passage(string path, string text)
        => System.IO.Path.GetFileNameWithoutExtension(path).Replace('-', ' ').Replace('_', ' ') + " - " + text;

    private long[] Ids(string text, int max)
        => ShiftIds(_tok.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false), max);

    public float[] EncodeQuery(string text) => Encode("query: " + text);
    public float[] EncodePassage(string text) => Encode("passage: " + text);

    /// <summary>Many passages in one run, padded to the longest and masked - on an accelerator this
    /// is several times faster than one at a time, and a document is many chunks.</summary>
    public float[][] EncodePassages(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        int n = texts.Count;
        var ids = new long[n][];
        int T = 0;
        for (int i = 0; i < n; i++) { ids[i] = Ids("passage: " + texts[i], 512); T = Math.Max(T, ids[i].Length); }
        var input = new DenseTensor<long>(new[] { n, T });
        var mask = new DenseTensor<long>(new[] { n, T });
        for (int i = 0; i < n; i++)
            for (int t = 0; t < T; t++)
            {
                bool real = t < ids[i].Length;
                input[i, t] = real ? ids[i][t] : 1;   // <pad>
                mask[i, t] = real ? 1 : 0;
            }
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", input),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_wantsTypeIds) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new[] { n, T })));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _s.Run(inputs);
        Tensor<float> hidden = Onnx.Hidden(results);
        int H = hidden.Dimensions[2];
        var outv = new float[n][];
        for (int i = 0; i < n; i++)
        {
            var pooled = new float[H];
            int c = 0;
            for (int t = 0; t < ids[i].Length; t++) { c++; for (int h = 0; h < H; h++) pooled[h] += hidden[i, t, h]; }
            if (c > 0) for (int h = 0; h < H; h++) pooled[h] /= c;
            var v = new float[VectorStore.Dim];
            Array.Copy(pooled, v, Math.Min(H, VectorStore.Dim));
            VectorStore.Normalise(v);
            outv[i] = v;
        }
        return outv;
    }

    private float[] Encode(string text)
    {
        long[] ids = Ids(text, 512);
        int T = ids.Length;
        var input = new DenseTensor<long>(new[] { 1, T });
        var mask = new DenseTensor<long>(new[] { 1, T });
        var maskArr = new long[T];
        for (int i = 0; i < T; i++) { input[0, i] = ids[i]; mask[0, i] = 1; maskArr[i] = 1; }
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", input),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_wantsTypeIds) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new[] { 1, T })));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _s.Run(inputs);
        Tensor<float> hidden = Onnx.Hidden(results);
        float[] pooled = Onnx.MeanPool(hidden, maskArr, hidden.Dimensions[2]);
        var v = new float[VectorStore.Dim];
        Array.Copy(pooled, v, Math.Min(pooled.Length, VectorStore.Dim));
        VectorStore.Normalise(v);
        return v;
    }
}
