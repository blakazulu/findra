using Findra;
using SkiaSharp;
using Xunit;

public class CardTextTests
{
    private static readonly SKTypeface Face = SKTypeface.Default;

    [Fact]
    public void MeasureGrowsWithLength()
    {
        Assert.True(CardText.Measure("mm", Face, 14) > CardText.Measure("m", Face, 14));
        Assert.Equal(0, CardText.Measure("", Face, 14));
    }

    [Fact]
    public void EllipsizeLeavesShortTextAlone()
    {
        Assert.Equal("short", CardText.Ellipsize("short", Face, 14, 1000));
    }

    [Fact]
    public void EllipsizeShortensLongTextToFit()
    {
        string s = CardText.Ellipsize(new string('m', 400), Face, 14, 60);
        Assert.True(CardText.Measure(s, Face, 14) <= 60.5f);
        Assert.True(s.Length < 400);
    }

    [Fact]
    public void WrapRespectsItsLineBudget()
    {
        var lines = CardText.Wrap(string.Join(' ', Enumerable.Repeat("word", 200)), Face, 12, 100, maxLines: 3);
        Assert.True(lines.Count <= 3);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void HebrewIsDetectedAsRightToLeft()
    {
        Assert.True(BidiText.HasRtl("הסכם"));
        Assert.False(BidiText.HasRtl("agreement"));
    }

    [Fact]
    public void AMixedStringLaysOutEveryCharacter()
    {
        // File names really are mixed: "הסכם-שכירות 2026.docx". Losing a cluster here loses
        // a character on screen.
        const string mixed = "הסכם-שכירות 2026.docx";
        Assert.Equal(mixed.Length, BidiText.Layout(mixed).Count);
    }

    [Fact]
    public void AsciiSurvivesTheVisualPass()
    {
        Assert.Equal("report.pdf", BidiText.ToVisual("report.pdf"));
    }
}
