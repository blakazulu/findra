using System.Globalization;

using Findra;
using Findra.Startup;   // HelperTaskState
using SkiaSharp;        // SKRect, for the layout assertions
using Xunit;

/// <summary>
/// Spec §6's first screen. What it can get wrong is mostly invisible in a screenshot: a preset
/// tile that stays lit while something else downloads, a size that is not the marginal one, a
/// "Not now" that comes back tomorrow, and a switch that latches on and cannot be turned off.
/// </summary>
[Collection("culture")]
public class FirstRunTests
{
    private static FirstRunState State(params Capability[] chosen) =>
        new() { Chosen = Capabilities.Close(chosen), HebrewOffered = true };

    private static FirstRunRow RowFor(FirstRunState s, Capability c) =>
        FirstRun.Rows(s).First(r => r.Capability == c);

    private static int IndexOf(FirstRunState s, Capability c) =>
        FirstRun.Rows(s).ToList().FindIndex(r => r.Capability == c);

    // ---- everything you can click does something -------------------------------------------

    /// <summary>
    /// Did this click change anything a person would see?
    ///
    /// <para>Compared by CONTENT, and that is the whole of it. <c>FirstRunState</c> is a record
    /// whose <c>Chosen</c> is an <c>IReadOnlySet</c>, and record equality uses
    /// <c>EqualityComparer&lt;T&gt;.Default</c> - which for a <c>HashSet</c> is reference
    /// equality. Every preset and row arm returns <c>s with { Chosen = a fresh HashSet }</c>, so
    /// <c>Assert.NotEqual(s, after)</c> is satisfied by the ALLOCATION whatever is in it. That
    /// would still catch a click falling through to <c>default: return s;</c>, which is the dead
    /// control shape, but not an arm that hands back a fresh copy of the same selection.</para>
    /// </summary>
    private static bool Differs(FirstRunState a, FirstRunState b) =>
        !a.Chosen.SetEquals(b.Chosen)
        || a.ContentOn != b.ContentOn || a.CheckUpdates != b.CheckUpdates
        || a.StartAtLogon != b.StartAtLogon || a.Stage != b.Stage || a.Problem != b.Problem;

    [Fact]
    public void EveryThingYouCanTouchOnThisScreenDoesSomething()
    {
        // The same sweep the settings model carries, for the same reason. The rejected draft's
        // settings window had five controls drawn and dead, so the shape is asserted here too
        // rather than assumed.
        //
        // Swept from a selection that is NOT any of the three presets, and that is required
        // rather than incidental: from an empty screen "Just names" is already the answer, so
        // its arm legitimately changes nothing and a sweep that started there would be asserting
        // that an idempotent choice is a dead control. One capability ticked is the smallest
        // state from which all three preset tiles, all four capability rows and all three
        // switches have real work to do.
        //
        // Not swept: "Not now" and "Get these". FirstRun.Apply has no arm for either by design -
        // they are answered by the window's Answered event - so they have the same "covered only
        // by the checklist" status as the settings window's host implementations. Task 7 says so
        // about its eight; this says so about these two.
        FirstRunState s = State(Capability.Photos);

        for (int i = 0; i < FirstRun.PresetTitles.Count; i++)
            Assert.True(Differs(s, FirstRun.Apply(s, new FirstRunHit(FirstRunTarget.Preset, i))),
                $"preset {i} changes nothing");

        IReadOnlyList<FirstRunRow> rows = FirstRun.Rows(s);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Free) continue;      // the documents row is prose: it has no download
            Assert.True(Differs(s, FirstRun.Apply(s, new FirstRunHit(FirstRunTarget.Row, i))),
                $"row {i} ({rows[i].Title}) changes nothing");
        }

        foreach (FirstRunTarget t in new[] { FirstRunTarget.Content, FirstRunTarget.Updates, FirstRunTarget.Autostart })
            Assert.True(Differs(s, FirstRun.Apply(s, new FirstRunHit(t, -1))), $"{t} changes nothing");
    }

    // ---- the presets --------------------------------------------------------------------

    [Fact]
    public void ThreePresetsAcrossTheTopAndOneClickDecidesIt()
    {
        Assert.Equal(3, FirstRun.PresetTitles.Count);

        FirstRunState after = FirstRun.Apply(State(), new FirstRunHit(FirstRunTarget.Preset, 1));
        Assert.Equal(Presets.Recommended.OrderBy(c => (int)c), after.Chosen.OrderBy(c => (int)c));
        Assert.Equal(Preset.Recommended, Presets.Match(after.Chosen));
    }

    [Fact]
    public void JustNamesCostsNothingAndEverythingIsTheNumberEverySurfaceQuotes()
    {
        Assert.Equal(Sizes.Human(0), FirstRun.PresetSize(Preset.JustNames));
        Assert.Equal(Sizes.Human(Capabilities.TotalBytes(Capabilities.All)), FirstRun.PresetSize(Preset.Everything));
    }

    [Fact]
    public void JustNamesTurnsContentIndexingBackOffAfterAnotherPresetTurnedItOn()
    {
        // Somebody tries Recommended, reads what it costs, and settles on Just names. A latched
        // "ContentOn || index > 0" leaves content indexing ON: every drive walked, every document
        // opened, for hours, on a first run - against spec §6, against PRIVACY.md's published
        // promise, and against the one change Plan 5 leads with.
        //
        // Choosing nothing is the affirmative act here, which is what makes this arm different
        // from the Row arm, where ticking a capability is an affirmative act with a download.
        FirstRunState explored = FirstRun.Apply(State(), new FirstRunHit(FirstRunTarget.Preset, 1));
        Assert.True(explored.ContentOn);

        FirstRunState settled = FirstRun.Apply(explored, new FirstRunHit(FirstRunTarget.Preset, 0));

        Assert.False(settled.ContentOn);
        Assert.Empty(settled.Chosen);
        Assert.False(FirstRun.Outcome(settled, Config.Default).IndexContent);
    }

    [Fact]
    public void TouchingAnyRowMovesThePresetToCustom()
    {
        // The failure this catches is a screen that says "Recommended, 900 MB" at the top while
        // fetching 1.4 GB. Spec §6 makes it explicit: "Touching any row moves the preset to Custom."
        FirstRunState recommended = FirstRun.Apply(State(), new FirstRunHit(FirstRunTarget.Preset, 1));
        FirstRunState after = FirstRun.Apply(recommended, new FirstRunHit(FirstRunTarget.Row, IndexOf(recommended, Capability.Speech)));

        Assert.Equal(Preset.Custom, Presets.Match(after.Chosen));
    }

    [Fact]
    public void TickingSpeechTakesTheDocumentModelsItCannotWorkWithout()
    {
        FirstRunState s = State();
        FirstRunState after = FirstRun.Apply(s, new FirstRunHit(FirstRunTarget.Row, IndexOf(s, Capability.Speech)));

        Assert.Contains(Capability.Speech, after.Chosen);
        Assert.Contains(Capability.Meaning, after.Chosen);
    }

    [Fact]
    public void UntickingSpeechUnticksTheHebrewSecondPassThatDependsOnIt()
    {
        // A naive Remove leaves a selection asking for the 1.5 GB fine-tune with no general model
        // to detect language with - 1.5 GB that installs and does nothing.
        FirstRunState s = State(Capability.Hebrew);
        FirstRunState after = FirstRun.Apply(s, new FirstRunHit(FirstRunTarget.Row, IndexOf(s, Capability.Speech)));

        Assert.DoesNotContain(Capability.Hebrew, after.Chosen);
        Assert.DoesNotContain(Capability.Speech, after.Chosen);
    }

    // ---- the rows ------------------------------------------------------------------------

    [Fact]
    public void HebrewSitsUnderSpeechAndIsIndentedUnderIt()
    {
        // Hebrew is a SECOND PASS, never an alternative (spec §6). A flat list in enum order reads
        // as a choice between two speech models, which is the wrong idea about what it does.
        IReadOnlyList<FirstRunRow> rows = FirstRun.Rows(State());
        int speech = rows.ToList().FindIndex(r => r.Capability == Capability.Speech);
        int hebrew = rows.ToList().FindIndex(r => r.Capability == Capability.Hebrew);

        Assert.Equal(speech + 1, hebrew);
        Assert.True(rows[hebrew].Indented);
        Assert.False(rows[speech].Indented);
    }

    [Fact]
    public void HebrewIsNotOnTheScreenAtAllWhereTheMachineHasNoHebrew()
    {
        // Not shown-but-disabled: a 1.5 GB row is a decision, and somebody with a Thai machine
        // should not have to make it.
        Assert.DoesNotContain(FirstRun.Rows(new FirstRunState { HebrewOffered = false }),
                              r => r.Capability == Capability.Hebrew);
    }

    [Fact]
    public void TheDocumentsRowSaysFreeAndSaysWhichKindOfFree()
    {
        // "Free" here means free of CHARGE, not free of consent: words in documents need no model
        // and no download, and they still do not run until content indexing is turned on. A row
        // that says only "free" promises something the product does not do.
        FirstRunRow free = FirstRun.Rows(State()).Single(r => r.Free);

        Assert.Null(free.Capability);
        Assert.Contains("free", free.Size, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inside", free.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRowsSizeIsWhatItAddsToWhatIsAlreadyTicked()
    {
        // Spec §6: "Every size shown in the UI is the MARGINAL cost given what is already
        // selected. A fixed per-row number would make the total visibly fail to add up."
        string alone = RowFor(State(), Capability.Speech).Size;
        string withDocs = RowFor(State(Capability.Meaning), Capability.Speech).Size;

        Assert.NotEqual(alone, withDocs);
        Assert.Equal(Sizes.Human(Capabilities.MarginalBytes(Capability.Speech, [])), alone);
        Assert.Equal(Sizes.Human(Capabilities.MarginalBytes(Capability.Speech, [Capability.Meaning])), withDocs);
    }

    [Fact]
    public void ATickedRowCostsNothingMoreToKeep()
    {
        Assert.Equal(Sizes.Human(0), RowFor(State(Capability.Photos), Capability.Photos).Size);
    }

    [Fact]
    public void TheTotalCountsAModelSharedByTwoCapabilitiesOnce()
    {
        // Speech closes over Meaning, so choosing both is the same download as choosing Speech. A
        // screen that sums per-capability totals shows a bigger number than it fetches, and the
        // arithmetic is visibly wrong to anyone who adds the rows up.
        Assert.Equal(FirstRun.TotalBytes(State(Capability.Speech)),
                     FirstRun.TotalBytes(State(Capability.Speech, Capability.Meaning)));
    }

    // ---- the three switches --------------------------------------------------------------

    [Fact]
    public void TickingACapabilityTurnsTheContentSwitchOnWhereThePersonCanSeeIt()
    {
        // Models that download and never read anything is the failure on one side; content
        // indexing turning itself on invisibly is the failure on the other. The switch flips in
        // the state, so it flips on screen, and the person sees what they agreed to.
        FirstRunState s = new() { ContentOn = false, HebrewOffered = true };
        Assert.True(FirstRun.Apply(s, new FirstRunHit(FirstRunTarget.Row, IndexOf(s, Capability.Photos))).ContentOn);
    }

    [Fact]
    public void TurningTheContentSwitchOffWithCapabilitiesChosenSaysWhatWillActuallyHappen()
    {
        FirstRunState s = State(Capability.Photos) with { ContentOn = false };
        string summary = FirstRun.Summary(s);

        Assert.Contains("download", summary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(FirstRun.Summary(s with { ContentOn = true }), summary);
    }

    [Fact]
    public void TheUpdateCheckIsDisclosedOnThisScreen()
    {
        // Spec §9b: "on by default, and disclosed on the first-run screen beside the model
        // downloads". PRIVACY.md repeats the promise. This is the sentence both refer to.
        Assert.Contains("GitHub", FirstRun.Disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anonymous", FirstRun.Disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("24 hours", FirstRun.Disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never installs", FirstRun.Disclosure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurningTheCheckOffHereMeansTheRequestIsNeverMade()
    {
        // Off means off, from the very first screen. The fetch below fails the test if it is ever
        // called, and force: true is the strongest path there is.
        FirstRunState off = FirstRun.Apply(new FirstRunState { CheckUpdates = true },
                                           new FirstRunHit(FirstRunTarget.Updates, -1));
        Config config = FirstRun.Outcome(off, Config.Default);

        Assert.False(config.CheckForUpdates);

        UpdateResult r = await UpdateCheck.CheckAsync(
            config, _ => throw new InvalidOperationException("a request was made after it was turned off"),
            DateTime.UtcNow, CancellationToken.None, force: true);

        Assert.Equal(UpdateState.Disabled, r.State);
    }

    // ---- the outcome ------------------------------------------------------------------------

    [Fact]
    public void NotNowIsACompleteAnswerAndTheScreenDoesNotComeBack()
    {
        // Spec §6: content indexing is off by default, so "Not now" is an answer rather than a
        // deferral - names alone are the fastest part of the product. A FirstRunDone written only
        // on the download path brings this screen back at every launch for everybody who said no.
        Config config = FirstRun.Outcome(new FirstRunState(), Config.Default);

        Assert.True(config.FirstRunDone);
        Assert.False(config.IndexContent);
    }

    [Fact]
    public void TheScreenIsAnsweredWhenThePersonAnswersItAndNotWhenTheDownloadFinishes()
    {
        // 2.9 GB takes a while and a laptop lid closes. If FirstRunDone waits for the last byte,
        // the whole screen comes back after a reboot and the person answers it twice.
        FirstRunState mid = State(Capability.Photos) with
        {
            Stage = FirstRunStage.Downloading,
            Downloads = [new CapabilityProgress(Capability.Photos, 100, 660_000_000)],
        };

        Assert.True(FirstRun.Outcome(mid, Config.Default).FirstRunDone);
    }

    [Theory]
    [InlineData(HelperTaskState.NotRegistered, true)]
    [InlineData(HelperTaskState.Unknown, true)]
    [InlineData(HelperTaskState.Registered, false)]
    public void TheOneElevatedThingIsRegisteredWhateverElseWasChosen(HelperTaskState state, bool needed)
    {
        // Searching by name is the part that is always on, and it does not work at all without the
        // scheduled task - which nothing in the tree registered before this task. Treating Unknown
        // as "already there" is how a machine ends up with a permanently empty name index and one
        // log line nobody reads.
        Assert.Equal(needed, FirstRun.NeedsHelperRegistration(state));
    }

    // ---- the second act ----------------------------------------------------------------------

    [Fact]
    public void ProgressIsCountedPerCapabilityAndNotAsOneBar()
    {
        // "8.4 GB of 2.9 GB" is what one aggregate bar looks like when a resume is counted twice.
        // Per capability, the person can also see WHICH download is the slow one.
        FirstRunState s = State(Capability.Photos, Capability.Speech) with
        {
            Stage = FirstRunStage.Downloading,
            Downloads =
            [
                new CapabilityProgress(Capability.Photos, 660_000_000, 660_000_000),
                new CapabilityProgress(Capability.Speech, 100_000_000, 818_000_000),
            ],
        };

        string summary = FirstRun.Summary(s);
        Assert.Contains(Capabilities.Title(Capability.Speech), summary, StringComparison.Ordinal);
        Assert.Contains("1 of 2", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AFinishedDownloadNamesNoCapabilityAtAll()
    {
        // FirstOrDefault over a sequence of STRUCTS returns default(CapabilityProgress) when
        // nothing matches, and assigning that to CapabilityProgress? gives a NON-null nullable -
        // so `current is { } c` is always true and the finished screen reads
        // "2 of 2 done - Photos" for ever, naming whichever capability is enum value zero.
        FirstRunState s = State(Capability.Photos, Capability.Speech) with
        {
            Stage = FirstRunStage.Downloading,
            Downloads =
            [
                new CapabilityProgress(Capability.Photos, 660_000_000, 660_000_000),
                new CapabilityProgress(Capability.Speech, 818_000_000, 818_000_000),
            ],
        };

        string summary = FirstRun.Summary(s);
        Assert.Contains("2 of 2", summary, StringComparison.Ordinal);
        foreach (Capability c in Capabilities.All)
            Assert.DoesNotContain(Capabilities.Title(c), summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAlreadyOnDiskCountsAsDoneRatherThanAsNothing()
    {
        // A resumed or already-present file reports Got == Total immediately. Counting only what
        // this run transferred shows "0 of 2 done" on a machine where one capability is complete.
        FirstRunState s = State(Capability.Photos) with
        {
            Stage = FirstRunStage.Downloading,
            Downloads = [new CapabilityProgress(Capability.Photos, 660_000_000, 660_000_000)],
        };

        Assert.Contains("1 of 1", FirstRun.Summary(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AFinishedRunStopsPromisingThatItCarriesOnInTheTray()
    {
        // Finished is the third stage and it has to be a stage rather than a spelling of
        // Downloading. A Summary that only special-cased Downloading fell straight through to the
        // choosing arm and went back to saying "900 MB to download" after the last byte landed;
        // one that treated Finished as Downloading kept telling somebody the work carries on in
        // the tray after it had stopped.
        FirstRunState done = State(Capability.Photos) with
        {
            Stage = FirstRunStage.Finished,
            Downloads = [new CapabilityProgress(Capability.Photos, 660_000_000, 660_000_000)],
        };

        string finished = FirstRun.Summary(done);
        Assert.Contains("1 of 1", finished, StringComparison.Ordinal);
        Assert.DoesNotContain("to download", finished, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tray", finished, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("tray", FirstRun.Summary(done with { Stage = FirstRunStage.Downloading }),
                        StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileTheRunIsNotFetchingIsOneThatWasAlreadyThereAndItsBarStartsFull()
    {
        // Somebody who took Recommended and comes back for Speech already has the e5 pair, so the
        // run fetches one file out of three. Counting only what THIS run moves leaves the Meaning
        // bar at zero for the whole install and the screen looks stuck on a capability that has
        // been finished for weeks.
        FirstRunState s = State(Capability.Speech);          // closes over Meaning
        IReadOnlyList<Model> fetching = [ModelStore.WhisperTurbo];

        IReadOnlyList<CapabilityProgress> bars = FirstRun.Progress(
            s, fetching, new Dictionary<string, long> { [ModelStore.WhisperTurbo.File] = 100_000_000 });

        Assert.Equal(2, bars.Count);
        CapabilityProgress meaning = bars.Single(b => b.Capability == Capability.Meaning);
        CapabilityProgress speech = bars.Single(b => b.Capability == Capability.Speech);

        Assert.Equal(meaning.Total, meaning.Got);
        Assert.Equal(100_000_000, speech.Got);
        Assert.Equal(ModelStore.WhisperTurbo.Bytes, speech.Total);

        // And the summary counts it: one of the two capabilities is complete before a byte moved.
        Assert.Contains("1 of 2", FirstRun.Summary(s with { Stage = FirstRunStage.Downloading, Downloads = bars }),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void ABarNeverRunsPastItsOwnEndWhenTheFileIsBiggerThanTheTableSaid()
    {
        // Four of the five real files miss their declared size upward, so a run reports more bytes
        // moved than the number the bar is scaled against. Parts.Bar clamps the fraction, but the
        // count of finished capabilities does not - and neither should read as "more than done".
        FirstRunState s = State(Capability.Photos);
        Model first = Capabilities.OwnModels(Capability.Photos)[0];

        CapabilityProgress bar = FirstRun.Progress(
            s, Capabilities.OwnModels(Capability.Photos),
            new Dictionary<string, long> { [first.File] = first.Bytes + 5_000_000 })[0];

        Assert.True(bar.Got <= bar.Total, $"{bar.Got} bytes moved against a total of {bar.Total}");
    }

    [Fact]
    public void ADownloadThatFailedSaysSoOnTheScreen()
    {
        // Spec §6's second act must "survive a reboot and a dropped connection"; checklist step 17
        // says a failed download must say so and recover. The rejected draft had a Problem field
        // that nothing ever set and nothing ever showed, and a background Task that swallowed the
        // exception - so the bars simply stopped.
        FirstRunState s = State(Capability.Photos) with
        {
            Stage = FirstRunStage.Downloading,
            Downloads = [new CapabilityProgress(Capability.Photos, 12_000, 660_000_000)],
            Problem = "the network went away",
        };

        string summary = FirstRun.Summary(s);
        Assert.Contains("network went away", summary, StringComparison.Ordinal);
        Assert.Contains("kept", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EverySizeOnTheScreenReadsTheSameOnEveryMachine()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            string neutral = string.Join("|", FirstRun.Rows(State()).Select(r => r.Size + r.Note))
                           + FirstRun.Summary(State(Capability.Speech));
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(neutral,
                string.Join("|", FirstRun.Rows(State()).Select(r => r.Size + r.Note))
                + FirstRun.Summary(State(Capability.Speech)));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    // ---- what it looks like ---------------------------------------------------------------------

    public static TheoryData<string> AllPalettes()
    {
        var d = new TheoryData<string>();
        foreach (Palette p in Palette.BuiltIn) d.Add(p.Name);
        return d;
    }

    [Theory, MemberData(nameof(AllPalettes))]
    public void EveryMarkThisScreenDrawsIsReadableOnTheSurfaceItLandsOn(string name)
    {
        // COMPOSITED, and that is the whole of why this test is worth having. Derived.Fade changes
        // only the alpha channel and Derived.Contrast reads only RGB, so Contrast(Fade(a), c) is
        // identical to Contrast(Ink, c) whatever the alpha - it measures a colour nothing paints
        // and passes at 9.8 to 18.3 for readings that are really 4.6. Blending onto the surface
        // first is what makes the number the one an eye gets.
        //
        // This caught two real defects in this painter's first draft, both invisible on the three
        // dark palettes: the sizes were drawn in the accent, which reads 3.07 to 3.58 as text on a
        // light palette, and the free row's tick was faded to alpha 170 over the accent, which
        // reads 2.77.
        Derived d = Derived.From(Palette.BuiltIn.Single(p => p.Name == name));

        // The three colours the painter CHOOSES are read from the painter rather than repeated
        // here. A hand-written list of pairs is a statement about the palette; this is a statement
        // about what is drawn, and only the second one fails when the painter changes its mind.
        FirstRunRow priced = FirstRun.Rows(State()).First(r => r.Capability == Capability.Speech);
        FirstRunRow paid = FirstRun.Rows(State(Capability.Photos)).First(r => r.Capability == Capability.Photos);
        FirstRunRow free = FirstRun.Rows(State()).Single(r => r.Free);
        (SKColor Fill, SKColor Mark) taken = FirstRunPainter.TickInk(on: true, free: false, d);
        (SKColor Fill, SKColor Mark) always = FirstRunPainter.TickInk(on: true, free: true, d);

        (string What, SKColor Fg, SKColor Bg)[] pairs =
        [
            ("the title and the row titles, on the card", d.Ink, d.Ground),
            ("the sentence under the title", d.Fade(170), d.Ground),
            ("a preset title, on its tile", d.Ink, d.Tile),
            ("the chosen preset's title", d.Ink, d.RowSelected),
            ("the chosen preset's size", FirstRunPainter.TileSizeInk(chosen: true, d), d.RowSelected),
            ("an unchosen preset's size", FirstRunPainter.TileSizeInk(chosen: false, d), d.Tile),
            ("a row's price, on the card", FirstRunPainter.PriceInk(priced, d), d.Ground),
            ("a row's price, hovered", FirstRunPainter.PriceInk(priced, d), d.RowHover),
            ("a ticked row's nothing-more-to-pay", FirstRunPainter.PriceInk(paid, d), d.Ground),
            ("the free row's \"free\"", FirstRunPainter.PriceInk(free, d), d.Ground),
            ("a row title, hovered", d.Ink, d.RowHover),
            ("a row note", d.Fade(150), d.Ground),
            ("a row note, hovered", d.Fade(150), d.RowHover),
            ("the disclosure and the summary", d.Fade(150), d.Ground),
            ("the two buttons", d.Ink, d.Chip),
            ("a tick, on a taken row", taken.Mark, taken.Fill),
            ("the free row's tick", always.Mark, always.Fill),
        ];

        foreach ((string what, SKColor fg, SKColor bg) in pairs)
        {
            double ratio = Derived.Contrast(Over(fg, bg), bg);
            Assert.True(ratio >= 4.5, $"{name}: {what} reads {ratio:0.00}:1, needs 4.5");
        }
    }

    /// <summary>An alpha-carrying ink composited over an opaque background, the way Skia does.
    /// The same helper <c>DerivedTests</c> measures the ink ramp with.</summary>
    private static SKColor Over(SKColor fg, SKColor bg)
    {
        float a = fg.Alpha / 255f;
        return new SKColor(
            (byte)Math.Round(bg.Red + (fg.Red - bg.Red) * a),
            (byte)Math.Round(bg.Green + (fg.Green - bg.Green) * a),
            (byte)Math.Round(bg.Blue + (fg.Blue - bg.Blue) * a));
    }

    // ---- the layout ----------------------------------------------------------------------------

    [Fact]
    public void EverythingFitsTheScreenWithHebrewOffered()
    {
        // The tallest the screen gets: five rows, three switches with a wrapped disclosure under
        // one, a summary line and two buttons.
        int rows = FirstRun.Rows(State()).Count;
        Assert.Equal(5, rows);      // the free documents row plus the four capabilities

        Assert.True(FirstRunLayout.SwitchRect(2, rows).Bottom < FirstRunLayout.SummaryRect(rows).Top,
            "the switches overlap the summary");
        Assert.True(FirstRunLayout.SummaryRect(rows).Bottom <= FirstRunLayout.ButtonRect(0).Top,
            "the summary overlaps the buttons");
        Assert.True(FirstRunLayout.SummaryRect(rows).Height >= 24,
            "there is no room for the sentence that says what will be downloaded");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void ThereIsARealDeadZoneBetweenTheLastRowAndTheFirstSwitch(int rows)
    {
        // The first draft placed the switches 14px below the last row while a row band is 48px
        // tall, so the notional next row and the first switch interleaved: a click one row past
        // the end landed on the content toggle. Asserting one contrived point missed it; this
        // walks the whole interval.
        //
        // Rows(state) is one shorter where Hebrew is not offered, which is why both counts are
        // tested: a hit test given a fixed count answers with an index Apply cannot look up.
        float from = FirstRunLayout.RowRect(rows - 1).Bottom + 1;
        float to = FirstRunLayout.SwitchRect(0, rows).Top - 1;

        Assert.True(to - from >= FirstRunLayout.RowH,
            $"only {to - from}px of dead air between the last row and the first switch");

        for (float y = from; y <= to; y += 4f)
            Assert.Equal(FirstRunTarget.None,
                FirstRunLayout.HitTest(FirstRunLayout.RowRect(0).MidX, y, rows).Target);
    }

    [Fact]
    public void TheDisclosureFitsTheBandTheLayoutReservesForIt()
    {
        // DisclosureH is a constant, for the reason RailLayout.ListTop is one: a layout that
        // measured the sentence would need a typeface, and every hit test would then carry a
        // font. The cost of the constant is that lengthening the sentence silently draws it over
        // the switch beneath it, so the constant is held to what the sentence actually measures -
        // in the SHIPPED face, which is the one the screen is drawn in.
        int lines = Parts.Wrap(FirstRun.Disclosure, Parts.Face, Parts.NoteSize,
                               FirstRunLayout.DisclosureRect(5).Width).Count;

        Assert.True(Parts.NoteHeight(lines) <= FirstRunLayout.DisclosureH,
            $"the disclosure wraps to {lines} lines, needing {Parts.NoteHeight(lines)}px of the " +
            $"{FirstRunLayout.DisclosureH}px reserved between the update switch and the one below it");
    }

    [Fact]
    public void EachPresetTileAnswersWithItsOwnIndex()
    {
        for (int i = 0; i < 3; i++)
        {
            SKRect t = FirstRunLayout.TileRect(i);
            FirstRunHit hit = FirstRunLayout.HitTest(t.MidX, t.MidY, 5);
            Assert.Equal(FirstRunTarget.Preset, hit.Target);
            Assert.Equal(i, hit.Index);
        }
    }
}

public class FirstRunDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-first-" + Guid.NewGuid().ToString("N"));

    public FirstRunDownloadTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } GC.SuppressFinalize(this); }

    private static readonly Model A = new("a.bin", "https://example.invalid/a", 3, 3, "a");
    private static readonly Model B = new("b.bin", "https://example.invalid/b", 3, 3, "b");

    private static Fetch Serving(byte[] body) => (_, from, _) =>
        Task.FromResult(new Fetched(new MemoryStream(body, (int)from, body.Length - (int)from),
                                    body.Length, from > 0));

    [Fact]
    public async Task TheGateIsRunOnceAfterEverythingRatherThanOncePerFile()
    {
        // Per file, a 2.9 GB install re-queues the whole disk seven times. Never, and a capability
        // that was just installed reads nothing until the next launch, which is what makes the
        // download look like it did not work.
        int calls = 0;
        await FirstRunDownloads.RunAsync([A, B], _dir, Serving("abc"u8.ToArray()), null,
                                         () => { calls++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ADownloadThatThrowsIsReportedRatherThanThrown()
    {
        // Plan 5's GetAsync catches RangeRefusedException and IOException and nothing else, so an
        // HttpRequestException escapes it, escapes GetAllAsync, and escapes an unobserved
        // background Task - which on screen is a progress bar that simply stops. This controller
        // owns that policy: every fetch failure becomes an outcome the screen can show.
        IReadOnlyList<DownloadOutcome> outcomes = await FirstRunDownloads.RunAsync(
            [A], _dir, (_, _, _) => throw new HttpRequestException("the network went away"), null,
            () => Task.CompletedTask, CancellationToken.None);

        DownloadOutcome only = Assert.Single(outcomes);
        Assert.False(only.Complete);
        Assert.Contains("network went away", only.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneFileThatFailsDoesNotStopTheNextOne()
    {
        // "Keep what you have and continue when the network returns" (checklist step 17). A set
        // that aborts on the first failure leaves everything after it unfetched even when the
        // failure was one bad mirror.
        int calls = 0;
        Fetch flaky = (url, from, ct) => url.EndsWith("/a", StringComparison.Ordinal)
            ? throw new HttpRequestException("no")
            : Serving("abc"u8.ToArray())(url, from, ct);

        IReadOnlyList<DownloadOutcome> outcomes = await FirstRunDownloads.RunAsync(
            [A, B], _dir, flaky, null, () => { calls++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.Single(outcomes, o => o.Complete);
        // The second file arrived even though the first did not, so its backlog still has to be
        // queued - or it waits until somebody happens to install something else.
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NothingIsQueuedWhenNothingArrived()
    {
        // A re-queue is expensive - it re-reads every file a capability covers. Running it after a
        // download that failed entirely costs an hour of disk for no new answers.
        int calls = 0;
        await FirstRunDownloads.RunAsync([A, B], _dir, (_, _, _) => throw new HttpRequestException("no network"),
                                         null, () => { calls++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellingIsNotAFailureToReport()
    {
        // Quitting Findra mid-download cancels the token. That is not a network problem and must
        // not put "the operation was canceled" on the screen as though something went wrong; the
        // .part files stay and the next run resumes.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FirstRunDownloads.RunAsync([A], _dir, Serving("abc"u8.ToArray()), null,
                                       () => Task.CompletedTask, cts.Token));
    }
}
