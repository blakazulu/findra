using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using SkiaSharp;

using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Findra;

/// <summary>
/// Sound turned into words, and video turned into pictures, for the indexer.
///
/// <para><b>Audio</b> is decoded by Media Foundation - the codecs Windows already has, which is
/// mp3, aac, m4a, wma, flac and the sound track of mp4/mov/mkv - resampled to whisper's 16 kHz
/// mono, and transcribed by whisper.cpp through Whisper.net. Two models are involved: a general
/// one for every language, and a Hebrew fine-tune of it. The first pass runs the general model
/// with language detection, and only a file it calls Hebrew is run again through the fine-tune.
/// Hebrew is a second pass and never an alternative, which is why the general model is required
/// for it and why the extra cost is paid only on Hebrew files.</para>
///
/// <para><b>Video frames</b> come from the media pipeline Windows ships
/// (<c>Windows.Media.Editing</c>): a frame at each sample time, snapped to the nearest key frame
/// and decoded by whatever codec plays the file. Nothing here links a media framework of its own.
/// </para>
///
/// <para>Everything in this file reads a file somebody else wrote, which is why it lives in the
/// indexer child at normal integrity and is never reachable from the elevated helper.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class Media
{
    public const int SampleRate = 16_000;

    /// <summary>Decode the sound track to 16 kHz mono float samples, up to <paramref name="maxSeconds"/>.</summary>
    public static (float[] Samples, double DurationSeconds) Decode(string path, double maxSeconds)
    {
        using var reader = new MediaFoundationReader(path);
        double duration = reader.TotalTime.TotalSeconds;
        ISampleProvider src = reader.ToSampleProvider();
        if (src.WaveFormat.Channels > 1) src = new StereoToMonoSampleProvider(src) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (src.WaveFormat.SampleRate != SampleRate) src = new WdlResamplingSampleProvider(src, SampleRate);

        long want = (long)(Math.Min(duration, maxSeconds) * SampleRate) + SampleRate;
        var all = new List<float>((int)Math.Min(want, 64_000_000));
        var buf = new float[SampleRate * 4];
        int n;
        while ((n = src.Read(buf.AsSpan())) > 0)
        {
            all.AddRange(new ArraySegment<float>(buf, 0, n));
            if (all.Count >= want) break;
        }
        return (all.ToArray(), duration);
    }

    /// <summary>Length in seconds without decoding.</summary>
    public static double Duration(string path)
    {
        using var reader = new MediaFoundationReader(path);
        return reader.TotalTime.TotalSeconds;
    }

    public readonly record struct Line(double T0, double T1, string Text, string Language);

    /// <summary>Transcribe. <paramref name="general"/> and <paramref name="hebrew"/> are the two
    /// factories; the second may be null. Returns the lines and the language decided on.</summary>
    public static async Task<(List<Line> Lines, string Language)> Transcribe(float[] samples, WhisperFactory general,
        WhisperFactory? hebrew, string? forceLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(general);
        var lines = new List<Line>();
        string lang = forceLanguage ?? "";
        await using (var proc = (forceLanguage is null ? general.CreateBuilder().WithLanguageDetection() : general.CreateBuilder().WithLanguage(forceLanguage))
                         .WithThreads(Math.Max(2, Environment.ProcessorCount / 2)).Build())
        {
            await foreach (var seg in proc.ProcessAsync(samples))
            {
                if (lang.Length == 0) lang = seg.Language ?? "";
                lines.Add(new Line(seg.Start.TotalSeconds, seg.End.TotalSeconds, seg.Text.Trim(), seg.Language ?? lang));
            }
        }

        if (forceLanguage is null && lang == "he" && hebrew is not null)
        {
            // the general model heard Hebrew; the fine-tune transcribes it properly
            lines.Clear();
            await using var proc = hebrew.CreateBuilder().WithLanguage("he")
                .WithThreads(Math.Max(2, Environment.ProcessorCount / 2)).Build();
            await foreach (var seg in proc.ProcessAsync(samples))
                lines.Add(new Line(seg.Start.TotalSeconds, seg.End.TotalSeconds, seg.Text.Trim(), "he"));
        }
        lines.RemoveAll(l => l.Text.Length == 0 || IsNoise(l.Text));
        return (lines, lang);
    }

    /// <summary>
    /// Make a freshly-opened factory actually transcribe, and throw if what comes back is not
    /// structurally sane.
    ///
    /// <para><b>Why a load is not proof.</b> The accelerated rung was accepted the moment
    /// <c>WhisperFactory.FromPath</c> returned - but the known integrated-GPU failures happen at
    /// TRANSCRIPTION time, after Vulkan has initialised cleanly, the shaders have compiled and the
    /// device has registered. whisper.cpp issue #2596 is exactly the hardware Findra ships to: an
    /// AMD 780M, the integrated GPU in a whole generation of Ryzen laptops, on Windows 11, where
    /// everything loads and then the output is garbled text with nonsense timestamps. In a debug
    /// build it asserts; in a release build it simply returns rubbish.</para>
    ///
    /// <para>And Findra would keep the rubbish. It would be embedded through e5 and written into
    /// the index as a finished transcript, and nothing re-reads a file that succeeded. That is the
    /// same defect as opening the Hebrew model and calling the capability ready, one level up:
    /// what was proved was that the thing STARTS.</para>
    ///
    /// <para><b>It asserts shape, never words.</b> A second of a synthesised tone has no correct
    /// transcript - a healthy Whisper returns nothing at all, or one short hallucination - so
    /// requiring particular text would fail on working machines. What the documented failure
    /// produces is broken STRUCTURE: timestamps that are not finite, run backwards or sit outside
    /// the audio, and text carrying control characters. That is what is checked, against audio
    /// generated here rather than a sample file, so nothing has to be shipped, licensed, or kept
    /// in step with the models.</para>
    ///
    /// <para>The failure direction is deliberate. A false rejection drops that machine to the
    /// processor runtime, which is slower and correct. A false acceptance writes nonsense into
    /// somebody's index for ever.</para>
    /// </summary>
    public static void ProveItTranscribes(WhisperFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        float[] probe = ProbeAudio();
        var lines = new List<Line>();

        using (var proc = factory.CreateBuilder().WithLanguage("en").WithThreads(2).Build())
            foreach (var seg in proc.ProcessAsync(probe).ToBlockingEnumerable())
                lines.Add(new Line(seg.Start.TotalSeconds, seg.End.TotalSeconds, seg.Text ?? "", seg.Language ?? ""));

        string? wrong = WhatIsWrongWith(lines, probe.Length / (double)SampleRate);
        if (wrong is not null)
            throw new InvalidOperationException(
                "this runtime loaded but did not transcribe correctly (" + wrong +
                "), which is the documented integrated-GPU failure; falling back");
    }

    /// <summary>The probe: a second of a quiet tone that rises, so it is neither silence nor
    /// noise. Generated rather than shipped - there is no sample file to license, to keep beside
    /// the models, or to lose.</summary>
    public static float[] ProbeAudio()
    {
        var a = new float[SampleRate];
        for (int i = 0; i < a.Length; i++)
        {
            double t = i / (double)SampleRate;
            a[i] = (float)(0.05 * Math.Sin(2 * Math.PI * (220 + 180 * t) * t));
        }
        return a;
    }

    /// <summary>What is structurally wrong with a transcript, or null when nothing is. Pure, so
    /// every arm of the judgement is testable without a GPU - which is the only way any of this
    /// can be tested at all.</summary>
    /// <summary>The three control characters a transcript may legitimately carry. Named
    /// rather than escaped so this comparison cannot be broken by an editing accident.</summary>
    private const char Lf = (char)10, Cr = (char)13, Tab = (char)9;

    public static string? WhatIsWrongWith(IReadOnlyList<Line> lines, double seconds)
    {
        ArgumentNullException.ThrowIfNull(lines);
        // No segments at all is a PASS. A second of a tone says nothing, and a healthy model is
        // entitled to report exactly that.
        double last = double.NegativeInfinity;
        foreach (Line l in lines)
        {
            if (!double.IsFinite(l.T0) || !double.IsFinite(l.T1)) return "a timestamp is not a number";
            if (l.T1 < l.T0) return "a segment ends before it starts";
            if (l.T0 < -1 || l.T0 > seconds + 30) return "a timestamp is outside the audio";
            // Whisper emits segments in order, so a later one starting EARLIER is broken output.
            // The half-second of slack is for float noise at a boundary, not for reordering;
            // a whole second was loose enough to let an obviously reversed pair through.
            if (l.T0 < last - 0.5) return "the segments run backwards";
            last = l.T0;

            foreach (char c in l.Text)
                if (char.IsControl(c) && c != Lf && c != Cr && c != Tab)
                    return "the text carries control characters";
        }
        return null;
    }

    /// <summary>
    /// Whether a transcript line is whisper hearing something in silence rather than speech.
    ///
    /// <para>A stretch with nothing in it produces bracketed sound effects and the musical-note
    /// family, in whatever language the model settled on. The tell is the bracket around the
    /// <em>whole</em> line, not a bracket anywhere in it: "She said [inaudible] and left" is a real
    /// sentence somebody may search for, and throwing it away is the more expensive mistake.</para>
    /// </summary>
    public static bool IsNoise(string t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return (t.StartsWith('[') && t.EndsWith(']')) || (t.StartsWith('(') && t.EndsWith(')')) || t.StartsWith('♪');
    }

    /// <summary>Video length, via the media pipeline the frames come from.</summary>
    public static async Task<double> VideoDuration(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
        return clip.OriginalDuration.TotalSeconds;
    }

    /// <summary>One frame at each of <paramref name="times"/> (seconds), decoded to a bitmap sized
    /// for the vision encoder. A time the pipeline cannot render yields null in its slot.</summary>
    public static async Task<List<SKBitmap?>> Frames(string path, IReadOnlyList<double> times, int maxDim = 320)
    {
        ArgumentNullException.ThrowIfNull(times);
        var frames = new List<SKBitmap?>(times.Count);
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
        var comp = new Windows.Media.Editing.MediaComposition();
        comp.Clips.Add(clip);
        foreach (double t in times)
        {
            try
            {
                using var stream = await comp.GetThumbnailAsync(TimeSpan.FromSeconds(t), maxDim, 0,
                    Windows.Media.Editing.VideoFramePrecision.NearestKeyFrame);
                using var s = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream);
                frames.Add(SKBitmap.Decode(s));
            }
            catch (Exception ex)
            {
                Log.Once("index|frame|" + ex.GetType().Name, "WARN", "index",
                         $"a frame could not be rendered :: {ex.GetType().Name}: {ex.Message}");
                frames.Add(null);
            }
        }
        return frames;
    }

    /// <summary>Where to sample a video: every <paramref name="every"/> seconds from a beat in, at
    /// most <paramref name="max"/> frames spread evenly when the video is long.</summary>
    public static List<double> SampleTimes(double duration, double every = 10, int max = 90)
    {
        var t = new List<double>();
        if (duration <= 0) return t;
        if (duration <= every) { t.Add(Math.Min(1.0, duration / 2)); return t; }
        double step = Math.Max(every, duration / max);
        for (double s = Math.Min(1.0, step / 2); s < duration - 0.5 && t.Count < max; s += step) t.Add(s);
        return t;
    }

    /// <summary>A whisper factory on the first runtime that will have it. The runtime order is a
    /// process-wide setting in the ggml loader rather than a per-call argument, so this sets it
    /// once and then reports what actually answered - which is the fact --searchmodels prints.
    /// </summary>
    public static Chosen<WhisperFactory> OpenWhisper(string path)
        => Providers.First<WhisperFactory>(
        [
            ("Vulkan", () =>
            {
                // Asked before the loader is touched: on an architecture the accelerated runtime
                // was never published for this is a missing file rather than a device that would
                // not start, and the two want different sentences in the report.
                Providers.RequireAcceleratedSpeechRuntime();
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan];
                WhisperFactory made = WhisperFactory.FromPath(path);
                // AND IT HAS TO TRANSCRIBE SOMETHING. See ProveItTranscribes: on this rung a
                // successful load proves nothing, because the failure being guarded against
                // happens after a clean init.
                try { ProveItTranscribes(made); }
                catch { made.Dispose(); throw; }
                return made;
            }),
            ("CPU", () =>
            {
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
                return WhisperFactory.FromPath(path);
            }),
        ]);
}
