using Findra;
using Xunit;

public class HotkeyTests
{
    [Fact]
    public void ParsesTheOrdinaryForms()
    {
        Assert.NotNull(Hotkey.Parse("Alt+Space"));
        Assert.NotNull(Hotkey.Parse("Ctrl+Alt+F"));
        Assert.NotNull(Hotkey.Parse("ctrl+shift+space"));
        Assert.Null(Hotkey.Parse("Banana+Space"));
        Assert.Null(Hotkey.Parse(""));
    }

    [Fact]
    public void DescribeRoundTripsParse()
    {
        var (mods, vk) = Hotkey.Parse("Ctrl+Alt+F")!.Value;
        Assert.Equal("Ctrl+Alt+F", Hotkey.Describe(mods, vk));
    }

    [Fact]
    public void TakesTheFirstCombinationThatRegisters()
    {
        var tried = new List<string>();
        string? landed = Hotkey.RegisterFirstThatWorks(
            ["Alt+Space", "Ctrl+Alt+Space", "Ctrl+Alt+F"],
            (m, v) => { tried.Add(Hotkey.Describe(m, v)); return tried.Count == 2; });

        Assert.Equal("Ctrl+Alt+Space", landed);
        Assert.Equal(2, tried.Count);   // it stopped at the one that worked
    }

    [Fact]
    public void ReturnsNullWhenTheWholeChainIsTaken()
    {
        // Every combination refused is a real outcome on a machine loaded with other tools.
        // The caller must be able to tell the user, so this returns null rather than throwing.
        Assert.Null(Hotkey.RegisterFirstThatWorks(["Alt+Space", "Ctrl+Alt+F"], (_, _) => false));
    }

    [Fact]
    public void AnUnparseableEntryIsSkippedNotFatal()
    {
        string? landed = Hotkey.RegisterFirstThatWorks(["Banana+Space", "Alt+Space"], (_, _) => true);
        Assert.Equal("Alt+Space", landed);
    }

    [Fact]
    public void AnUnrecognisedModifierIsRejectedWhereverItAppears()
    {
        // The old implementation only rejected a bad modifier while `mods` was still zero, so
        // one bad token after a good one was silently dropped instead of failing the parse.
        Assert.Null(Hotkey.Parse("Ctrl+Banana+F"));
        Assert.Null(Hotkey.Parse("Banana+Ctrl+F"));
    }

    [Fact]
    public void ABareKeyWithNoModifierIsRejected()
    {
        // RegisterHotKey with fsModifiers == 0 is legal Win32 and steals that key process-wide
        // from every app on the desktop - unrecoverable from inside the app that did it, since
        // the user can no longer type the letter anywhere to fix the config. This is the rule
        // that "A" (and "Space", with no modifier) must always fail on.
        Assert.Null(Hotkey.Parse("A"));
        Assert.Null(Hotkey.Parse("Space"));
    }

    [Fact]
    public void AModifierOnlyStringWithNoKeyIsRejected()
    {
        // "Ctrl+Alt" has two tokens, but only the first is read as a modifier - the last token
        // is always the key, so "Alt" here is asked to parse as a key, fails, and the whole
        // string is rejected rather than silently keying off "Alt" as if it were a letter.
        Assert.Null(Hotkey.Parse("Ctrl+Alt"));
    }

    // ---- what a key press is called ----------------------------------------------------------
    //
    // The settings window captures a chord from a real key press, and Avalonia's Key enum is not
    // the Win32 virtual-key numbering RegisterHotKey wants: Avalonia's Key.A is 44, Win32's VK_A
    // is 0x41. Something has to translate, and it is here rather than in the window so that it
    // has a test at all.

    [Theory]
    [InlineData(Avalonia.Input.Key.F, 0x46u)]
    [InlineData(Avalonia.Input.Key.A, 0x41u)]
    [InlineData(Avalonia.Input.Key.Z, 0x5Au)]
    [InlineData(Avalonia.Input.Key.D0, 0x30u)]
    [InlineData(Avalonia.Input.Key.D7, 0x37u)]
    [InlineData(Avalonia.Input.Key.Space, 0x20u)]
    [InlineData(Avalonia.Input.Key.F1, 0x70u)]
    [InlineData(Avalonia.Input.Key.F12, 0x7Bu)]
    [InlineData(Avalonia.Input.Key.PageDown, 0x22u)]
    [InlineData(Avalonia.Input.Key.Delete, 0x2Eu)]
    public void TheKeyThatWasPressedIsTheVirtualKeyRegisterHotKeyWants(Avalonia.Input.Key key, uint vk)
    {
        Assert.Equal(vk, Hotkey.VirtualKeyOf(key));
    }

    [Fact]
    public void EveryKeyFindraCanNameSurvivesTheRoundTripToAChordAndBack()
    {
        // The whole point of the mapping. Whatever it names is handed to SettingsModel.ChordFrom,
        // which builds the chord through Hotkey.Describe, which is saved to config.json and read
        // back by Hotkey.Parse before RegisterHotKey ever sees it. A key this map claims to know
        // but Describe cannot spell produces "Ctrl+0xBB" - a chord that saves and never registers.
        // Swept over the whole enum, so adding a key to the map without teaching Describe about it
        // fails here rather than on somebody's desktop.
        foreach (Avalonia.Input.Key key in Enum.GetValues<Avalonia.Input.Key>())
        {
            if (Hotkey.VirtualKeyOf(key) is not { } vk) continue;
            if (Hotkey.ModifierKeys.Contains(vk)) continue;   // a modifier is not a chord's key

            string chord = Hotkey.Describe(Hotkey.MOD_CONTROL, vk);
            (uint Mods, uint Vk)? parsed = Hotkey.Parse(chord);
            Assert.True(parsed is not null, $"{key} became '{chord}', which Hotkey.Parse rejects");
            Assert.Equal(vk, parsed!.Value.Vk);
        }
    }

    [Fact]
    public void APressedModifierIsStillNamedSoTheModelIsTheOneThatRefusesIt()
    {
        // Named rather than dropped, so SettingsModel.ChordFrom's refusal is the single place that
        // decides a bare modifier is not a chord. Answering null here instead would put a second
        // copy of that rule in the window's key handler, where nothing measures it.
        Assert.Equal(0x11u, Hotkey.VirtualKeyOf(Avalonia.Input.Key.LeftCtrl));
        Assert.Equal(0x11u, Hotkey.VirtualKeyOf(Avalonia.Input.Key.RightCtrl));
        Assert.Equal(0x12u, Hotkey.VirtualKeyOf(Avalonia.Input.Key.LeftAlt));
        Assert.Equal(0x10u, Hotkey.VirtualKeyOf(Avalonia.Input.Key.LeftShift));
        Assert.Equal(0x5Bu, Hotkey.VirtualKeyOf(Avalonia.Input.Key.LWin));
        Assert.Equal(0x5Cu, Hotkey.VirtualKeyOf(Avalonia.Input.Key.RWin));
    }

    [Fact]
    public void AKeyFindraHasNoNameForIsNotGuessedAt()
    {
        // Capture stays open instead. A guessed code would be described as "0xBB", saved, and
        // never register - and the row would report a combination that does nothing.
        Assert.Null(Hotkey.VirtualKeyOf(Avalonia.Input.Key.OemComma));
        Assert.Null(Hotkey.VirtualKeyOf(Avalonia.Input.Key.None));
    }
}
