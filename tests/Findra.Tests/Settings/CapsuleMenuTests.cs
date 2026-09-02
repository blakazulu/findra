using Findra;
using Xunit;

/// <summary>
/// Spec §7 surface 4: "Palette and pause live here too, so most people never open settings." The
/// menu is a pure list, so the part that can be wrong - which palette it says you are using - is
/// testable without a desktop.
/// </summary>
public class CapsuleMenuTests
{
    private static readonly Config Pair =
        Config.Default with { DarkPalette = "Brass", LightPalette = "Blueprint" };

    /// <summary>Single, not FirstOrDefault: a missing entry should read as "no menu item named
    /// Blueprint" rather than as an assertion failure on a default struct with a null header.</summary>
    private static MenuEntry Find(IReadOnlyList<MenuEntry> items, string header) =>
        items.Single(i => i.Header.Contains(header, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TheMenuTicksThePaletteThatIsActuallyOnScreen()
    {
        // The obvious implementation ticks config.DarkPalette. On a light desktop following
        // Windows, that puts the tick next to a palette nothing is painted in - and clicking the
        // one that IS painted looks like it did nothing, because it was already selected.
        IReadOnlyList<MenuEntry> items = CapsuleMenu.Items(Pair, Palette.BuiltIn, windowsIsLight: true, indexerAlive: false);

        Assert.True(Find(items, "Blueprint").Checked);
        Assert.DoesNotContain(items, i => i.Header.Contains("Brass", StringComparison.Ordinal));
    }

    [Fact]
    public void PinningToDarkTicksTheDarkPickWhateverWindowsIsSetTo()
    {
        IReadOnlyList<MenuEntry> items = CapsuleMenu.Items(
            Pair with { Mode = ThemeMode.AlwaysDark }, Palette.BuiltIn, windowsIsLight: true, indexerAlive: false);

        Assert.True(Find(items, "Brass").Checked);
    }

    [Fact]
    public void OnlyPalettesOfTheSideInUseAreOffered()
    {
        // Offering all six from the capsule means a click on a dark palette in a light session
        // writes DarkPalette and changes nothing visible. Changing SIDE is a settings decision.
        IReadOnlyList<MenuEntry> items = CapsuleMenu.Items(Pair, Palette.BuiltIn, windowsIsLight: true, indexerAlive: false);
        string[] offered = [.. items.Where(i => i.Command.StartsWith("palette:", StringComparison.Ordinal))
                                    .Select(i => i.Header)];

        Assert.Equal(["Paper", "Blueprint", "Porcelain"], offered);
    }

    [Fact]
    public void APaletteSomebodyWroteThemselvesIsOfferedFromTheCapsuleToo()
    {
        var mine = new Palette("Slate", new SkiaSharp.SKColor(0x7A, 0xA2, 0xF7),
                               new SkiaSharp.SKColor(0xE0, 0xE0, 0xE0), new SkiaSharp.SKColor(0x10, 0x14, 0x1C), false);
        IReadOnlyList<MenuEntry> items = CapsuleMenu.Items(
            Pair with { Mode = ThemeMode.AlwaysDark }, [.. Palette.BuiltIn, mine], windowsIsLight: true, indexerAlive: false);

        Assert.Contains(items, i => i.Header == "Slate");
    }

    [Fact]
    public void ContentIndexingCanBeTurnedOffWithoutOpeningSettings()
    {
        MenuEntry on = Find(CapsuleMenu.Items(Pair with { IndexContent = true }, Palette.BuiltIn, true, true), "inside");
        MenuEntry off = Find(CapsuleMenu.Items(Pair with { IndexContent = false }, Palette.BuiltIn, true, false), "inside");

        Assert.Equal("content", on.Command);
        Assert.True(on.Checked);
        Assert.False(off.Checked);
    }

    [Fact]
    public void TheMenuSaysWhenIndexingIsOnButNothingIsRunning()
    {
        // Spec §3: the interface must say "indexing is paused because Findra is closed" plainly
        // rather than looking idle. The first draft threaded indexerAlive through this signature
        // and then wrote a ternary whose two arms were the same string - a parameter that could
        // not affect the output, and a test that could not tell.
        MenuEntry running = Find(CapsuleMenu.Items(Pair with { IndexContent = true }, Palette.BuiltIn, true, indexerAlive: true), "inside");
        MenuEntry stalled = Find(CapsuleMenu.Items(Pair with { IndexContent = true }, Palette.BuiltIn, true, indexerAlive: false), "inside");

        Assert.NotEqual(running.Header, stalled.Header);
        Assert.Contains("not running", stalled.Header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMenuAlwaysOffersAWayIntoSettingsAndAWayOut()
    {
        // Config.ShowCapsule = false is supported and so is a hidden tray area. If the capsule's
        // own menu is nothing but palettes, somebody in that state has no route to settings.
        IReadOnlyList<MenuEntry> items = CapsuleMenu.Items(Pair, Palette.BuiltIn, true, false);

        Assert.Contains(items, i => i.Command == "settings");
        Assert.Contains(items, i => i.Command == "quit");
    }

    [Fact]
    public void NoTwoItemsCarryTheSameCommand()
    {
        // The shell switches on Command. Two items sharing one means the second is dead and the
        // first fires for both.
        IReadOnlyList<MenuEntry> items = CapsuleMenu.Items(Pair, Palette.BuiltIn, true, false);
        string[] commands = [.. items.Where(i => i.Command.Length > 0).Select(i => i.Command)];

        Assert.Equal(commands.Length, commands.Distinct(StringComparer.Ordinal).Count());
    }
}
