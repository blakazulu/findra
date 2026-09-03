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
        || a.StartAtLogon != b.StartAtLogon || a.Stage != b.Stage || a.Problem != b.Problem
        || a.TranscribeMinutes != b.TranscribeMinutes;

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

        // The limit row, from a state where Speech is taken and so the row is on the screen. Every
        // option but the one already chosen has to move the number; the one already chosen is an
        // idempotent choice rather than a dead control, exactly as "Just names" is on an empty
        // screen.
        FirstRunState speech = State(Capability.Speech);
        for (int o = 0; o < FirstRun.LimitOptions.Count; o++)
        {
            if (TranscribeLimit.Presets[o] == speech.TranscribeMinutes) continue;
            Assert.True(Differs(speech, FirstRun.Apply(speech, new FirstRunHit(FirstRunTarget.Limit, o))),
                $"limit option {o} ({FirstRun.LimitOptions[o]}) changes nothing");
        }
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
    public void ARowsSizeIsTheSameNumberWhateverElseIsTicked()
    {
        // The number beside a row is that row's download, and it does not move. A marginal figure
        // is internally consistent and useless to read: the size you are weighing turns into
        // "0 MB" at the moment you tick it, so the one number you were deciding on disappears
        // exactly when you decide. What it costs is a fact about the row, not about the screen's
        // current state.
        foreach (Capability c in Capabilities.All)
        {
            string alone = RowFor(State(), c).Size;
            Assert.Equal(alone, RowFor(State(c), c).Size);                        // ticked
            Assert.Equal(alone, RowFor(State(Capability.Meaning), c).Size);       // a neighbour ticked
            Assert.Equal(alone, RowFor(State(Capability.Hebrew), c).Size);        // everything ticked
        }
    }

    [Fact]
    public void ARowIsPricedAtItsOwnFilesSoTheRowsAddUpToTheEverythingTotal()
    {
        // OWN files, not the closed set. Speech's closed set is 818 MB and Hebrew's is 2.37 GB,
        // and four rows priced that way add up to 4.08 GB - a list whose own arithmetic disagrees
        // with the "2.93 GB" printed on the Everything tile above it. Own files are the only
        // pricing where the column sums to the total.
        long sum = 0;
        foreach (FirstRunRow row in FirstRun.Rows(State()))
        {
            if (row.Capability is not { } c) continue;
            long own = ModelStore.TotalBytes(Capabilities.OwnModels(c));
            Assert.Equal(Sizes.Human(own), row.Size);
            sum += own;
        }

        Assert.Equal(Capabilities.TotalBytes(Capabilities.All), sum);
        Assert.Equal(FirstRun.PresetSize(Preset.Everything), Sizes.Human(sum));
    }

    [Fact]
    public void SpeechCostsMoreThanItsOwnRowAndTheSummaryIsWhereThatIsSaid()
    {
        // The honest consequence of pricing rows by their own files: Speech pulls the document
        // models with it, so ticking it alone downloads more than its row says. The summary is
        // the number that tells the truth about the download, and it is the closed set.
        FirstRunState s = State(Capability.Speech);
        long own = ModelStore.TotalBytes(Capabilities.OwnModels(Capability.Speech));

        Assert.Equal(Sizes.Human(own), RowFor(s, Capability.Speech).Size);
        Assert.True(FirstRun.TotalBytes(s) > own,
            "the summary is the closed set, so it has to be larger than Speech's own files");
        Assert.Contains(Sizes.Human(FirstRun.TotalBytes(s)), FirstRun.Summary(s), StringComparison.Ordinal);
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

    // ---- how long a recording is worth transcribing ------------------------------------------

    [Fact]
    public void TheTranscriptionLimitIsOnTheScreenExactlyWhenSpeechIsTaken()
    {
        // Ticking Speech signs somebody up for transcription, and one number decides how much of
        // each recording is transcribed. Five minutes by default, so a two-hour lecture is cut at
        // five - and until now the only place that number could be changed was the settings
        // window, which nobody has opened yet on the screen that turned speech on.
        Assert.Equal(-1, FirstRun.LimitRow(State()));
        Assert.Equal(-1, FirstRun.LimitRow(State(Capability.Photos)));

        FirstRunState speech = State(Capability.Speech);
        Assert.Equal(IndexOf(speech, Capability.Speech), FirstRun.LimitRow(speech));

        // Hebrew closes over Speech, so it brings the row with it.
        FirstRunState hebrew = State(Capability.Hebrew);
        Assert.Equal(IndexOf(hebrew, Capability.Speech), FirstRun.LimitRow(hebrew));

        // And it goes when Speech goes.
        Assert.Equal(-1, FirstRun.LimitRow(
            FirstRun.Apply(speech, new FirstRunHit(FirstRunTarget.Row, IndexOf(speech, Capability.Speech)))));
    }

    [Fact]
    public void TheLimitRowSitsUnderSpeechAndAboveTheHebrewPass()
    {
        // Under Speech because it is Speech's setting, and above Hebrew because Hebrew is a second
        // pass over the same recordings - a limit drawn below it would read as belonging to the
        // fine-tune alone.
        FirstRunState s = State(Capability.Hebrew);
        int limit = FirstRun.LimitRow(s);
        SKRect speech = FirstRunLayout.RowRect(IndexOf(s, Capability.Speech), limit);
        SKRect band = FirstRunLayout.LimitRect(limit);
        SKRect hebrew = FirstRunLayout.RowRect(IndexOf(s, Capability.Hebrew), limit);

        Assert.True(speech.Bottom <= band.Top, "the limit row is drawn over the Speech row");
        Assert.True(band.Bottom <= hebrew.Top, "the limit row is drawn over the Hebrew row");
        // The rows below it MOVE, rather than the band being painted on top of them.
        Assert.True(hebrew.Top > FirstRunLayout.RowRect(IndexOf(s, Capability.Hebrew), -1).Top,
            "the Hebrew row did not move down when the limit row appeared above it");
    }

    [Fact]
    public void EveryLimitOptionAnswersWithItsOwnIndexAndSetsThatManyMinutes()
    {
        FirstRunState s = State(Capability.Speech);
        int rows = FirstRun.Rows(s).Count, limit = FirstRun.LimitRow(s);

        Assert.Equal(TranscribeLimit.Presets.Count, FirstRun.LimitOptions.Count);
        for (int o = 0; o < FirstRun.LimitOptions.Count; o++)
        {
            SKRect r = FirstRunLayout.LimitOptionRect(o, limit);
            FirstRunHit hit = FirstRunLayout.HitTest(r.MidX, r.MidY, rows, limit);

            Assert.Equal(FirstRunTarget.Limit, hit.Target);
            Assert.Equal(o, hit.Index);
            Assert.Equal(TranscribeLimit.Presets[o], FirstRun.Apply(s, hit).TranscribeMinutes);
        }
    }

    [Fact]
    public void AnOptionOffTheEndOfTheListChangesNothing()
    {
        // The same shape as the row arm: a hit carries an index and an index can be wrong, and
        // indexing Presets with it is a crash on a screen with no way out but the task manager.
        FirstRunState s = State(Capability.Speech);
        Assert.Equal(s.TranscribeMinutes, FirstRun.Apply(s, new FirstRunHit(FirstRunTarget.Limit, 9)).TranscribeMinutes);
        Assert.Equal(s.TranscribeMinutes, FirstRun.Apply(s, new FirstRunHit(FirstRunTarget.Limit, -1)).TranscribeMinutes);
    }

    [Fact]
    public void TheLimitChosenOnThisScreenIsTheOneTheIndexerIsGiven()
    {
        // Answered here or not, the number reaches the config the content loop reads. Without the
        // last line the row is a control that draws and decides nothing.
        FirstRunState s = State(Capability.Speech);
        Assert.Equal(TranscribeLimit.Default, FirstRun.Outcome(s, Config.Default).TranscribeMinutes);

        int twoHours = TranscribeLimit.Presets.ToList().IndexOf(120);
        FirstRunState raised = FirstRun.Apply(s, new FirstRunHit(FirstRunTarget.Limit, twoHours));

        Assert.Equal(120, raised.TranscribeMinutes);
        Assert.Equal(120, FirstRun.Outcome(raised, Config.Default).TranscribeMinutes);
    }

    [Fact]
    public void EveryLimitLabelFitsThePillItIsDrawnIn()
    {
        // Measured with Parts.Face at Parts.LabelSize, which is what the painter hands Parts.Pill.
        // The settings window's five-option row is where these labels were shortened - "30 minutes"
        // is 65.3px against a 62.8px pill - and the short forms are shared rather than written out
        // twice, so a pill that ellipsises here is one that ellipsises there.
        for (int o = 0; o < FirstRun.LimitOptions.Count; o++)
        {
            float room = FirstRunLayout.LimitOptionRect(o, 3).Width - 12;
            float need = CardText.Measure(FirstRun.LimitOptions[o], Parts.Face, Parts.LabelSize);
            Assert.True(need <= room,
                $"'{FirstRun.LimitOptions[o]}' needs {need:F1}px and its pill gives {room:F1}px");
        }
    }

    [Fact]
    public void TheLimitRowsLabelFitsTheColumnBeforeItsPills()
    {
        // The other direction, and the pair is the point: widening the pills to fit their labels
        // narrows this column, and widening this column narrows the pills. Satisfying one by
        // moving the geometry breaks the other, so the answer to a tight label is a shorter label.
        float room = FirstRunLayout.LimitOptionRect(0, 3).Left - FirstRunLayout.LimitLabelLeft(3) - 12;
        float need = CardText.Measure(FirstRun.LimitLabel, Parts.Face, Parts.LabelSize);

        Assert.True(need <= room,
            $"the label '{FirstRun.LimitLabel}' needs {need:F1}px and its column gives {room:F1}px");
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
            ("a ticked row's price", FirstRunPainter.PriceInk(paid, d), d.Ground),
            ("the free row's \"free\"", FirstRunPainter.PriceInk(free, d), d.Ground),
            ("a row title, hovered", d.Ink, d.RowHover),
            ("a row note", d.Fade(150), d.Ground),
            ("a row note, hovered", d.Fade(150), d.RowHover),
            ("the disclosure", d.Fade(150), d.Ground),
            // The summary is the one line the whole screen comes down to, so it is drawn in full
            // ink at the lead size rather than as another note. Its reading is the ink ramp's top,
            // which is the point of moving it.
            ("the summary", d.Ink, d.Ground),
            ("the transcription limit's label", d.Ink, d.Ground),
            ("a limit pill, resting", d.Ink, d.Chip),
            ("a limit pill, hovered", d.Ink, d.RowHover),
            ("the chosen limit pill", d.Ink, d.RowSelected),
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

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void EverythingFitsTheScreenWithHebrewOffered(int limit)
    {
        // The tallest the screen gets: five rows, the transcription limit under Speech where
        // Speech is taken, three switches with a wrapped disclosure under one, a summary and two
        // buttons. Both configurations, because the limit row pushes everything below it down and
        // the screen has to hold the taller one.
        int rows = FirstRun.Rows(State()).Count;
        Assert.Equal(5, rows);      // the free documents row plus the four capabilities

        Assert.True(FirstRunLayout.SwitchRect(2, rows, limit).Bottom < FirstRunLayout.SummaryRect(rows, limit).Top,
            "the switches overlap the summary");
        Assert.True(FirstRunLayout.SummaryRect(rows, limit).Bottom <= FirstRunLayout.ButtonRect(0).Top,
            "the summary overlaps the buttons");
        Assert.True(FirstRunLayout.SummaryRect(rows, limit).Height >= Parts.LeadHeight(3),
            "there is no room for the sentence that says what will be downloaded");
    }

    [Theory]
    [InlineData(4, -1)]
    [InlineData(5, -1)]
    [InlineData(5, 3)]
    public void ThereIsARealDeadZoneBetweenTheLastRowAndTheFirstSwitch(int rows, int limit)
    {
        // The first draft placed the switches 14px below the last row while a row band is 48px
        // tall, so the notional next row and the first switch interleaved: a click one row past
        // the end landed on the content toggle. Asserting one contrived point missed it; this
        // walks the whole interval.
        //
        // Rows(state) is one shorter where Hebrew is not offered, which is why both counts are
        // tested: a hit test given a fixed count answers with an index Apply cannot look up. The
        // third case is the same interval with the transcription limit on the screen, which moves
        // both ends of it.
        float from = FirstRunLayout.RowRect(rows - 1, limit).Bottom + 1;
        float to = FirstRunLayout.SwitchRect(0, rows, limit).Top - 1;

        Assert.True(to - from >= FirstRunLayout.RowH,
            $"only {to - from}px of dead air between the last row and the first switch");

        for (float y = from; y <= to; y += 4f)
            Assert.Equal(FirstRunTarget.None,
                FirstRunLayout.HitTest(FirstRunLayout.RowRect(0).MidX, y, rows, limit).Target);
    }

    [Theory]
    [InlineData(4, -1)]
    [InlineData(5, -1)]
    [InlineData(5, 3)]
    public void TheDeadZoneIsNoWiderThanItHasToBe(int rows, int limit)
    {
        // The other direction of the pair above, and the two of them are what keeps this band
        // honest: the floor stops a click one row past the list landing on the content toggle,
        // and this stops the answer to that being sixty-odd pixels of nothing in the middle of
        // the screen. Satisfying either one alone is trivial - widen it, or close it - and both
        // together is what leaves a dead zone that reads as a parting rather than as a hole.
        float air = FirstRunLayout.SwitchRect(0, rows, limit).Top - FirstRunLayout.RowRect(rows - 1, limit).Bottom;
        Assert.True(air <= FirstRunLayout.RowH + 8,
            $"{air}px of dead air between the last row and the first switch reads as a hole");
    }

    [Theory]
    [InlineData(4, -1)]
    [InlineData(5, -1)]
    [InlineData(5, 3)]
    public void TheDeadZoneCarriesARuleSoItReadsAsAPartingAndNotAsAHole(int rows, int limit)
    {
        // A band that cannot be closed - the click one row past the end has to land on nothing -
        // is given something to be instead. It is drawn between the two things it separates and
        // touches neither, and it answers no click of its own: a rule that took clicks would be
        // the dead zone with a target painted on it.
        SKRect rule = FirstRunLayout.RuleRect(rows, limit);

        Assert.True(rule.Top > FirstRunLayout.RowRect(rows - 1, limit).Bottom, "the rule touches the last row");
        Assert.True(rule.Bottom < FirstRunLayout.SwitchRect(0, rows, limit).Top, "the rule touches the first switch");
        Assert.Equal(FirstRunTarget.None,
            FirstRunLayout.HitTest(rule.MidX, rule.MidY, rows, limit).Target);
    }

    [Fact]
    public void NoRowIsDrawnWhereTheLimitRowIsAndNoClickThereLandsOnOne()
    {
        // The band is inserted into the list rather than laid over it, so the rows below Speech
        // move and the band's own air belongs to nothing. A click on it that fell through to a row
        // would tick a capability somebody was aiming a pill at.
        int rows = FirstRun.Rows(State()).Count;
        SKRect band = FirstRunLayout.LimitRect(3);
        float x = FirstRunLayout.RowRect(0).Left + 4;   // left of the label, right of the edge

        for (float y = band.Top; y <= band.Bottom; y += 4f)
            Assert.Equal(FirstRunTarget.None, FirstRunLayout.HitTest(x, y, rows, 3).Target);

        for (int i = 0; i < rows; i++)
            Assert.False(FirstRunLayout.RowRect(i, 3).IntersectsWith(band),
                $"row {i} overlaps the transcription limit");
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
    public void TheSummaryIsAHeadlineRatherThanAnotherNote()
    {
        // The rows say what each one costs; the summary is the only place the whole download is
        // stated, and since the rows stopped moving it is the number that carries the screen. It
        // is drawn in the vocabulary's largest register, which is the one the section headers use,
        // and the note size is the smallest - "raising" it by moving NoteSize would raise the row
        // notes and the disclosure with it.
        Assert.True(Parts.LeadSize > Parts.LabelSize,
            $"the summary is drawn at {Parts.LeadSize}px and a row label at {Parts.LabelSize}px");
        Assert.True(Parts.LabelSize > Parts.NoteSize,
            $"a row label is {Parts.LabelSize}px and a note {Parts.NoteSize}px");
    }

    [Fact]
    public void TheLongestSummaryFitsTheBandTheLayoutLeavesForIt()
    {
        // The same shape as the disclosure's check, and needed for the same reason: the band is
        // arithmetic in FirstRunLayout, the sentence is prose in FirstRun, and nothing but this
        // holds the two together. The worst case is the second act with something wrong - the
        // longest capability title, a real network message, and the promise about the tray, all
        // in one sentence - measured in the SHIPPED face at the size the summary is drawn.
        FirstRunState worst = State(Capability.Hebrew) with
        {
            Stage = FirstRunStage.Downloading,
            Downloads =
            [
                new CapabilityProgress(Capability.Meaning, 283_000_000, 283_000_000),
                new CapabilityProgress(Capability.Speech, 12_000, 574_000_000),
                new CapabilityProgress(Capability.Hebrew, 0, 1_549_000_000),
            ],
            Problem = "No such host is known. (huggingface.co:443)",
        };

        // In the TIGHTEST configuration: Hebrew closes over Speech, so the transcription limit is
        // on the screen and every band below it has moved down.
        SKRect band = FirstRunLayout.SummaryRect(FirstRun.Rows(worst).Count, FirstRun.LimitRow(worst));
        int lines = Parts.Wrap(FirstRun.Summary(worst), Parts.Face, Parts.LeadSize, band.Width).Count;

        Assert.True(Parts.LeadHeight(lines) <= band.Height,
            $"the summary wraps to {lines} lines, needing {Parts.LeadHeight(lines)}px of the " +
            $"{band.Height}px between the last switch and the buttons");
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
    // ---- what the screen explains ---------------------------------------------------------------

    [Fact]
    public void TheTranscriptionLimitSaysItCoversVideoAndNotOnlySound()
    {
        // The label is "Transcribe up to" and the row it belongs to is Speech, so nothing on this
        // screen said the number also decides what happens to every video on the disk. The
        // settings window has said so since it was written; the screen somebody commits a 547 MB
        // download on had no note at all.
        string note = FirstRun.LimitNote;

        Assert.Contains("audio", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("video", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frames", note, StringComparison.OrdinalIgnoreCase);

        // And it is true of the code rather than merely reassuring: Decoders.Video reads the
        // frames only where Photos is installed, so the note names that condition rather than
        // promising the frames unconditionally.
        Assert.Contains("Photos", note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLimitNoteFitsTheBandTheLayoutReservesForIt()
    {
        // The same shape as the disclosure's check and for the same reason: the band is a
        // constant in FirstRunLayout, the sentence is prose in FirstRun, and nothing else holds
        // the two together. Measured in the SHIPPED face, at the size a note is drawn.
        SKRect band = FirstRunLayout.LimitNoteRect(3);
        int lines = Parts.Wrap(FirstRun.LimitNote, Parts.Face, Parts.NoteSize, band.Width).Count;

        Assert.True(Parts.NoteHeight(lines) <= FirstRunLayout.LimitNoteH,
            $"the limit note wraps to {lines} lines, needing {Parts.NoteHeight(lines)}px of the " +
            $"{FirstRunLayout.LimitNoteH}px reserved under the pills");

        Assert.True(band.Top >= FirstRunLayout.LimitRect(3).Bottom, "the note overlaps the pills");
        Assert.True(band.Bottom <= FirstRunLayout.RowRect(4, 3).Top, "the note overlaps the row below it");
    }

    [Fact]
    public void TheSpeechRowNamesVideoWhereItIntroducesTranscription()
    {
        // "Transcribe recordings" is what somebody reads before deciding to spend 547 MB, and a
        // video library is not obviously a set of recordings - least of all with a row above it
        // called "Photos and video", which makes video look like somebody else's business.
        string note = RowFor(State(), Capability.Speech).Note;
        Assert.Contains("video", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheContentSwitchSaysWhatLookingInsideFilesActuallyMeans()
    {
        // The most consequential privacy choice on the screen was a bare label while the update
        // check under it carried four lines. Each clause here is checked against the code and
        // against PRIVACY.md rather than written from the label.
        string note = FirstRun.ContentNote;

        // Names work either way, which is what makes "Not now" a complete answer.
        Assert.Contains("name", note, StringComparison.OrdinalIgnoreCase);
        // What it does, and that it is not instant.
        Assert.Contains("drive", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hours", note, StringComparison.OrdinalIgnoreCase);
        // Only while Findra is running: the indexer is a child of the interface.
        Assert.Contains("open", note, StringComparison.OrdinalIgnoreCase);
        // PRIVACY.md's straight answer, no softer here than it is there.
        Assert.Contains("index", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not encrypted", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheContentNoteFitsTheBandTheLayoutReservesForIt()
    {
        SKRect band = FirstRunLayout.ContentNoteRect(5, -1);
        int lines = Parts.Wrap(FirstRun.ContentNote, Parts.Face, Parts.NoteSize, band.Width).Count;

        Assert.True(Parts.NoteHeight(lines) <= FirstRunLayout.ContentNoteH,
            $"the content note wraps to {lines} lines, needing {Parts.NoteHeight(lines)}px of the " +
            $"{FirstRunLayout.ContentNoteH}px reserved between the first switch and the second");

        Assert.True(band.Top >= FirstRunLayout.SwitchRect(0, 5, -1).Bottom, "the note overlaps its own switch");
        Assert.True(band.Bottom <= FirstRunLayout.SwitchRect(1, 5, -1).Top, "the note overlaps the switch below it");
    }

    // ---- the second act, continued ------------------------------------------------------------

    [Fact]
    public void AFileShorterThanItsDeclaredSizeStillCompletesItsCapability()
    {
        // The declared table is the spec's figure in MiB to one decimal place, and two of the
        // seven real files are SMALLER than it: siglip2-vision.onnx by 42,692 bytes and
        // whisper-ivrit.bin by 3,521. Crediting only the bytes that moved leaves Photos and
        // Hebrew 0.012% short for ever, so a complete Everything install reads "2 of 4 done"
        // with every bar visually full. REAL numbers, because a synthetic model whose real size
        // equals its declared size cannot fail this - which is why nothing caught it.
        const long realVision = 371_992_072;    // declared 372,034,764
        const long realIvrit = 1_624_555_275;   // declared 1,624,558,796

        Assert.True(realVision < ModelStore.Siglip2Vision.Bytes, "the vision tower is no longer short");
        Assert.True(realIvrit < ModelStore.WhisperHebrew.Bytes, "the Hebrew fine-tune is no longer short");

        FirstRunState all = State(Capability.Photos, Capability.Hebrew);   // Everything, in effect
        var moved = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (Model m in ModelStore.All) moved[m.File] = m.Bytes + 12_000;
        moved[ModelStore.Siglip2Vision.File] = realVision;
        moved[ModelStore.WhisperHebrew.File] = realIvrit;

        IReadOnlyList<CapabilityProgress> bars = FirstRun.Progress(all, ModelStore.All, moved);

        foreach (CapabilityProgress p in bars)
            Assert.True(p.Got >= p.Total,
                $"{Capabilities.Title(p.Capability)} is {p.Total - p.Got} bytes short of its own total");

        Assert.Contains($"{bars.Count} of {bars.Count}",
            FirstRun.Summary(all with { Stage = FirstRunStage.Finished, Downloads = bars }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AHalfFetchedFileIsStillHalfFetched()
    {
        // The slack that credits a finished-but-short file must not credit one that is genuinely
        // still coming: it is one part in fifty, not "near enough".
        FirstRunState s = State(Capability.Photos);
        Model vision = ModelStore.Siglip2Vision;

        CapabilityProgress bar = FirstRun.Progress(
            s, Capabilities.OwnModels(Capability.Photos),
            new Dictionary<string, long> { [vision.File] = vision.Bytes / 2 })
            .Single(b => b.Capability == Capability.Photos);

        Assert.True(bar.Got < bar.Total, "a half-fetched file was credited in full");
    }

    [Fact]
    public void TheRunningScreenSaysItIsDownloadingAndNotIndexing()
    {
        // "2 of 4 done" could be anything, and content indexing - a different, later and far
        // longer job - may be about to start on the same machine. The word has to be on screen.
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
        Assert.Contains("Downloading", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("indexing", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFinishedRunSaysSoAndPointsAtTheWayOut()
    {
        FirstRunState done = State(Capability.Photos) with
        {
            Stage = FirstRunStage.Finished,
            Downloads = [new CapabilityProgress(Capability.Photos, 660_000_000, 660_000_000)],
        };

        Assert.Contains("close", FirstRun.Summary(done), StringComparison.OrdinalIgnoreCase);

        // The button under it says the same thing, in both halves of the second act.
        Assert.Equal(FirstRun.CloseLabel, FirstRun.GoLabel(FirstRunStage.Finished));
        Assert.Equal(FirstRun.CloseLabel, FirstRun.GoLabel(FirstRunStage.Downloading));
        Assert.NotEqual(FirstRun.CloseLabel, FirstRun.GoLabel(FirstRunStage.Choosing));

        // A run that ended badly keeps its report rather than being told it is ready.
        string bad = FirstRun.Summary(done with { Problem = "the network went away" });
        Assert.Contains("network went away", bad, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void TheSecondActsSummaryFollowsItsListAndClearsTheWayOut(int limit)
    {
        // The second act draws no switches, so the sentence moves out of the band they were in
        // and follows the list it is about. Both ends have to hold: it may not run into the last
        // row above it, and it may not run into the button below it.
        int rows = FirstRun.Rows(State()).Count;
        SKRect band = FirstRunLayout.SettledSummaryRect(rows, limit);

        Assert.True(band.Top > FirstRunLayout.RowRect(rows - 1, limit).Bottom, "the summary overlaps the last row");
        Assert.True(band.Bottom <= FirstRunLayout.ButtonRect(1).Top, "the summary overlaps the way out");
        Assert.True(band.Right <= FirstRunLayout.ButtonRect(1).Left, "the summary runs under the button");

        // And the longest thing it can say still fits. The worst case is a run that ended badly:
        // the longest capability title, a real network message, and the promise about the tray.
        FirstRunState worst = State(Capability.Hebrew) with
        {
            Stage = FirstRunStage.Downloading,
            Downloads =
            [
                new CapabilityProgress(Capability.Meaning, 283_000_000, 283_000_000),
                new CapabilityProgress(Capability.Speech, 12_000, 574_000_000),
                new CapabilityProgress(Capability.Hebrew, 0, 1_549_000_000),
            ],
            Problem = "No such host is known. (huggingface.co:443)",
        };

        SKRect w = FirstRunLayout.SettledSummaryRect(FirstRun.Rows(worst).Count, FirstRun.LimitRow(worst));
        int lines = Parts.Wrap(FirstRun.Summary(worst), Parts.Face, Parts.LeadSize, w.Width).Count;
        Assert.True(Parts.LeadHeight(lines) <= w.Height,
            $"the summary wraps to {lines} lines, needing {Parts.LeadHeight(lines)}px of the {w.Height}px it has");
    }

    [Fact]
    public void EveryLabelOnTheButtonFitsTheButtonItIsDrawnIn()
    {
        // Parts.Pill ellipsises, so a label too wide is drawn over both ends of its own outline.
        float room = FirstRunLayout.ButtonRect(0).Width - 12;
        string[] labels =
        [
            FirstRun.NotNowLabel,
            FirstRun.GoLabel(FirstRunStage.Choosing),
            FirstRun.GoLabel(FirstRunStage.Downloading),
            FirstRun.GoLabel(FirstRunStage.Finished),
            // The three the state-aware overload can add: the last question's pair, and the word
            // for a machine with nothing left to fetch.
            FirstRun.LaterLabel,
            FirstRun.StartReadingLabel,
            FirstRun.ContinueLabel,
        ];

        foreach (string label in labels)
        {
            float need = CardText.Measure(label, Parts.Face, Parts.LabelSize);
            Assert.True(need <= room, $"'{label}' needs {need:F1}px and its button gives {room:F1}px");
        }
    }

    [Fact]
    public void NothingButTheWayOutAnswersAClickOnceTheDownloadHasStarted()
    {
        // "In that phase nothing should be clickable." A tile, a row, a switch or a limit pill
        // that still answers is a control acting on a selection already handed to the shell, and
        // a screen that looks live while it is not is worse than one that is plainly settled.
        int rows = FirstRun.Rows(State()).Count;

        var points = new List<(string What, float X, float Y)>();
        for (int i = 0; i < 3; i++)
            points.Add(($"tile {i}", FirstRunLayout.TileRect(i).MidX, FirstRunLayout.TileRect(i).MidY));
        for (int i = 0; i < rows; i++)
            points.Add(($"row {i}", FirstRunLayout.RowRect(i, 3).MidX, FirstRunLayout.RowRect(i, 3).MidY));
        for (int o = 0; o < FirstRun.LimitOptions.Count; o++)
            points.Add(($"limit {o}",
                        FirstRunLayout.LimitOptionRect(o, 3).MidX, FirstRunLayout.LimitOptionRect(o, 3).MidY));
        for (int i = 0; i < 3; i++)
            points.Add(($"switch {i}",
                        FirstRunLayout.SwitchRect(i, rows, 3).MidX, FirstRunLayout.SwitchRect(i, rows, 3).MidY));
        points.Add(("not now", FirstRunLayout.ButtonRect(0).MidX, FirstRunLayout.ButtonRect(0).MidY));

        foreach ((string what, float x, float y) in points)
            Assert.True(FirstRunLayout.HitTest(x, y, rows, 3, settled: true).Target == FirstRunTarget.None,
                        $"{what} still answers a click while the download runs");

        // Every one of them answers while the screen is still a question, so the sweep is a
        // statement about the stage rather than about a layout with nothing in it.
        foreach ((string what, float x, float y) in points)
            Assert.True(FirstRunLayout.HitTest(x, y, rows, 3).Target != FirstRunTarget.None,
                        $"{what} answers nothing even while the screen is a question");

        // The button included, WHILE THE FILES ARE STILL COMING. "It should be just a download
        // status screen at that point": a pill that answers is a pill that has to mean something,
        // and the only thing it could mean during a download is a choice nobody was offered. The
        // way out here is the window's own close, which is never disabled - a 2.93 GB fetch is
        // long enough that a screen answering nothing at all AND refusing to close would be a
        // trap rather than a settled question.
        // The settled window is SHORTER than the choosing one, so the button is not where it was
        // - taking its rect from the choosing height would test a point off the bottom of the
        // window and pass for the wrong reason.
        SKRect wayOut = FirstRunLayout.ButtonRect(1, FirstRunLayout.SettledHeight(rows, 3));

        Assert.Equal(FirstRunTarget.None,
            FirstRunLayout.HitTest(wayOut.MidX, wayOut.MidY, rows, 3, settled: true, finished: false).Target);

        // And once the last byte has landed it answers, because now there is something for it to
        // say. Both halves are asserted at the same point on the screen, so this is a statement
        // about the stage and not about where the button happens to sit.
        Assert.Equal(FirstRunTarget.Go,
            FirstRunLayout.HitTest(wayOut.MidX, wayOut.MidY, rows, 3, settled: true, finished: true).Target);

        // Nothing ELSE answers when it is finished either - the chooser stays settled.
        foreach ((string what, float x, float y) in points)
            Assert.True(FirstRunLayout.HitTest(x, y, rows, 3, settled: true, finished: true).Target
                            == FirstRunTarget.None,
                        $"{what} answers a click after the download finished");
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


    // ---- the last question ----------------------------------------------------------------

    private static FirstRunState Finished(bool contentOn) => new()
    {
        Chosen = Capabilities.Close([Capability.Photos]),
        HebrewOffered = true,
        ContentOn = contentOn,
        Stage = FirstRunStage.Finished,
    };

    [Fact]
    public void TheLastActAsksAboutReadingOnlyWhenReadingWasChosen()
    {
        // The first act asks what Findra should be able to UNDERSTAND and has no room for the one
        // warning this question needs - that a first pass walks every drive and can run for hours.
        // The last act has the room and is the moment: the download is done, nothing is competing
        // for the disk, and the person is still here.
        Assert.True(FirstRun.Asks(Finished(contentOn: true)));

        // Reading was declined on the first act, so there is nothing to ask and nothing to start.
        Assert.False(FirstRun.Asks(Finished(contentOn: false)));

        // And never before the end. While files are still arriving the screen answers nothing at
        // all, and the question would be about work that would compete with the download.
        Assert.False(FirstRun.Asks(Finished(contentOn: true) with { Stage = FirstRunStage.Downloading }));
        Assert.False(FirstRun.Asks(Finished(contentOn: true) with { Stage = FirstRunStage.Choosing }));
    }

    [Fact]
    public void TheQuestionBringsBackTheSecondButtonAndBothOfThemAnswerIt()
    {
        // Two buttons, and they mean different things - which is the whole reason the second one
        // comes back. Everywhere else in the second act there is one way out, because a second
        // pill doing exactly what the first does is a choice with no difference in it.
        FirstRunState asking = Finished(contentOn: true);
        int rows = FirstRun.Rows(asking).Count;
        int limitRow = FirstRun.LimitRow(asking);
        float h = FirstRunLayout.SettledHeight(rows, limitRow, asking: true);

        SKRect later = FirstRunLayout.ButtonRect(0, h);
        SKRect start = FirstRunLayout.ButtonRect(1, h);

        Assert.Equal(FirstRunTarget.NotNow,
            FirstRunLayout.HitTest(later.MidX, later.MidY, rows, limitRow, settled: true, finished: true, asking: true).Target);
        Assert.Equal(FirstRunTarget.Go,
            FirstRunLayout.HitTest(start.MidX, start.MidY, rows, limitRow, settled: true, finished: true, asking: true).Target);

        // Without the question that left-hand pill is not drawn, so nothing may answer a click
        // there - a hit test that still reported NotNow would be a control nobody can see.
        float plain = FirstRunLayout.SettledHeight(rows, limitRow, asking: false);
        Assert.Equal(FirstRunTarget.None,
            FirstRunLayout.HitTest(FirstRunLayout.ButtonRect(0, plain).MidX, FirstRunLayout.ButtonRect(0, plain).MidY,
                                   rows, limitRow, settled: true, finished: true, asking: false).Target);
    }

    [Fact]
    public void TheWindowMakesRoomForTheQuestionAndGivesItBackWhenThereIsNone()
    {
        FirstRunState asking = Finished(contentOn: true);
        int rows = FirstRun.Rows(asking).Count;
        int limitRow = FirstRun.LimitRow(asking);

        float withQuestion = FirstRunLayout.SettledHeight(rows, limitRow, asking: true);
        float without = FirstRunLayout.SettledHeight(rows, limitRow, asking: false);

        Assert.True(withQuestion > without, "the question needs room the plain finished screen does not");
        Assert.Equal(FirstRunLayout.AskH, withQuestion - without);
    }

    [Fact]
    public void TheLastQuestionFitsTheRoomTheWindowMakesForIt()
    {
        // Measured in the shipped face, on the terms every other band on this screen is held to.
        // AskH is a constant and the two strings are prose; a copy-edit that adds a line is what
        // this catches, and the fix is to shorten the sentence or raise the constant deliberately.
        SKTypeface face = Parts.Face;
        FirstRunState asking = Finished(contentOn: true);
        SKRect ask = FirstRunLayout.AskRect(FirstRun.Rows(asking).Count, FirstRun.LimitRow(asking));

        // The rule and the title sit in the first 40px; the note has the rest.
        float need = 40f + Parts.NoteHeight(Parts.Wrap(FirstRun.AskNote, face, Parts.NoteSize, ask.Width).Count);
        Assert.True(need <= FirstRunLayout.AskH,
            $"the question needs {need:0.0}px and AskH is {FirstRunLayout.AskH:0.0}px");

        // And the title fits on one line, because a wrapped question reads as two questions.
        Assert.True(CardText.Measure(FirstRun.AskTitle, face, Parts.LeadSize) <= ask.Width,
            "the question wraps; shorten it rather than widening the card");
    }

    [Fact]
    public void NothingOnTheAskingScreenTellsSomebodyToLeaveIt()
    {
        // Both of these used to. "Findra is in the tray. Settings can add any of the rest later."
        // and "...and you can close this window." are right when closing is the only thing left,
        // and wrong six lines above the one thing on the screen still worth answering.
        string summary = FirstRun.Summary(Finished(contentOn: true) with
        {
            Downloads = [new CapabilityProgress(Capability.Photos, 659_000_000, 659_000_000)],
        });

        Assert.DoesNotContain("close this window", summary, StringComparison.OrdinalIgnoreCase);

        // The screen that is NOT asking still says it, because there it is the whole truth.
        string done = FirstRun.Summary(Finished(contentOn: false) with
        {
            Downloads = [new CapabilityProgress(Capability.Photos, 659_000_000, 659_000_000)],
        });
        Assert.Contains("close this window", done, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRightHandButtonSaysWhatItStartsRatherThanClose()
    {
        Assert.Equal(FirstRun.StartReadingLabel, FirstRun.GoLabel(Finished(contentOn: true)));
        Assert.Equal(FirstRun.CloseLabel, FirstRun.GoLabel(Finished(contentOn: false)));
    }

    // ---- what is already on the disk ---------------------------------------------------------

    private static IReadOnlySet<string> Everything() =>
        new HashSet<string>(ModelStore.All.Select(m => m.File), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ACapabilityAlreadyOnTheDiskIsPricedAtNothingAndSaysSo()
    {
        // The screen never asked the disk. Every row printed its capability's full size and the
        // summary printed the whole closed selection, while Wanted - which is what actually
        // fetches - skipped what was there. Somebody who kept their models through an uninstall
        // was offered 2.93 GB, pressed the button, and watched every bar fill at once.
        var s = new FirstRunState
        {
            Chosen = Capabilities.Close([Capability.Photos, Capability.Meaning]),
            HebrewOffered = true,
            OnDisk = Everything(),
        };

        foreach (FirstRunRow row in FirstRun.Rows(s).Where(r => r.Capability is not null))
            Assert.Equal(FirstRun.InstalledLabel, row.Size);

        // The same word Settings uses for the same fact, so the two surfaces do not describe an
        // installed capability two ways.
        Assert.Equal("installed", FirstRun.InstalledLabel);

        Assert.Equal(0, FirstRun.TotalBytes(s));
        Assert.Equal("0 MB", FirstRun.PresetSize(Preset.Everything, s.OnDisk));
    }

    [Fact]
    public void AnEmptyDiskIsPricedExactlyAsItAlwaysWas()
    {
        // The other half, and the one that must not move: the numbers on a fresh machine are the
        // ones the README and the website quote.
        var fresh = new FirstRunState { Chosen = Capabilities.Close([Capability.Photos]), HebrewOffered = true };

        FirstRunRow photos = FirstRun.Rows(fresh).Single(r => r.Capability == Capability.Photos);
        Assert.Equal(Sizes.Human(ModelStore.TotalBytes(Capabilities.OwnModels(Capability.Photos))), photos.Size);
        Assert.NotEqual(0, FirstRun.TotalBytes(fresh));
    }

    [Fact]
    public void HalfACapabilityIsPricedAtTheHalfThatIsMissing()
    {
        // An ordinary state, not an edge case: a resumed download, or Speech on a machine that
        // already took Meaning and so already holds the e5 pair. Priced per capability it would
        // quote 547 MB where most of it is already here - and "installed" would be a lie.
        Model one = Capabilities.OwnModels(Capability.Photos)[0];
        var half = new FirstRunState
        {
            Chosen = Capabilities.Close([Capability.Photos]),
            HebrewOffered = true,
            OnDisk = new HashSet<string>([one.File], StringComparer.OrdinalIgnoreCase),
        };

        FirstRunRow photos = FirstRun.Rows(half).Single(r => r.Capability == Capability.Photos);
        Assert.NotEqual(FirstRun.InstalledLabel, photos.Size);
        Assert.Equal(
            Sizes.Human(ModelStore.TotalBytes(
                Capabilities.OwnModels(Capability.Photos).Where(m => m.File != one.File))),
            photos.Size);
    }

    [Fact]
    public void NothingLeftToFetchIsASentenceAndNeverZeroMegabytes()
    {
        // "0 MB to download" is arithmetically true and reads as a fault, on the screen a reinstall
        // over kept models always shows.
        string said = FirstRun.Summary(new FirstRunState
        {
            Chosen = Capabilities.Close([Capability.Photos]),
            HebrewOffered = true,
            ContentOn = true,
            OnDisk = Everything(),
        });

        Assert.DoesNotContain("0 MB", said, StringComparison.Ordinal);
        Assert.Contains("already on this machine", said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowsTheSummaryAndTheTilesAllAskTheSameDisk()
    {
        // Three numbers describing one download. They used to come from three expressions and only
        // one of them - none, in fact - consulted the disk.
        var s = new FirstRunState
        {
            Chosen = Capabilities.Close([Capability.Photos, Capability.Speech]),
            HebrewOffered = true,
            OnDisk = new HashSet<string>(
                Capabilities.OwnModels(Capability.Photos).Select(m => m.File), StringComparer.OrdinalIgnoreCase),
        };

        Assert.Equal(FirstRun.InstalledLabel,
                     FirstRun.Rows(s).Single(r => r.Capability == Capability.Photos).Size);

        // And the summary counts only what is really missing, which is the closed Speech set less
        // the Photos files already here.
        Assert.Equal(ModelStore.TotalBytes(FirstRun.NotHereYet(Capabilities.ModelsFor(s.Chosen), s.OnDisk)),
                     FirstRun.TotalBytes(s));
    }

    [Fact]
    public void TheButtonDoesNotOfferToFetchWhatIsAlreadyHere()
    {
        // "Get these" over a machine that already holds every file promises a download that will
        // not happen - the same defect as the sizes beside it, on the one control somebody presses
        // to agree to it. The press still writes the settings and registers the scheduled task, so
        // it is reworded rather than disabled.
        var here = new FirstRunState
        {
            Chosen = Capabilities.Close([Capability.Photos]),
            HebrewOffered = true,
            OnDisk = Everything(),
        };
        Assert.Equal(FirstRun.ContinueLabel, FirstRun.GoLabel(here));

        // Nothing chosen at all is the same fact by a different route, and it always was.
        Assert.Equal(FirstRun.ContinueLabel, FirstRun.GoLabel(new FirstRunState { HebrewOffered = true }));

        // And a real download still says what it is.
        Assert.Equal(FirstRun.GoLabel(FirstRunStage.Choosing),
                     FirstRun.GoLabel(here with { OnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase) }));
    }

    [Fact]
    public void WhatIsAlreadyInstalledOpensTicked()
    {
        // The screen opened with every row unticked over a folder that already held all 2.9 GB,
        // asking somebody to choose again from a list where every answer was already yes.
        //
        // And leaving one unticked did not even take the capability away: what Findra can read is
        // read from the FILES on disk, never from this selection, which only decides what gets
        // fetched. So an unticked row over a present model was a control whose two positions meant
        // the same thing.
        IReadOnlySet<Capability> ticked = FirstRun.AlreadyChosen(Everything(), hebrewOffered: true);

        foreach (Capability c in Capabilities.All) Assert.Contains(c, ticked);

        // An empty disk ticks nothing, which is the fresh install this screen was designed for.
        Assert.Empty(FirstRun.AlreadyChosen(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), hebrewOffered: true));
    }

    [Fact]
    public void TickingWhatIsHereDragsInWhatItStillNeeds()
    {
        // A machine holding the Whisper model but not the e5 pair opens with Speech ticked AND its
        // dependency ticked with it, because that is the truth: Speech needs both and one of them
        // still has to be fetched. Ticking only what is present would show Speech ready to go and
        // then quietly download 270 MB.
        var justSpeech = new HashSet<string>(
            Capabilities.OwnModels(Capability.Speech).Select(m => m.File), StringComparer.OrdinalIgnoreCase);

        IReadOnlySet<Capability> ticked = FirstRun.AlreadyChosen(justSpeech, hebrewOffered: true);

        Assert.Contains(Capability.Speech, ticked);
        Assert.Contains(Capability.Meaning, ticked);
        Assert.DoesNotContain(Capability.Photos, ticked);

        // And the screen prices what is actually left, which is the e5 pair and not the whole of
        // Speech.
        var s = new FirstRunState { Chosen = ticked, HebrewOffered = true, OnDisk = justSpeech };
        Assert.Equal(ModelStore.TotalBytes(FirstRun.NotHereYet(Capabilities.ModelsFor(ticked), justSpeech)),
                     FirstRun.TotalBytes(s));
        Assert.NotEqual(0, FirstRun.TotalBytes(s));
    }

    [Fact]
    public void HebrewIsNeverTickedOnAMachineThatIsNotOfferedIt()
    {
        // Its row is not drawn there, and a selection carrying a capability with no row would
        // price a download nobody can see and put the preset tiles on a set they do not match.
        IReadOnlySet<Capability> ticked = FirstRun.AlreadyChosen(Everything(), hebrewOffered: false);

        Assert.DoesNotContain(Capability.Hebrew, ticked);
        Assert.Contains(Capability.Speech, ticked);
    }
}
