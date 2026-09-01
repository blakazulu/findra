namespace Findra;

/// <summary>
/// Parses and describes the global hotkey, and walks a fallback chain of candidate
/// combinations against a caller-supplied registrar. Kept free of Win32 and of any window
/// handle so it is testable without one - the real `RegisterHotKey` call and the
/// `WM_HOTKEY` pump live with the window that owns them.
/// </summary>
public static class Hotkey
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;

    // The registrar ORs this into the modifier flags passed to RegisterHotKey so Windows
    // does not repeat WM_HOTKEY while the key is held down.
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>The chain tried in order at startup. Alt+Space is the system menu chord in
    /// some configurations and is expected to fail there; the rest exist to still land
    /// somewhere rather than leave the app silent.</summary>
    public static readonly IReadOnlyList<string> DefaultChain =
        ["Alt+Space", "Ctrl+Alt+Space", "Ctrl+Alt+F", "Ctrl+Shift+Space"];

    private static readonly (string Name, uint Vk)[] SpecialKeys =
    [
        ("SPACE", 0x20), ("TAB", 0x09), ("ENTER", 0x0D), ("ESC", 0x1B), ("ESCAPE", 0x1B),
        ("BACKSPACE", 0x08), ("INSERT", 0x2D), ("DELETE", 0x2E), ("HOME", 0x24), ("END", 0x23),
        ("PAGEUP", 0x21), ("PAGEDOWN", 0x22),
    ];

    /// <summary>Parses a string like "Ctrl+Alt+F" into MOD_* flags and a virtual-key code.
    /// Anything unrecognised returns null rather than throwing, because this reads a
    /// hand-edited config file: an empty string, any token before the last that is not
    /// Ctrl/Alt/Shift/Win (case-insensitively, checked one token at a time - a bad token after
    /// a good one is rejected exactly like a bad token before one), an unrecognised key token,
    /// a bare key with no modifier at all ("A" - <c>RegisterHotKey</c> with no modifier bits is
    /// legal Win32 but steals that key process-wide from every app on the desktop, which is
    /// unrecoverable from inside the app that did it, since the user can no longer type the
    /// letter anywhere to fix the config), and a modifier-only string with no key
    /// ("Ctrl+Alt" - the last token is always read as the key, so "Alt" is asked to parse as
    /// one, fails, and the whole string is rejected).</summary>
    public static (uint Mods, uint Vk)? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null; // a bare key with no modifier - see the doc comment

        uint mods = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            uint bit = parts[i].ToUpperInvariant() switch
            {
                "CTRL" => MOD_CONTROL,
                "ALT" => MOD_ALT,
                "SHIFT" => MOD_SHIFT,
                "WIN" => MOD_WIN,
                _ => 0u,
            };
            // Reject the whole hotkey the moment any modifier token fails to parse, not only
            // while `mods` is still zero - otherwise a bad token after a good one is silently
            // dropped instead of failing, and a typo in config.json registers a different
            // chord than the one written.
            if (bit == 0) return null;
            mods |= bit;
        }

        uint? vk = ParseKey(parts[^1]);
        if (vk is null) return null;

        return (mods, vk.Value);
    }

    private static uint? ParseKey(string token)
    {
        string upper = token.ToUpperInvariant();

        foreach (var (name, vk) in SpecialKeys)
            if (upper == name) return vk;

        if (upper.Length is 2 or 3 && upper[0] == 'F' &&
            int.TryParse(upper.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x70 + fn - 1); // VK_F1 = 0x70

        if (upper.Length == 1)
        {
            char c = upper[0];
            if (c is >= 'A' and <= 'Z') return c; // VK_A..VK_Z equal the ASCII letter codes
            if (c is >= '0' and <= '9') return c; // VK_0..VK_9 equal the ASCII digit codes
        }

        return null;
    }

    /// <summary>The canonical text form of a parsed hotkey, modifiers always in the order
    /// Ctrl, Alt, Shift, Win. This is what round-trips through <see cref="Parse"/>.</summary>
    public static string Describe(uint mods, uint vk)
    {
        var parts = new List<string>(5);
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(DescribeKey(vk));
        return string.Join("+", parts);
    }

    private static string DescribeKey(uint vk)
    {
        foreach (var (name, keyVk) in SpecialKeys)
            if (keyVk == vk && name != "ESCAPE") return Capitalize(name);

        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk is >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();

        return "0x" + vk.ToString("X");
    }

    private static string Capitalize(string name) => name switch
    {
        "SPACE" => "Space",
        "TAB" => "Tab",
        "ENTER" => "Enter",
        "ESC" => "Esc",
        "BACKSPACE" => "Backspace",
        "INSERT" => "Insert",
        "DELETE" => "Delete",
        "HOME" => "Home",
        "END" => "End",
        "PAGEUP" => "PageUp",
        "PAGEDOWN" => "PageDown",
        _ => name,
    };

    /// <summary>Walks the chain in order, skipping any entry that does not parse, and calls
    /// <paramref name="register"/> with each parsed combination until one returns true.
    /// Returns the canonical description of the combination that registered, or null if the
    /// whole chain was refused - a real outcome on a machine loaded with other tools, and one
    /// the caller must be able to tell the user about rather than fail silently.</summary>
    public static string? RegisterFirstThatWorks(IReadOnlyList<string> chain, Func<uint, uint, bool> register)
    {
        foreach (string entry in chain)
        {
            var parsed = Parse(entry);
            if (parsed is null) continue;

            var (mods, vk) = parsed.Value;
            if (register(mods, vk)) return Describe(mods, vk);
        }
        return null;
    }
}
