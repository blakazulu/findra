using System.Globalization;
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

        // A correctness bound between two constants that live in different files, checked here
        // rather than in a static constructor: a static constructor throw surfaces as a type
        // initialization error from wherever the type is first touched - out of a query, out of
        // the tail, out of a pipe accept - which is unreportable. A self-check line is something
        // a person can read and paste.
        failed += Check("one journal apply slice fits a session's outbound queue", () =>
            Pipe.NameServer.MaxOutbound < JournalTail.MaxApplyBatch
                ? $"MaxOutbound is {Pipe.NameServer.MaxOutbound.ToString("N0", CultureInfo.InvariantCulture)} " +
                  $"but the tail publishes slices of {JournalTail.MaxApplyBatch.ToString("N0", CultureInfo.InvariantCulture)}, " +
                  "so an ordinary catch-up would drop events on a healthy client"
                : null);

        // ---- the content path ----
        //
        // Three checks, in the order a failure would actually bite: can the bundled SQLite do
        // full-text search at all, does the index on this machine match this build, and does a
        // real document survive extraction, chunking, indexing and a query.

        failed += Check("sqlite has fts5", () =>
            ContentDb.CompileOptions().Any(o => o.Contains("FTS5", StringComparison.OrdinalIgnoreCase))
                ? null : "the bundled sqlite has no FTS5 - document search cannot work");

        failed += Check("the index schema is current", () =>
        {
            // A missing index is the ordinary state of a machine that has not run the interface
            // yet. It is not a failure, and saying so is more useful than an "ok" that skipped
            // silently.
            if (!File.Exists(ContentDb.DefaultPath)) { Console.WriteLine("        no index yet - nothing to check"); return null; }
            using var db = new ContentDb(ContentDb.DefaultPath, readOnly: true);
            string? v = db.Get("schema");
            return v == ContentDb.SchemaVersion.ToString(CultureInfo.InvariantCulture)
                ? null
                : $"index schema is '{v}', this build wants {ContentDb.SchemaVersion.ToString(CultureInfo.InvariantCulture)}";
        });

        failed += Check("a document round-trips through a temporary index", () =>
        {
            // The whole content path in one check, against a throwaway database in a temp folder:
            // queue a real file, drain it with the indexer's own code, and ask for a word that is
            // inside it. It never touches the real index - a self-test that left files in
            // someone's search results is a self-test nobody runs twice.
            string dir = Path.Combine(Path.GetTempPath(), "findra-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string doc = Path.Combine(dir, "selftest.txt");
                File.WriteAllText(doc, "Findra self-test: the quarterly lease agreement and its deposit.");
                using var db = new ContentDb(Path.Combine(dir, "search.db"));
                db.Enqueue("C", 1, doc, ResultKind.Document, "selftest");
                // A vector store in the same throwaway folder, never the real one: a self-test
                // that appended rows to somebody's index would be exactly the file-leaving this
                // check was written to avoid. The capability set is whatever this machine has,
                // because the check is worth more when it exercises that, and the limit is the
                // default because what it drains is a .txt.
                using var vectors = new VectorStore(Path.Combine(dir, "vectors.bin"), writer: true);
                using var decoders = new Decoders(CapabilitySet.Installed(), vectors);
                Indexer.DrainOnce(db, _ => { }, decoders);
                if (db.PendingCount() != 0) return "the queue did not drain";
                if (db.Fts("deposit", 5).Count != 1) return "the indexed word was not found again";
                // Through the branch the card actually calls, not just the store: the grammar, the
                // dedupe and the finish-and-order pass are all between a keystroke and a row.
                if (ContentBranch.Search(db, "deposit", 5).Rows.Count != 1)
                    return "the content branch did not answer with the file the store found";
                return null;
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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

        // Both of these run on a machine that has downloaded nothing, which is the point: the
        // capability graph is the one thing here that can be wrong everywhere at once.
        failed += Check("the capability graph is consistent", () =>
        {
            foreach (Capability c in Capabilities.All)
            {
                IReadOnlySet<Capability> once = Capabilities.Close([c]);
                if (!Capabilities.Close(once).SetEquals(once)) return $"closing {c} twice changes it";
                foreach (int k in Capabilities.KindsCovered(c))
                    if (!FileKinds.HasContent((ResultKind)k)) return $"{c} claims {(ResultKind)k}, which has no content";
            }
            // The measured total is what the README and the winget manifest quote.
            long all = Capabilities.TotalBytes(Capabilities.All);
            return Sizes.Human(all) == "2.93 GB" ? null : $"the whole model set measures {Sizes.Human(all)}";
        });

        failed += Check("every installed capability loads", () =>
        {
            CapabilitySet have = CapabilitySet.Installed();
            if (have.Have.Count == 0) { Console.WriteLine("        no capability is installed - nothing to load"); return null; }
            foreach (Capability c in have.Have)
                foreach (Model m in Capabilities.OwnModels(c))
                    if (!ModelStore.Present(m)) return $"{c} is installed but {m.File} is not there";
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
