using System.Globalization;

using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The capsule's two bands: the bar somebody clicks, and the progress pill under it.
/// </summary>
public class CapsuleTests
{
    [Fact]
    public void ThePlaceholderFitsTheBarItIsDrawnIn()
    {
        // Nothing ellipsises this string - CapsulePainter draws it straight - so a placeholder
        // wider than the bar runs off the end of the capsule and is clipped by the window.
        //
        // It went unseen because the shot drew a DIFFERENT string: the window had
        // "Search files, photos, words..." and --searchshot had "Search 1.5M files", so every
        // render ever reviewed showed a placeholder the product does not use. One constant now.
        SKTypeface face = Parts.Face;
        float budget = CapsuleLayout.Width - (CapsuleLayout.Pad + 34f) - CapsuleLayout.Pad;
        float w = CardText.Measure(CapsulePainter.Placeholder, face, CapsuleLayout.TextSize);

        Assert.True(w <= budget,
            $"'{CapsulePainter.Placeholder}' is {w:0.0}px against a bar that holds {budget:0.0}px");
    }

    [Fact]
    public void TheProgressPillFitsInsideTheCapsuleWithAirUnderIt()
    {
        // It is drawn into the same bitmap the bar is, so a pill that reached the bottom edge
        // would be clipped by the window rather than merely tight. At 128 it ended 4px from the
        // bottom, which reads as cut off.
        SKRect pill = CapsuleLayout.PillRect();

        Assert.True(pill.Top >= CapsuleLayout.BarRect().Bottom, "the pill overlaps the bar");
        Assert.True(pill.Bottom + 8f <= CapsuleLayout.Height,
            $"the pill ends {CapsuleLayout.Height - pill.Bottom:0.0}px from the bottom");
        Assert.True(pill.Left > 0 && pill.Right < CapsuleLayout.Width);
    }

    [Fact]
    public void TheWidestLabelAndCountStillLeaveATrackBetweenThem()
    {
        // The painter measures both ends and lays the track in what is left, so this is what stops
        // that room going to nothing: "indexing recordings" is the longest label, and a machine
        // with a million files to read is the longest count. Below 24px the painter draws no track
        // at all, which is honest but is not the design.
        SKTypeface face = Parts.Face;
        string label = IndexStatus.Doing("Audio");
        string count = IndexStatus.Pill(true, "Audio", 1_000_000, 999_999, true).Count;

        float room = CapsuleLayout.PillW - CapsuleLayout.PillPad * 2 - CapsuleLayout.PillGap * 2
                   - CardText.Measure(label, face, CapsuleLayout.PillTextSize)
                   - CardText.Measure(count, face, CapsuleLayout.PillTextSize);

        Assert.True(room >= 24f, $"only {room:0.0}px left for the track between '{label}' and '{count}'");
    }

    [Fact]
    public void ThePillSaysNothingWhenThereIsNothingToSay()
    {
        // A permanently visible progress pill makes an idle widget feel busy, which is the thing
        // spec 3 says the capsule must not do. Three states, one answer.
        Assert.False(IndexStatus.Pill(contentEnabled: false, "Doc", 1_000, 10, alive: true).Show);
        Assert.False(IndexStatus.Pill(contentEnabled: true, "Doc", 1_000, 10, alive: false).Show);
        Assert.False(IndexStatus.Pill(contentEnabled: true, "Doc", 0, 4_000, alive: true).Show);

        Assert.True(IndexStatus.Pill(contentEnabled: true, "Doc", 1_000, 10, alive: true).Show);
    }

    [Fact]
    public void TheLabelIsAWordAndNeverAnEnumName()
    {
        // The kind arrives as the ResultKind's own ToString off the queue row, and printing that
        // would put an identifier on the desktop.
        Assert.Equal("indexing photos", IndexStatus.Doing("Photo"));
        Assert.Equal("indexing recordings", IndexStatus.Doing("Audio"));
        Assert.Equal("indexing documents", IndexStatus.Doing("Doc"));
        Assert.Equal("indexing video", IndexStatus.Doing("Video"));

        // A kind the pill has no word for, and the row read mid-write, both fall back to the verb
        // rather than to a guess.
        Assert.Equal("indexing", IndexStatus.Doing(""));
        Assert.Equal("indexing", IndexStatus.Doing("Folder"));
    }

    [Fact]
    public void TheCountsReadTheSameOnEveryMachine()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("6,680 of 10,800", IndexStatus.Pill(true, "Photo", 4_120, 6_680, true).Count);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }
}
