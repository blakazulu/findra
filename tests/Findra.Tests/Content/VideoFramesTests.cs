using Findra;
using SkiaSharp;
using Xunit;

public class VideoFramesTests
{
    [Theory]
    // The subtype GUID for a video format carries its FourCC in the first four bytes.
    [InlineData("34363248-0000-0010-8000-00AA00389B71", "H264")]
    [InlineData("43564548-0000-0010-8000-00AA00389B71", "HEVC")]
    [InlineData("5634504d-0000-0010-8000-00AA00389B71", "MP4V")]
    [InlineData("63766964-0000-0010-8000-00AA00389B71", "divc")]
    public void ACodecIsNamedByTheFourCharactersItsFormatIdCarries(string guid, string expected)
        => Assert.Equal(expected, VideoFrames.CodecName(new Guid(guid)));

    [Fact]
    public void AFormatIdThatIsNotAFourCharacterCodeIsNamedByItsGuid()
    {
        string name = VideoFrames.CodecName(new Guid("11111111-2222-3333-4444-555555555555"));
        Assert.Contains("11111111", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(unchecked((int)0xC00D5212), "no decoder")]      // MF_E_TOPO_CODEC_NOT_FOUND
    [InlineData(unchecked((int)0xC00D36B3), "no video stream")] // MF_E_INVALIDSTREAMNUMBER
    [InlineData(unchecked((int)0xC00D36C4), "container")]       // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
    public void TheOrdinaryOpenFailuresBecomeReasonsRatherThanErrors(int hresult, string fragment)
        => Assert.Contains(fragment, VideoFrames.SkipFor(hresult), StringComparison.Ordinal);

    [Fact]
    public void AnythingElseIsNotASkipAndIsLeftToTheFailurePath()
        => Assert.Null(VideoFrames.SkipFor(unchecked((int)0x80004005)));   // E_FAIL

    [Fact]
    public void AFileThatIsNotAVideoIsARecordedReasonRatherThanAThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), "findra-notavideo-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(path, "this is not a video");
        try
        {
            VideoFrames.VideoOpen opened = VideoFrames.Open(path);
            Assert.False(string.IsNullOrEmpty(opened.Skip));
            Assert.Equal(IntPtr.Zero, opened.Reader);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OpeningARealVideoReadsItsLengthAndTheCodecItStates()
    {
        string path = Path.Combine(Path.GetTempPath(), "findra-open-" + Guid.NewGuid().ToString("N") + ".mp4");
        TestClip.Write(path, (40, 40, 220), (40, 200, 40), (220, 60, 40));
        try
        {
            VideoFrames.VideoOpen opened = VideoFrames.Open(path);
            Assert.Null(opened.Skip);
            Assert.NotEqual(IntPtr.Zero, opened.Reader);
            try
            {
                Assert.Equal("H264", opened.Codec);
                Assert.InRange(opened.Seconds, 2.5, 3.5);
            }
            finally { VideoFrames.Close(opened.Reader); }
        }
        finally { File.Delete(path); }
    }

    private static (byte B, byte G, byte R) MiddlePixel(SKBitmap b)
    {
        SKColor c = b.GetPixel(b.Width / 2, b.Height / 2);
        return (c.Blue, c.Green, c.Red);
    }

    [Fact]
    public void SeekingIntoASecondReturnsTheFrameFromThatSecond()
    {
        string path = Path.Combine(Path.GetTempPath(), "findra-clip-" + Guid.NewGuid().ToString("N") + ".mp4");
        TestClip.Write(path, (40, 40, 220), (40, 200, 40), (220, 60, 40));
        try
        {
            VideoFrames.VideoOpen opened = VideoFrames.Open(path);
            Assert.Null(opened.Skip);
            Assert.InRange(opened.Seconds, 2.5, 3.5);
            try
            {
                foreach ((double at, (byte B, byte G, byte R) want) in new[]
                         {
                             (0.5, ((byte)40, (byte)40, (byte)220)),
                             (1.5, ((byte)40, (byte)200, (byte)40)),
                             (2.5, ((byte)220, (byte)60, (byte)40)),
                         })
                {
                    VideoFrames.FrameResult f = VideoFrames.Frame(opened.Reader, at, 320);
                    Assert.NotNull(f.Picture);
                    using SKBitmap picture = f.Picture!;
                    (byte b, byte g, byte r) = MiddlePixel(picture);
                    Assert.InRange(b, want.B - 30, want.B + 30);
                    Assert.InRange(g, want.G - 30, want.G + 30);
                    Assert.InRange(r, want.R - 30, want.R + 30);
                }
            }
            finally { VideoFrames.Close(opened.Reader); }
        }
        finally { File.Delete(path); }
    }
}
