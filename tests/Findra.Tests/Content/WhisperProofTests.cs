using Findra;
using Xunit;

/// <summary>
/// The accelerated speech runtime has to prove it TRANSCRIBES, not that it loads.
///
/// <para>It was accepted the moment the factory opened, and the known integrated-GPU failures
/// happen after that: Vulkan initialises cleanly, the shaders compile, the device registers, and
/// then the output is garbled text with nonsense timestamps. Findra would embed that through e5
/// and write it into the index as a finished transcript, and nothing re-reads a file that
/// succeeded. Same defect as opening the Hebrew model and calling the capability ready.</para>
///
/// <para>The judgement is a pure function precisely so it can be tested without a GPU.</para>
/// </summary>
public class WhisperProofTests
{
    private const double Second = 1.0;

    private static Media.Line At(double t0, double t1, string text = "hello") => new(t0, t1, text, "en");

    [Fact]
    public void NothingAtAllIsAPass()
    {
        // A second of a tone says nothing. A healthy model is entitled to report exactly that, and
        // demanding words would reject every working machine.
        Assert.Null(Media.WhatIsWrongWith([], Second));
    }

    [Fact]
    public void AnOrdinaryTranscriptIsAPass()
    {
        Assert.Null(Media.WhatIsWrongWith([At(0, 0.5), At(0.5, 1.0)], Second));
    }

    [Theory]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void ATimestampThatIsNotANumberIsRejected(double t0, double t1)
    {
        string? wrong = Media.WhatIsWrongWith([At(t0, t1)], Second);
        Assert.Equal("a timestamp is not a number", wrong);
    }

    [Fact]
    public void ASegmentThatEndsBeforeItStartsIsRejected()
        => Assert.Equal("a segment ends before it starts", Media.WhatIsWrongWith([At(0.9, 0.2)], Second));

    [Fact]
    public void ATimestampMilesOutsideTheAudioIsRejected()
        => Assert.Equal("a timestamp is outside the audio", Media.WhatIsWrongWith([At(9_999, 10_000)], Second));

    [Fact]
    public void SegmentsRunningBackwardsAreRejected()
        => Assert.Equal("the segments run backwards",
                        Media.WhatIsWrongWith([At(0.8, 1.0), At(0.0, 0.2)], Second));

    [Fact]
    public void ControlCharactersInTheTextAreRejected()
    {
        string garbled = "he" + (char)7 + "llo";
        Assert.Equal("the text carries control characters", Media.WhatIsWrongWith([At(0, 1, garbled)], Second));
    }

    [Fact]
    public void OrdinaryWhitespaceIsNotAControlCharacter()
    {
        // Whisper wraps long segments, so rejecting a newline would fail every real transcript.
        string wrapped = "a line" + (char)10 + "and another" + (char)9 + "tabbed";
        Assert.Null(Media.WhatIsWrongWith([At(0, 1, wrapped)], Second));
    }

    [Fact]
    public void AModelIsAllowedToOverrunTheAudioALittle()
    {
        // Whisper pads to its own window, so a segment can end past the sample it was given. That
        // is normal and must not be read as the failure this is looking for.
        Assert.Null(Media.WhatIsWrongWith([At(0.0, 29.0)], Second));
    }

    [Fact]
    public void TheProbeIsASecondOfRealSignalRatherThanSilence()
    {
        float[] probe = Media.ProbeAudio();

        Assert.Equal(Media.SampleRate, probe.Length);
        Assert.Contains(probe, x => Math.Abs(x) > 0.01f);
        // Quiet on purpose: loud synthetic tone makes some builds hallucinate confidently, which
        // is noise in the one measurement this is trying to take.
        Assert.All(probe, x => Assert.InRange(x, -0.2f, 0.2f));
    }
}
