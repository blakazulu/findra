using Findra;
using SkiaSharp;
using Xunit;

public class UpdatePromptTests
{
    private static readonly ReleaseAsset Asset =
        new("findra-setup-x64.exe", "https://github.com/a.exe", 85_727_274, new string('a', 64));

    private static UpdateView At(UpdateStep step, string? source = "installer", ReleaseAsset? installer = null) =>
        UpdateFlow.Start("1.0.0", source) with { Step = step, Latest = "v1.1.0", Installer = installer ?? Asset, Problem = "Something went wrong." };

    public static TheoryData<UpdateStep> Steps()
    {
        var d = new TheoryData<UpdateStep>();
        foreach (UpdateStep s in Enum.GetValues<UpdateStep>()) d.Add(s);
        return d;
    }

    [Theory, MemberData(nameof(Steps))]
    public void EveryStepHasATitleABodyAndItsButtonsLabelled(UpdateStep step)
    {
        UpdateView v = At(step);
        Assert.False(string.IsNullOrWhiteSpace(UpdatePrompt.Title(v)));
        Assert.False(string.IsNullOrWhiteSpace(UpdatePrompt.Body(v)));
        int buttons = UpdatePrompt.Buttons(v);
        if (buttons >= 1) Assert.False(string.IsNullOrWhiteSpace(UpdatePrompt.CloseLabel(v)));
        if (buttons == 2) Assert.False(string.IsNullOrWhiteSpace(UpdatePrompt.GoLabel(v)));
    }

    [Theory]
    [InlineData(UpdateStep.Checking, 0)]
    [InlineData(UpdateStep.UpToDate, 1)]
    [InlineData(UpdateStep.Unreachable, 2)]
    [InlineData(UpdateStep.Available, 2)]
    [InlineData(UpdateStep.Downloading, 1)]
    [InlineData(UpdateStep.DownloadFailed, 2)]
    [InlineData(UpdateStep.Installing, 0)]
    [InlineData(UpdateStep.InstallerDidNotRun, 2)]
    [InlineData(UpdateStep.Winget, 0)]
    [InlineData(UpdateStep.WingetFailed, 1)]
    public void EachStepHasTheButtonsTheSpecGivesIt(UpdateStep step, int buttons) =>
        Assert.Equal(buttons, UpdatePrompt.Buttons(At(step)));

    [Theory]
    [InlineData("installer", true, "Update now")]
    [InlineData("winget", false, "Update now")]
    [InlineData("source", true, "Open releases")]
    [InlineData("installer", false, "Open releases")]
    public void TheOfferSaysWhatThisCopyCanDo(string source, bool hasInstaller, string go)
    {
        UpdateView v = At(UpdateStep.Available, source) with { Installer = hasInstaller ? Asset : null };
        Assert.Equal(go, UpdatePrompt.GoLabel(v));
        Assert.Equal("Not now", UpdatePrompt.CloseLabel(v));
    }

    [Fact]
    public void AnInstalledCopysOfferNamesTheDownloadSizeAndThePermissionPrompt()
    {
        string body = UpdatePrompt.Body(At(UpdateStep.Available));
        Assert.Contains("82 MB", body, StringComparison.Ordinal);
        Assert.Contains("permission", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AWingetCopysOfferNamesTheCommand() =>
        Assert.Contains("winget upgrade blakazulu.Findra", UpdatePrompt.Body(At(UpdateStep.Available, "winget", null)),
                        StringComparison.Ordinal);

    [Fact]
    public void TheHeadingNamesTheNewVersionWithoutTheTagsV() =>
        Assert.Equal("Findra 1.1.0 is available", UpdatePrompt.Title(At(UpdateStep.Available)));

    [Fact]
    public void AWingetCopyIsUpToDateWithTheCatalogueAndIsToldSo() =>
        Assert.Contains("winget", UpdatePrompt.Body(At(UpdateStep.UpToDate, "winget")), StringComparison.Ordinal);

    [Theory]
    [InlineData(UpdateStep.DownloadFailed)]
    [InlineData(UpdateStep.InstallerDidNotRun)]
    [InlineData(UpdateStep.WingetFailed)]
    public void AProblemIsShownInTheWordsItCameWith(UpdateStep step) =>
        Assert.Equal("Something went wrong.", UpdatePrompt.Body(At(step)));

    [Fact]
    public void TheDownloadCountsInMegabytes()
    {
        UpdateView v = At(UpdateStep.Downloading) with { Got = 36_000_000, Total = Asset.Size };
        Assert.Equal("34 MB of 82 MB", UpdatePrompt.Count(v));
        Assert.True(UpdatePrompt.HasBar(v));
        Assert.False(UpdatePrompt.HasBar(At(UpdateStep.Available)));
    }

    [Fact]
    public void TheAffirmativeButtonIsAlwaysTheRightmostOne()
    {
        SKRect panel = UpdatePrompt.Panel(2, 2, bar: false);
        Assert.True(UpdatePrompt.Button(panel, 1, 2).Left > UpdatePrompt.Button(panel, 0, 2).Right);
        Assert.Equal(UpdatePrompt.Button(panel, 1, 2).Right, UpdatePrompt.Button(panel, 0, 1).Right);
    }

    [Fact]
    public void OnlyTheButtonsAnswerAClick()
    {
        SKRect panel = UpdatePrompt.Panel(2, 2, bar: false);
        SKRect go = UpdatePrompt.Button(panel, 1, 2);
        Assert.Equal(UpdatePromptTarget.Go, UpdatePrompt.HitTest(go.MidX, go.MidY, panel, 2));
        Assert.Equal(UpdatePromptTarget.None, UpdatePrompt.HitTest(panel.MidX, panel.Top + 10, panel, 2));
    }

    [Fact]
    public void TheSecondPressOfADoubleClickPressesNothing()
    {
        // Update now brings up the download, whose one button, Cancel, sits almost exactly where
        // Update now was: a double-click on Update now would start the download and cancel it.
        SKRect offer = UpdatePrompt.Panel(3, 2, bar: false);
        SKRect updateNow = UpdatePrompt.Button(offer, 1, 2);
        SKRect downloading = UpdatePrompt.Panel(1, 1, bar: true);

        Assert.Equal(UpdatePromptTarget.Close,
            UpdatePrompt.Press(updateNow.MidX, updateNow.Bottom - 4, downloading, 1, clickCount: 1));
        Assert.Equal(UpdatePromptTarget.None,
            UpdatePrompt.Press(updateNow.MidX, updateNow.Bottom - 4, downloading, 1, clickCount: 2));
    }

    [Fact]
    public void TheWindowIsTallerWithButtonsAndTallerStillWithTheBar()
    {
        float none = UpdatePrompt.Panel(2, 0, bar: false).Height;
        float buttons = UpdatePrompt.Panel(2, 1, bar: false).Height;
        float bar = UpdatePrompt.Panel(2, 1, bar: true).Height;
        Assert.True(none < buttons && buttons < bar);
        Assert.Equal(0f, UpdatePrompt.Panel(2, 1, bar: true).Left);
        Assert.Equal(0f, UpdatePrompt.Panel(2, 1, bar: true).Top);
    }
}
