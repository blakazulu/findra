using Findra;

namespace Findra.Tests.App;

public class TrayTextTests
{
    [Fact]
    public void TheTooltipNamesTheVersionAndTheHotkeyItGot()
    {
        string tip = TrayText.Tooltip("1.2.0", "Ctrl+Alt+Space", UpdateState.NotDue, null);
        Assert.Contains("Findra 1.2.0", tip);
        Assert.Contains("Hotkey: Ctrl+Alt+Space", tip);
    }

    [Fact]
    public void AHotkeyThatCouldNotBeRegisteredIsSaidPlainly()
    {
        // The worst outcome is a hotkey that does nothing with no explanation anywhere.
        string tip = TrayText.Tooltip("1.2.0", null, UpdateState.NotDue, null);
        Assert.Contains("No hotkey could be registered", tip);
    }

    [Fact]
    public void AnAvailableUpdateNamesTheVersion()
        => Assert.Contains("Update available: 1.3.0",
            TrayText.Tooltip("1.2.0", "Alt+Space", UpdateState.Available, "1.3.0"));

    [Fact]
    public void BeingCurrentIsWorthSaying()
        => Assert.Contains("Up to date", TrayText.Tooltip("1.2.0", "Alt+Space", UpdateState.Current, "1.2.0"));

    [Fact]
    public void AnUnknownOrDisabledStateAddsNoLine()
    {
        // A broken network is not something the user has to acknowledge, and neither is a check
        // they turned off.
        Assert.Equal(2, TrayText.Tooltip("1.2.0", "Alt+Space", UpdateState.Unknown, null).Split('\n').Length);
        Assert.Equal(2, TrayText.Tooltip("1.2.0", "Alt+Space", UpdateState.Disabled, null).Split('\n').Length);
    }

    [Fact]
    public void AnAvailableUpdateWithNoVersionSaysNothingRatherThanNothingUseful()
        => Assert.Equal(2, TrayText.Tooltip("1.2.0", "Alt+Space", UpdateState.Available, null).Split('\n').Length);
}

public class UpdateMemoryTests
{
    [Fact]
    public void ARememberedNewerTagStillReadsAsAnUpdateOnALaunchThatChecksNothing()
    {
        // 23 launches in 24 are not due for a check. Going quiet about a waiting update for a
        // whole day is the defect this remembers its way out of.
        Assert.Equal(UpdateState.Available, UpdateMemory.Remembered("1.2.0", "1.3.0"));
        Assert.Contains("Update available: 1.3.0",
            TrayText.Tooltip("1.2.0", "Alt+Space", UpdateMemory.Remembered("1.2.0", "1.3.0"), "1.3.0"));
    }

    [Fact]
    public void ARememberedMatchingTagReadsAsUpToDate()
    {
        Assert.Equal(UpdateState.Current, UpdateMemory.Remembered("1.2.0", "1.2.0"));
        Assert.Equal(UpdateState.Current, UpdateMemory.Remembered("1.2.0", "v1.2.0"));
    }

    [Fact]
    public void ARunningBuildAheadOfTheLastReleaseIsNotAnUpdate()
        => Assert.Equal(UpdateState.Current, UpdateMemory.Remembered("1.10.0", "1.9.0"));

    [Fact]
    public void NothingRememberedSaysNothing()
    {
        Assert.Equal(UpdateState.NotDue, UpdateMemory.Remembered("1.2.0", null));
        Assert.Equal(UpdateState.NotDue, UpdateMemory.Remembered("1.2.0", "  "));
    }

    [Fact]
    public void ATagThatDoesNotParseIsNotReportedAsUpToDate()
    {
        // Compare returns zero for an unparseable tag, and saying "up to date" on no information
        // is worse than saying nothing.
        Assert.Equal(UpdateState.Unknown, UpdateMemory.Remembered("1.2.0", "nightly"));
        Assert.Equal(2, TrayText.Tooltip("1.2.0", "Alt+Space", UpdateMemory.Remembered("1.2.0", "nightly"), "nightly")
            .Split('\n').Length);
    }

    [Fact]
    public void TheMenuItemSaysWhatTheCheckFound()
    {
        Assert.Equal("Checked: 1.3.0 available", UpdateMemory.CheckedHeader(UpdateState.Available, "1.3.0"));
        Assert.Equal("Checked: up to date", UpdateMemory.CheckedHeader(UpdateState.Current, "1.2.0"));
        Assert.Equal("Checked: could not reach GitHub", UpdateMemory.CheckedHeader(UpdateState.Unknown, null));
        Assert.Equal("Update checks are turned off", UpdateMemory.CheckedHeader(UpdateState.Disabled, null));
    }

    [Fact]
    public void AnAvailableUpdateWithNoTagDoesNotClaimAVersionItDoesNotHave()
        => Assert.Equal("Checked: could not reach GitHub", UpdateMemory.CheckedHeader(UpdateState.Available, null));

    [Fact]
    public void TheTooltipSaysWhatTheIndexIsDoing()
    {
        // The same sentence the capsule shows under its bar, so hovering the tray and glancing at
        // the capsule cannot disagree about how far the index has got.
        string tip = TrayText.Tooltip("1.2.0", "Alt+Space", UpdateState.Current, "1.2.0",
                                      IndexStatus.Line(true, "indexing", 1_333, 640, true, false));
        string[] lines = [.. tip.Split(Environment.NewLine)];

        Assert.Equal(4, lines.Length);
        Assert.Contains("1,333", tip, StringComparison.Ordinal);
        Assert.Contains("640", tip, StringComparison.Ordinal);

        // ABOVE the update line. Windows truncates a tray tooltip, and the update state is the
        // same all day while this one moves every second - so a truncation should lose the still
        // thing rather than the moving one.
        Assert.Equal("Indexing 1,333 · 640 done", lines[2]);
        Assert.Equal("Up to date", lines[3]);
    }

    [Fact]
    public void NothingToSayAboutTheIndexAddsNoLine()
    {
        // Reading turned off, or a session where no reading has been taken yet. A blank line in a
        // tooltip is a gap somebody reads as a fault.
        foreach (string? nothing in new[] { null, "", "   " })
            Assert.Equal(2, TrayText.Tooltip("1.2.0", "Alt+Space", UpdateState.NotDue, null, nothing)
                                    .Split(Environment.NewLine).Length);
    }
}
