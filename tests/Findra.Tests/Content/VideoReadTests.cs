using Findra;
using SkiaSharp;
using Xunit;

public class VideoReadTests
{
    private sealed class FakeVideo : IVideoSource
    {
        private readonly Func<double, VideoFrames.FrameResult> _answer;
        public FakeVideo(double seconds, Func<double, VideoFrames.FrameResult> answer)
        { Seconds = seconds; _answer = answer; }
        public double Seconds { get; }
        public string Codec => "TEST";
        public int Asked { get; private set; }
        public VideoFrames.FrameResult Frame(double seconds, int maxDim) { Asked++; return _answer(seconds); }
        public void Dispose() { }
    }

    private static SKBitmap Pixel() => new(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque));

    private static VideoFrames.FrameResult Picture(double at) => new(Pixel(), at, false, false);
    private static VideoFrames.FrameResult Empty() => new(null, 0, true, false);

    [Fact]
    public void EveryFrameThatCameBackIsKept()
    {
        var video = new FakeVideo(100, Picture);
        VideoTake take = VideoRead.Take(video, [1, 11, 21], () => { }, () => TimeSpan.Zero);
        Assert.Equal(3, take.Frames.Count);
        Assert.Null(take.Skip);
        foreach ((_, SKBitmap p) in take.Frames) p.Dispose();
    }

    [Fact]
    public void ThreeEmptyFramesInARowWithNothingDecodedEndTheFile()
    {
        var video = new FakeVideo(1000, _ => Empty());
        VideoTake take = VideoRead.Take(video, [1, 11, 21, 31, 41, 51], () => { }, () => TimeSpan.Zero);
        Assert.Empty(take.Frames);
        Assert.Equal(Decoders.NoFrames, take.Skip);
        Assert.Equal(3, video.Asked);        // it stopped asking
    }

    [Fact]
    public void AFilmThatStartsBlackAndThenHasPicturesIsStillRead()
    {
        // Two empty frames then a picture: the give-up rule must not fire on a fade-in that the
        // decoder could not produce, only on a file with nothing in it at all.
        int n = 0;
        var video = new FakeVideo(1000, at => ++n <= 2 ? Empty() : Picture(at));
        VideoTake take = VideoRead.Take(video, [1, 11, 21, 31], () => { }, () => TimeSpan.Zero);
        Assert.Equal(2, take.Frames.Count);
        Assert.Null(take.Skip);
        foreach ((_, SKBitmap p) in take.Frames) p.Dispose();
    }

    [Fact]
    public void NoVideoSpendsMoreThanTheBudgetOnFrames()
    {
        var clock = TimeSpan.Zero;
        var video = new FakeVideo(10_000, Picture);
        VideoTake take = VideoRead.Take(video, [.. Enumerable.Range(0, 90).Select(i => i * 10.0)],
                                        () => { }, () => clock += TimeSpan.FromMinutes(1));
        // The budget is five minutes, so it stops after the fifth frame rather than taking ninety.
        Assert.True(take.Frames.Count <= 6, $"took {take.Frames.Count} frames");
        foreach ((_, SKBitmap p) in take.Frames) p.Dispose();
    }

    [Fact]
    public void EveryFrameReportsProgress()
    {
        int beats = 0;
        var video = new FakeVideo(100, Picture);
        VideoTake take = VideoRead.Take(video, [1, 11, 21], () => beats++, () => TimeSpan.Zero);
        Assert.Equal(3, beats);
        foreach ((_, SKBitmap p) in take.Frames) p.Dispose();
    }
}
