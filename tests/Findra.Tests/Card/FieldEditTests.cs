using Findra;
using Xunit;

/// <summary>
/// The card's field edits text the way every other text box on Windows does: a selection that
/// typing replaces, Backspace and Delete that take the selection first, Ctrl+A, Shift with the
/// arrows, and a double-click that takes a word. The field is painted rather than a TextBox, so
/// each of those is written here and none of them comes for free.
/// </summary>
public class FieldEditTests
{
    private static FieldEdit.Text T(string text, int caret, int anchor = -1) => new(text, caret, anchor);

    [Fact]
    public void ACaretWithNoAnchorSelectsNothing()
    {
        Assert.False(FieldEdit.HasSelection(T("sunset", 3)));
        Assert.Equal("", FieldEdit.Selected(T("sunset", 3)));
    }

    [Fact]
    public void AnAnchorOnTheCaretIsNoSelectionEither()
    {
        Assert.False(FieldEdit.HasSelection(T("sunset", 3, 3)));
    }

    [Fact]
    public void TheSelectionReadsTheSameWhicheverEndTheCaretIsAt()
    {
        Assert.Equal((1, 4), FieldEdit.Range(T("sunset", 4, 1)));
        Assert.Equal((1, 4), FieldEdit.Range(T("sunset", 1, 4)));
        Assert.Equal("uns", FieldEdit.Selected(T("sunset", 1, 4)));
    }

    [Fact]
    public void SelectAllTakesEveryCharacterWithTheCaretAtTheEnd()
    {
        FieldEdit.Text all = FieldEdit.SelectAll(T("sunset over", 3));
        Assert.Equal(0, all.Anchor);
        Assert.Equal(11, all.Caret);
        Assert.Equal("sunset over", FieldEdit.Selected(all));
    }

    [Fact]
    public void SelectAllOnAnEmptyFieldSelectsNothing()
    {
        Assert.False(FieldEdit.HasSelection(FieldEdit.SelectAll(T("", 0))));
    }

    [Fact]
    public void ExtendingDropsTheAnchorWhereTheCaretWasAndKeepsItThere()
    {
        FieldEdit.Text once = FieldEdit.Extend(T("sunset", 2), 4);
        Assert.Equal((2, 4), FieldEdit.Range(once));
        FieldEdit.Text twice = FieldEdit.Extend(once, 0);
        Assert.Equal(2, twice.Anchor);
        Assert.Equal((0, 2), FieldEdit.Range(twice));
    }

    [Fact]
    public void ExtendingBackOntoTheAnchorLeavesNoSelection()
    {
        FieldEdit.Text back = FieldEdit.Extend(FieldEdit.Extend(T("sunset", 2), 4), 2);
        Assert.False(FieldEdit.HasSelection(back));
        Assert.Equal(-1, back.Anchor);
    }

    [Fact]
    public void MovingWithoutShiftCollapsesTheSelectionToTheEndInTheDirectionOfTravel()
    {
        // Left over a selection lands on its start and Right on its end, without stepping a
        // further character - which is what every text box does and what hands expect.
        Assert.Equal(T("sunset", 1), FieldEdit.Collapse(T("sunset", 4, 1), -1));
        Assert.Equal(T("sunset", 4), FieldEdit.Collapse(T("sunset", 1, 4), +1));
    }

    [Fact]
    public void TypingReplacesTheSelection()
    {
        FieldEdit.Text next = FieldEdit.Insert(T("sunset", 6, 3), "day", max: 200);
        Assert.Equal(T("sunday", 6), next);
    }

    [Fact]
    public void TypingWithNoSelectionInsertsAtTheCaret()
    {
        Assert.Equal(T("sunnyset", 5), FieldEdit.Insert(T("sunset", 3), "ny", max: 200));
    }

    [Fact]
    public void TypingPastTheLimitIsCutAtTheLimit()
    {
        FieldEdit.Text next = FieldEdit.Insert(T("abcd", 4), "efgh", max: 6);
        Assert.Equal("abcdef", next.Value);
        Assert.Equal(6, next.Caret);
    }

    [Fact]
    public void BackspaceAndDeleteTakeTheSelectionRatherThanOneCharacter()
    {
        Assert.Equal(T("sun", 3), FieldEdit.Backspace(T("sunset", 3, 6), word: false));
        Assert.Equal(T("set", 0), FieldEdit.Delete(T("sunset", 0, 3), word: true));
    }

    [Fact]
    public void BackspaceAndDeleteWithoutASelectionTakeACharacterOrAWord()
    {
        Assert.Equal(T("sunse", 5), FieldEdit.Backspace(T("sunset", 6), word: false));
        Assert.Equal(T("lake ", 5), FieldEdit.Backspace(T("lake sunset", 11), word: true));
        Assert.Equal(T("unset", 0), FieldEdit.Delete(T("sunset", 0), word: false));
        Assert.Equal(T("sunset", 0), FieldEdit.Delete(T("lake sunset", 0), word: true));
    }

    [Fact]
    public void BackspaceAtTheStartAndDeleteAtTheEndChangeNothing()
    {
        Assert.Equal(T("sun", 0), FieldEdit.Backspace(T("sun", 0), word: false));
        Assert.Equal(T("sun", 3), FieldEdit.Delete(T("sun", 3), word: false));
    }

    [Fact]
    public void WordBoundariesSkipSpacesThenAWord()
    {
        Assert.Equal(5, FieldEdit.WordLeft("lake sunset", 11));
        Assert.Equal(0, FieldEdit.WordLeft("lake sunset", 5));
        Assert.Equal(5, FieldEdit.WordRight("lake sunset", 0));
        Assert.Equal(11, FieldEdit.WordRight("lake sunset", 5));
    }

    [Fact]
    public void ADoubleClickTakesTheWordUnderIt()
    {
        Assert.Equal((5, 11), FieldEdit.WordAt("lake sunset over", 7));
        Assert.Equal((0, 4), FieldEdit.WordAt("lake sunset", 0));
        // At the end of a word it is still that word, not the space after it.
        Assert.Equal((0, 4), FieldEdit.WordAt("lake sunset", 4));
    }

    [Fact]
    public void ADoubleClickOnARunOfSpacesTakesTheSpaces()
    {
        Assert.Equal((4, 7), FieldEdit.WordAt("lake   sunset", 5));
    }

    [Fact]
    public void ADoubleClickTakesAHebrewWordToo()
    {
        Assert.Equal((0, 4), FieldEdit.WordAt("הסכם שכירות", 2));
    }
}
