using Findra;
using Findra.Diagnostics;
using Xunit;

/// <summary>
/// Which files were passed over, and why. Skipping is a normal state rather than a failure, so
/// these are deliberately not in the failures section - but the reason a skip carries had no
/// reader anywhere in the product, which made "waiting for a model", "too small to be a picture"
/// and "no decoder for this format" one undifferentiated count. It is the first thing anybody asks
/// when a file they can see is not findable, and the answer was in the index the whole time.
/// </summary>
public class SearchIndexSkipTests
{
    [Fact]
    public void TheReportSaysWhichFilesWereSkippedAndWhy()
    {
        string report = SearchIndexReport.Render(SearchIndexReportTests.Sample(skips:
        [
            (@"C:\Users\robin\Pictures\tiny.png", "too small to be a picture"),
            (@"C:\Users\robin\Music\lecture.m4a", "longer than the transcription limit"),
        ]));

        Assert.Contains("skipped", report, StringComparison.Ordinal);
        Assert.Contains("too small to be a picture", report, StringComparison.Ordinal);
        Assert.Contains("longer than the transcription limit", report, StringComparison.Ordinal);
        Assert.Contains("lecture.m4a", report, StringComparison.Ordinal);
        // The sample records 900 skipped and two are shown, so the remainder comes from the real
        // total rather than from the length of the sample - which would cap it at "and 0 more".
        Assert.Contains("and 898 more", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ASkipWithNoRecordedReasonSaysSoRatherThanLeavingABlankLine()
    {
        string report = SearchIndexReport.Render(SearchIndexReportTests.Sample(skips: [(@"C:\a\b.png", "")]));
        Assert.Contains("no reason recorded", report, StringComparison.Ordinal);
    }
}
