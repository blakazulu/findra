using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// Where a selection is painted. The field draws its text reordered for Hebrew, so a LOGICAL
/// range is not one rectangle on the screen: a range that crosses from a Latin word into a Hebrew
/// one lands in two places. The highlight is worked out from the same cells the caret uses, so it
/// covers exactly the letters that are selected.
/// </summary>
public class FieldSelectionTests
{
    private const float Size = 27.2f;

    private static List<FieldCaret.Cell> Cells(string text) => FieldCaret.Cells(text, Parts.Face, Size);

    [Fact]
    public void NothingSelectedPaintsNothing()
    {
        Assert.Empty(FieldCaret.Spans(Cells("sunset"), 2, 2));
    }

    [Fact]
    public void ALatinRangeIsOneSpanFromItsFirstLetterToItsLast()
    {
        List<FieldCaret.Cell> cells = Cells("sunset");
        List<(float Left, float Right)> spans = FieldCaret.Spans(cells, 1, 4);
        Assert.Single(spans);
        Assert.Equal(FieldCaret.SlotX(cells, 1), spans[0].Left, 3);
        Assert.Equal(FieldCaret.SlotX(cells, 4), spans[0].Right, 3);
    }

    [Fact]
    public void TheWholeTextIsOneSpanAcrossTheField()
    {
        List<FieldCaret.Cell> cells = Cells("lake הסכם");
        List<(float Left, float Right)> spans = FieldCaret.Spans(cells, 0, "lake הסכם".Length);
        Assert.Single(spans);
        Assert.Equal(0, spans[0].Left, 3);
        Assert.Equal(cells[^1].Right, spans[0].Right, 3);
    }

    [Fact]
    public void ARangeThatCrossesIntoHebrewCoversOnlyTheSelectedLetters()
    {
        // "ab " then a Hebrew word drawn right to left. Selecting the b, the space and the first
        // Hebrew letter (logical 1..4) takes the b and the space at the left and the Hebrew letter
        // at the FAR right of the word, since that is where the first one is drawn - two spans.
        string text = "ab אבג";
        List<FieldCaret.Cell> cells = Cells(text);
        List<(float Left, float Right)> spans = FieldCaret.Spans(cells, 1, 4);

        Assert.Equal(2, spans.Count);
        foreach (FieldCaret.Cell c in cells)
        {
            bool selected = c.LogStart >= 1 && c.LogEnd <= 4;
            bool painted = spans.Any(s => c.Left >= s.Left - 0.01f && c.Right <= s.Right + 0.01f);
            Assert.Equal(selected, painted);
        }
    }
}
