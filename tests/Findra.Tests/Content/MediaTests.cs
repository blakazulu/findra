using SkiaSharp;

using Findra;

using Xunit;

/// <summary>
/// The rules that decide what a recording and a picture contribute to the index, and the one the
/// card's stage runs over whatever row is selected.
///
/// <para>Nothing here needs a model, a sound card or a screen: the sample times, the noise rule,
/// the transcript windowing and the script check are all pure, and the preview is asserted against
/// a PNG the test paints itself. Transcription, frame extraction and the recognisers Windows ships
/// need a real file and a real machine, and they belong on the end-to-end checklist.</para>
/// </summary>
public class MediaTests
{
    // ---- where a video is sampled ----

    [Fact]
    public void AClipShorterThanOneStepIsStillSampledOnce()
    {
        // A stepping loop that starts at `every` returns nothing for an eight-second clip, and
        // every short video on the disk is then indexed as having no pictures in it at all.
        List<double> times = Media.SampleTimes(8);

        Assert.Single(times);
        Assert.InRange(times[0], 0, 8);
    }

    [Fact]
    public void ALongFilmIsSpreadOverItsWholeLengthAndNeverExceedsTheFrameBudget()
    {
        // Ten hours. A fixed ten-second step is 3,600 frames - an afternoon of GPU per file -
        // and the budget is what stops one film starving the whole queue.
        List<double> times = Media.SampleTimes(36_000);

        Assert.InRange(times.Count, 2, 90);
        Assert.True(times[^1] > 30_000, $"the last sample is at {times[^1]}s of 36,000");
        for (int i = 1; i < times.Count; i++)
            Assert.True(times[i] > times[i - 1], "the sample times are not increasing");
    }

    [Fact]
    public void EverySampleIsInsideTheVideo()
    {
        foreach (double duration in new[] { 3.0, 11.0, 95.0, 3600.0 })
            foreach (double t in Media.SampleTimes(duration))
                Assert.InRange(t, 0, duration);
    }

    [Fact]
    public void AVideoOfNoLengthIsSampledNowhereRatherThanAtZero()
    {
        Assert.Empty(Media.SampleTimes(0));
        Assert.Empty(Media.SampleTimes(-1));
    }

    // ---- what a transcript is allowed to contain ----

    [Theory]
    [InlineData("[Music]", true)]
    [InlineData("(applause)", true)]
    [InlineData("♪ la la la", true)]
    [InlineData("The lease agreement is signed", false)]
    [InlineData("She said [inaudible] and left", false)]   // a bracket INSIDE a real line
    public void SilenceHallucinationsAreDroppedAndRealSpeechIsKept(string line, bool noise)
        => Assert.Equal(noise, Media.IsNoise(line));

    // ---- how transcript lines become segments ----

    [Fact]
    public void TranscriptLinesAreMergedIntoWindowsASentenceFitsIn()
    {
        // Whisper emits lines of two or three seconds. One segment per line means a search for a
        // phrase spanning two of them finds neither.
        var lines = new List<Media.Line>();
        for (int i = 0; i < 10; i++) lines.Add(new Media.Line(i, i + 1, $"word{i}", "en"));

        List<ContentDb.Segment> segs = Speech.Merge(lines, _ => 0, maxSeconds: 20, maxChars: 600);

        Assert.Single(segs);
        Assert.Equal(0, segs[0].T0);
        Assert.Equal(10, segs[0].T1);
        Assert.Contains("word0", segs[0].Text, StringComparison.Ordinal);
        Assert.Contains("word9", segs[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastWindowIsFlushedEvenWhenItNeverFilledUp()
    {
        // The classic shape of this bug: a loop that only writes a window when it overflows,
        // and drops whatever was in the buffer when the input ran out. The tail of every
        // transcript on the machine is then missing, and nothing anywhere says so.
        var lines = new List<Media.Line>();
        for (int i = 0; i < 30; i++) lines.Add(new Media.Line(i, i + 1, $"word{i}", "en"));

        List<ContentDb.Segment> segs = Speech.Merge(lines, _ => 0, maxSeconds: 20, maxChars: 600);

        Assert.True(segs.Count >= 2, $"30 seconds at a 20-second window gave {segs.Count} segment(s)");
        Assert.Contains("word29", segs[^1].Text, StringComparison.Ordinal);
        Assert.Equal(30, segs[^1].T1);
    }

    [Fact]
    public void NoWordIsLostBetweenTwoWindows()
    {
        var lines = new List<Media.Line>();
        for (int i = 0; i < 30; i++) lines.Add(new Media.Line(i, i + 1, $"word{i}", "en"));

        string all = string.Join(" ", Speech.Merge(lines, _ => 0, 20, 600).Select(s => s.Text));

        for (int i = 0; i < 30; i++) Assert.Contains($"word{i}", all, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySpeechSegmentCarriesTheVectorRowItWasGiven()
    {
        // The embed callback hands back the row the vector went into, and the segment has to
        // carry it or the transcript is in the store with nothing pointing at it.
        long next = 40;
        var lines = new List<Media.Line> { new(0, 2, "hello there", "en") };

        List<ContentDb.Segment> segs = Speech.Merge(lines, _ => next++, 20, 600);

        Assert.Equal(40, segs[0].Vec);
        Assert.Equal(ContentDb.SegSpeech, segs[0].Kind);
    }

    [Fact]
    public void AnEmptyTranscriptIsNoSegmentsRatherThanOneEmptyOne()
    {
        Assert.Empty(Speech.Merge([], _ => 0, 20, 600));
    }

    // ---- words inside pictures ----

    [Theory]
    [InlineData("the quarterly revenue report", true, true)]
    [InlineData("הסכם שכירות חתום", false, true)]
    [InlineData("the quarterly revenue report", false, false)]   // latin text from the Hebrew engine
    [InlineData("ab", true, false)]                              // too short to judge
    [InlineData("abcdefg", true, false)]                         // long enough to count, short enough to doubt
    [InlineData("", true, false)]
    public void ARecogniserReadingTheWrongScriptIsThrownAway(string text, bool latin, bool keep)
    {
        // Two recognisers each read the whole image, and the one reading a script that is not
        // there hallucinates. Without this every screenshot carries a line of nonsense into the
        // full-text index, and nonsense in FTS is matches nobody asked for.
        Assert.Equal(keep, ImageText.MostlyScript(text, latin));
    }

    // ---- the card's preview pane ----

    [Fact]
    public void APictureOnDiskDecodesToAnImageAtTheSizeAsked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "findra-prev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string png = Path.Combine(dir, "wide.png");
            using (var bmp = new SKBitmap(800, 400))
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.CornflowerBlue);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 90);
                using var fs = File.Create(png);
                data.SaveTo(fs);
            }

            using SKImage? preview = PreviewDecoder.DecodeWithSkia(png, 200);

            Assert.NotNull(preview);
            Assert.True(preview!.Width <= 300, $"a 200 px preview came back {preview.Width} px wide");
            Assert.True(preview.Width > preview.Height, "the aspect ratio was not kept");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void SomethingThatIsNotAPictureDecodesToNothingRatherThanThrowing()
    {
        // The card's stage runs this over whatever row is selected. An exception here is an
        // exception on the UI thread for every text file somebody arrows onto.
        string dir = Path.Combine(Path.GetTempPath(), "findra-prev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string txt = Path.Combine(dir, "notes.txt");
            File.WriteAllText(txt, "this is not a picture");
            Assert.Null(PreviewDecoder.DecodeWithSkia(txt, 200));
            Assert.Null(PreviewDecoder.Decode(Path.Combine(dir, "gone.jpg"), ResultKind.Photo, 200));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
