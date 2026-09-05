using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using SkiaSharp;
using Whisper.net;

namespace Findra;

/// <summary>
/// What came of asking to read inside one file: its segments, and either the reason nothing was
/// read or a note about what was left undone. A skip is a normal outcome and never an error.
///
/// <para><see cref="Skip"/> and <see cref="Note"/> are different facts, and the state follows
/// Skip alone. Skip means nothing usable came back, and the item is
/// <see cref="ContentDb.StateSkipped"/>. Note means something did come back - and something else
/// did not: a long video whose frames were read while its sound track was passed over for length
/// is a genuinely INDEXED file that is also incomplete, and calling it skipped would tell
/// <c>--searchindex</c> and the card that a whole film library had never been read.</para>
///
/// <para>Both land in the same <c>items.error</c> column, because both are "the recorded reason
/// this row is the way it is" and a re-queue reads that column without caring which produced the
/// string. Skip wins if a decoder ever sets both; no arm does.</para>
/// </summary>
public readonly record struct KindResult(List<ContentDb.Segment> Segments, string? Skip, string? Note = null);

/// <summary>
/// What this machine can read inside a file, given what is installed.
///
/// <para><see cref="CanRead"/> is the GATE and it lives here, on the interface, so that the
/// indexer can ask before it opens anything and a test's fake can answer with the same rule the
/// real one uses. A gate buried inside <see cref="Decode"/> is not testable: "the decoder was
/// never asked" stops being an assertion anybody can make, because the fake that would prove it
/// has to reimplement the rule and then the test tests the fake.</para>
///
/// <para>This is an interface for one reason: the per-capability gate has to be provable without
/// a 2.9 GB download.</para>
/// </summary>
public interface IDecoders : IDisposable
{
    /// <summary>What is on disk, as at the last time <see cref="CanRead"/> was asked. It is a
    /// snapshot rather than a constant: a model that arrives while the child is running has to
    /// reach the very files the interface has just queued for it, and the child is started
    /// once.</summary>
    CapabilitySet Installed { get; }

    /// <summary>Is there any point opening this kind of file at all? Asked before
    /// <see cref="Decode"/>, once per queued file, and a false answer is a Skipped row with
    /// <see cref="Decoders.NoModel"/> - never a Failed one.
    ///
    /// <para>It is also where <see cref="Installed"/> is taken, which is why it is asked per file
    /// rather than per child: one snapshot for the whole of one file's decoding, and a fresh one
    /// for the next.</para></summary>
    bool CanRead(ResultKind kind);

    KindResult Decode(ResultKind kind, string path, long bytes);

    /// <summary>Make every vector written so far durable. Called BEFORE the transaction that
    /// references those rows commits, because a database row pointing past the vector header's
    /// count is a segment that silently never matches again.</summary>
    void Flush();

    /// <summary>Release the vector rows a replaced or deleted item was pointing at. Called AFTER
    /// that transaction commits: a tombstone is destructive, and a rollback that has already
    /// zeroed the old rows leaves the surviving segments pointing at nothing.</summary>
    void Release(IReadOnlyList<long> vectorRows);
}

/// <summary>
/// The real decoders. Every one of them reads a file somebody else put on the disk, which is
/// exactly why this whole type runs in the indexer child at normal integrity and never in the
/// elevated helper (spec §3).
///
/// <para>Nothing here downloads anything. A capability whose files are not present is not an
/// error, a warning, or a reason to wait: the kind is skipped with a reason, the row stays
/// exactly where <see cref="ContentDb.RequeueKinds"/> can find it later, and the interface
/// offers the download.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class Decoders : IDecoders
{
    /// <summary>
    /// How much audio can be pulled into memory at once. This is a MEMORY bound and not a policy
    /// one - how long a recording is worth transcribing is <see cref="TranscribeLimit"/>'s, and it
    /// is the user's. Samples are 16 kHz float32, which is 3.66 MB a minute, so "no limit" over a
    /// long archive would be gigabytes of List&lt;float&gt;. A recording longer than this is
    /// transcribed up to here and the log line says so.
    ///
    /// <para>An hour for audio and three minutes for a video's sound track is the shape this
    /// deliberately does NOT have. That is two constants taking a decision that costs hours of
    /// somebody else's machine, and taking it differently for a sound file and for a video of the
    /// same length - an asymmetry nothing in the interface could show. The decision belongs to
    /// <see cref="TranscribeLimit"/>, as one number, and to the person using it.</para>
    /// </summary>
    public const double MaxDecodeSeconds = 4 * 3600;

    public const long MaxVideoBytes = 8L << 30;
    public const long MaxDocBytes = 200L << 20;
    public const long MaxImageBytes = 120L << 20;

    /// <summary>
    /// Below this a file is too small to hold a picture of anything - a 1x1 tracking pixel, a
    /// gradient strip, a spinner frame.
    ///
    /// <para>It was 10 KB, on the reasoning that anything smaller is a UI icon. That is true of a
    /// checkbox and false of a great many real pictures: an 8.6 KB PNG of a pair of headphones is
    /// a picture of headphones, and a machine whose images are mostly application assets had 890
    /// of its 1,086 images thrown away by this line alone. The floor is there to skip the
    /// degenerate, not to judge what counts as a photograph.</para>
    /// </summary>
    public const long MinImageBytes = 2 << 10;

    /// <summary>Recorded against a file whose KIND needs a model this machine has not got. It is
    /// a normal, re-queueable outcome and never a failure: the capability that arrives later
    /// picks up exactly the rows carrying it. Its exact text crosses into the re-queue's
    /// exclusion list and into <c>--searchindex</c>'s models section, so it is one constant
    /// rather than a literal per reader.</summary>
    public const string NoModel = "no decoder for this kind yet";

    /// <summary>A file too big to be worth reading whole. A capability arriving later cannot make
    /// it smaller, which is why a re-queue excludes it.</summary>
    public const string TooLarge = "too large";

    /// <summary>A document with nothing in it. Same reasoning as <see cref="TooLarge"/>.</summary>
    public const string NoText = "no text";

    /// <summary>A format this build has no reader for - the doc/xls/ppt/rtf/odt set. Distinct
    /// from <see cref="NoModel"/>, which means "no MODEL for this KIND", and a later reader
    /// re-queues exactly these rows.</summary>
    public const string NoFormatReader = "no decoder for this format yet";

    /// <summary>Too small to be a picture of anything. Deliberately not "an icon, not a picture",
    /// which was the old wording and the old rule: an icon IS an image, and this reason now means
    /// only what it says.</summary>
    public const string TooSmall = "too small to be a picture";

    /// <summary>Recorded against a recording longer than the transcription limit. It is its OWN
    /// reason, distinct from <see cref="TooLarge"/>, because it is the only one on this list a
    /// user can change from a settings control: raising the limit re-queues exactly the rows
    /// carrying this and nothing else (spec §6). It is also written as a NOTE on an indexed
    /// video whose frames were read and whose sound track was not.</summary>
    public const string TooLong = "longer than the transcription limit";

    private readonly VectorStore _vectors;
    private readonly string? _dir;
    private readonly bool _ownsVectors;
    private readonly Func<int> _transcribeMinutes;
    private readonly Func<CapabilitySet> _installed;
    private ClipImageEncoder? _vision;
    private E5Encoder? _e5;
    private WhisperFactory? _whisper, _whisperHe;

    /// <summary>The Hebrew fine-tune is on disk and will not open. Set once, so a broken file is
    /// one log line rather than one failed decode per recording on the machine.</summary>
    private bool _heIsBroken;
    private bool _dirty;

    public CapabilitySet Installed { get; private set; }

    /// <summary><paramref name="ownsVectors"/> is false by default and that is the safe
    /// direction: a store the caller opened stays the caller's, and a type that guesses closes a
    /// test's store under it. Only <see cref="ForThisMachine"/> passes true, because only it
    /// opened one.
    ///
    /// <para><paramref name="installed"/> and <paramref name="transcribeMinutes"/> are both
    /// delegates rather than values, for one reason: each of them can change while the child is
    /// running, and the child is started once. The limit is a setting the interface writes to
    /// <c>index:transcribeminutes</c>; what is installed is seven files that arrive on disk
    /// whenever somebody accepts a download. Captured, either one means the change does nothing
    /// until the child is restarted, with no message anywhere saying so - and for the models that
    /// is worse than a delay, because the interface queues the files the moment they land and the
    /// child records every one of them unreadable.</para></summary>
    public Decoders(Func<CapabilitySet> installed, VectorStore vectors, Func<int>? transcribeMinutes = null,
                    string? modelDir = null, bool ownsVectors = false)
    {
        ArgumentNullException.ThrowIfNull(installed);
        _installed = installed;
        Installed = installed();
        _vectors = vectors;
        _transcribeMinutes = transcribeMinutes ?? (() => TranscribeLimit.Default);
        _dir = modelDir;
        _ownsVectors = ownsVectors;
    }

    /// <summary>The set this machine actually has, with a writer on the real vector store. Only
    /// the <c>--index</c> child calls this. A diagnostic that calls it takes a writer on a file
    /// the running child already holds, and appends rows to a store its throwaway database will
    /// never reference.</summary>
    public static Decoders ForThisMachine(Func<int> transcribeMinutes, string? modelDir = null)
        => new(() => CapabilitySet.Installed(modelDir), new VectorStore(writer: true), transcribeMinutes,
               modelDir, ownsVectors: true);

    /// <summary>
    /// Look at the disk again, and take the new set if it has moved.
    ///
    /// <para>Seven <c>File.Exists</c> calls, once per queued file, beside opening that file and
    /// the three transactions the queue already costs for it. Rebuilding a session is what would
    /// be expensive, and nothing here does: the encoders are opened lazily and only for a
    /// capability that is installed, so one that ARRIVES needs nothing rebuilt at all - the first
    /// file that needs it opens it.</para>
    ///
    /// <para>One that GOES is the case with a handle in it. Its session is dropped here, between
    /// two files, where nothing is mid-decode and no vector row is half written: the whole of
    /// this type runs on the one flow that drains the queue. The vector store is never touched -
    /// it is the writer this process holds for its whole life, and closing it under a running
    /// drain is exactly the stranded handle this must not create.</para>
    /// </summary>
    private void Refresh()
    {
        CapabilitySet now = _installed();
        bool same = true;
        foreach (Capability c in Capabilities.All) if (now.Has(c) != Installed.Has(c)) { same = false; break; }
        if (same) return;

        Installed = now;
        if (!now.Has(Capability.Photos)) { _vision?.Dispose(); _vision = null; }
        if (!now.Has(Capability.Meaning)) { _e5?.Dispose(); _e5 = null; }
        if (!now.Has(Capability.Speech)) { _whisper?.Dispose(); _whisper = null; }
        if (!now.Has(Capability.Hebrew)) { _whisperHe?.Dispose(); _whisperHe = null; }

        Log.Info("index", "what Findra can read inside a file has changed while the indexer was running: "
                          + (now.Have is null || now.Have.Count == 0
                             ? "nothing beyond words in documents"
                             : string.Join(", ", Capabilities.All.Where(now.Has).Select(Capabilities.Title))));
    }

    /// <summary>
    /// Whether a kind is worth opening at all, given what is installed. Static and pure, so the
    /// indexer, the real decoders and a test's fake all answer the same question the same way.
    ///
    /// <para>Video is the reason this cannot be a reverse lookup from kind to capability: its
    /// frames need the vision tower and its sound track needs whisper, and a video is worth
    /// opening for EITHER. A lookup that returns the first capability covering the kind drops
    /// every video on a speech-only machine, silently.</para>
    /// </summary>
    public static bool Covers(ResultKind kind, CapabilitySet installed) => kind switch
    {
        // Words in documents costs no download and no model, so nothing here gates it. Whether
        // it runs at all is a different question with a different answer - content indexing is
        // off until asked for (spec §6) - and that is the queue's pause, not this. Covers says
        // "can this be read"; the pause says "should anything be read at all".
        ResultKind.Document => true,
        ResultKind.Photo => installed.Has(Capability.Photos),
        ResultKind.Video => installed.Has(Capability.Photos) || installed.Has(Capability.Speech),
        ResultKind.Audio => installed.Has(Capability.Speech),
        _ => false,
    };

    /// <summary>The gate, and the one moment in a file's life where what is installed is looked
    /// up again. Per file rather than per child, so a capability that arrives reaches the backlog
    /// queued for it; and once per file rather than at each mention of <see cref="Installed"/>,
    /// so the arms below cannot see a set change halfway through one video.</summary>
    public bool CanRead(ResultKind kind)
    {
        Refresh();
        return Covers(kind, Installed);
    }

    /// <summary>The size rules, in one place, asked by the arms rather than repeated inside them.
    /// Null means "go ahead"; anything else is the skip reason.</summary>
    public static string? SizeGate(ResultKind kind, long bytes) => kind switch
    {
        ResultKind.Photo when bytes > MaxImageBytes => TooLarge,
        ResultKind.Photo when bytes < MinImageBytes => TooSmall,
        ResultKind.Document when bytes > MaxDocBytes => TooLarge,
        ResultKind.Video when bytes > MaxVideoBytes => TooLarge,
        _ => null,
    };

    /// <summary>Which whisper models a transcription uses. The general model is ALWAYS the first
    /// pass - it is what detects the language - and the fine-tune is only ever the second, over
    /// the files the first one calls Hebrew. There is deliberately no arrangement of capabilities
    /// that returns the fine-tune as <c>General</c>.</summary>
    public static (Model General, Model? Hebrew) SpeechModels(CapabilitySet installed)
        => (ModelStore.WhisperTurbo, installed.Has(Capability.Hebrew) ? ModelStore.WhisperHebrew : null);

    public void Flush()
    {
        // Only when something was actually appended. A model-free build writes no vectors at all,
        // and three FlushFileBuffers per file across a hundred thousand files is three hundred
        // thousand fsyncs on the queue's critical path for nothing.
        if (!_dirty) return;
        _vectors.Flush();
        _dirty = false;
    }

    public void Release(IReadOnlyList<long> vectorRows)
    {
        ArgumentNullException.ThrowIfNull(vectorRows);
        if (vectorRows.Count == 0) return;
        foreach (long row in vectorRows) _vectors.Tombstone(row);
        _dirty = true;
    }

    public void Dispose()
    {
        _vision?.Dispose(); _e5?.Dispose();
        _whisper?.Dispose(); _whisperHe?.Dispose();
        if (_ownsVectors) _vectors.Dispose();
    }

    /// <summary>Read inside one file. Only ever called for a kind <see cref="CanRead"/> said yes
    /// to - the gate is not repeated here, because two gates is two places to change.</summary>
    public KindResult Decode(ResultKind kind, string path, long bytes)
    {
        if (SizeGate(kind, bytes) is { } tooBig) return new KindResult([], tooBig);
        return kind switch
        {
            ResultKind.Document => Document(path),
            ResultKind.Photo => Photo(path),
            ResultKind.Video => Video(path),
            ResultKind.Audio => Audio(path),
            _ => new KindResult([], "not a content kind"),
        };
    }

    // ---- kinds ----

    /// <summary>Words in documents, free of charge. The full-text segments are written whatever is
    /// installed - a document indexed without Meaning is a complete, correct, findable document,
    /// and taking that away from somebody who declined a download would break the product for
    /// them. Its segments simply carry <c>Vec = -1</c>, and Meaning arriving later re-queues the
    /// file with <see cref="Indexer.Recheck"/> so a second pass fills the column in.</summary>
    private KindResult Document(string path)
    {
        if (!DocText.CanExtract(path)) return new KindResult([], NoFormatReader);
        string text = DocText.Extract(path);
        if (text.Length < 40) return new KindResult([], NoText);

        List<string> chunks = DocText.Chunk(text);
        var segs = new List<ContentDb.Segment>(chunks.Count);
        if (!Installed.Has(Capability.Meaning))
        {
            foreach (string chunk in chunks) segs.Add(new ContentDb.Segment(ContentDb.SegText, -1, -1, -1, chunk));
            return new KindResult(segs, null);
        }

        E5Encoder e5 = E5();
        for (int i = 0; i < chunks.Count; i += 16)
        {
            List<string> batch = chunks.GetRange(i, Math.Min(16, chunks.Count - i));
            float[][] vs = e5.EncodePassages(batch.ConvertAll(c => E5Encoder.Passage(path, c)));
            for (int k = 0; k < batch.Count; k++)
                segs.Add(new ContentDb.Segment(ContentDb.SegText, -1, -1, Append(vs[k], ContentDb.SegText), batch[k]));
        }
        return new KindResult(segs, null);
    }

    private KindResult Photo(string path)
    {
        using SKBitmap? bmp = LoadBitmap(path, 384);
        if (bmp is null) return new KindResult([], "undecodable");

        long row = Append(Vision().Encode([ClipImageEncoder.Preprocess(bmp)])[0], ContentDb.SegImage);
        var segs = new List<ContentDb.Segment> { new(ContentDb.SegImage, -1, -1, row, "") };

        // The words INSIDE the picture: most of a real image library is screenshots, and the words
        // are what anybody remembers of a screenshot. Reading them needs no model at all, so it
        // runs whenever a photo is being opened anyway.
        //
        // THE WORDS ONLY, and never a meaning vector. Recognised text is not prose: it is UI
        // chrome, timestamps, phone numbers, half of a menu, and whatever the recogniser made of a
        // language it was not sure about. Embedded with e5 as though it were writing, that soup
        // sits just inside the "unrelated text" band for almost any query - so searching for
        // "headphones" returned eleven screenshots explaining that they said something like it,
        // above the one picture that actually looked like it. Every false hit a real machine
        // produced for that query came through this line.
        //
        // The chunk is still stored and still full-text indexed, which is the half that works:
        // "the screenshot with the invoice number in it" is a real thing to want, and it is a
        // question about WORDS. A vector answer to it was never asked for.
        string ocr = ImageText.Read(path);
        if (ocr.Length >= 12)
            foreach (string chunk in DocText.Chunk(ocr, max: 8))
                segs.Add(new ContentDb.Segment(ContentDb.SegText, -1, -1, -1, chunk));
        return new KindResult(segs, null);
    }

    /// <summary>A sound file. Its length is read from the container - a metadata call that decodes
    /// nothing - and a recording over the limit is skipped for a reason of its own before a single
    /// sample is pulled into memory. There is nothing else inside a sound file to index, so the
    /// length decides the whole outcome.</summary>
    private KindResult Audio(string path)
    {
        double duration = Media.Duration(path);
        if (!TranscribeLimit.Covers(_transcribeMinutes(), duration)) return new KindResult([], TooLong);

        (float[] samples, double actual) = Media.Decode(path, MaxDecodeSeconds);
        if (samples.Length < Media.SampleRate) return new KindResult([], "no audio");
        string? note = actual > MaxDecodeSeconds
            ? $"first {(MaxDecodeSeconds / 60).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} min of {(actual / 60).ToString("0", System.Globalization.CultureInfo.InvariantCulture)}"
            : null;
        return new KindResult(Transcribe(path, samples, note), null);
    }

    /// <summary>A video is two things at once, gated separately: its frames want the vision tower
    /// and its sound track wants whisper. Either alone is worth opening the file for, which is why
    /// <see cref="Covers"/> is an OR - and why a clip whose frames were read and whose sound track
    /// was passed over for length is INDEXED with a note rather than skipped.</summary>
    private KindResult Video(string path)
    {
        double duration = Media.VideoDuration(path).GetAwaiter().GetResult();
        var segs = new List<ContentDb.Segment>();
        if (Installed.Has(Capability.Photos)) segs.AddRange(Frames(path, duration));

        string? tooLong = null;
        if (Installed.Has(Capability.Speech))
        {
            if (!TranscribeLimit.Covers(_transcribeMinutes(), duration)) tooLong = TooLong;
            else
                try
                {
                    (float[] samples, _) = Media.Decode(path, MaxDecodeSeconds);
                    if (samples.Length >= Media.SampleRate) segs.AddRange(Transcribe(path, samples, null));
                }
                catch (Exception ex)
                {
                    Log.Once($"index|videoaudio|{ex.GetType().Name}", "WARN", "index",
                             $"a video sound track could not be read :: {ex.GetType().Name}: {ex.Message}");
                }
        }

        // Something was read: the file really is searchable, and the note records only what was
        // left undone. Nothing was read: the length is the skip reason if it was the cause.
        return segs.Count > 0 ? new KindResult(segs, null, tooLong) : new KindResult(segs, tooLong ?? "no frames");
    }

    private List<ContentDb.Segment> Frames(string path, double duration)
    {
        var segs = new List<ContentDb.Segment>();
        List<double> times = Media.SampleTimes(duration);
        List<SKBitmap?> frames = Media.Frames(path, times).GetAwaiter().GetResult();
        ClipImageEncoder vision = Vision();
        var batch = new List<float[]>();
        var batchTimes = new List<double>();

        void FlushBatch()
        {
            if (batch.Count == 0) return;
            float[][] vs = vision.Encode(batch);
            for (int i = 0; i < vs.Length; i++)
                segs.Add(new ContentDb.Segment(ContentDb.SegFrame, batchTimes[i], batchTimes[i],
                                               Append(vs[i], ContentDb.SegFrame), ""));
            batch.Clear();
            batchTimes.Clear();
        }

        for (int i = 0; i < frames.Count; i++)
        {
            using SKBitmap? f = frames[i];
            if (f is null) continue;
            batch.Add(ClipImageEncoder.Preprocess(f));
            batchTimes.Add(times[i]);
            if (batch.Count == 8) FlushBatch();
        }
        FlushBatch();
        return segs;
    }

    /// <summary>Samples into windowed segments. The general model is the first pass in every
    /// configuration - it is what detects the language - and the fine-tune is handed in as the
    /// second argument and used only over what the first one called Hebrew.</summary>
    private List<ContentDb.Segment> Transcribe(string path, float[] samples, string? note)
    {
        (Model general, Model? hebrew) = SpeechModels(Installed);
        _whisper ??= Media.OpenWhisper(ModelStore.PathOf(general, _dir)).Value;

        // The FINE-TUNE is allowed to fail on its own, and this is the whole reason it has a try
        // of its own. It is a second pass over what the general model called Hebrew, so a machine
        // where it will not open should transcribe everything else exactly as a machine without it
        // does. Under the general model's own open it did the opposite: one corrupt or truncated
        // whisper-ivrit.bin threw out of here for EVERY recording on the disk, each one was
        // recorded as failed - a state nothing re-queues - and the transcripts the general model
        // would have produced were never written. A 1.5 GB file for one language silently took
        // speech search away from every other language on the machine.
        //
        // Tried once. _heIsBroken stops a file that will not open being opened again for every
        // recording in the queue, which on a first pass is a decode failure per recording rather
        // than one line in the log.
        if (_whisperHe is null && !_heIsBroken && hebrew is not null && ModelStore.Present(hebrew, _dir))
        {
            try { _whisperHe = Media.OpenWhisper(ModelStore.PathOf(hebrew, _dir)).Value; }
            catch (Exception ex)
            {
                _heIsBroken = true;
                Log.Error("index", "the Hebrew speech model is on disk and would not open - " +
                                   "recordings are transcribed by the general model only", ex);
            }
        }

        (List<Media.Line> lines, string lang) = Media.Transcribe(samples, _whisper, _whisperHe).GetAwaiter().GetResult();
        Log.Once($"index|speech|{lang}", "INFO", "index",
                 $"speech: first '{lang}' transcript, {lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} line(s)"
                 + (note is null ? "" : $" ({note})"));

        E5Encoder e5 = E5();
        return Speech.Merge(lines, text => Append(e5.EncodePassage(E5Encoder.Passage(path, text)), ContentDb.SegSpeech));
    }

    // ---- the pieces the arms share ----

    private long Append(float[] v, int segKind)
    {
        long row = _vectors.Append(v, (byte)segKind);
        _dirty = true;
        return row;
    }

    private ClipImageEncoder Vision() => _vision ??= new ClipImageEncoder(wantAccelerator: true, _dir);

    /// <summary>
    /// On the accelerator, which is the largest single lever on how long a first pass takes.
    ///
    /// <para>Measured on one desktop: 134 segments a second on the processor against 408 on
    /// DirectML. Everything else in a content pass is cheap beside it - the benchmark's extraction
    /// row reports 58,501 files a minute with no model loaded, and a real first pass runs at tens
    /// of files a minute with one.</para>
    ///
    /// <para>Safe only because <see cref="ModelStore.E5Base"/> is full precision. The query side
    /// stays on the processor and the two vectors are compared to each other, so this pairing works
    /// by the two providers agreeing rather than by a threshold absorbing the difference. With the
    /// quantised file it did not agree, and <c>ProviderAgreementTests</c> is what keeps that
    /// true.</para>
    /// </summary>
    private E5Encoder E5() => _e5 ??= new E5Encoder(wantAccelerator: true, _dir);

    private static SKBitmap? LoadBitmap(string path, int maxDim)
    {
        try
        {
            using SKImage? img = PreviewDecoder.DecodeWithSkia(path, maxDim);
            if (img is not null) return SKBitmap.FromImage(img);
        }
        catch (Exception ex)
        {
            Log.Once($"index|skia|{ex.GetType().Name}", "WARN", "index",
                     $"a picture could not be decoded directly :: {ex.GetType().Name}: {ex.Message}");
        }
        // HEIC, RAW and anything else Skia has no codec for: the shell renders it.
        using SKImage? thumb = PreviewDecoder.ShellThumbnail(path, maxDim);
        return thumb is null ? null : SKBitmap.FromImage(thumb);
    }
}
