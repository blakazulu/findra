using Findra;
using Xunit;

/// <summary>
/// The Models toggle page: every installed add-on can be turned off and removed, removing asks first,
/// and the question says what goes with it and what is kept.
/// </summary>
public class SettingsAddOnsTests
{
    private static CapabilitySet Have(params Capability[] c) => new(new HashSet<Capability>(c));

    private static SettingsState Page(CapabilitySet installed, params Capability[] off) =>
        new(Config.Default with { IndexContent = true, AddOnsOff = [.. off.Select(x => x.ToString())] })
        {
            Section = Section.AddOns,
            Installed = installed,
            HebrewOffered = true,
        };

    private static int RowOf(SettingsState s, Capability c) =>
        SettingsModel.Controls(s).ToList().FindIndex(r => r.Id == ControlId.AddOn && r.Tag == (int)c);

    [Fact]
    public void TurnOffTurnsItOffAndTurnOnTurnsItBackOn()
    {
        SettingsState s = Page(Have(Capability.Photos));
        SettingsOutcome off = SettingsModel.Apply(s, new PanelHit(PanelTarget.Option, RowOf(s, Capability.Photos), 0));
        Assert.Equal([nameof(Capability.Photos)], off.State.Config.AddOnsOff);
        Assert.Equal("Turn on", SettingsModel.Controls(off.State)[RowOf(off.State, Capability.Photos)].Options[0]);
        Assert.StartsWith("Off.", SettingsModel.Controls(off.State)[RowOf(off.State, Capability.Photos)].Note, StringComparison.Ordinal);

        SettingsOutcome on = SettingsModel.Apply(off.State, new PanelHit(PanelTarget.Option, RowOf(off.State, Capability.Photos), 0));
        Assert.Empty(on.State.Config.AddOnsOff);
    }

    [Fact]
    public void HebrewReadsOffWhileSpeechIsOffAndTurningItOnTurnsSpeechOnToo()
    {
        SettingsState s = Page(Have(Capability.Meaning, Capability.Speech, Capability.Hebrew), Capability.Speech);
        Control hebrew = SettingsModel.Controls(s)[RowOf(s, Capability.Hebrew)];
        Assert.Equal("Turn on", hebrew.Options[0]);
        Assert.Equal("Off, because Speech is off.", hebrew.Note);

        Config on = SettingsModel.ToggleAddOn(s.Config, Capability.Hebrew);
        Assert.Empty(on.AddOnsOff);
    }

    [Fact]
    public void RemoveAsksFirstAndDeletesNothingUntilAnswered()
    {
        SettingsState s = Page(Have(Capability.Photos));
        SettingsOutcome asked = SettingsModel.Apply(s, new PanelHit(PanelTarget.Option, RowOf(s, Capability.Photos), 1));
        Assert.Equal(SettingsAction.None, asked.Action);
        Assert.Equal(Capability.Photos, asked.State.Removing);
        Assert.True(asked.State.KeepFound);

        SettingsOutcome cancelled = SettingsModel.AnswerRemove(asked.State, RemoveTarget.Cancel);
        Assert.Equal(SettingsAction.None, cancelled.Action);
        Assert.Null(cancelled.State.Removing);
    }

    [Fact]
    public void RemoveKeepsWhatWasFoundUnlessTheTickIsTakenOff()
    {
        SettingsState asked = Page(Have(Capability.Photos)) with { Removing = Capability.Photos };

        SettingsOutcome keep = SettingsModel.AnswerRemove(asked, RemoveTarget.Remove);
        Assert.Equal(SettingsAction.RemoveAddOn, keep.Action);
        Assert.Equal("Photos|keep", keep.Argument);
        Assert.Null(keep.State.Removing);
        Assert.Equal(Capability.Photos, keep.State.RemovingNow);

        SettingsState unticked = SettingsModel.AnswerRemove(asked, RemoveTarget.Keep).State;
        Assert.False(unticked.KeepFound);
        Assert.Equal("Photos|forget", SettingsModel.AnswerRemove(unticked, RemoveTarget.Remove).Argument);
    }

    [Fact]
    public void AnAddOnBeingRemovedSaysSoAndOffersNothing()
    {
        SettingsState s = Page(Have(Capability.Photos)) with { RemovingNow = Capability.Photos };
        Control row = SettingsModel.Controls(s).Single(r => r.Id == ControlId.AddOn && r.Tag == (int)Capability.Photos);
        Assert.Equal(ControlKind.Text, row.Kind);
        Assert.Equal("Removing...", row.Value);
    }

    [Fact]
    public void TheQuestionSaysWhatComesBackAndWhatGoesWithIt()
    {
        CapabilitySet all = Have(Capability.Meaning, Capability.Speech, Capability.Hebrew);
        string body = RemovePrompt.Body(Capability.Speech, all);
        Assert.StartsWith("This frees ", body, StringComparison.Ordinal);
        Assert.Contains("This also removes Speech in Hebrew.", body, StringComparison.Ordinal);
        Assert.Equal("Remove", RemovePrompt.GoLabel(Capability.Speech, all));
    }

    [Fact]
    public void MeaningUnderSpeechIsTurnedOffBecauseItsFilesCannotGo()
    {
        CapabilitySet both = Have(Capability.Meaning, Capability.Speech);
        Assert.Contains("nothing is freed", RemovePrompt.Body(Capability.Meaning, both), StringComparison.Ordinal);
        Assert.Equal("Turn off", RemovePrompt.GoLabel(Capability.Meaning, both));
    }

    [Fact]
    public void HebrewHasNothingOfItsOwnToKeepSoItAsksNoSuchQuestion()
    {
        Assert.Equal("", RemovePrompt.KeepLabel(Capability.Hebrew));
        SettingsState asked = Page(Have(Capability.Meaning, Capability.Speech, Capability.Hebrew)) with { Removing = Capability.Hebrew };
        // The tick is not there to take off, so the answer is always "keep".
        Assert.Equal(SettingsAction.None, SettingsModel.AnswerRemove(asked, RemoveTarget.Keep).Action);
        Assert.Equal("Hebrew|keep", SettingsModel.AnswerRemove(asked with { KeepFound = false }, RemoveTarget.Remove).Argument);
    }

    [Fact]
    public void NothingOnThePageUsesADashThatIsNotAHyphen()
    {
        SettingsState s = Page(Have(Capability.Photos, Capability.Meaning, Capability.Speech, Capability.Hebrew), Capability.Speech);
        foreach (Control c in SettingsModel.Controls(s))
            Assert.DoesNotContain('—', c.Label + c.Value + c.Note + string.Concat(c.Options));
        foreach (Capability c in Capabilities.All)
            Assert.DoesNotContain('—', RemovePrompt.Title(c) + RemovePrompt.Body(c, s.Installed) + RemovePrompt.KeepLabel(c));
    }
}
