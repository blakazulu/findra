using Findra;

using SkiaSharp;

using Xunit;

/// <summary>
/// The panel that answers "Check now".
///
/// <para>The answer used to land in a text row three lines above the button that asked for it,
/// which is the quietest place in the window to put the only piece of news this product ever has.
/// What is asserted here is mostly that the panel tells the truth about how THIS copy would be
/// updated - a button offering a winget command to somebody who built from source is worse than
/// no button at all.</para>
/// </summary>
public class UpdatePromptTests
{
    private static SKRect PanelFor(UpdatePromptState state) =>
        UpdatePrompt.Panel(RailLayout.Width, 700f, bodyLines: 3, UpdatePrompt.Buttons(state));

    [Fact]
    public void OnlyTheNewsHasTwoButtons()
    {
        // A second pill that does exactly what the first does is a choice with no difference in
        // it - the same rule the first-run screen's last act is written under.
        Assert.Equal(2, UpdatePrompt.Buttons(UpdatePromptState.Available));
        Assert.Equal(1, UpdatePrompt.Buttons(UpdatePromptState.UpToDate));
        Assert.Equal(1, UpdatePrompt.Buttons(UpdatePromptState.Unreachable));

        // And none while the request is out: there is nothing to decide yet, and a Cancel that
        // cannot call back a ten-second HTTP request would be a lie.
        Assert.Equal(0, UpdatePrompt.Buttons(UpdatePromptState.Asking));
        Assert.Equal(0, UpdatePrompt.Buttons(UpdatePromptState.None));
    }

    [Theory]
    [InlineData("winget", "Update now")]
    [InlineData("installer", "Open releases")]
    [InlineData("source", "Open releases")]
    [InlineData("unknown", "Open releases")]
    [InlineData(null, "Open releases")]
    public void TheButtonOffersWhatThisCopyCanActuallyDo(string? source, string want)
    {
        // Only a winget copy can be upgraded by a command. Everything else gets sent to the page,
        // because "Update now" on a source build would promise something Findra cannot do and the
        // rule it would be breaking - never install anything itself - is the one that keeps a
        // running executable and an elevated logon task out of harm's way.
        Assert.Equal(want, UpdatePrompt.GoLabel(UpdatePromptState.Available, source));

        // And no state but the news offers it at all.
        foreach (UpdatePromptState s in new[]
                 { UpdatePromptState.UpToDate, UpdatePromptState.Unreachable, UpdatePromptState.Asking })
        {
            Assert.Equal("", UpdatePrompt.GoLabel(s, source));
        }
    }

    [Fact]
    public void TheBodySaysHowThisCopyIsUpdatedAndNeverTheWrongWay()
    {
        string winget = UpdatePrompt.Body(UpdatePromptState.Available, "0.1.0", "v0.2.0", "winget");
        Assert.Contains("winget upgrade blakazulu.Findra", winget, StringComparison.Ordinal);

        // A source build must not be told to run a winget command it has no package for, and an
        // installed copy must not either - it has one, but the upgrade it wants is the installer
        // it already knows how to run.
        foreach (string other in new[] { "source", "installer" })
        {
            string body = UpdatePrompt.Body(UpdatePromptState.Available, "0.1.0", "v0.2.0", other);
            Assert.DoesNotContain("winget upgrade", body, StringComparison.Ordinal);
            Assert.Contains("releases page", body, StringComparison.Ordinal);
        }

        // The unknown arm is the only one allowed to name both, because it is the only one that
        // does not know which is right.
        string unknown = UpdatePrompt.Body(UpdatePromptState.Available, "0.1.0", "v0.2.0", null);
        Assert.Contains("installer", unknown, StringComparison.Ordinal);
        Assert.Contains("winget", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeadingNamesTheNewVersionWithoutTheTagsV()
    {
        // GitHub returns v0.2.0 and a person reads 0.2.0. Everything else about the string is left
        // alone - inventing a shape for it is how a version nobody recognises reaches the screen.
        Assert.Equal("Findra 0.2.0 is available", UpdatePrompt.Title(UpdatePromptState.Available, "v0.2.0"));
        Assert.Equal("Findra 0.2.0 is available", UpdatePrompt.Title(UpdatePromptState.Available, "0.2.0"));

        Assert.Equal(UpdatePrompt.UpToDateTitle, UpdatePrompt.Title(UpdatePromptState.UpToDate, null));
        Assert.Contains("0.1.0 is the newest release",
                        UpdatePrompt.Body(UpdatePromptState.UpToDate, "0.1.0", null, "winget"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheAffirmativeButtonIsAlwaysTheRightmostOne()
    {
        // Whether there are one or two. A person reaches for the same corner either way, and the
        // one-button states put Close there because it is the only answer they have.
        SKRect two = PanelFor(UpdatePromptState.Available);
        Assert.True(UpdatePrompt.Button(two, 1, 2).Left > UpdatePrompt.Button(two, 0, 2).Left);
        Assert.Equal(two.Right - UpdatePrompt.Pad, UpdatePrompt.Button(two, 1, 2).Right, 3);

        SKRect one = PanelFor(UpdatePromptState.UpToDate);
        Assert.Equal(one.Right - UpdatePrompt.Pad, UpdatePrompt.Button(one, 0, 1).Right, 3);

        // Neither overlaps the other, which a "from the right" expression gets wrong easily.
        Assert.True(UpdatePrompt.Button(two, 0, 2).Right <= UpdatePrompt.Button(two, 1, 2).Left);
    }

    [Fact]
    public void NothingButTheButtonsAnswersAClickWhileThePanelIsUp()
    {
        SKRect panel = PanelFor(UpdatePromptState.Available);

        Assert.Equal(UpdatePromptTarget.Close,
            UpdatePrompt.HitTest(UpdatePrompt.Button(panel, 0, 2).MidX, UpdatePrompt.Button(panel, 0, 2).MidY, panel, 2));
        Assert.Equal(UpdatePromptTarget.Go,
            UpdatePrompt.HitTest(UpdatePrompt.Button(panel, 1, 2).MidX, UpdatePrompt.Button(panel, 1, 2).MidY, panel, 2));

        // The panel's own body is not a target: a click on the question must not answer it. Nor is
        // the dimmed pane behind it - a control somebody cannot see must not take a press.
        Assert.Equal(UpdatePromptTarget.None, UpdatePrompt.HitTest(panel.MidX, panel.Top + 30, panel, 2));
        Assert.Equal(UpdatePromptTarget.None, UpdatePrompt.HitTest(10, 10, panel, 2));

        // And while the request is out there is nothing to press at all, including where the
        // buttons would be if there were any.
        SKRect asking = PanelFor(UpdatePromptState.Asking);
        Assert.Equal(UpdatePromptTarget.None,
            UpdatePrompt.HitTest(asking.Right - UpdatePrompt.Pad - 10, asking.Bottom - UpdatePrompt.Pad - 10, asking, 0));
    }

    [Fact]
    public void ThePanelIsCentredAndTallerWhenItHasButtons()
    {
        SKRect withButtons = UpdatePrompt.Panel(RailLayout.Width, 700f, 3, 2);
        SKRect without = UpdatePrompt.Panel(RailLayout.Width, 700f, 3, 0);

        Assert.True(withButtons.Height > without.Height, "buttons need a band the asking state does not");
        Assert.Equal(RailLayout.Width / 2f, withButtons.MidX, 3);
        Assert.Equal(350f, withButtons.MidY, 3);

        // A longer body makes a taller panel, or the text runs out of the bottom of it.
        Assert.True(UpdatePrompt.Height(6, 2) > UpdatePrompt.Height(2, 2));
    }

    [Fact]
    public void EveryPromptStateHasATitleABodyAndALabelSet()
    {
        // The switches all throw on an unknown arm, so a state added to the enum and forgotten in
        // one of them is a crash rather than a blank panel. This is what walks every arm.
        foreach (UpdatePromptState s in Enum.GetValues<UpdatePromptState>())
        {
            _ = UpdatePrompt.Title(s, "v9.9.9");
            _ = UpdatePrompt.Body(s, "0.1.0", "v9.9.9", "winget");
            _ = UpdatePrompt.CloseLabel(s);
            _ = UpdatePrompt.GoLabel(s, "winget");
            _ = UpdatePrompt.Buttons(s);
        }
    }
}
