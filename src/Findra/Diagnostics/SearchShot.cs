using System.Linq;
using SkiaSharp;

namespace Findra.Diagnostics;

/// <summary>
/// `--searchshot`: any surface, in any palette, rendered to a PNG with no window and no screen.
/// Findra's card renders entirely offscreen, so this is not a debugging aid bolted on afterward -
/// it is how every state the capsule and card can be in gets built and reviewed. It must learn
/// each new palette and each new surface as they are written.
/// </summary>
public static class SearchShot
{
    public static readonly IReadOnlyList<string> States =
        ["capsule", "empty", "typing", "results", "noresults", "many", "adv", "opening", "openingempty"];

    public static int Render(string outPath, string state, string paletteName = "Mond")
    {
        state = state.Trim().ToLowerInvariant();
        if (!States.Contains(state))
        {
            Console.Error.WriteLine($"searchshot: unknown state '{state}'");
            Console.Error.WriteLine("  states: " + string.Join(", ", States));
            return 1;
        }

        // Palette.ByName resolves against the palettes loaded from disk, not just the built-ins,
        // so a shot of a hand-written palette is possible - the same reasoning as the self-test's
        // legibility check.
        Palette? palette = Palette.ByName(paletteName);
        if (palette is null)
        {
            Console.Error.WriteLine($"searchshot: unknown palette '{paletteName}'");
            Console.Error.WriteLine("  palettes: " +
                string.Join(", ", PaletteStore.LoadFromDisk().Select(p => p.Name)));
            return 1;
        }

        Derived d = Derived.From(palette);
        // The real face ships with the window in Plan 3. A missing font must not stop a shot
        // from rendering, so this uses whatever the platform hands back as the default.
        SKTypeface face = SKTypeface.Default;

        using SKBitmap content = state == "capsule" ? RenderCapsule(d, face) : RenderCard(state, d, face);

        int w = content.Width, h = content.Height;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        SKCanvas c = surface.Canvas;

        // A checkerboard so a transparent corner shows up in the PNG itself rather than only in
        // an alpha channel nobody opens a viewer to inspect. Its two squares are close to the
        // palette's own ground, so a light palette is not reviewed sitting on a board built for
        // a dark one.
        SKColor toward = palette.Light ? SKColors.Black : SKColors.White;
        SKColor square1 = Derived.Mix(palette.Ground, toward, 0.05f);
        SKColor square2 = Derived.Mix(palette.Ground, toward, 0.11f);
        using (var bg = new SKPaint { Color = square1 }) c.DrawRect(0, 0, w, h, bg);
        using (var sq = new SKPaint { Color = square2 })
            for (int yy = 0; yy < h; yy += 24)
                for (int xx = ((yy / 24) % 2) * 24; xx < w; xx += 48)
                    c.DrawRect(xx, yy, 24, 24, sq);

        c.DrawBitmap(content, 0, 0);
        c.Flush();

        try
        {
            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream fs = File.Open(Path.GetFullPath(outPath), FileMode.Create);
            data.SaveTo(fs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"searchshot: could not write '{outPath}': {ex.Message}");
            return 2;
        }

        Console.WriteLine($"searchshot: '{state}' in {palette.Name}, {w}x{h} -> {Path.GetFullPath(outPath)}");
        return 0;
    }

    private static SKBitmap RenderCapsule(Derived d, SKTypeface face)
    {
        var info = new SKImageInfo((int)CapsuleLayout.Width, (int)CapsuleLayout.Height,
                                   SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        CapsulePainter.Paint(surface.Canvas, "Search 1.5M files", "indexing 4,120 to go", 0.62f, d, face);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    private static SKBitmap RenderCard(string state, Derived d, SKTypeface face)
    {
        SearchCardState s = Build(state);
        int w = (int)Math.Ceiling(SearchCardLayout.Width);
        int h = (int)Math.Ceiling(SearchCardLayout.Height(s.Rows.Count, s.HasQuery, s.AdvOpen));
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        SearchCardPainter.Paint(surface.Canvas, s, d, face);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    // A fixed, fake result set per state - no query engine, no admin rights, no index. This is
    // what lets every state in every palette be reviewed on a machine that has never indexed
    // anything.
    private static SearchCardState Build(string state)
    {
        if (state == "empty")
            return SearchCardState.Empty with { IndexLine = "index: 1.5M names · idle", Clock = 0.2 };

        // The unfold has a Restore on each of the painter's two return paths, and an unbalanced
        // canvas is exactly what that shape produces when one is missed - so both are shot. This
        // is the short one, and the one a user actually sees: clicking the capsule opens a card
        // with no query in it. `opening` below is the same animation over a full result list.
        if (state == "openingempty")
            return SearchCardState.Empty with
            {
                IndexLine = "index: 1.5M names · idle", Clock = 0.2, OpenedAt = 0.13,
            };

        if (state == "adv")
            return SearchCardState.Empty with
            {
                AdvOpen = true, AdvFocus = 0, IndexLine = "index: 1.5M names · idle", Clock = 0.2,
                AdvRules = new SearchAdvanced(AllWords: "lake cabin", NoneWords: "draft",
                    Kind: 1, SizeFrom: "1mb", DateFrom: "2026-01-01"),
                // Close hovered, so the popup's three button surfaces are all on screen at once:
                // Close takes the hover step, Apply rests one above it, and the chosen Kind chip
                // takes the top one. Nothing but a shot ever looked at these.
                HoverTarget = SearchTarget.AdvButton, HoverIndex = 1,
            };

        string query = state switch
        {
            "typing" => "sun",
            "noresults" => "zqxjkv",
            _ => "sunset",
        };

        var fake = new List<SearchResult>();
        if (state is "results" or "many" or "opening")
        {
            fake.Add(new SearchResult(ResultKind.Photo, "IMG_4471.HEIC",
                @"D:\Photos\2025\08 Crete\IMG_4471.HEIC", 0.91f, "looks like \u201csunset over water\u201d"));
            fake.Add(new SearchResult(ResultKind.Photo, "DSC_0982.jpg",
                @"D:\Photos\2024\Eilat\DSC_0982.jpg", 0.86f, "looks like \u201csunset over water\u201d"));
            fake.Add(new SearchResult(ResultKind.Video, "GX010233.MP4",
                @"D:\Video\GoPro\GX010233.MP4", 0.74f, "a moment at 2:14", 134));
            fake.Add(new SearchResult(ResultKind.Document, "Q3-revenue-review.pdf",
                @"C:\Users\rae\Documents\Finance\Q3-revenue-review.pdf", 0.66f, "says it",
                Excerpt: "…the quarterly revenue came in 12% above the plan, driven by the sunset of the legacy tier…"));
            fake.Add(new SearchResult(ResultKind.Document, "הסכם-שכירות-2026.docx",
                @"C:\Users\rae\Documents\הסכם-שכירות-2026.docx", 0.62f, "says it",
                Excerpt: "השוכר מתחייב לפנות את הדירה עד סוף אוגוסט, ולהשיב את המפתחות"));
            fake.Add(new SearchResult(ResultKind.Audio, "Voice 014.m4a",
                @"C:\Users\rae\Music\Voice Memos\Voice 014.m4a", 0.58f, "spoken at 1:07", 67,
                Excerpt: "\u201c…we said sunset, like eight-thirty, and bring the tripod…\u201d"));
            fake.Add(new SearchResult(ResultKind.File, "sunset-preset.lrtemplate",
                @"C:\Users\rae\AppData\Roaming\Adobe\Lightroom\sunset-preset.lrtemplate", 0.90f, "name starts with it"));
            fake.Add(new SearchResult(ResultKind.File, "sunset_over_water.txt",
                @"C:\Users\rae\Documents\notes\sunset_over_water.txt", 0.90f, "name starts with it"));
            fake.Add(new SearchResult(ResultKind.Folder, "Sunsets",
                @"D:\Photos\Collections\Sunsets", 1.0f, "exact name"));
            if (state == "many")
                for (int i = 0; i < 14; i++)
                    fake.Add(new SearchResult(ResultKind.File, $"sunset-{i:00}.txt",
                        $@"C:\Users\rae\Documents\notes\sunset-{i:00}.txt", 0.68f, "name"));
        }

        var results = new SearchResults(query, fake, 2.4, 0, false);
        IReadOnlyList<SearchResult> rows = SearchCardState.Filtered(results, 0);
        // `many` scrolls three rows down, so its highlight has to move with the window - at 0 the
        // one shot with enough rows to be worth looking at showed no selection at all.
        int scroll = state == "many" ? 3 : 0;
        int highlight = scroll + (state == "many" ? 1 : 0);
        return new SearchCardState(query, results, rows, 0, highlight, scroll, state == "typing",
            Caret: query.Length, IndexLine: "index: 1.5M names · idle",
            StageDetail: "3.1 MB · 12 Aug 2026 19:42", Clock: 0.2,
            // `results` hovers a row that is not the selected one: without it the list's hover
            // fill is painted by no shot and no test, which is what let RowHover sit within
            // 1.4 L* of Row - and inverted on two palettes - without anything noticing.
            HoverTarget: state == "results" ? SearchTarget.Row : SearchTarget.None,
            HoverIndex: state == "results" ? 2 : -1,
            // `opening` catches the card mid-unfold with a full list, a third of the way through
            // the 220 ms: the painter takes its SaveLayer + ClipRect branch and has to balance the
            // canvas on the long return path. `openingempty` above covers the early one.
            OpenedAt: state == "opening" ? 0.13 : -1);
    }

}
