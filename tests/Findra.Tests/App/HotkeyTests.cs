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
}
