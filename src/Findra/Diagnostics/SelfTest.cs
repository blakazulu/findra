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

        failed += Check("every palette derives readable colours", () =>
        {
            foreach (Palette p in PaletteStore.LoadFromDisk())
            {
                Derived d = Derived.From(p);
                if (Derived.Contrast(d.Ink, d.Ground) < 4.5)
                    return $"{p.Name}: ink on ground is {Derived.Contrast(d.Ink, d.Ground):F1}:1, needs 4.5";
                if (Derived.Contrast(d.OnAccent, d.Accent) < 4.5)
                    return $"{p.Name}: text on the accent is unreadable";
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
