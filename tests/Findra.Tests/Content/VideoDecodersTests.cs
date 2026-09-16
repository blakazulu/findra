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
    public void TheFingerprintIsTheSameTwiceRunning()
    {
        // Stability is the whole contract: it is compared against the last one seen, and a value
        // that moved on its own would re-queue every codec-blocked video on the machine for
        // nothing.
        //
        // It is deliberately NOT asserted to be non-empty. Fingerprint answers "" when the platform
        // will not list its transforms, which is exactly a Windows N install with no Media Feature
        // Pack - a machine this product supports - and the caller reads "" as "nothing changed" for
        // that reason. Requiring something here would assert the machine the test runs on rather
        // than anything about the code.
        string a = VideoDecoders.Fingerprint();
        string b = VideoDecoders.Fingerprint();
        Assert.Equal(a, b);
    }
}
