namespace Findra;

/// <summary>One line of the capsule's right-click menu. <see cref="Command"/> is what the shell
/// switches on; a separator carries an empty one.</summary>
public readonly record struct MenuEntry(string Header, string Command, bool Checked, bool Enabled)
{
    public static readonly MenuEntry Separator = new("-", "", false, true);
}

/// <summary>
/// The capsule's right-click menu (spec §7 surface 4). Palette and content indexing live here so
/// that most people never open settings at all.
///
/// <para>It offers the palettes of the side actually in use - the side <see cref="Theme.Resolve"/>
/// would pick right now, not the dark list and not all six. A palette from the other side would be
/// written to the config and change nothing on screen.</para>
/// </summary>
public static class CapsuleMenu
{
    public static IReadOnlyList<MenuEntry> Items(
        Config config, IReadOnlyList<Palette> palettes, bool windowsIsLight, bool indexerAlive)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(palettes);

        bool light = config.Mode switch
        {
            ThemeMode.AlwaysDark => false,
            ThemeMode.AlwaysLight => true,
            _ => windowsIsLight,
        };
        string chosen = light ? config.LightPalette : config.DarkPalette;

        var items = new List<MenuEntry>();
        foreach (Palette p in palettes.Where(p => p.Light == light))
            items.Add(new MenuEntry(p.Name, "palette:" + p.Name,
                                    string.Equals(p.Name, chosen, StringComparison.OrdinalIgnoreCase), true));

        items.Add(MenuEntry.Separator);
        // Spec §3 wants the interface to say plainly that indexing only happens while Findra runs,
        // rather than looking idle. On is not the same as working, and the label says which.
        items.Add(new MenuEntry(
            config.IndexContent && !indexerAlive
                ? "Look inside my files (not running)"
                : "Look inside my files",
            "content", config.IndexContent, true));
        items.Add(MenuEntry.Separator);
        items.Add(new MenuEntry("Settings", "settings", false, true));
        items.Add(new MenuEntry("Quit", "quit", false, true));
        return items;
    }
}
