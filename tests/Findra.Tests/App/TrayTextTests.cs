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
