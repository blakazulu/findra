using SkiaSharp;

namespace Findra.Diagnostics;

/// <summary>
/// `--searchtest`: everything that can be checked in this process, with no helper,
/// no pipe and no admin rights.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        int failed = 0;
        Console.WriteLine("findra --searchtest");
        Console.WriteLine();

        failed += Check("paths are writable", () =>
        {
            foreach (string d in new[] { Paths.Config, Paths.Models, Paths.Index, Paths.Logs })
            {
                Paths.Ensure(d);
                string probe = Path.Combine(d, ".write-probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            return null;
        });

        failed += Check("models are not under Roaming", () =>
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Paths.Models.StartsWith(roaming, StringComparison.OrdinalIgnoreCase)
                 ? $"models resolve to {Paths.Models}" : null;
        });

        failed += Check("query grammar parses", () =>
        {
            var q = new SearchQuery("sunset ext:jpg size:>1mb");
            if (!q.HasNameTerms) return "no name terms parsed from a query that has one";
            if (!q.Exts.Contains("jpg")) return "ext:jpg not parsed";
            if (q.MinBytes <= 0) return "size:>1mb not parsed";
            return null;
        });

        failed += Check("name index round-trips a record", () =>
        {
            var ix = new NameIndex('C');
            ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
            ix.Upsert(100, 5, 0, "findra-selftest.txt");
            var hits = new List<NameIndex.Hit>();
            ix.Search(new SearchQuery("findra-selftest"), hits);
            if (hits.Count != 1) return $"expected 1 hit, got {hits.Count}";
            if (ix.PathOf(hits[0].Record) != @"C:\findra-selftest.txt") return "path rebuild wrong";
            return null;
        });

        // This runs over PaletteStore.LoadFromDisk(), so it is the only thing standing between a
        // hand-written palettes.json and someone's desktop. Checking ink against the ground alone
        // was not enough: a palette can clear 4.5 there and still paint 3.4 on the stage's badge
        // and 3.6 on a chip, because those are DERIVED surfaces a step off the ground, not the
        // ground. Every surface ink actually lands on gets checked - the same set DerivedTests
        // holds the six built-ins to.
        failed += Check("every palette derives readable colours", () =>
        {
            foreach (Palette p in PaletteStore.LoadFromDisk())
            {
                Derived d = Derived.From(p);
                foreach ((string surface, SKColor c) in new[]
                {
                    ("the ground", d.Ground), ("a result row", d.Row), ("a hovered row", d.RowHover),
                    ("the selected row", d.RowSelected), ("a chip", d.Chip), ("a stage badge", d.Stage),
                })
                {
                    double ratio = Derived.Contrast(d.Ink, c);
                    if (ratio < 4.5) return $"{p.Name}: ink on {surface} is {ratio:F2}:1, needs 4.5";
                }
                // The capsule paints AccentSoft across the whole bar and draws Dim on top of
                // that, not on the bare ground - so AccentSoft is the pair actually on screen,
                // same reasoning as the loop above for ink on a derived surface.
                double dimOnBar = Derived.Contrast(d.Dim, d.AccentSoft);
                if (dimOnBar < 4.5) return $"{p.Name}: dim text on the capsule's bar fill is {dimOnBar:F2}:1, needs 4.5";
                double dim = Derived.Contrast(d.Dim, d.Ground);
                if (dim < 4.5) return $"{p.Name}: dim text on the ground is {dim:F2}:1, needs 4.5";
                if (Derived.Contrast(d.OnAccent, d.Accent) < 4.5)
                    return $"{p.Name}: text on the accent is unreadable";
            }
            return null;
        });

        failed += Check("config.json round-trips", () =>
        {
            Config c = Config.Default with
            {
                DarkPalette = "Verdigris", LightPalette = "Blueprint", Mode = ThemeMode.AlwaysDark,
                Hotkey = "Ctrl+Alt+F", CapsuleX = 120, CapsuleY = 900, ShowCapsule = false,
                CheckForUpdates = false,
            };
            Config back = Config.Load(c.ToJson());
            return back == c ? null : "the loaded config does not equal the one that was saved";
        });

        // Not a pass/fail check by itself - Theme.Resolve always returns SOME palette, honouring
        // a wrong-side pick rather than rejecting it (App/ThemeTests.cs:
        // APaletteOnTheWrongSideIsHonouredButNoted). This exists so a hand-edited config.json or
        // palettes.json is visible here rather than only discovered on someone's desktop.
        failed += Check("the configured palettes resolve", () =>
        {
            Config config = Config.LoadFromDisk();
            IReadOnlyList<Palette> palettes = PaletteStore.LoadFromDisk();

            foreach (bool windowsIsLight in new[] { false, true })
            {
                Palette p = Theme.Resolve(config, windowsIsLight, palettes);
                string side = windowsIsLight ? "light" : "dark";
                string note = p.Light == windowsIsLight
                    ? ""
                    : $"  (named a {(p.Light ? "light" : "dark")} palette for the {side} side)";
                Console.WriteLine($"        windows {side,-5} -> '{p.Name}'{note}");
            }
            return null;
        });

        failed += Check("the card renders", () =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(
                (int)SearchCardLayout.Width, 400, SKColorType.Bgra8888, SKAlphaType.Premul));
            SearchCardPainter.Paint(surface.Canvas, SearchCardState.Empty,
                                    Derived.From(Palette.DefaultDark), SKTypeface.Default);
            surface.Canvas.Flush();
            return null;
        });

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) FAILED");
        return failed == 0 ? 0 : 1;
    }

    private static int Check(string name, Func<string?> body)
    {
        try
        {
            string? problem = body();
            Console.WriteLine($"  {(problem is null ? "ok  " : "FAIL")}  {name}{(problem is null ? "" : "  -  " + problem)}");
            return problem is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}  -  {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
