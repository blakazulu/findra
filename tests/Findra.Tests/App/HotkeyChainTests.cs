using Findra;

namespace Findra.Tests.App;

public class HotkeyChainTests
{
    [Fact]
    public void TheUsersOwnChoiceIsTriedFirst()
    {
        IReadOnlyList<string> chain = HotkeyChain.Build("Ctrl+Shift+K", Hotkey.DefaultChain);
        Assert.Equal("Ctrl+Shift+K", chain[0]);
        Assert.Equal(Hotkey.DefaultChain.Count + 1, chain.Count);
    }

    [Fact]
    public void AChoiceThatIsAlreadyInTheChainIsNotTriedTwice()
    {
        // Registering the same combination twice wastes an attempt and, worse, makes the second
        // one fail against our own registration and look like a busy machine.
        IReadOnlyList<string> chain = HotkeyChain.Build("ctrl+alt+f", Hotkey.DefaultChain);
        Assert.Equal("ctrl+alt+f", chain[0]);
        Assert.Equal(Hotkey.DefaultChain.Count, chain.Count);
        Assert.DoesNotContain("Ctrl+Alt+F", chain.Skip(1));
    }

    [Fact]
    public void SpellingIsNotWhatMakesTwoEntriesDifferent()
    {
        // "ALT + space" and "Alt+Space" are one combination, whatever the file says.
        IReadOnlyList<string> chain = HotkeyChain.Build("ALT + space", ["Alt+Space"]);
        Assert.Single(chain);
    }

    [Fact]
    public void NoChoiceAtAllLeavesTheDefaults()
    {
        Assert.Equal(Hotkey.DefaultChain.Count, HotkeyChain.Build(null, Hotkey.DefaultChain).Count);
        Assert.Equal(Hotkey.DefaultChain.Count, HotkeyChain.Build("   ", Hotkey.DefaultChain).Count);
    }

    [Fact]
    public void AnUnreadableChoiceIsKeptRatherThanSwallowed()
    {
        // It cannot register, and RegisterFirstThatWorks skips it - but dropping it here would
        // hide a typo in a hand-edited config that the log should be able to show.
        IReadOnlyList<string> chain = HotkeyChain.Build("Banana+Space", Hotkey.DefaultChain);
        Assert.Equal("Banana+Space", chain[0]);
    }
}
