using Findra;
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
    public void TheTwoOrdinaryOpenFailuresBecomeReasonsRatherThanErrors(int hresult, string fragment)
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
}
