using SkiaSharp;

namespace Findra;

/// <summary>
/// The whole public contract for how Findra looks: a name, three colours, and which side of
/// the light/dark line the ground sits on. Everything the card paints - rows, tiles, chips,
/// edges, shadows, the ink drawn on an accent fill - is derived from these four values, so a
/// new palette is four constants and never a layout.
/// </summary>
public sealed record Palette(string Name, SKColor Accent, SKColor Ink, SKColor Ground, bool Light)
{
    private static SKColor Hex(uint rgb) =>
        new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static readonly Palette Mond      = new("Mond",      Hex(0xFA7E00), Hex(0xEBDBC0), Hex(0x14141A), false);
    public static readonly Palette Brass     = new("Brass",     Hex(0xD8A657), Hex(0xEDE4D3), Hex(0x0F1219), false);
    public static readonly Palette Verdigris = new("Verdigris", Hex(0x4FBFA0), Hex(0xE3E4DA), Hex(0x0D1311), false);
    public static readonly Palette Paper     = new("Paper",     Hex(0xC2410C), Hex(0x221F1A), Hex(0xF4F0E6), true);
    public static readonly Palette Blueprint = new("Blueprint", Hex(0x2F5FD0), Hex(0x182432), Hex(0xEDF2F8), true);
    public static readonly Palette Porcelain = new("Porcelain", Hex(0xD93A3A), Hex(0x101012), Hex(0xFBFBF9), true);

    public static readonly IReadOnlyList<Palette> BuiltIn =
        [Mond, Brass, Verdigris, Paper, Blueprint, Porcelain];

    public static IReadOnlyList<Palette> Darks  => BuiltIn.Where(p => !p.Light).ToList();
    public static IReadOnlyList<Palette> Lights => BuiltIn.Where(p =>  p.Light).ToList();

    public static Palette DefaultDark  => Mond;
    public static Palette DefaultLight => Paper;

    /// <summary>Resolve a palette by name, honouring the user's own.
    ///
    /// This searches the loaded set - the six below with anything from
    /// <c>%APPDATA%\Findra\palettes.json</c> replacing or extending them - not just the
    /// built-ins. Searching only the built-ins would mean a configured palette that the user
    /// wrote themselves silently resolves to a shipped one, which looks like Findra ignoring
    /// their file rather than failing to find it.</summary>
    public static Palette? ByName(string name) =>
        PaletteStore.LoadFromDisk().FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
