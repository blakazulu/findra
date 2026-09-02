using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The geometry both new surfaces stand on. Spec §7 chose a section rail over a tall scroller
/// because "a scrolling list inside a scrolling card is hand-drawn hit testing not worth owning",
/// and every test here defends some part of that choice: the pane is fixed, the rail is finite,
/// notes push rows down rather than over them, and a click lands on the thing under it or on
/// nothing at all.
/// </summary>
public class RailTests
{
    /// <summary>A pane with no options and no notes anywhere - the plainest shape possible.</summary>
    private static int[] Plain(int rows) => new int[rows];

    private static (float X, float Y) Centre(SKRect r) => (r.MidX, r.MidY);

    [Fact]
    public void TheRailAndThePaneDoNotOverlap()
    {
        // A rail one pixel too wide puts the pane's first control underneath it, where the rail's
        // own hit test answers first and the control can never be clicked. Widening the rail to
        // fit a longer section title is the edit that does it.
        Assert.True(RailLayout.RailRect().Right <= RailLayout.PaneRect().Left,
            $"rail ends at {RailLayout.RailRect().Right}, pane starts at {RailLayout.PaneRect().Left}");
    }

    [Fact]
    public void EverySectionRowSitsInsideTheRailAndBelowTheOneBeforeIt()
    {
        SKRect rail = RailLayout.RailRect();
        for (int i = 0; i < RailLayout.Sections.Count; i++)
        {
            SKRect row = RailLayout.SectionRect(i);
            Assert.True(rail.Contains(row), $"section {i} escapes the rail");
            if (i > 0)
                Assert.True(row.Top >= RailLayout.SectionRect(i - 1).Bottom,
                    $"section {i} overlaps section {i - 1}");
        }
    }

    [Fact]
    public void AClickOnASectionSelectsThatSectionAndNotItsNeighbour()
    {
        for (int i = 0; i < RailLayout.Sections.Count; i++)
        {
            (float x, float y) = Centre(RailLayout.SectionRect(i));
            PanelHit hit = RailLayout.HitTest(x, y, Plain(0), Plain(0));
            Assert.Equal(PanelTarget.Section, hit.Target);
            Assert.Equal(i, hit.Row);
        }
    }

    [Fact]
    public void AClickBelowTheLastSectionSelectsNothing()
    {
        // The lazy hit test is (int)((y - top) / RowH), which for a click in the empty rail below
        // the five sections answers 5, 6 or 7 - and the caller indexes RailLayout.Sections with it
        // on the very next line.
        SKRect rail = RailLayout.RailRect();
        Assert.Equal(PanelTarget.None, RailLayout.HitTest(rail.MidX, rail.Bottom - 2, Plain(0), Plain(0)).Target);
    }

    [Fact]
    public void AClickInThePaneOnAPlainRowAnswersAsThatRowAndNotAsAnOption()
    {
        // A control with no options must not report option 0: Apply switches on the option index,
        // and a phantom option 0 toggles the first thing in a list the row has not got.
        int[] notes = Plain(4);
        (float x, float y) = Centre(RailLayout.ControlRect(2, notes));
        PanelHit hit = RailLayout.HitTest(x, y, Plain(4), notes);

        Assert.Equal(PanelTarget.Control, hit.Target);
        Assert.Equal(2, hit.Row);
        Assert.Equal(-1, hit.Option);
    }

    [Fact]
    public void AClickOnAnOptionAnswersWithItsOwnIndex()
    {
        int[] counts = [0, 6, 0];           // row 1 holds the six palette swatches
        int[] notes = Plain(3);
        for (int o = 0; o < 6; o++)
        {
            (float x, float y) = Centre(RailLayout.OptionRect(1, o, 6, notes));
            PanelHit hit = RailLayout.HitTest(x, y, counts, notes);
            Assert.Equal(PanelTarget.Option, hit.Target);
            Assert.Equal(1, hit.Row);
            Assert.Equal(o, hit.Option);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public void OptionsInOneRowNeitherOverlapNorEscapeTheRow(int count)
    {
        // Dividing the row's width by the count without subtracting the gaps pushes the last
        // option past the right edge, where its centre is outside the row and the hit test above
        // silently downgrades a swatch click to a row click.
        int[] notes = Plain(1);
        SKRect row = RailLayout.ControlRect(0, notes);
        for (int o = 0; o < count; o++)
        {
            SKRect here = RailLayout.OptionRect(0, o, count, notes);
            Assert.True(here.Left >= row.Left && here.Right <= row.Right, $"option {o} of {count} escapes its row");
            if (o > 0)
                Assert.True(here.Left >= RailLayout.OptionRect(0, o - 1, count, notes).Right,
                    $"option {o} of {count} overlaps option {o - 1}");
        }
    }

    // ---- notes push rows down, they do not draw over them --------------------------------

    [Fact]
    public void ARowWithANoteAboveItIsPushedDownByExactlyWhatThatNoteNeeds()
    {
        // The defect this replaces: a fixed 14px gap with a four-line note drawn into it, straight
        // over the next two rows. The About disclosure is four lines. Counting rows would not see
        // it; measuring does.
        int[] none = [0, 0];
        int[] four = [4, 0];

        float withoutNote = RailLayout.ControlRect(1, none).Top;
        float withNote = RailLayout.ControlRect(1, four).Top;

        Assert.Equal(withoutNote + Parts.NoteHeight(4), withNote, 3);
    }

    [Fact]
    public void ANoteIsDrawnEntirelyBetweenItsOwnRowAndTheNextOne()
    {
        // The property the layout exists to hold, asserted directly rather than through a gap
        // constant somebody could widen without checking.
        int[] notes = [3, 2, 0];
        for (int i = 0; i < 2; i++)
        {
            SKRect note = RailLayout.NoteRect(i, notes);
            Assert.True(note.Top >= RailLayout.ControlRect(i, notes).Bottom, $"note {i} starts inside its own row");
            Assert.True(note.Bottom <= RailLayout.ControlRect(i + 1, notes).Top, $"note {i} runs into row {i + 1}");
            Assert.True(note.Height >= Parts.NoteHeight(notes[i]) - 0.5f, $"note {i} has no room for its lines");
        }
    }

    [Fact]
    public void AClickInTheGapUnderARowIsNotAClickOnTheRowBelowIt()
    {
        // A note band that the hit test does not know about makes every click on an explanatory
        // sentence land on whichever row the arithmetic thinks is there.
        int[] notes = [4, 0];
        SKRect note = RailLayout.NoteRect(0, notes);
        Assert.Equal(PanelTarget.None, RailLayout.HitTest(note.MidX, note.MidY, Plain(2), notes).Target);
    }

    [Fact]
    public void TheFullestSectionStillFitsThePane()
    {
        // Content with Hebrew offered is seven rows, two of which carry a note. Spec §7 rejected a
        // scrolling card, so a section that stops fitting loses a row - it does not make the
        // window taller and it does not scroll.
        int[] fullest = [3, 2, 0, 0, 0, 0, 0];
        Assert.True(RailLayout.SectionFits(fullest),
            $"the fullest section reaches {RailLayout.ControlRect(6, fullest).Bottom} and the pane ends at {RailLayout.PaneRect().Bottom}");
    }

    // ---- the exclusions list ---------------------------------------------------------------

    [Fact]
    public void AClickOnAnExclusionAnswersWithItsOwnIndexAndNotWithAControlRow()
    {
        // The exclusions list is the ONLY scroller in either surface (spec §7) and it sits under
        // two control rows. A hit test that stops at the control rows answers None for every click
        // in it, and the list becomes decoration.
        int[] notes = [0, 2];
        for (int i = 0; i < 4; i++)
        {
            SKRect r = RailLayout.ListRowRect(i);
            PanelHit hit = RailLayout.HitTest(r.Left + 8, r.MidY, Plain(2), notes, listRows: 6);
            Assert.Equal(PanelTarget.ListItem, hit.Target);
            Assert.Equal(i, hit.Row);
        }
    }

    [Fact]
    public void AClickPastTheLastExclusionIsNotAClickOnAnExclusion()
    {
        // Six rows shown, a click where the seventh would be. Dividing the offset by the row
        // height without bounding it hands the caller index 6 for a list of six, and the very next
        // line removes SearchExclusions[6].
        int[] notes = [0, 2];
        SKRect r = RailLayout.ListRowRect(6);
        Assert.Equal(PanelTarget.None, RailLayout.HitTest(r.Left + 8, r.MidY, Plain(2), notes, listRows: 6).Target);
    }

    [Fact]
    public void TheRemoveCrossIsNotTheRowItself()
    {
        // Clicking a row selects it; clicking the cross deletes it. If the whole row answers
        // ListRemove, selecting an exclusion deletes it.
        int[] notes = [0, 2];
        SKRect r = RailLayout.ListRowRect(1);
        Assert.Equal(PanelTarget.ListRemove, RailLayout.HitTest(r.Right - 8, r.MidY, Plain(2), notes, listRows: 6).Target);
        Assert.Equal(PanelTarget.ListItem, RailLayout.HitTest(r.Left + 8, r.MidY, Plain(2), notes, listRows: 6).Target);
    }

    [Fact]
    public void TheListStartsBelowTheRowsAndTheNotesAboveIt()
    {
        // Two control rows sit above the list in "What it searches", and both carry a note - the
        // second saying how many folders are skipped. ListTop is a constant, so this is what
        // checks the constant against what those rows actually measure: raise a note to three
        // lines, or add a row, and the list starts drawing over it.
        int[] notes = [2, 2];
        Assert.True(RailLayout.ListRect().Top >= RailLayout.NoteRect(1, notes).Bottom,
            $"the list starts at {RailLayout.ListRect().Top} and the note above it ends at {RailLayout.NoteRect(1, notes).Bottom}");
        Assert.True(RailLayout.ListRowsThatFit >= 8,
            $"only {RailLayout.ListRowsThatFit} exclusions are visible at once; the default list has 30");
    }

    // ---- the footer --------------------------------------------------------------------------

    [Fact]
    public void TheLabelColumnNarrowsAsARowGetsMoreCrowded()
    {
        // The geometric half of the crowded-row problem. A LabelWidthFor that returns the wide
        // column whatever the count is what gives five transcription presets 56px each, of which
        // Parts.Pill ellipsises all but 44 - five ellipses in a row.
        //
        // Monotonic, and strictly: an implementation that narrows only at four and then stops
        // leaves the five-option row exactly where it was.
        Assert.True(RailLayout.LabelWidthFor(5) < RailLayout.LabelWidthFor(4),
            "a five-option row gets no more room than a four-option one");
        Assert.True(RailLayout.LabelWidthFor(4) < RailLayout.LabelWidthFor(3),
            "a four-option row gets no more room than a three-option one");
        Assert.Equal(RailLayout.LabelW, RailLayout.LabelWidthFor(2));
    }

    [Fact]
    public void ASwatchStaysWideEnoughForTheColoursItDraws()
    {
        // A swatch draws its accent dot at Left+14 and its ink dot at Left+30, so under about
        // 44px the two dots leave the rounded rect they are supposed to sit inside. Six per side
        // is three built-ins plus three somebody wrote; beyond that a single row is the wrong
        // shape for the problem and this is the assertion that says so rather than the pixels
        // saying it quietly.
        int[] notes = Plain(1);
        for (int n = 3; n <= 6; n++)
            for (int o = 0; o < n; o++)
                Assert.True(RailLayout.OptionRect(0, o, n, notes).Width >= 44,
                    $"swatch {o} of {n} is only {RailLayout.OptionRect(0, o, n, notes).Width}px wide");
    }

    [Fact]
    public void TheCloseButtonCannotBeCoveredByAnythingTheSectionDraws()
    {
        // The first draft asserted that Close "answers before anything under it", which is
        // vacuous: CloseRect sits in the footer, below the pane and outside the rail, so nothing
        // is ever under it and the assertion held however the hit test was ordered.
        //
        // The property worth holding is the one that geometry can actually break: shrink Height or
        // FooterH and the pane's last row reaches into the footer, at which point the close pill
        // is drawn over a control and one of the two is unreachable.
        SKRect close = RailLayout.CloseRect();
        Assert.False(close.IntersectsWith(RailLayout.PaneRect()), "the close button overlaps the pane");
        Assert.False(close.IntersectsWith(RailLayout.RailRect()), "the close button overlaps the rail");

        int[] fullest = [3, 2, 0, 0, 0, 0, 0];
        Assert.True(RailLayout.ControlRect(6, fullest).Bottom <= close.Top, "the fullest section reaches the close button");

        (float x, float y) = Centre(close);
        Assert.Equal(PanelTarget.Close, RailLayout.HitTest(x, y, Plain(6), Plain(6)).Target);
    }

    [Fact]
    public void NothingOutsideTheWindowHits()
    {
        Assert.Equal(PanelTarget.None, RailLayout.HitTest(-1, 10, Plain(4), Plain(4)).Target);
        Assert.Equal(PanelTarget.None, RailLayout.HitTest(10, -1, Plain(4), Plain(4)).Target);
        Assert.Equal(PanelTarget.None, RailLayout.HitTest(RailLayout.Width + 1, 10, Plain(4), Plain(4)).Target);
        Assert.Equal(PanelTarget.None, RailLayout.HitTest(10, RailLayout.Height + 1, Plain(4), Plain(4)).Target);
    }

    [Fact]
    public void EverySectionHasATitleSomebodyCanRead()
    {
        foreach (Section s in RailLayout.Sections)
            Assert.False(string.IsNullOrWhiteSpace(RailLayout.Title(s)), $"{s} has no title");
        Assert.Equal(5, RailLayout.Sections.Count);
        Assert.Equal("What it searches", RailLayout.Title(Section.Searches));
    }
}
