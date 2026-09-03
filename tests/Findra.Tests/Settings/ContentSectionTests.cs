using Findra;
using Xunit;

/// <summary>
/// The two controls the Content section was missing, both of which existed everywhere except on
/// a screen: the duty cycle the indexer has always honoured, and a way to say "begin" that reads
/// as an instruction rather than as a preference.
/// </summary>
[Collection("culture")]
public class ContentSectionTests
{
    private static SettingsState State(Config? c = null) =>
        new(c ?? Config.Default) { Section = Section.Content, HebrewOffered = true, Drives = ["C", "D"] };

    private static Control Row(SettingsState s, ControlId id) =>
        SettingsModel.Controls(s).Single(c => c.Id == id);

    private static int RowOf(SettingsState s, ControlId id)
    {
        IReadOnlyList<Control> rows = SettingsModel.Controls(s);
        for (int i = 0; i < rows.Count; i++) if (rows[i].Id == id) return i;
        Assert.Fail($"no control with id {id} in {s.Section}");
        return -1;
    }

    // ---- the indexing power ----------------------------------------------------------------

    [Fact]
    public void IndexingPowerOffersFourLevelsAndMarksTheOneInForce()
    {
        // The setting has been read by the indexer, clamped by the config and written to the
        // index's meta row since it was written; the only piece missing was a row, so the only
        // way to change it was to hand-edit config.json.
        Control row = Row(State(Config.Default with { IndexPower = 75 }), ControlId.IndexPower);

        Assert.Equal(ControlKind.Choice, row.Kind);
        Assert.Equal(4, row.Options.Count);
        Assert.Equal([25, 50, 75, 100], IndexPowerLevels.Presets);
        Assert.Equal([false, false, true, false], row.OptionOn);
    }

    [Fact]
    public void EveryLevelOfferedIsOneTheConfigWillKeep()
    {
        // Config.Load clamps to 10..100 and a pill offering something outside that would be a
        // control that changes to a number the next launch quietly replaces.
        foreach (int p in IndexPowerLevels.Presets)
            Assert.Equal(p, Config.Load($"{{\"indexPower\":{p}}}").IndexPower);
    }

    [Fact]
    public void ChoosingALevelWritesTheNumberAndNotTheIndexOfThePill()
    {
        // The off-by-one this shape invites: writing hit.Option (0..3) into IndexPower, which
        // clamps to 10 and leaves the indexer resting nine tenths of the time.
        SettingsState s = State();
        for (int o = 0; o < IndexPowerLevels.Presets.Count; o++)
        {
            SettingsOutcome outcome = SettingsModel.Apply(s, new PanelHit(PanelTarget.Option, RowOf(s, ControlId.IndexPower), o));
            Assert.Equal(IndexPowerLevels.Presets[o], outcome.State.Config.IndexPower);
        }
    }

    [Fact]
    public void OneListOfLevelNamesRatherThanTwoThatCanDisagree()
    {
        // The rule the five transcription pills already follow: the labels a surface draws come
        // out of the same table the numbers do, so a second copy cannot drift from it.
        Control row = Row(State(), ControlId.IndexPower);
        Assert.Equal([.. IndexPowerLevels.Presets.Select(IndexPowerLevels.ShortName)], row.Options);
    }

    [Fact]
    public void AHandEditedLevelTicksNothingRatherThanTheNearestPill()
    {
        // 10..100 is what the config keeps, so 40 is a legitimate hand-edited value that is not
        // one of the four. Rounding it to a pill would show a number the config does not hold.
        Control row = Row(State(Config.Default with { IndexPower = 40 }), ControlId.IndexPower);
        Assert.DoesNotContain(true, row.OptionOn);
    }

    // ---- starting it -------------------------------------------------------------------------

    [Fact]
    public void StartingItTurnsReadingOnAndAsksForItToBeginNow()
    {
        // Both halves. The config write is what survives a restart; the action is what the person
        // actually sees happen, and without it "start" waits for the next turn of a loop that
        // comes round every second and reads as a button that did nothing.
        SettingsState off = State(Config.Default with { IndexContent = false });
        SettingsOutcome o = SettingsModel.Apply(off, new PanelHit(PanelTarget.Control, RowOf(off, ControlId.StartIndexing), -1));

        Assert.True(o.State.Config.IndexContent);
        Assert.Equal(SettingsAction.StartIndexing, o.Action);
    }

    [Fact]
    public void StartingItAgainWhenItIsAlreadyOnStillAsksForIt()
    {
        // Findra only reads while it is open, so "start" over a session that has been idle is a
        // real request and not a no-op - and a row that answers nothing at all is the defect the
        // click sweep exists to catch.
        SettingsState on = State(Config.Default with { IndexContent = true });
        SettingsOutcome o = SettingsModel.Apply(on, new PanelHit(PanelTarget.Control, RowOf(on, ControlId.StartIndexing), -1));

        Assert.True(o.State.Config.IndexContent);
        Assert.Equal(SettingsAction.StartIndexing, o.Action);
    }

    [Fact]
    public void TheContentSectionStillFitsThePaneInEveryStateItsSentenceCanBeIn()
    {
        // Two rows were added to a section whose first row carries a sentence that changes with
        // the machine, and the longest of the three is on the state a new install is in. Spec §7
        // does not scroll: a section that stops fitting loses a row.
        foreach (Config c in new[]
        {
            Config.Default with { IndexContent = false },
            Config.Default with { IndexContent = true },
            Config.Default with { IndexContent = true, IndexPower = 100 },
        })
            foreach (bool ever in new[] { false, true })
                foreach (bool alive in new[] { false, true })
                {
                    SettingsState s = State(c) with { EverIndexed = ever, IndexerAlive = alive };
                    IReadOnlyList<int> notes = SettingsModel.NoteLines(s, Parts.Face);
                    Assert.True(RailLayout.SectionFits(notes),
                        $"content reaches {RailLayout.NoteRect(notes.Count - 1, notes).Bottom} " +
                        $"and the pane ends at {RailLayout.PaneRect().Bottom} " +
                        $"(content {c.IndexContent}, ever {ever}, alive {alive})");
                }
    }
}
