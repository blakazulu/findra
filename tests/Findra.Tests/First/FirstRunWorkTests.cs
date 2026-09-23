using Findra;
using Xunit;

/// <summary>
/// The card that says what Findra is doing after a welcome-screen button: a step list, ticked as
/// each step really finishes, with nothing behind it answering a click.
/// </summary>
public class FirstRunWorkTests
{
    [Fact]
    public void SettingUpNamesEveryStepAndTheWindowsPrompt()
    {
        FirstRunWork w = FirstRunWork.SettingUp(downloading: true);
        Assert.Equal("Setting up Findra", w.Title);
        Assert.Equal(4, w.Steps.Count);
        Assert.Contains(w.Steps, s => s.Label.Contains("Windows may ask for permission", StringComparison.Ordinal));
        Assert.Equal("Starting the download", w.Steps[^1].Label);
        // Nothing to fetch, no download step: a step that is ticked at once is noise.
        Assert.DoesNotContain(FirstRunWork.SettingUp(downloading: false).Steps, s => s.Label.Contains("download", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFirstStepIsUnderWayAndTheRestAreWaiting()
    {
        FirstRunWork w = FirstRunWork.SettingUp(downloading: true);
        Assert.Equal(StepState.Now, w.Steps[0].State);
        Assert.All(w.Steps.Skip(1), s => Assert.Equal(StepState.Todo, s.State));
    }

    [Fact]
    public void AStepFinishingOutOfOrderIsTickedAndTheSpinnerStaysOnTheFirstUnfinishedOne()
    {
        // Name search waits for the Windows prompt, so the hotkey step can finish before it.
        FirstRunWork w = FirstRunWork.SettingUp(downloading: true).Done(0).Done(2);
        Assert.Equal(StepState.Done, w.Steps[0].State);
        Assert.Equal(StepState.Now, w.Steps[1].State);
        Assert.Equal(StepState.Done, w.Steps[2].State);
        Assert.Equal(StepState.Todo, w.Steps[3].State);
        Assert.False(w.Finished);
        Assert.True(w.Done(1).Done(3).Finished);
    }

    [Fact]
    public void AStepThatIsNotThereIsIgnored()
    {
        FirstRunWork w = FirstRunWork.SettingUp(downloading: false);
        Assert.Equal(w, w.Done(-1));
        Assert.Equal(w, w.Done(99));
    }

    [Fact]
    public void ClosingSaysWhetherReadingStarts()
    {
        Assert.Contains(FirstRunWork.Closing(startReading: true).Steps, s => s.Label == "Starting to read your files");
        Assert.DoesNotContain(FirstRunWork.Closing(startReading: false).Steps, s => s.Label == "Starting to read your files");
    }

    [Fact]
    public void NothingBehindTheCardAnswersAClick()
    {
        var s = new FirstRunState { Work = FirstRunWork.SettingUp(downloading: true) };
        for (float y = 0; y < FirstRunLayout.Height; y += 7)
            for (float x = 0; x < FirstRunLayout.Width; x += 7)
                Assert.Equal(FirstRunTarget.None, FirstRunLayout.HitTest(x, y, s).Target);
    }

    [Fact]
    public void NoLabelUsesADashThatIsNotAHyphen()
    {
        foreach (FirstRunWork w in new[] { FirstRunWork.SettingUp(true), FirstRunWork.Closing(true) })
            Assert.DoesNotContain(w.Steps, s => s.Label.Contains('—') || s.Label.Contains('–'));
    }
}
