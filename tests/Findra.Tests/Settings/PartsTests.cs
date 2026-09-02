using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The one piece of the drawing vocabulary that is a pure function and can therefore be wrong in
/// a way nothing would see: wrapping. Every explanatory sentence in both surfaces goes through it,
/// and the layout asks it how tall a row's note is before deciding where the next row goes.
/// </summary>
public class PartsTests
{
    // Through Parts.Face, not SKTypeface.Default: it resolves to the platform default in this
    // task and to the shipped Quicksand once Task 3 fills it in, so these measurements follow the
    // product rather than diverging from it the moment the font lands.
    private static readonly SKTypeface Face = Parts.Face;
    private const float Size = 12f;

    private const string Sentence =
        "Reading inside files walks every drive and opens every document, so Findra does not " +
        "start it on its own.";

    [Fact]
    public void NoWrappedLineIsWiderThanTheColumn()
    {
        foreach (string line in Parts.Wrap(Sentence, Face, Size, 200f))
            Assert.True(CardText.Measure(line, Face, Size) <= 200f,
                $"'{line}' measures {CardText.Measure(line, Face, Size)} in a 200 column");
    }

    [Fact]
    public void WrappingKeepsEveryWordInOrder()
    {
        // The classic greedy-wrap bug drops the word that caused the overflow: the line is
        // emitted, the loop continues, and the word is never re-added. The result reads fine and
        // is missing a word, which is worse than a line that is too long.
        string[] want = Sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] got = string.Join(" ", Parts.Wrap(Sentence, Face, Size, 200f))
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(want, got);
    }

    [Fact]
    public void AWordWiderThanTheColumnIsPlacedRatherThanDroppedOrLoopedOn()
    {
        // A token that can never fit is what turns "while it does not fit, emit a line" into an
        // infinite loop, or into an empty result. Long paths in the exclusions list are exactly
        // this shape.
        IReadOnlyList<string> lines = Parts.Wrap(
            @"C:\Users\somebody\AppData\Roaming\a-very-long-single-token-with-no-spaces-at-all",
            Face, Size, 60f);

        Assert.Single(lines);
        Assert.Contains("a-very-long-single-token", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyStringWrapsToNoLinesRatherThanToOneBlankOne()
    {
        // A blank line is real vertical space: the layout multiplies the line count by the line
        // height and pushes every row below it down by that much.
        Assert.Empty(Parts.Wrap("", Face, Size, 200f));
        Assert.Empty(Parts.Wrap("   ", Face, Size, 200f));
    }

    [Fact]
    public void AColumnWithNoRoomInItProducesOneLineRatherThanOnePerWord()
    {
        // The first draft asserted only that the result was non-empty, and the mutation it named -
        // dropping the maxWidth > 0 guard - still passed, because every word simply landed on its
        // own line. One line per word is not "no room", it is the worst possible answer: the
        // layout would then reserve a note band as tall as the sentence has words.
        Assert.Single(Parts.Wrap("two words", Face, Size, 0f));
        Assert.Single(Parts.Wrap("two words", Face, Size, -10f));
        Assert.Contains("two words", Parts.Wrap("two words", Face, Size, 0f)[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(1, 19.5f)]
    [InlineData(4, 66f)]
    public void ANotesHeightIsWhatTheLayoutWillReserveForIt(int lines, float height)
    {
        // The layout multiplies this and moves every row below by the result, so an off-by-one
        // here is a row drawn over a sentence on exactly the sections that carry long notes.
        // Zero lines is zero height, not one line's worth of air.
        Assert.Equal(height, Parts.NoteHeight(lines), 3);
    }
}
