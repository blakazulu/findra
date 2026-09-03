using System.Globalization;

using Findra;
using Findra.Startup;   // HelperTaskState
using Xunit;

/// <summary>
/// The settings window's whole mind. Spec §7 accepts that a hand-drawn window forfeits keyboard
/// and screen-reader support; what it does not accept is a control that lies about what it did,
/// and every test here is one of those.
/// </summary>
[Collection("culture")]
public class SettingsModelTests
{
    /// <summary>
    /// The fixture every test here starts from, and it is deliberately NOT `Config.Default`.
    ///
    /// <para>A dragged capsule position is the difference between the sweep running and the sweep
    /// failing on its own headline row. `Config.Default.CapsuleX` and `.CapsuleY` are already
    /// null, so on a default config the "Bring the capsule back" arm produces a `Config` that
    /// compares equal to the one it started from, `SettingsState` compares equal, the action is
    /// `None`, and `EveryDrawnControlDoesSomethingWhenItIsClicked` reports that the row does
    /// nothing - which is true from that starting state and useless as a test.</para>
    ///
    /// <para><b>The fix is the fixture, never the skip list.</b> Widening the sweep's skip list
    /// past `Note` and `Text` is the cheap green, and it disarms the sweep for the five controls
    /// it exists for.</para>
    /// </summary>
    private static SettingsState State(Config? c = null, Section section = Section.Look) =>
        new(c ?? Config.Default with { CapsuleX = 1_400, CapsuleY = 60 })
        { Section = section, Palettes = Palette.BuiltIn, Drives = ["C", "D"] };

    private static Control Row(SettingsState s, ControlId id) =>
        SettingsModel.Controls(s).Single(c => c.Id == id);

    private static int RowOf(SettingsState s, ControlId id)
    {
        IReadOnlyList<Control> rows = SettingsModel.Controls(s);
        for (int i = 0; i < rows.Count; i++) if (rows[i].Id == id) return i;
        Assert.Fail($"no control with id {id} in {s.Section}");
        return -1;
    }

    /// <summary>A click on a row, choosing an option that is NOT already chosen where the row has
    /// options - clicking the chosen one is legitimately a no-op and would make the sweep below
    /// fail for the wrong reason.</summary>
    private static PanelHit ClickOn(SettingsState s, int row)
    {
        Control c = SettingsModel.Controls(s)[row];
        if (c.Options.Count == 0) return new PanelHit(PanelTarget.Control, row, -1);
        for (int o = 0; o < c.OptionOn.Count; o++) if (!c.OptionOn[o]) return new PanelHit(PanelTarget.Option, row, o);
        return new PanelHit(PanelTarget.Option, row, 0);
    }

    // ---- the sweep the rejected draft had no equivalent of --------------------------------

    [Fact]
    public void EveryDrawnControlDoesSomethingWhenItIsClicked()
    {
        // THE test of this task. The first draft drew five controls wired to nothing - the
        // autostart toggle, the hotkey, the capability buttons, "Check now" and the palettes file
        // - and nothing in it could tell, because its only structural test proved the painter and
        // the hit test agreed on how many rows there were.
        //
        // A click is answered if it changes the state or asks the shell for something. Neither is
        // the defect. Note and Text rows are excluded because they are prose.
        foreach (Section section in RailLayout.Sections)
        {
            SettingsState s = State(section: section) with { HebrewOffered = true };
            IReadOnlyList<Control> rows = SettingsModel.Controls(s);

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind is ControlKind.Note or ControlKind.Text) continue;

                SettingsOutcome o = SettingsModel.Apply(s, ClickOn(s, i));
                Assert.True(o.State != s || o.Action != SettingsAction.None,
                    $"{section} row {i} ({rows[i].Id}, '{rows[i].Label}') does nothing when it is clicked");
            }
        }
    }

    [Fact]
    public void NoControlIsDispatchedByItsLabel()
    {
        // The rejected draft matched row.Label.StartsWith("Show") and the literal "Register it".
        // A copy-edit to either silently unwires the control, and for the second one that control
        // is the recovery path for the defect this plan exists to fix. So: every clickable row
        // carries an id, no two rows in a section share one, and none of them is None.
        foreach (Section section in RailLayout.Sections)
        {
            IReadOnlyList<Control> rows = SettingsModel.Controls(State(section: section) with { HebrewOffered = true });
            var seen = new List<ControlId>();

            foreach (Control c in rows)
            {
                if (c.Kind == ControlKind.Note) continue;
                Assert.NotEqual(ControlId.None, c.Id);
                // Capability is the one id that repeats, and Tag is what tells those rows apart.
                if (c.Id != ControlId.Capability) Assert.DoesNotContain(c.Id, seen);
                seen.Add(c.Id);
            }
        }
    }

    // ---- the shape of every section -------------------------------------------------------

    [Fact]
    public void EverySectionFitsTheFixedPaneWithItsNotesInIt()
    {
        // Spec §7 rejected a scrolling card, so a section that stops fitting loses a row; it never
        // makes the window taller and it never scrolls.
        //
        // Counting rows is not enough, and that is the whole reason NoteLines exists. The About
        // disclosure is about 290 characters - four wrapped lines, 66px - and a row count sees a
        // six-row section either way while the pane runs out of room. SectionFits measures.
        foreach (Section section in RailLayout.Sections)
        {
            SettingsState s = State(section: section) with { HebrewOffered = true };
            IReadOnlyList<int> notes = SettingsModel.NoteLines(s, Parts.Face);

            Assert.Equal(SettingsModel.Controls(s).Count, notes.Count);
            Assert.True(RailLayout.SectionFits(notes),
                $"{section} reaches {RailLayout.NoteRect(notes.Count - 1, notes).Bottom} and the pane ends at {RailLayout.PaneRect().Bottom}");
        }
    }

    [Fact]
    public void ARowWithNoNoteReservesNoRoomForOne()
    {
        // NoteLines is what the layout multiplies by the line height. A row that reports one line
        // for an empty note pushes every row below it down by 19.5px for nothing, and the fullest
        // section stops fitting for a reason nobody can see on screen.
        SettingsState s = State(section: Section.Look);
        IReadOnlyList<Control> rows = SettingsModel.Controls(s);
        IReadOnlyList<int> notes = SettingsModel.NoteLines(s, Parts.Face);

        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Note.Length == 0) Assert.Equal(0, notes[i]);
    }

    [Fact]
    public void TheHitTestIsToldAboutExactlyTheRowsThePainterDraws()
    {
        // Two lists describing the same rows is how every click lands one row out on one section
        // and nowhere else - the kind of bug that survives a screenshot review.
        foreach (Section section in RailLayout.Sections)
        {
            SettingsState s = State(section: section) with { HebrewOffered = true };
            IReadOnlyList<Control> rows = SettingsModel.Controls(s);
            IReadOnlyList<int> counts = SettingsModel.OptionCounts(s);

            Assert.Equal(rows.Count, counts.Count);
            for (int i = 0; i < rows.Count; i++) Assert.Equal(rows[i].Options.Count, counts[i]);
        }
    }

    [Fact]
    public void EveryOptionRowStatesWhichOfItsOptionsIsOn()
    {
        // OptionOn and Options are drawn together. A row whose OptionOn is shorter is an
        // IndexOutOfRangeException inside the painter, on one palette, on one section.
        foreach (Section section in RailLayout.Sections)
            foreach (Control c in SettingsModel.Controls(State(section: section) with { HebrewOffered = true }))
                Assert.Equal(c.Options.Count, c.OptionOn.Count);
    }

    // ---- Look -----------------------------------------------------------------------------

    [Theory]
    // showing dark, pick a light palette -> the mode has to move or the click is invisible
    [InlineData(ThemeMode.AlwaysDark, false, ControlId.LightPalette, ThemeMode.AlwaysLight)]
    [InlineData(ThemeMode.FollowWindows, false, ControlId.LightPalette, ThemeMode.AlwaysLight)]
    // showing light, pick a dark palette -> likewise, the other way
    [InlineData(ThemeMode.AlwaysLight, true, ControlId.DarkPalette, ThemeMode.AlwaysDark)]
    [InlineData(ThemeMode.FollowWindows, true, ControlId.DarkPalette, ThemeMode.AlwaysDark)]
    // picking the side already on screen changes nothing about the mode, and in particular does
    // not throw away Follow Windows for somebody who never left it
    [InlineData(ThemeMode.FollowWindows, false, ControlId.DarkPalette, ThemeMode.FollowWindows)]
    [InlineData(ThemeMode.FollowWindows, true, ControlId.LightPalette, ThemeMode.FollowWindows)]
    [InlineData(ThemeMode.AlwaysDark, false, ControlId.DarkPalette, ThemeMode.AlwaysDark)]
    [InlineData(ThemeMode.AlwaysLight, true, ControlId.LightPalette, ThemeMode.AlwaysLight)]
    public void PickingAPaletteFromTheSideYouCannotSeeSwitchesToThatSide(
        ThemeMode before, bool windowsIsLight, ControlId row, ThemeMode after)
    {
        // The defect a person hit: clicking a light swatch while the dark side was on screen wrote
        // the light slot and changed nothing they could see, so the swatch read as broken and they
        // had to go up and move the mode themselves before anything happened. A control that looks
        // like it applies a colour has to apply it.
        //
        // Mode is a row on the same panel, so it visibly moves with the click rather than changing
        // behind their back, and the two-pick design survives: a palette for the side already
        // showing is applied without disturbing Follow Windows, because that click was never
        // silent in the first place.
        SettingsState s = State(Config.Default with { Mode = before }) with { WindowsIsLight = windowsIsLight };

        // Read the name out of the row rather than writing one down: option 2 is Verdigris in the
        // dark list and Porcelain in the light one, and a hard-coded name would be asserting which
        // palettes ship rather than what the click did.
        string picked = Row(s, row).Options[2];

        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Option, RowOf(s, row), 2));

        Assert.Equal(after, o.State.Config.Mode);
        // And the palette itself still landed in its own slot, whichever way the mode went.
        Assert.Equal(picked, row == ControlId.DarkPalette
            ? o.State.Config.DarkPalette
            : o.State.Config.LightPalette);
        // The other slot is untouched: this is still two picks, not one.
        Assert.Equal(row == ControlId.DarkPalette ? Config.Default.LightPalette : Config.Default.DarkPalette,
                     row == ControlId.DarkPalette ? o.State.Config.LightPalette : o.State.Config.DarkPalette);
    }

    [Fact]
    public void ChoosingADarkPaletteLeavesTheLightOneAlone()
    {
        // Spec §7: the user picks one dark and one light, "which is why it is two picks". A model
        // that keeps one palette field cannot follow Windows at all, and the symptom is a light
        // desktop painted in a dark palette.
        SettingsState s = State();
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Option, RowOf(s, ControlId.DarkPalette), 2));

        Assert.Equal("Verdigris", o.State.Config.DarkPalette);
        Assert.Equal(Config.Default.LightPalette, o.State.Config.LightPalette);
    }

    [Fact]
    public void ChoosingAModeThrowsAwayNeitherPick()
    {
        SettingsState s = State(Config.Default with { DarkPalette = "Brass", LightPalette = "Blueprint" });
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Option, RowOf(s, ControlId.Mode), 2));

        Assert.Equal(ThemeMode.AlwaysLight, o.State.Config.Mode);
        Assert.Equal("Brass", o.State.Config.DarkPalette);
        Assert.Equal("Blueprint", o.State.Config.LightPalette);
    }

    [Fact]
    public void EachSwatchRowOffersOnlyPalettesOfItsOwnSide()
    {
        // One list of all six on both rows lets somebody choose Paper as their dark palette, which
        // Theme.Resolve then never selects, because it only looks at DarkPalette when the resolved
        // side is dark. The setting would appear to save and do nothing.
        SettingsState s = State();
        Assert.Equal(new[] { "Mond", "Brass", "Verdigris" }, Row(s, ControlId.DarkPalette).Options);
        Assert.Equal(new[] { "Paper", "Blueprint", "Porcelain" }, Row(s, ControlId.LightPalette).Options);
    }

    [Fact]
    public void APaletteSomebodyWroteThemselvesAppearsBesideTheSixThatShip()
    {
        // %APPDATA%\Findra\palettes.json is the whole public theming contract (spec §7). Reading
        // Palette.BuiltIn here instead of the loaded set makes a hand-written palette work
        // everywhere in the product except in the one place a person would go to choose it.
        var mine = new Palette("Ash", new SkiaSharp.SKColor(0x8A, 0xB4, 0xF8),
                               new SkiaSharp.SKColor(0xE8, 0xE8, 0xE8), new SkiaSharp.SKColor(0x11, 0x11, 0x11), false);
        SettingsState s = State() with { Palettes = [.. Palette.BuiltIn, mine] };

        Assert.Contains("Ash", Row(s, ControlId.DarkPalette).Options);
    }

    [Fact]
    public void ThePalettesFileRowOpensThePalettesFile()
    {
        // One of the five the rejected draft drew and never wired. Spec §7 says users extend
        // palettes.json; a row that names the file and does not open it is a signpost to a path
        // the person then has to type out.
        SettingsState s = State();
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, RowOf(s, ControlId.PalettesFile), -1));

        Assert.Equal(SettingsAction.OpenPalettesFile, o.Action);
        Assert.Equal(Paths.PalettesFile, o.Argument);
    }

    // ---- Opening it: the hotkey, captured and rebound ---------------------------------------

    [Fact]
    public void ClickingTheHotkeyRowStartsListeningForACombination()
    {
        // The row's own note says "Click, then press the combination you want". The rejected draft
        // wrote that sentence and no mechanism: no capture mode, no key handler, and no call to
        // Rebind anywhere. Spec §7 requires a rebind control; this is its first half.
        SettingsState s = State(section: Section.Opening);
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, RowOf(s, ControlId.Hotkey), -1));

        Assert.True(o.State.Capturing);
        Assert.Equal(SettingsAction.CaptureChord, o.Action);
        // And the row says so, because a window silently listening for keys is indistinguishable
        // from one that ignored the click.
        Assert.NotEqual(Row(s, ControlId.Hotkey).Value, Row(o.State, ControlId.Hotkey).Value);
    }

    [Theory]
    [InlineData(true, false, false, false, 0x46u, "Ctrl+F")]
    [InlineData(false, true, false, false, 0x20u, "Alt+Space")]
    [InlineData(true, true, true, false, 0x50u, "Ctrl+Alt+Shift+P")]
    // The Win row is not decoration: without it `win` was false in every row of both theories in
    // this file, so deleting the MOD_WIN line from ChordFrom passed the whole suite - and then
    // pressing Win+F during capture produced no chord at all, which reads as a window that ignored
    // the key. Win+something is a realistic landing spot, because Alt+Space is taken on some
    // machines and the fallback chain has to go somewhere.
    [InlineData(false, false, false, true, 0x46u, "Win+F")]
    public void AChordIsBuiltFromWhatWasActuallyPressedAndReadsBack(bool ctrl, bool alt, bool shift, bool win, uint vk, string want)
    {
        // The round trip is the point: whatever ChordFrom produces, Hotkey.Parse must read back,
        // because Rebind hands it to RegisterHotKey through Hotkey.Parse. A hand-rolled string
        // builder that spells "Control+F" produces a chord that saves and never registers.
        string? chord = SettingsModel.ChordFrom(ctrl, alt, shift, win, vk);
        Assert.Equal(want, chord);
        Assert.NotNull(Hotkey.Parse(chord!));
    }

    [Theory]
    [InlineData(0x10u)]   // Shift
    [InlineData(0x11u)]   // Ctrl
    [InlineData(0x12u)]   // Alt
    [InlineData(0x5Bu)]   // Left Windows
    public void AModifierPressedOnItsOwnIsNotAChord(uint vk)
    {
        // Capture starts the moment the row is clicked, so the first key down is the modifier the
        // person is reaching for. Binding "Ctrl+" there takes the hotkey away before they have
        // finished pressing the combination they wanted, and the way back is the hotkey.
        Assert.Null(SettingsModel.ChordFrom(ctrl: true, alt: false, shift: false, win: false, vk));
        // And with the Windows key held, because 0x5B is the one row where holding it is what a
        // person is actually doing.
        Assert.Null(SettingsModel.ChordFrom(ctrl: false, alt: false, shift: false, win: true, vk));
    }

    [Fact]
    public void AKeyWithNoModifierAtAllIsNotAChord()
    {
        // RegisterHotKey will happily take a bare letter, and it then swallows that key everywhere
        // in Windows - in every text field in every application.
        Assert.Null(SettingsModel.ChordFrom(false, false, false, false, 0x46u));
    }

    [Fact]
    public void AChordTheSystemAcceptsIsSavedAndCaptureStops()
    {
        SettingsState s = State(section: Section.Opening) with { Capturing = true };
        SettingsState after = SettingsModel.Rebind(s, "Ctrl+Alt+F", _ => true);

        Assert.Equal("Ctrl+Alt+F", after.Config.Hotkey);
        Assert.Equal("", after.HotkeyMessage);
        Assert.False(after.Capturing);
    }

    [Fact]
    public void AChordTheSystemRefusesIsNotSavedAndSaysWhichOneWasTaken()
    {
        // The pair above is what gives this teeth: a Rebind that never saves anything passes this
        // one on its own. Saving a chord that would not register leaves the next launch on a
        // hotkey that cannot be pressed and no visible way back - and Alt+Space, the default, is
        // exactly the combination that is taken on some machines (spec §7).
        SettingsState s = State(Config.Default with { Hotkey = "Alt+Space" }, Section.Opening) with { Capturing = true };
        SettingsState after = SettingsModel.Rebind(s, "Ctrl+Shift+P", _ => false);

        Assert.Equal("Alt+Space", after.Config.Hotkey);
        Assert.Contains("Ctrl+Shift+P", after.HotkeyMessage, StringComparison.Ordinal);
        Assert.Contains("taken", after.HotkeyMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(after.Capturing);
    }

    [Fact]
    public void LeavingTheSectionStopsListening()
    {
        // Capture is a mode, and a mode nothing cancels is one the person is stuck in: every key
        // they press afterwards is being read as a hotkey rather than reaching anything else.
        SettingsState s = State(section: Section.Opening) with { Capturing = true };
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Section, 0, -1));

        Assert.False(o.State.Capturing);
    }

    // ---- Opening it: autostart and the helper -------------------------------------------------

    [Fact]
    public void BringingTheCapsuleBackClearsItsPositionAndAsksForItToBeMovedNow()
    {
        // Two halves, and the second is the one that was missing. Clearing the saved position is
        // what survives a restart; the action is what the person sees. The capsule window is
        // built from the config when the shell starts, so without the action an off-screen
        // capsule stays off-screen until the next launch - and being off-screen is the only
        // reason anybody presses this row.
        SettingsState s = State(Config.Default with { CapsuleX = 9_000, CapsuleY = 9_000 }, Section.Opening);
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, RowOf(s, ControlId.ResetCapsule), -1));

        Assert.Null(o.State.Config.CapsuleX);
        Assert.Null(o.State.Config.CapsuleY);
        Assert.Equal(SettingsAction.RecentreCapsule, o.Action);
    }

    [Fact]
    public void EveryOptionLabelFitsThePillItIsDrawnIn()
    {
        // Measured with Parts.Face - the face the painter is handed - and Parts.LabelSize, over
        // every option of every Choice row of every section. A pill that ellipsises its own label
        // is a control whose choices cannot be told apart, and no shot review catches it reliably
        // because "30 min…" and "2 hou…" both look deliberate at a glance.
        //
        // Choice rows only: a Swatch draws colours rather than text, and its options are palette
        // names a stranger's palettes.json can make arbitrarily long. Asserting on those would be
        // a test somebody else's file can break.
        foreach (Section section in RailLayout.Sections)
        {
            SettingsState s = State(section: section) with { HebrewOffered = true };
            IReadOnlyList<Control> rows = SettingsModel.Controls(s);
            IReadOnlyList<int> notes = SettingsModel.NoteLines(s, Parts.Face);

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind != ControlKind.Choice) continue;
                for (int o = 0; o < rows[i].Options.Count; o++)
                {
                    float room = RailLayout.OptionRect(i, o, rows[i].Options.Count, notes).Width - 12;
                    float need = CardText.Measure(rows[i].Options[o], Parts.Face, Parts.LabelSize);
                    Assert.True(need <= room,
                        $"{section}: '{rows[i].Options[o]}' needs {need:F0}px and its pill gives {room:F0}px");
                }
            }
        }
    }

    [Fact]
    public void EveryRowLabelFitsItsOwnColumn()
    {
        // The other direction, and the pair is the point: narrowing LabelWidthFor to make the
        // pills fit breaks the labels, and widening it to make the labels fit breaks the pills.
        // One test alone would be satisfied by moving the column all the way to either end.
        foreach (Section section in RailLayout.Sections)
        {
            SettingsState s = State(section: section) with { HebrewOffered = true };
            foreach (Control c in SettingsModel.Controls(s))
            {
                if (c.Kind == ControlKind.Note) continue;
                float room = RailLayout.LabelWidthFor(c.Options.Count) - 12;
                float need = CardText.Measure(c.Label, Parts.Face, Parts.LabelSize);
                Assert.True(need <= room,
                    $"{section}: the label '{c.Label}' needs {need:F0}px and its column gives {room:F0}px");
            }
        }
    }

    [Fact]
    public void TheAutostartToggleActuallyAsksForTheRegistryValueToChange()
    {
        // One of the five dead controls. Autostart.Set and Clear existed and were called from
        // exactly one place in the whole rejected draft - the first-run handler - so this toggle
        // redrew the same state and checklist step 31 could not pass.
        SettingsState off = State(section: Section.Opening) with { StartsAtLogon = false };
        SettingsOutcome on = SettingsModel.Apply(off, new PanelHit(PanelTarget.Control, RowOf(off, ControlId.Autostart), -1));

        Assert.True(on.State.StartsAtLogon);
        Assert.Equal(SettingsAction.SetAutostart, on.Action);

        SettingsOutcome back = SettingsModel.Apply(on.State, new PanelHit(PanelTarget.Control, RowOf(on.State, ControlId.Autostart), -1));
        Assert.False(back.State.StartsAtLogon);
        Assert.Equal(SettingsAction.ClearAutostart, back.Action);
    }

    [Theory]
    [InlineData(HelperTaskState.NotRegistered, true)]
    [InlineData(HelperTaskState.Unknown, true)]
    [InlineData(HelperTaskState.Registered, false)]
    public void TheHelperCanBeRegisteredFromHereWheneverItIsNotKnownToBeThere(HelperTaskState state, bool offered)
    {
        // Spec §12 risk 4: scheduled-task registration is the one thing that can fail on a
        // stranger's machine in a way Findra cannot fix, so there has to be a way back. Treating
        // Unknown as "fine" leaves a locked-down machine with no path at all - and Unknown is
        // exactly what HelperTask.Query returns there, on purpose.
        SettingsState s = State(section: Section.Opening) with { Helper = state };
        Assert.Equal(offered, Row(s, ControlId.Helper).Kind == ControlKind.Button);
    }

    [Fact]
    public void PressingRegisterItActuallyAsksForTheTaskToBeRegistered()
    {
        // The row's shape was tested in the rejected draft and its click was not, so the recovery
        // path for the defect this plan exists to fix hung on a display string no test touched.
        SettingsState s = State(section: Section.Opening) with { Helper = HelperTaskState.NotRegistered };
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, RowOf(s, ControlId.Helper), -1));

        Assert.Equal(SettingsAction.RegisterHelper, o.Action);
    }

    // ---- What it searches ------------------------------------------------------------------

    [Fact]
    public void NoDriveTickedMeansEveryFixedVolumeAndSaysSo()
    {
        // Config.IndexDrives is documented as "empty means every fixed NTFS volume, which is what
        // almost everyone wants". A surface that renders empty as "none" tells the user their
        // machine is indexing nothing while it indexes everything.
        SettingsState s = State(Config.Default with { IndexDrives = [] }, Section.Searches);
        Control row = Row(s, ControlId.Drives);

        Assert.Equal("All", row.Options[0]);
        Assert.True(row.OptionOn[0]);
        Assert.All(row.OptionOn.Skip(1), on => Assert.False(on));
    }

    [Fact]
    public void TheAddFolderRowAsksForTheOsDialog()
    {
        // Spec §7: "calling the OS dialog for folder picking, which is the one place a native
        // control is genuinely required". The model cannot open it, so it asks.
        SettingsState s = State(section: Section.Searches);
        Assert.Equal(SettingsAction.PickFolder,
                     SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, RowOf(s, ControlId.AddFolder), -1)).Action);
    }

    [Fact]
    public void AFolderAddedToTheSkipListIsOneTheIndexerActuallySkips()
    {
        // The strongest available assertion is the round trip through the predicate the indexer
        // itself calls. An implementation that stores the folder's NAME rather than its path -
        // which is what a picker hands back if nobody looks - excludes every folder anywhere with
        // that name, and the second assertion is what catches it.
        SettingsState s = State(Config.Default with { SearchExclusions = [] }, Section.Searches);
        SettingsState after = SettingsModel.AddExclusion(s, @"D:\Games");

        Assert.True(FileKinds.Excluded(@"D:\Games\steam\app.exe", after.Config.SearchExclusions));
        Assert.False(FileKinds.Excluded(@"C:\Work\Games\report.pdf", after.Config.SearchExclusions));
    }

    [Fact]
    public void TheSameFolderAddedTwiceIsOneEntry()
    {
        SettingsState s = State(Config.Default with { SearchExclusions = [] }, Section.Searches);
        SettingsState after = SettingsModel.AddExclusion(SettingsModel.AddExclusion(s, @"D:\Games"), @"D:\Games");

        Assert.Single(after.Config.SearchExclusions);
    }

    [Fact]
    public void RemovingTheLastExclusionLeavesAnEmptyListRatherThanNothingAtAll()
    {
        // SearchExclusions is a string[] the indexer reads on every file. Null takes the indexer
        // down on the next file it opens, in a different process, minutes later.
        SettingsState s = State(Config.Default with { SearchExclusions = [@"\Windows\"] }, Section.Searches);
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.ListRemove, 0, -1));

        Assert.NotNull(o.State.Config.SearchExclusions);
        Assert.Empty(o.State.Config.SearchExclusions);
    }

    [Fact]
    public void ScrollingTheExclusionsStopsAtTheLastPageRatherThanRunningOffTheEnd()
    {
        string[] many = [.. Enumerable.Range(0, 40).Select(i => $@"\folder-{i.ToString(CultureInfo.InvariantCulture)}\")];
        SettingsState s = State(Config.Default with { SearchExclusions = many }, Section.Searches)
            with { ExclusionScroll = 999 };

        IReadOnlyList<string> shown = SettingsModel.VisibleExclusions(s);
        Assert.NotEmpty(shown);
        Assert.Equal(@"\folder-39\", shown[^1]);
    }

    // ---- Content ---------------------------------------------------------------------------

    [Fact]
    public void TheThreeThingsAPausedIndexCanMeanAreThreeDifferentSentences()
    {
        // Plan 5's constraint: there is exactly ONE switch, and what the interface says is
        // derived. A single sentence for all three states means somebody who never turned content
        // indexing on reads "paused" and goes looking for a resume button that does not exist.
        string neverAsked = SettingsModel.ContentSentence(Config.Default with { IndexContent = false }, false, false);
        string turnedOff = SettingsModel.ContentSentence(Config.Default with { IndexContent = false }, true, false);
        string closed = SettingsModel.ContentSentence(Config.Default with { IndexContent = true }, true, false);

        Assert.NotEqual(neverAsked, turnedOff);
        Assert.NotEqual(turnedOff, closed);
        Assert.NotEqual(neverAsked, closed);
        Assert.All(new[] { neverAsked, turnedOff, closed }, t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }

    [Fact]
    public void ThereIsExactlyOneSwitchForContentIndexing()
    {
        // A second "paused" bit beside this one is two settings that can disagree, and there is no
        // honest sentence for the disagreement. The equality assertion catches any other field
        // moving at the same time.
        SettingsState s = State(Config.Default with { IndexContent = false }, Section.Content);
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, RowOf(s, ControlId.IndexContent), -1));

        Assert.True(o.State.Config.IndexContent);
        Assert.Equal(s.Config with { IndexContent = true }, o.State.Config);
    }

    [Fact]
    public void TheTranscriptionPresetsAreTheOneNumberAndNotASecondSetting()
    {
        SettingsState s = State(Config.Default with { TranscribeMinutes = 5 }, Section.Content);
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Option, RowOf(s, ControlId.Transcribe), 3));

        Assert.Equal(120, o.State.Config.TranscribeMinutes);
    }

    [Fact]
    public void ATypedLimitIsShownAsItselfRatherThanRoundedToAPreset()
    {
        // The presets are names for one number. A model that keeps a preset enum beside it either
        // loses a typed 17 or shows "30 minutes" while transcribing 17.
        SettingsState s = State(Config.Default with { TranscribeMinutes = 17 }, Section.Content);
        Control row = Row(s, ControlId.Transcribe);

        Assert.Equal("17 minutes", row.Value);
        Assert.All(row.OptionOn, on => Assert.False(on));
    }

    [Fact]
    public void EveryCapabilityRowShowsWhatItWouldAddToWhatIsAlreadyInstalled()
    {
        // Spec §6: sizes shown in the UI are MARGINAL. A fixed per-row table makes the total
        // visibly fail to add up, because Speech and Meaning share the e5 pair - so Speech's row
        // has to read differently depending on whether Meaning is there.
        SettingsState bare = State(section: Section.Content) with { Installed = CapabilitySet.None };
        SettingsState withDocs = State(section: Section.Content)
            with { Installed = new CapabilitySet(new HashSet<Capability> { Capability.Meaning }) };

        string a = Speech(bare).Value, b = Speech(withDocs).Value;

        Assert.NotEqual(a, b);
        Assert.Equal(Sizes.Human(Capabilities.MarginalBytes(Capability.Speech, [])), a);
        Assert.Equal(Sizes.Human(Capabilities.MarginalBytes(Capability.Speech, [Capability.Meaning])), b);

        static Control Speech(SettingsState s) =>
            SettingsModel.Controls(s).Single(c => c.Id == ControlId.Capability && c.Tag == (int)Capability.Speech);
    }

    [Fact]
    public void PressingACapabilityRowAsksForThatCapabilityAndNotAnother()
    {
        // The fourth dead control. The rows were drawn as buttons carrying a price and no arm
        // installed anything, so after first run the only route to a capability was
        // `findra --models install` - which a person in the settings window will not find.
        SettingsState s = State(section: Section.Content) with { HebrewOffered = true };
        int row = SettingsModel.Controls(s).ToList()
            .FindIndex(c => c.Id == ControlId.Capability && c.Tag == (int)Capability.Photos);

        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, row, -1));

        Assert.Equal(SettingsAction.InstallCapability, o.Action);
        Assert.Equal(nameof(Capability.Photos), o.Argument);
    }

    [Fact]
    public void AnInstalledCapabilityIsNotOfferedAgain()
    {
        // Pairs with the test above so neither can be satisfied by a constant. An installed
        // capability is a Text row, which the sweep skips and the painter draws flat.
        SettingsState s = State(section: Section.Content)
            with { Installed = new CapabilitySet(new HashSet<Capability> { Capability.Photos }) };

        Control photos = SettingsModel.Controls(s).Single(c => c.Id == ControlId.Capability && c.Tag == (int)Capability.Photos);
        Assert.Equal(ControlKind.Text, photos.Kind);
    }

    [Fact]
    public void HebrewIsNotOnTheScreenWhereTheMachineHasNoHebrew()
    {
        SettingsState without = State(section: Section.Content) with { HebrewOffered = false };
        SettingsState with = State(section: Section.Content) with { HebrewOffered = true };

        Assert.DoesNotContain(SettingsModel.Controls(without),
            c => c.Id == ControlId.Capability && c.Tag == (int)Capability.Hebrew);
        Assert.Contains(SettingsModel.Controls(with),
            c => c.Id == ControlId.Capability && c.Tag == (int)Capability.Hebrew);
    }

    // ---- About ------------------------------------------------------------------------------

    [Theory]
    [InlineData("winget", "winget upgrade blakazulu.Findra")]
    [InlineData("installer", "releases")]
    [InlineData("source", "releases")]
    public void TheAboutLineNamesTheActionThatMatchesHowThisCopyWasInstalled(string source, string expect)
    {
        // Spec §9b. Telling somebody who built from source to run winget upgrade is advice they
        // cannot act on, and it is what a single fixed sentence produces.
        string line = SettingsModel.AboutUpdateLine("1.2.0", UpdateState.Available, "1.3.0", source);
        Assert.Contains(expect, line, StringComparison.Ordinal);
        Assert.Contains("1.3.0", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpToDateBuildIsNotOfferedAnUpgradeCommand()
    {
        // The negative alone is satisfied by an empty string, and by a switch collapsed to one
        // sentence. The positive half says the line still tells somebody something.
        string line = SettingsModel.AboutUpdateLine("1.3.0", UpdateState.Current, "1.3.0", "winget");
        Assert.DoesNotContain("winget upgrade", line, StringComparison.Ordinal);
        Assert.Contains("1.3.0", line, StringComparison.Ordinal);
        Assert.Contains("newest", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFourThingsTheAboutLineCanSayAreFourDifferentSentences()
    {
        // Three of the five arms of AboutUpdateLine had no test at all, so the whole switch could
        // have collapsed into one sentence with only the Available theory noticing. Every state a
        // person can land on has to read differently from every other, or the line is decoration.
        string[] lines =
        [
            SettingsModel.AboutUpdateLine("1.2.0", UpdateState.Available, "1.3.0", "winget"),
            SettingsModel.AboutUpdateLine("1.3.0", UpdateState.Current, "1.3.0", "winget"),
            SettingsModel.AboutUpdateLine("1.3.0", UpdateState.Disabled, null, "winget"),
            SettingsModel.AboutUpdateLine("1.3.0", UpdateState.NotDue, null, "winget"),
        ];

        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.Equal(lines.Length, lines.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheUpdateToggleCarriesTheDisclosureAndNotJustASwitch()
    {
        // PRIVACY.md and spec §9b both promise this is written down rather than buried. The note
        // beside the switch is where it is written down inside the product.
        Control row = Row(State(section: Section.About), ControlId.CheckUpdates);

        Assert.Contains("GitHub", row.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("24 hours", row.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anonymous", row.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckNowAsksForACheckRatherThanRedrawingTheSameSentence()
    {
        // The fifth dead control. The tray has a working "Check for updates"; the settings window
        // drew one that did nothing, which is worse than not drawing it at all.
        SettingsState s = State(section: Section.About);
        Assert.Equal(SettingsAction.CheckNow,
                     SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, RowOf(s, ControlId.CheckNow), -1)).Action);
    }

    [Fact]
    public async Task TurningTheCheckOffHereMeansNoRequestIsMadeEvenWhenSomebodyForcesOne()
    {
        // Off means off (spec §9b, PRIVACY.md). The fetch below fails the test if it is ever
        // called, and force: true is the tray's own "Check for updates" path.
        SettingsState s = State(section: Section.About);
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, RowOf(s, ControlId.CheckUpdates), -1));

        Assert.False(o.State.Config.CheckForUpdates);

        UpdateResult r = await UpdateCheck.CheckAsync(
            o.State.Config,
            _ => throw new InvalidOperationException("a request was made after the check was turned off"),
            DateTime.UtcNow, CancellationToken.None, force: true);

        Assert.Equal(UpdateState.Disabled, r.State);
    }

    [Fact]
    public void EveryStringInEverySectionReadsTheSameOnEveryMachine()
    {
        // InvariantGlobalization is false, so a bare {n:N0} renders "9.000" under de-DE. The
        // rejected draft compared only Value, on only the Content section - and the single
        // culture-sensitive format in the whole file is a COUNT, in a NOTE, in Searches. So:
        // every section, and Note as well as Value.
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            string[] neutral = [.. Everything()];
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string[] german = [.. Everything()];
            Assert.Equal(neutral, german);
        }
        finally { CultureInfo.CurrentCulture = before; }

        static IEnumerable<string> Everything()
        {
            // FOUR digits, deliberately. The only culture-sensitive format in SettingsModel is the
            // exclusion COUNT in N0, and the default list is about thirty-eight entries - which
            // renders "38" under de-DE exactly as under the invariant culture. A group separator
            // needs four digits, so a fixture of the real defaults leaves this test green whether
            // the Fixed argument is there or not.
            string[] many = [.. Enumerable.Range(0, 1_200).Select(i => $@"older-{i.ToString(CultureInfo.InvariantCulture)}\")];

            foreach (Section section in RailLayout.Sections)
            {
                var s = new SettingsState(Config.Default with { SearchExclusions = many })
                { Section = section, Palettes = Palette.BuiltIn, Drives = ["C", "D"], HebrewOffered = true };
                foreach (Control c in SettingsModel.Controls(s)) { yield return c.Value; yield return c.Note; }
            }
        }
    }

    [Theory]
    [InlineData(Section.About, ControlId.CheckNow)]
    [InlineData(Section.Opening, ControlId.Helper)]
    [InlineData(Section.Content, ControlId.StartIndexing)]
    [InlineData(Section.Content, ControlId.Capability)]
    public void ARowAlreadyWaitingOnItsOwnWorkAnswersNothing(Section section, ControlId id)
    {
        // Each of these four starts something slow and none of them used to say so: the update
        // check makes a request, "Register it" raises a prompt and waits up to a minute on
        // schtasks, a capability row downloads hundreds of megabytes, "Start now" wakes a child.
        // Two clicks meant the work twice - two stacked UAC prompts, or a second fetch that then
        // collides with the first one's part file on a FileShare.None handle.
        SettingsState s = State(Config.Default with { IndexContent = false }, section);
        int row = RowOf(s, id);
        Assert.True(row >= 0, $"{id} is not a row in {section}");

        // The first click asks for the work.
        SettingsOutcome first = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, row, -1));
        Assert.NotEqual(SettingsAction.None, first.Action);

        // The second, while it is still running, asks for nothing.
        SettingsState waiting = s with { Busy = new HashSet<ControlId> { id } };
        SettingsOutcome second = SettingsModel.Apply(waiting, new PanelHit(PanelTarget.Control, RowOf(waiting, id), -1));
        Assert.Equal(SettingsAction.None, second.Action);
        Assert.Same(waiting, second.State);
    }

    [Theory]
    [InlineData(Section.About, ControlId.CheckNow)]
    [InlineData(Section.Opening, ControlId.Helper)]
    [InlineData(Section.Content, ControlId.StartIndexing)]
    [InlineData(Section.Content, ControlId.Capability)]
    public void AWaitingRowSaysSoRatherThanLookingUntouched(Section section, ControlId id)
    {
        // Refusing the click is half of it. A control that refuses AND looks exactly as it did is
        // a control somebody clicks again harder, so the value beside the label has to change.
        SettingsState idle = State(Config.Default with { IndexContent = false }, section);
        SettingsState busy = idle with { Busy = new HashSet<ControlId> { id } };

        // First, not Single: there is one Capability row per capability, so Single throws before
        // it can assert anything.
        Control was = SettingsModel.Controls(idle).First(c => c.Id == id);
        Control now = SettingsModel.Controls(busy).First(c => c.Id == id);

        Assert.NotEqual(was.Value, now.Value);
        Assert.EndsWith("...", now.Value, StringComparison.Ordinal);

        // And nothing else on the panel changes its mind because one row is busy. Compared by
        // POSITION rather than by looking each row up by its id: several rows share an id (there
        // is one Capability row per capability) and a lookup that matched more than one would
        // throw rather than assert. Position is also the stronger claim - it catches a row that
        // moved as well as one that changed.
        IReadOnlyList<Control> before = SettingsModel.Controls(idle);
        IReadOnlyList<Control> after = SettingsModel.Controls(busy);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Id, after[i].Id);
            if (before[i].Id != id) Assert.Equal(before[i].Value, after[i].Value);
        }
    }

    [Fact]
    public void TheContentSentenceChangesWhenTheIndexerStarts()
    {
        // The window took EverIndexed and IndexerAlive when it opened and never asked again, so
        // pressing "Start now" changed nothing anybody could see and the report was that nothing
        // had happened. The indexer HAD started; this surface simply had no way to hear about it.
        // The sentence has to be a function of the state, so that pushing a new state changes it.
        SettingsState before = State(Config.Default with { IndexContent = true }, Section.Content)
            with { EverIndexed = false, IndexerAlive = false };
        SettingsState after = before with { IndexerAlive = true };

        string was = Row(before, ControlId.IndexContent).Note;
        string now = Row(after, ControlId.IndexContent).Note;

        Assert.NotEqual(was, now);
        Assert.NotEqual("", now);
    }

    [Fact]
    public void TheContentSentenceReportsHowFarTheIndexHasGotAndNotJustThatItIsOn()
    {
        // The half the two booleans could not say. With reading on and a live indexer, the note
        // was "On. Findra reads inside files while it is running." whether the queue held 1,773
        // files or nothing at all - one string for every state a working indexer can be in.
        //
        // So on a machine that was ALREADY reading, pressing "Start now" could not change anything
        // on the screen, and it was pressed three times by somebody watching a sentence that had
        // nowhere to move. The button was doing its job. This sentence was the part with nothing
        // to say, and it is the same question the card's footer, the capsule, --searchprobe and
        // --searchindex all answer from IndexStatus.Line - so it says that, rather than being a
        // fifth answer.
        SettingsState on = State(Config.Default with { IndexContent = true }, Section.Content)
            with { EverIndexed = true, IndexerAlive = true };

        string busy = Row(on with { Progress = IndexStatus.Line(true, "indexing", 1_773, 200, true, false) },
                          ControlId.IndexContent).Note;
        string done = Row(on with { Progress = IndexStatus.Line(true, "idle", 0, 1_973, true, false) },
                          ControlId.IndexContent).Note;

        Assert.NotEqual(busy, done);
        Assert.Contains("1,773", busy, StringComparison.Ordinal);
        Assert.Contains("200", busy, StringComparison.Ordinal);
        Assert.Contains("1,973", done, StringComparison.Ordinal);

        // Still prose: a capital to open on and a stop before the sentence about the index staying
        // on this machine, which follows it in the same note.
        Assert.StartsWith("Indexing", busy, StringComparison.Ordinal);
        Assert.Contains("done.", busy, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsWindowOpenedBetweenTwoReadingsStillSaysSomething()
    {
        // Progress is empty for the second between the window opening and the status pump coming
        // round, and a row that went blank there would be a worse version of the defect above.
        string gap = Row(State(Config.Default with { IndexContent = true }, Section.Content)
                         with { EverIndexed = true, IndexerAlive = true, Progress = "" },
                         ControlId.IndexContent).Note;

        Assert.NotEqual("", gap);
        Assert.DoesNotContain("..", gap, StringComparison.Ordinal);
    }
}
