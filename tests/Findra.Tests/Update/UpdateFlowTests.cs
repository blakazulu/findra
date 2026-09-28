using Findra;
using Xunit;

public class UpdateFlowTests
{
    private static readonly ReleaseAsset Asset =
        new("findra-setup-x64.exe", "https://github.com/a.exe", 85_727_274, new string('a', 64));

    private static UpdateView Available(string? source, ReleaseAsset? installer) =>
        UpdateFlow.Checked(UpdateFlow.Start("1.0.0", source),
            new UpdateResult(UpdateState.Available, "1.1.0", null, Config.Default, installer));

    [Fact]
    public void ItOpensChecking() =>
        Assert.Equal(UpdateStep.Checking, UpdateFlow.Start("1.0.0", "installer").Step);

    [Theory]
    [InlineData(UpdateState.Current, UpdateStep.UpToDate)]
    [InlineData(UpdateState.Available, UpdateStep.Available)]
    [InlineData(UpdateState.Unknown, UpdateStep.Unreachable)]
    public void TheCheckDecidesTheNextStep(UpdateState state, UpdateStep step) =>
        Assert.Equal(step, UpdateFlow.Checked(UpdateFlow.Start("1.0.0", null),
            new UpdateResult(state, "1.1.0", null, Config.Default)).Step);

    [Theory]
    [InlineData(UpdateState.Disabled)]
    [InlineData(UpdateState.NotDue)]
    public void AManualCheckNeverAnswersOffOrNotDue(UpdateState state) =>
        Assert.Throws<InvalidOperationException>(() => UpdateFlow.Checked(UpdateFlow.Start("1.0.0", null),
            new UpdateResult(state, null, null, Config.Default)));

    [Theory]
    [InlineData("installer", true, UpdateRoute.Installer)]
    [InlineData(null, true, UpdateRoute.Installer)]
    [InlineData("unknown", true, UpdateRoute.Installer)]
    [InlineData("winget", false, UpdateRoute.Winget)]
    [InlineData("WINGET", false, UpdateRoute.Winget)]
    [InlineData("source", true, UpdateRoute.Releases)]
    [InlineData("installer", false, UpdateRoute.Releases)]
    public void UpdateNowFollowsHowThisCopyWasInstalled(string? source, bool hasInstaller, UpdateRoute route) =>
        Assert.Equal(route, UpdateFlow.Route(Available(source, hasInstaller ? Asset : null)));

    [Fact]
    public void UpdateNowOnAnInstalledCopyDownloads()
    {
        UpdateMove m = UpdateFlow.Go(Available("installer", Asset));
        Assert.Equal(UpdateAction.Download, m.Action);
        Assert.Equal(UpdateStep.Downloading, m.View.Step);
        Assert.Equal(Asset.Size, m.View.Total);
    }

    [Fact]
    public void UpdateNowOnAWingetCopyRunsWinget()
    {
        UpdateMove m = UpdateFlow.Go(Available("winget", null));
        Assert.Equal((UpdateStep.Winget, UpdateAction.RunWinget), (m.View.Step, m.Action));
    }

    [Fact]
    public void OpenReleasesOpensThePageAndLeavesTheViewAlone()
    {
        UpdateView v = Available("source", Asset);
        Assert.Equal(new UpdateMove(v, UpdateAction.OpenReleases), UpdateFlow.Go(v));
    }

    [Fact]
    public void ACheckedDownloadGoesStraightToTheInstaller()
    {
        UpdateView downloading = UpdateFlow.Go(Available("installer", Asset)).View;
        UpdateMove m = UpdateFlow.Downloaded(downloading, DownloadResult.Ok(@"C:\u\findra-setup-x64.exe"));

        Assert.Equal((UpdateStep.Installing, UpdateAction.RunInstaller), (m.View.Step, m.Action));
        Assert.Equal(@"C:\u\findra-setup-x64.exe", m.View.DownloadedTo);
    }

    [Theory]
    [InlineData(DownloadFailure.Digest, "checksum")]
    [InlineData(DownloadFailure.Short, "incomplete")]
    [InlineData(DownloadFailure.Host, "other than GitHub")]
    [InlineData(DownloadFailure.TooLarge, "larger")]
    [InlineData(DownloadFailure.Network, "did not get through")]
    public void AFailedDownloadSaysWhyAndThatNothingWasInstalled(DownloadFailure why, string words)
    {
        UpdateView downloading = UpdateFlow.Go(Available("installer", Asset)).View;
        UpdateView v = UpdateFlow.Downloaded(downloading, DownloadResult.Failed(why, "")).View;

        Assert.Equal(UpdateStep.DownloadFailed, v.Step);
        Assert.Contains(words, v.Problem, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was installed.", v.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelGoesBackToTheOfferAndStopsTheDownload()
    {
        UpdateView downloading = UpdateFlow.Go(Available("installer", Asset)).View;
        UpdateMove m = UpdateFlow.Close(downloading);

        Assert.Equal((UpdateStep.Available, UpdateAction.CancelDownload), (m.View.Step, m.Action));
    }

    [Fact]
    public void ADownloadThatEndsAfterCancelChangesNothing()
    {
        UpdateView offer = UpdateFlow.Close(UpdateFlow.Go(Available("installer", Asset)).View).View;
        Assert.Equal(new UpdateMove(offer, UpdateAction.None),
            UpdateFlow.Downloaded(offer, DownloadResult.Failed(DownloadFailure.Cancelled, "")));
    }

    [Fact]
    public void TryAgainAfterUnreachableChecksAgain() =>
        Assert.Equal(UpdateAction.Check, UpdateFlow.Go(UpdateFlow.Checked(UpdateFlow.Start("1.0.0", null),
            new UpdateResult(UpdateState.Unknown, null, null, Config.Default))).Action);

    [Theory]
    [InlineData(UpdateStep.DownloadFailed)]
    [InlineData(UpdateStep.InstallerDidNotRun)]
    public void TryAgainAfterAFailedDownloadOrInstallDownloadsAgain(UpdateStep step)
    {
        UpdateView v = Available("installer", Asset) with { Step = step, Problem = "x" };
        UpdateMove m = UpdateFlow.Go(v);

        Assert.Equal((UpdateStep.Downloading, UpdateAction.Download), (m.View.Step, m.Action));
        Assert.Null(m.View.Problem);
    }

    [Theory]
    [InlineData(HandoffOutcome.DidNotRun, UpdateStep.InstallerDidNotRun)]
    [InlineData(HandoffOutcome.WingetFailed, UpdateStep.WingetFailed)]
    [InlineData(HandoffOutcome.WingetNothingNewer, UpdateStep.WingetFailed)]
    [InlineData(HandoffOutcome.WingetMissing, UpdateStep.WingetFailed)]
    public void AHandOffThatComesBackIsAProblemToShow(HandoffOutcome outcome, UpdateStep step)
    {
        UpdateView v = UpdateFlow.HandedOff(Available("installer", Asset) with { Step = UpdateStep.Installing },
                                            new Handoff(outcome, "said"));
        Assert.Equal((step, "said"), (v.Step, v.Problem));
    }

    [Theory]
    [InlineData(UpdateStep.Checking, true)]
    [InlineData(UpdateStep.Available, true)]
    [InlineData(UpdateStep.Downloading, true)]
    [InlineData(UpdateStep.Installing, false)]
    [InlineData(UpdateStep.Winget, false)]
    public void OnlyAHandOffInFlightCannotBeClosed(UpdateStep step, bool closable) =>
        Assert.Equal(closable, UpdateFlow.Closable(UpdateFlow.Start("1.0.0", null) with { Step = step }));

    [Theory]
    [InlineData(UpdateStep.Checking)]
    [InlineData(UpdateStep.Installing)]
    [InlineData(UpdateStep.Winget)]
    [InlineData(UpdateStep.UpToDate)]
    [InlineData(UpdateStep.WingetFailed)]
    [InlineData(UpdateStep.Downloading)]
    public void GoDoesNothingWhereThereIsNoGoButton(UpdateStep step)
    {
        UpdateView v = Available("installer", Asset) with { Step = step };
        Assert.Equal(UpdateAction.None, UpdateFlow.Go(v).Action);
    }

    [Fact]
    public void ProgressMovesOnlyADownload()
    {
        UpdateView downloading = UpdateFlow.Go(Available("installer", Asset)).View;
        Assert.Equal(1234, UpdateFlow.Progress(downloading, 1234, Asset.Size).Got);

        UpdateView offer = Available("installer", Asset);
        Assert.Equal(offer, UpdateFlow.Progress(offer, 1234, Asset.Size));
    }
}
