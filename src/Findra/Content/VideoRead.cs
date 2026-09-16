using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using SkiaSharp;

namespace Findra;

/// <summary>A video, open, as the indexer needs it. An interface so the rules below can be tested
/// without a file, a decoder or a model.</summary>
public interface IVideoSource : IDisposable
{
    double Seconds { get; }
    string Codec { get; }
    VideoFrames.FrameResult Frame(double seconds, int maxDim);
}

/// <summary>What came of taking frames out of one video.</summary>
public readonly record struct VideoTake(List<(double At, SKBitmap Picture)> Frames, string? Skip);

/// <summary>
/// The rules about taking frames from a video: how many, when to stop, and what to record when
/// nothing came back.
///
/// <para>Separate from the decoding because the rules are the part that must not be wrong, and
/// they are the part a test can drive. A queue that hands out one file at a time needs a limit on
/// every file: without one the slowest file sets the pace for everything behind it, and "slow" can
/// mean forever.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class VideoRead
{
    /// <summary>The size a frame is stored at, which is what the vision tower wants.</summary>
    public const int FrameSize = 320;

    /// <summary>Empty frames in a row, with nothing decoded yet, before the file is given up on.
    /// Three rather than one: a real film can open on frames the decoder will not produce.</summary>
    public const int GiveUpAfter = 3;

    /// <summary>The most any one video may spend on frames.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    public static (IVideoSource? Source, string? Skip) Open(string path)
    {
        VideoFrames.VideoOpen opened = VideoFrames.Open(path);
        if (opened.Skip is { } why)
            return (null, why == Decoders.NoVideoCodec && opened.Codec.Length > 0
                          ? $"{Decoders.NoVideoCodec} ({opened.Codec})"
                          : why);
        return (new SourceReaderVideo(opened), null);
    }

    public static IReadOnlyList<double> Plan(double duration) => Media.SampleTimes(duration);

    /// <summary>Take the frames, stopping when the file is not worth more time.
    /// <paramref name="beat"/> is called per frame so that a watching parent can tell work from a
    /// hang; <paramref name="elapsed"/> is how long this file has had, injected so the budget is
    /// testable without waiting five minutes.</summary>
    public static VideoTake Take(IVideoSource source, IReadOnlyList<double> times, Action beat, Func<TimeSpan> elapsed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(beat);
        ArgumentNullException.ThrowIfNull(elapsed);

        var frames = new List<(double, SKBitmap)>();
        int emptyInARow = 0;
        foreach (double at in times)
        {
            VideoFrames.FrameResult f = source.Frame(at, FrameSize);
            beat();
            if (f.Picture is { } picture)
            {
                frames.Add((f.AtSeconds > 0 ? f.AtSeconds : at, picture));
                emptyInARow = 0;
            }
            else
            {
                if (f.Ended) break;
                emptyInARow++;
                if (frames.Count == 0 && emptyInARow >= GiveUpAfter) break;
            }
            if (elapsed() >= Budget) break;
        }
        return new VideoTake(frames, frames.Count == 0 ? Decoders.NoFrames : null);
    }

    private sealed class SourceReaderVideo(VideoFrames.VideoOpen opened) : IVideoSource
    {
        private IntPtr _reader = opened.Reader;
        public double Seconds { get; } = opened.Seconds;
        public string Codec { get; } = opened.Codec;

        public VideoFrames.FrameResult Frame(double seconds, int maxDim)
            => _reader == IntPtr.Zero ? new VideoFrames.FrameResult(null, 0, false, true)
                                      : VideoFrames.Frame(_reader, seconds, maxDim);

        public void Dispose()
        {
            if (_reader == IntPtr.Zero) return;
            VideoFrames.Close(_reader);
            _reader = IntPtr.Zero;
        }
    }
}
