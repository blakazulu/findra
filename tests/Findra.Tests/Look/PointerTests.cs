using Findra;
using Xunit;

/// <summary>
/// What the pointer looks like over each part of each surface.
///
/// <para>Findra draws every one of its surfaces itself, so nothing about a rectangle tells
/// Windows what it is: until this existed, the capsule, the card, the settings window and the
/// first-run screen all showed the plain arrow, and the capsule in particular reads as scenery
/// rather than as something that can be picked up and moved.</para>
/// </summary>
public class PointerTests
{
    [Fact]
    public void TheCapsuleBodyOffersTheMoveCursorBecauseItCanBeDragged()
    {
        // The capsule has been draggable since it was written and nothing ever said so.
        Assert.Equal(PointerShape.Move, Pointers.OverCapsule);
    }

    [Fact]
    public void TheSearchFieldTakesTheTextCursorAndEverythingElseOnTheCardTheHand()
    {
        Assert.Equal(PointerShape.Text, Pointers.ForCard(SearchTarget.Field));
        Assert.Equal(PointerShape.Text, Pointers.ForCard(SearchTarget.AdvField));
        Assert.Equal(PointerShape.Arrow, Pointers.ForCard(SearchTarget.None));

        foreach (SearchTarget t in Enum.GetValues<SearchTarget>())
        {
            if (t is SearchTarget.None or SearchTarget.Field or SearchTarget.AdvField) continue;
            Assert.Equal(PointerShape.Hand, Pointers.ForCard(t));
        }
    }

    [Fact]
    public void EveryPartOfTheSettingsWindowAndTheFirstScreenThatCanBeClickedTakesTheHand()
    {
        Assert.Equal(PointerShape.Arrow, Pointers.ForPanel(PanelTarget.None));
        foreach (PanelTarget t in Enum.GetValues<PanelTarget>())
            if (t != PanelTarget.None)
                Assert.Equal(PointerShape.Hand, Pointers.ForPanel(t));

        Assert.Equal(PointerShape.Arrow, Pointers.ForFirstRun(FirstRunTarget.None));
        foreach (FirstRunTarget t in Enum.GetValues<FirstRunTarget>())
            if (t != FirstRunTarget.None)
                Assert.Equal(PointerShape.Hand, Pointers.ForFirstRun(t));
    }

    [Fact]
    public void NothingClickableOffersTheMoveCursor()
    {
        // The decision, in one test: the capsule body says "pick me up", and a button, a pill, a
        // row or the field never do - a move cursor over something that answers a click tells
        // the user the wrong thing about what pressing it will do.
        foreach (SearchTarget t in Enum.GetValues<SearchTarget>())
            Assert.NotEqual(PointerShape.Move, Pointers.ForCard(t));
        foreach (PanelTarget t in Enum.GetValues<PanelTarget>())
            Assert.NotEqual(PointerShape.Move, Pointers.ForPanel(t));
        foreach (FirstRunTarget t in Enum.GetValues<FirstRunTarget>())
            Assert.NotEqual(PointerShape.Move, Pointers.ForFirstRun(t));
    }

    [Fact]
    public void ATargetWithNoAnswerIsRefusedRatherThanGivenTheArrow()
    {
        // The defect this guards: a target added to one of the three enums and forgotten here
        // would silently take whatever the default arm returns, and a control that shows the
        // plain arrow is exactly the thing this work exists to remove. Every arm is written out,
        // so an unmapped value throws where a test can see it rather than on a desktop.
        Assert.Throws<ArgumentOutOfRangeException>(() => Pointers.ForCard((SearchTarget)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pointers.ForPanel((PanelTarget)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pointers.ForFirstRun((FirstRunTarget)999));
    }
}
