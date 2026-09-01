using Microsoft.Win32;

namespace Findra;

/// <summary>
/// Turns a <see cref="Config"/>, the live Windows light/dark setting, and the loaded palette
/// set into the one <see cref="Palette"/> the card paints with.
///
/// <see cref="Resolve"/> is pure - it takes the Windows setting as a parameter rather than
/// reading the registry itself - so the policy (which side wins, what a missing name falls
/// back to) is testable without touching the registry. <see cref="WindowsIsLight"/> is the
/// one impure lookup, kept to a single method so it is the only thing a test needs to avoid.
/// </summary>
public static class Theme
{
    private static bool _fallbackLogged;

    public static Palette Resolve(Config config, bool windowsIsLight, IReadOnlyList<Palette> available)
    {
        bool light = config.Mode switch
        {
            ThemeMode.AlwaysDark => false,
            ThemeMode.AlwaysLight => true,
            _ => windowsIsLight,
        };

        string wanted = light ? config.LightPalette : config.DarkPalette;
        Palette fallback = light ? Palette.DefaultLight : Palette.DefaultDark;

        Palette? found = available.FirstOrDefault(
            p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (found is not null) return found;

        if (!_fallbackLogged)
        {
            _fallbackLogged = true;
            Log.Warn("look", $"palette '{wanted}' was not found; falling back to '{fallback.Name}'");
        }
        return fallback;
    }

    /// <summary>Reads HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme.
    /// A missing key or an unreadable value means a dark-mode-era default, and a registry read
    /// must never throw into startup.</summary>
    public static bool WindowsIsLight()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }
}
