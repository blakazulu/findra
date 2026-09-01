using System.Text.Json;
using SkiaSharp;

namespace Findra;

/// <summary>
/// The six built-ins, plus whatever the user added. A palette is four values, so extending
/// Findra's look is appending an object - never authoring a layout.
///
/// Nothing here throws. A hand-edited file with one typo costs its owner that one entry and
/// a line in the log, not their theme list and not the app.
/// </summary>
public static class PaletteStore
{
    private sealed record Entry(string? Name, string? Accent, string? Ink, string? Ground, bool Light);

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<Palette> Load(string? json)
    {
        var list = Palette.BuiltIn.ToList();
        if (string.IsNullOrWhiteSpace(json)) return list;

        Entry[]? entries;
        try { entries = JsonSerializer.Deserialize<Entry[]>(json, Opts); }
        catch (Exception ex) { Log.Warn("look", "palettes.json is not readable: " + ex.Message); return list; }
        if (entries is null) return list;

        foreach (Entry e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Name)) { Log.Warn("look", "a palette with no name was skipped"); continue; }
            if (!TryHex(e.Accent, out SKColor accent) ||
                !TryHex(e.Ink, out SKColor ink) ||
                !TryHex(e.Ground, out SKColor ground))
            {
                Log.Warn("look", $"palette '{e.Name}' has a missing or unreadable colour and was skipped");
                continue;
            }

            var p = new Palette(e.Name.Trim(), accent, ink, ground, e.Light);
            int at = list.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) list[at] = p; else list.Add(p);
        }
        return list;
    }

    public static IReadOnlyList<Palette> LoadFromDisk()
    {
        try
        {
            string path = Paths.PalettesFile;
            return Load(File.Exists(path) ? File.ReadAllText(path) : null);
        }
        catch (Exception ex) { Log.Warn("look", "could not read palettes.json: " + ex.Message); return Palette.BuiltIn; }
    }

    /// <summary>Writes the shipped file if it is absent, and returns its path either way.</summary>
    public static string EnsureOnDisk()
    {
        string path = Paths.PalettesFile;
        try
        {
            Paths.Ensure(Paths.Config);
            if (!File.Exists(path)) File.WriteAllText(path, DefaultJson);
        }
        catch (Exception ex) { Log.Warn("look", "could not write palettes.json: " + ex.Message); }
        return path;
    }

    private static bool TryHex(string? s, out SKColor c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        ReadOnlySpan<char> h = s.AsSpan().Trim();
        if (h.Length > 0 && h[0] == '#') h = h[1..];

        if (h.Length == 3)
        {
            if (!byte.TryParse($"{h[0]}{h[0]}", System.Globalization.NumberStyles.HexNumber, null, out byte r) ||
                !byte.TryParse($"{h[1]}{h[1]}", System.Globalization.NumberStyles.HexNumber, null, out byte g) ||
                !byte.TryParse($"{h[2]}{h[2]}", System.Globalization.NumberStyles.HexNumber, null, out byte b)) return false;
            c = new SKColor(r, g, b);
            return true;
        }
        if (h.Length != 6) return false;
        if (!uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return false;
        c = new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>The file Findra ships. It is documentation as much as configuration.</summary>
    public static string DefaultJson =>
        "// Findra palettes. A palette is four values: an accent, the ink drawn on the ground,\n" +
        "// the ground itself, and whether that ground is light. Everything else - rows, tiles,\n" +
        "// chips, edges, shadows - is worked out from them, so adding a look is appending one\n" +
        "// object here. An entry whose name matches one below replaces it.\n" +
        "[\n" +
        string.Join(",\n", Palette.BuiltIn.Select(p =>
            $"  {{ \"name\": \"{p.Name}\", \"accent\": \"{Hex(p.Accent)}\", \"ink\": \"{Hex(p.Ink)}\", " +
            $"\"ground\": \"{Hex(p.Ground)}\", \"light\": {(p.Light ? "true" : "false")} }}")) +
        "\n]\n";

    private static string Hex(SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
}
