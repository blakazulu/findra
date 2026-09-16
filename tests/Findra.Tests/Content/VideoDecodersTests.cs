using Findra;
using Xunit;

public class VideoDecodersTests
{
    [Fact]
    public void TheCodecIsReadBackOutOfTheRecordedReason()
    {
        Assert.Equal("HEVC", VideoDecoders.CodecFromReason(Decoders.NoVideoCodec + " (HEVC)"));
        Assert.Null(VideoDecoders.CodecFromReason(Decoders.NoVideoCodec));
        Assert.Null(VideoDecoders.CodecFromReason("no text"));
    }

    [Fact]
    public void TheFingerprintIsTheSameTwiceRunningAndSaysSomething()
    {
        string a = VideoDecoders.Fingerprint();
        string b = VideoDecoders.Fingerprint();
        Assert.Equal(a, b);
        Assert.NotEqual("", a);
    }
}
