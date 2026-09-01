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
}
