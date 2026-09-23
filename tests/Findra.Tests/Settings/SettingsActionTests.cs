using Findra;
using Xunit;

/// <summary>
/// The last link in the chain from a click to the machine.
///
/// <para>Task 5 proves every row answers a click with a state change or an action. This proves
/// every action reaches something. Without it the two halves are a closed loop that satisfies its
/// own tests while a switch in a Window subclass quietly drops half the cases - which is exactly
/// how five controls in the first draft came to be drawn and dead.</para>
/// </summary>
public class SettingsActionTests
{
    private sealed class Recorder : ISettingsHost
    {
        public List<string> Calls { get; } = [];
        public void OpenPalettesFile(string path) => Calls.Add("palettes:" + path);
        public void BeginChordCapture() => Calls.Add("capture");
        public void SetAutostart(bool on) => Calls.Add("autostart:" + on);
        public void RegisterHelper() => Calls.Add("helper");
        public void PickFolder() => Calls.Add("folder");
        public void InstallCapability(Capability c) => Calls.Add("install:" + c);
        public void CheckNow() => Calls.Add("check");
        public void UpdateNow() => Calls.Add("update");
        public void RecentreCapsule() => Calls.Add("recentre");
        public void StartIndexing() => Calls.Add("start");
        public void OpenLogs() => Calls.Add("logs");
        public void OpenCodecStore(string productId) => Calls.Add("codec:" + productId);
        public void RemoveAddOn(Capability c, bool forget) => Calls.Add($"remove:{c}:{(forget ? "forget" : "keep")}");
    }

    [Fact]
    public void EveryActionReachesTheHostExactlyOnce()
    {
        // Driven off the enum itself, so an action added later without an arm fails here on the
        // commit that adds it rather than on somebody's desktop.
        //
        // Counting the calls is not enough, and the name of this test says so: six of the ten
        // arms could have been cross-wired to any other host method and the count stayed at one.
        // What each action must reach is written down here, beside the enum, so a swapped arm is a
        // failure rather than a surprise on a stranger's machine.
        var expected = new Dictionary<SettingsAction, string>
        {
            [SettingsAction.OpenPalettesFile] = @"palettes:C:\x\palettes.json",
            [SettingsAction.CaptureChord] = "capture",
            [SettingsAction.SetAutostart] = "autostart:True",
            [SettingsAction.ClearAutostart] = "autostart:False",
            [SettingsAction.RegisterHelper] = "helper",
            [SettingsAction.PickFolder] = "folder",
            [SettingsAction.InstallCapability] = "install:Photos",
            [SettingsAction.CheckNow] = "check",
            [SettingsAction.UpdateNow] = "update",
            [SettingsAction.RecentreCapsule] = "recentre",
            [SettingsAction.StartIndexing] = "start",
            [SettingsAction.OpenLogs] = "logs",
            [SettingsAction.OpenCodecStore] = "codec:9NMZLZ57R3T7",
            [SettingsAction.RemoveAddOn] = "remove:Speech:forget",
        };

        foreach (SettingsAction action in Enum.GetValues<SettingsAction>())
        {
            if (action == SettingsAction.None) continue;

            var host = new Recorder();
            string argument = action == SettingsAction.InstallCapability ? nameof(Capability.Photos)
                            : action == SettingsAction.OpenPalettesFile ? @"C:\x\palettes.json"
                            : action == SettingsAction.OpenCodecStore ? "9NMZLZ57R3T7"
                            : action == SettingsAction.RemoveAddOn ? "Speech|forget"
                            : "";

            SettingsActions.Dispatch(action, argument, host);
            Assert.True(host.Calls.Count == 1, $"{action} produced {host.Calls.Count} host calls, expected 1");
            Assert.True(expected.TryGetValue(action, out string? want),
                $"{action} has no expected destination written down here");
            Assert.Equal(want, host.Calls[0]);
        }

        // And no two actions may land on the same call, which is the other half of a cross-wire.
        Assert.Equal(expected.Count, expected.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NothingHappensForNoAction()
    {
        // The common case - most clicks are a config change and nothing else - and it must not
        // reach the machine at all.
        var host = new Recorder();
        SettingsActions.Dispatch(SettingsAction.None, "", host);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void TheTwoAutostartActionsAreNotTheSameCall()
    {
        // The pair that decides whether the toggle works in both directions. One arm handling both
        // - "SetAutostart(true)" for either - is a switch that turns on and never off, which is
        // the failure the toggle had before it had any arm at all.
        var on = new Recorder(); var off = new Recorder();
        SettingsActions.Dispatch(SettingsAction.SetAutostart, "", on);
        SettingsActions.Dispatch(SettingsAction.ClearAutostart, "", off);

        Assert.Equal(["autostart:True"], on.Calls);
        Assert.Equal(["autostart:False"], off.Calls);
    }

    [Fact]
    public void TheCapabilityAskedForIsTheOneInstalled()
    {
        var host = new Recorder();
        SettingsActions.Dispatch(SettingsAction.InstallCapability, nameof(Capability.Speech), host);
        Assert.Equal(["install:Speech"], host.Calls);
    }

    [Fact]
    public void AnArgumentThatNamesNoCapabilityInstallsNothing()
    {
        // The argument crosses a string boundary. A parse that falls back to the first enum value
        // installs Photos when something upstream renamed a capability - a 630 MB download nobody
        // asked for.
        var host = new Recorder();
        SettingsActions.Dispatch(SettingsAction.InstallCapability, "Telepathy", host);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ARemoveThatDoesNotParseRemovesNothing()
    {
        // A fallback to the first enum value would delete an add-on nobody asked about.
        foreach (string bad in new[] { "", "Photos", "Photos|", "Photos|maybe", "Nonsense|keep", "7|keep", "Photos|keep|x" })
        {
            var host = new Recorder();
            SettingsActions.Dispatch(SettingsAction.RemoveAddOn, bad, host);
            Assert.Empty(host.Calls);
        }
        var ok = new Recorder();
        SettingsActions.Dispatch(SettingsAction.RemoveAddOn, "Photos|keep", ok);
        Assert.Equal(["remove:Photos:keep"], ok.Calls);
    }
}
