using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace Findra;

// The search card: the field, the filter chips, the list, and the stage showing the highlighted
// result large. The layout is a PURE FUNCTION of the state's shape, called by the painter and by
// the hit test, so there is no stored rect list for a pointer event to race against. Do not add one.

public enum SearchTarget { None, Field, Chip, Row, Open, Reveal, Copy, Stage, Content, Adv, AdvField, AdvCheck, AdvKind, AdvButton }

public readonly record struct SearchHit(SearchTarget Target, int Index)
{
    public static readonly SearchHit None = new(SearchTarget.None, -1);
}

public static class SearchCardLayout
{
    public const float Width = 820f;
    public const float Pad = 14f;
    public const float Radius = 16f;
    public const float FieldTop = 12f;
    public const float FieldH = 68f;
    public const float HeaderTop = 90f;       // count + timing line
    public const float ChipsTop = 114f;
    public const float ChipH = 28f;
    public const float BodyTop = 154f;
    public const float RowH = 54f;
    public const float ListW = 496f;          // list column; the stage takes the rest
    public const float StageMinH = 330f;
    public const float FooterH = 40f;
    public const float EmptyHintH = 34f;
    public const int MaxRows = 8;

    public static readonly string[] ChipLabels = { "All", "Photos", "Videos", "Documents", "Audio", "Files & folders" };
    private static readonly float[] ChipW = { 54, 76, 74, 104, 68, 128 };

    public const float ContentW = 96f;        // the two pills beside the field, stacked

    public static SKRect FieldRect() => new(Pad, FieldTop, Width - Pad - ContentW - 10, FieldTop + FieldH);

    public static SKRect ContentRect()
        => new(Width - Pad - ContentW, FieldTop + 1, Width - Pad, FieldTop + 31);

    public static SKRect AdvRect()
        => new(Width - Pad - ContentW, FieldTop + FieldH - 31, Width - Pad, FieldTop + FieldH - 1);

    public static SKRect ChipRect(int i)
    {
        float x = Pad + 2;
        for (int k = 0; k < i; k++) x += ChipW[k] + 6;
        return new SKRect(x, ChipsTop, x + ChipW[i], ChipsTop + ChipH);
    }

    public static int VisibleRows(int count) => Math.Clamp(count, 0, MaxRows);
    public static int MaxScroll(int count) => Math.Max(0, count - MaxRows);
    public static int ClampScroll(int scroll, int count) => Math.Clamp(scroll, 0, MaxScroll(count));

    /// <summary>Body height: the rows, but never shorter than the stage needs.</summary>
    public static float BodyH(int count, bool hasQuery)
    {
        if (!hasQuery) return 0;
        float rows = Math.Max(1, VisibleRows(count)) * RowH;
        return count == 0 ? RowH * 1.6f : Math.Max(rows, StageMinH);
    }

    public static float Height(int count, bool hasQuery, bool advOpen = false)
    {
        float h = hasQuery ? BodyTop + BodyH(count, hasQuery) + FooterH : FieldTop + FieldH + EmptyHintH + 6;
        // the popup draws inside this window, so the card grows to hold it while it is open
        return advOpen ? Math.Max(h, SearchAdvancedLayout.Panel().Bottom + 14) : h;
    }

    public static SKRect RowRect(int visibleIndex)
        => new(Pad, BodyTop + visibleIndex * RowH, Pad + ListW, BodyTop + (visibleIndex + 1) * RowH);

    public static SKRect StageRect(int count, bool hasQuery)
        => new(Pad + ListW + 10, BodyTop, Width - Pad, BodyTop + BodyH(count, hasQuery));

    public static SKRect FooterRect(int count, bool hasQuery)
        => new(0, BodyTop + BodyH(count, hasQuery), Width, Height(count, hasQuery));

    // the three actions under the stage, right column, bottom
    public static SKRect ActionRect(int count, bool hasQuery, int which)
    {
        var st = StageRect(count, hasQuery);
        float w = 88, gap = 6, h = 28;
        float x = st.Left + 8 + which * (w + gap);
        return new SKRect(x, st.Bottom - h - 8, x + w, st.Bottom - 8);
    }

    public static SearchHit HitTest(float x, float y, int count, int scroll, bool hasQuery, bool advOpen = false)
    {
        if (x < 0 || x > Width || y < 0 || y > Height(count, hasQuery, advOpen)) return SearchHit.None;
        // the open popup overlays the card: it answers first, and a miss outside it carries the
        // "close me" marker (Index -2) - except the two pills, which keep working
        if (advOpen)
        {
            var ab0 = AdvRect(); ab0.Inflate(2, 4);
            if (ab0.Contains(x, y)) return new SearchHit(SearchTarget.Adv, -1);
            var cb0 = ContentRect(); cb0.Inflate(2, 4);
            if (cb0.Contains(x, y)) return new SearchHit(SearchTarget.Content, -1);
            return SearchAdvancedLayout.HitTest(x, y);
        }
        if (FieldRect().Contains(x, y)) return new SearchHit(SearchTarget.Field, -1);
        var cb = ContentRect(); cb.Inflate(2, 4);
        if (cb.Contains(x, y)) return new SearchHit(SearchTarget.Content, -1);
        var ab = AdvRect(); ab.Inflate(2, 4);
        if (ab.Contains(x, y)) return new SearchHit(SearchTarget.Adv, -1);
        if (!hasQuery) return SearchHit.None;

        for (int i = 0; i < ChipLabels.Length; i++)
        {
            var c = ChipRect(i); c.Inflate(2, 4);
            if (c.Contains(x, y)) return new SearchHit(SearchTarget.Chip, i);
        }
        if (y >= FooterRect(count, hasQuery).Top) return SearchHit.None;
        if (y < BodyTop) return SearchHit.None;

        if (count > 0)
        {
            for (int a = 0; a < 3; a++)
                if (ActionRect(count, hasQuery, a).Contains(x, y))
                    return new SearchHit(a == 0 ? SearchTarget.Open : a == 1 ? SearchTarget.Reveal : SearchTarget.Copy, -1);
            if (StageRect(count, hasQuery).Contains(x, y)) return new SearchHit(SearchTarget.Stage, -1);
        }
        if (x < Pad || x > Pad + ListW) return SearchHit.None;
        int visible = (int)((y - BodyTop) / RowH);
        if (visible < 0 || visible >= VisibleRows(count)) return SearchHit.None;
        return new SearchHit(SearchTarget.Row, ClampScroll(scroll, count) + visible);
    }
}

/// <summary>Everything the card draws, snapshotted. Rows is the FILTERED list; Highlight and Scroll
/// index into it.</summary>
public sealed record SearchCardState(
    string Query,
    SearchResults Results,
    IReadOnlyList<SearchResult> Rows,
    int Filter,
    int Highlight,
    int Scroll,
    bool Searching,
    int Caret = 0,
    int CaretSlot = -1,
    SearchTarget HoverTarget = SearchTarget.None,
    int HoverIndex = -1,
    string IndexLine = "",
    string StageDetail = "",
    SKImage? StageImage = null,
    double Clock = 0,
    double OpenedAt = -1,
    SearchSort Sort = SearchSort.Best,
    bool Content = false,
    SearchAdvanced? AdvRules = null,
    bool AdvOpen = false,
    int AdvFocus = 0,
    bool QueryAdv = false)
{
    public static readonly SearchCardState Empty =
        new("", SearchResults.Empty, Array.Empty<SearchResult>(), 0, 0, 0, false);

    // The popup's rules are a DRAFT: Apply composes them into the field and empties them, so the
    // field is always the whole query. Only the field decides whether there is one.
    public bool HasQuery => Query.Trim().Length > 0;
    public SearchAdvanced Adv => AdvRules ?? SearchAdvanced.Empty;

    public static IReadOnlyList<SearchResult> Filtered(SearchResults r, int filter)
    {
        if (filter == 0) return r.Rows;
        var list = new List<SearchResult>();
        foreach (var row in r.Rows)
        {
            bool keep = filter switch
            {
                1 => row.Kind == ResultKind.Photo,
                2 => row.Kind == ResultKind.Video,
                3 => row.Kind == ResultKind.Document,
                4 => row.Kind == ResultKind.Audio,
                5 => row.Kind is ResultKind.File or ResultKind.Folder,
                _ => true
            };
            if (keep) list.Add(row);
        }
        return list;
    }

    public static int CountOf(SearchResults r, int filter) => filter == 0 ? r.Rows.Count : Filtered(r, filter).Count;
}

/// <summary>The paint pass. Pure: state in, pixels out.</summary>
public static class SearchCardPainter
{
    public static void Paint(SKCanvas canvas, SearchCardState s, Derived d, SKTypeface face)
    {
        SKColor accent = d.Accent; SKColor text = d.Ink;
        // The card's own face: opaque-ish rather than fully opaque, same as the source, so the
        // window keeps a hair of softness at its own edge.
        SKColor cardBg = d.Ground.WithAlpha(246);

        bool hasQuery = s.HasQuery;
        int count = s.Rows.Count;
        float w = SearchCardLayout.Width, h = SearchCardLayout.Height(count, hasQuery, s.AdvOpen);
        // Two weights under the ink, and both stay legible against the dark card: the first pass
        // used 135/70 and every second line - paths, excerpts, the chips - read as faded.
        var dim = text.WithAlpha(200);
        var faint = text.WithAlpha(130);

        canvas.Clear(SKColors.Transparent);

        // The unfold: for the first 220 ms the card fades in and the part below the field is
        // revealed downward from under it, so the click on the capsule reads as the bar opening
        // rather than a window appearing. A pure function of the clock; nothing is stored.
        float open = s.OpenedAt < 0 ? 1f : (float)Math.Clamp((s.Clock - s.OpenedAt) / 0.22, 0, 1);
        open = 1 - (1 - open) * (1 - open) * (1 - open);   // ease out
        bool unfolding = open < 1f;
        if (unfolding)
        {
            float fieldBottom = SearchCardLayout.FieldTop + SearchCardLayout.FieldH + 6;
            float reveal = fieldBottom + (h - fieldBottom) * open;
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Min(1f, 0.35f + open))) };
            canvas.SaveLayer(layer);
            canvas.ClipRect(new SKRect(0, 0, w, reveal));
        }

        var card = new SKRoundRect(new SKRect(0, 0, w, h), SearchCardLayout.Radius);
        using (var bg = new SKPaint { Color = cardBg, IsAntialias = true }) canvas.DrawRoundRect(card, bg);
        using (var edge = new SKPaint { Color = accent.WithAlpha(52), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
            canvas.DrawRoundRect(card, edge);

        // ---- the field: the same capsule shape the desktop capsule shows at rest, now with a caret in it ----
        var f = SearchCardLayout.FieldRect();
        DrawCapsule(canvas, f, accent, text, d, s.Query, s.Caret,
            hasQuery ? "" : s.Content ? "Describe a photo, words in a document, speech…" : "Search files, photos, words…",
            face, caret: true, clock: s.Clock, focused: true, caretSlot: s.CaretSlot);

        // ---- the two pills: Content (what question the query asks) and Advanced (the popup).
        // Advanced latches orange with a `!` badge while any rule is set, which is the whole
        // "an advanced search is active" indicator - the field stays the user's own words. ----
        void Pill(SKRect cr, string label, bool on, bool hover, bool badge)
        {
            var rr = new SKRoundRect(cr, cr.Height / 2);
            if (on || hover)
                using (var p = new SKPaint { Color = d.RowSelected.WithAlpha((byte)(on ? (hover ? 70 : 46) : 26)), IsAntialias = true })
                    canvas.DrawRoundRect(rr, p);
            using (var p = new SKPaint { Color = on ? accent : text.WithAlpha(hover ? (byte)140 : (byte)70), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f })
                canvas.DrawRoundRect(rr, p);
            CardText.DrawCentred(canvas, label, cr.MidX, cr.MidY + 4.3f, 12.5f, face, on ? accent : hover ? text : dim);
            if (badge)
            {
                float bx = cr.Right - 3, by = cr.Top + 3, br = 8f;
                using (var bp = new SKPaint { Color = accent, IsAntialias = true }) canvas.DrawCircle(bx, by, br, bp);
                using (var be = new SKPaint { Color = cardBg, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f }) canvas.DrawCircle(bx, by, br, be);
                CardText.DrawCentred(canvas, "!", bx, by + 3.8f, 11.5f, face, d.OnAccent, bold: true);
            }
        }
        Pill(SearchCardLayout.ContentRect(), "Content", s.Content, s.HoverTarget == SearchTarget.Content, false);
        Pill(SearchCardLayout.AdvRect(), "Advanced", s.AdvOpen || s.QueryAdv, s.HoverTarget == SearchTarget.Adv, s.QueryAdv);

        if (!hasQuery)
        {
            CardText.Draw(canvas, s.Content
                    ? "a sunset over water  ·  the lease agreement  ·  what was said in a recording  ·  type:photo still narrows"
                    : "*.jpg  ·  \"exact words\"  ·  a | b  ·  !word  ·  ext:pdf  ·  type:photo  ·  in:Downloads  ·  size:huge  ·  modified:week  ·  regex:",
                SearchCardLayout.Pad + 6, f.Bottom + 24, 12.5f, face, dim);
            if (s.AdvOpen) SearchAdvancedPainter.Paint(canvas, s, d, face);
            if (unfolding) canvas.Restore();
            return;
        }

        // ---- header line ----
        string countLine = s.Searching && count == 0 ? "searching…"
            : $"{count} result{(count == 1 ? "" : "s")} for “{s.Query.Trim()}”";
        CardText.Draw(canvas, countLine, SearchCardLayout.Pad + 4, SearchCardLayout.HeaderTop + 12, 13.5f, face, dim);
        string timing = s.Results.NamesMs > 0 ? $"names {s.Results.NamesMs:0.0} ms" : "";
        if (s.Results.ContentReady && s.Results.ContentMs > 0)
            timing += (timing.Length > 0 ? " · " : "") + $"content {s.Results.ContentMs:0} ms";
        if (s.Results.Note.Length > 0) timing = s.Results.Note;
        if (s.Sort != SearchSort.Best) timing = (s.Sort == SearchSort.Newest ? "newest first" : "largest first") + (timing.Length > 0 ? " · " + timing : "");
        CardText.DrawRight(canvas, timing, w - SearchCardLayout.Pad - 4, SearchCardLayout.HeaderTop + 12, 11.5f, face, faint);

        // ---- chips ----
        for (int i = 0; i < SearchCardLayout.ChipLabels.Length; i++)
        {
            var r = SearchCardLayout.ChipRect(i);
            bool on = s.Filter == i;
            bool hover = s.HoverTarget == SearchTarget.Chip && s.HoverIndex == i;
            int n = SearchCardState.CountOf(s.Results, i);
            var rr = new SKRoundRect(r, r.Height / 2);
            if (on)
                using (var p = new SKPaint { Color = d.RowSelected.WithAlpha(46), IsAntialias = true }) canvas.DrawRoundRect(rr, p);
            using (var p = new SKPaint { Color = on ? accent : hover ? text.WithAlpha(120) : text.WithAlpha(60), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f })
                canvas.DrawRoundRect(rr, p);
            var ink = on ? accent : n == 0 ? text.WithAlpha(95) : hover ? text : dim;
            string label = SearchCardLayout.ChipLabels[i];
            float lw = CardText.Measure(label, face, 12.5f);
            string num = n.ToString();
            float nw = CardText.Measure(num, face, 10.5f);
            float x = r.MidX - (lw + 5 + nw) / 2;
            CardText.Draw(canvas, label, x, r.MidY + 4.5f, 12.5f, face, ink);
            CardText.Draw(canvas, num, x + lw + 5, r.MidY + 4.5f, 10.5f, face, ink.WithAlpha((byte)(ink.Alpha * 0.75)));
        }

        DrawRule(canvas, SearchCardLayout.BodyTop - 4, w, d);

        // ---- list ----
        if (count == 0)
        {
            string why = s.Searching ? "Looking…" : s.Filter == 0 ? "Nothing matches" : $"No {SearchCardLayout.ChipLabels[s.Filter].ToLowerInvariant()} match";
            CardText.Draw(canvas, why, SearchCardLayout.Pad + 8, SearchCardLayout.BodyTop + 34, 15f, face, dim);
            string hint = s.Searching ? ""
                : s.Content && !s.Results.ContentReady ? "Content search (photos, documents, speech) is still being built."
                : s.Content ? "" : "Press Content to search what is inside files instead of their names.";
            if (hint.Length > 0)
                CardText.Draw(canvas, hint, SearchCardLayout.Pad + 8, SearchCardLayout.BodyTop + 58, 12.5f, face, faint);
        }
        else
        {
            int scroll = SearchCardLayout.ClampScroll(s.Scroll, count);
            int visible = SearchCardLayout.VisibleRows(count);
            for (int i = 0; i < visible; i++)
            {
                int idx = scroll + i;
                var row = s.Rows[idx];
                var r = SearchCardLayout.RowRect(i);
                bool hi = idx == s.Highlight;
                bool hover = s.HoverTarget == SearchTarget.Row && s.HoverIndex == idx;
                if (hi || hover)
                    using (var p = new SKPaint { Color = d.RowSelected.WithAlpha((byte)(hi ? 40 : 22)), IsAntialias = true })
                        canvas.DrawRoundRect(new SKRoundRect(new SKRect(r.Left, r.Top + 3, r.Right, r.Bottom - 3), 9), p);

                // the tile: a kind glyph on a name-hashed tint (no thumbnails in the list - the
                // stage shows the picture; a list of 8 decodes per keystroke is what made Load slow)
                var tile = new SKRect(r.Left + 8, r.Top + 9, r.Left + 44, r.Bottom - 9);
                DrawTile(canvas, tile, row, face, text, accent);

                float tx = tile.Right + 12;
                float maxW = r.Right - tx - 66;
                CardText.Draw(canvas, CardText.Ellipsize(row.Name, face, 15f, maxW), tx, r.Top + 24, 15f, face, text, bold: hi);
                string sub = row.Excerpt.Length > 0 ? row.Excerpt : Folder(row.Path);
                CardText.Draw(canvas, CardText.Ellipsize(sub, face, 11.5f, maxW + 30, keepEnd: row.Excerpt.Length == 0), tx, r.Top + 41, 11.5f, face, hi ? dim : text.WithAlpha(150));

                // kind tag, right
                string tag = FileKinds.Label(row.Kind).ToUpperInvariant();
                float tw = CardText.Measure(tag, face, 9.5f);
                var tagR = new SKRect(r.Right - tw - 22, r.Top + 17, r.Right - 8, r.Top + 33);
                using (var p = new SKPaint { Color = d.Chip.WithAlpha(hi ? (byte)40 : (byte)24), IsAntialias = true })
                    canvas.DrawRoundRect(new SKRoundRect(tagR, 4), p);
                CardText.Draw(canvas, tag, tagR.Left + 7, tagR.MidY + 3.5f, 9.5f, face, hi ? text : dim);
            }
            DrawScrollHint(canvas, count, scroll, d);

            // ---- stage ----
            DrawStage(canvas, s, d, accent, text, face);
        }

        // ---- footer ----
        var footer = SearchCardLayout.FooterRect(count, hasQuery);
        DrawRule(canvas, footer.Top, w, d);
        CardText.Draw(canvas, "Enter opens · right-click reveals · drag a row into any app · Ctrl+1/2/3 best / newest / largest",
            SearchCardLayout.Pad + 4, footer.MidY + 4.5f, 11.5f, face, faint);
        CardText.DrawRight(canvas, s.IndexLine, w - SearchCardLayout.Pad - 4, footer.MidY + 4.5f, 11.5f, face, faint);
        if (s.AdvOpen) SearchAdvancedPainter.Paint(canvas, s, d, face);
        if (unfolding) canvas.Restore();
    }

    /// <summary>Is the open animation still running at this clock?</summary>
    public static bool Unfolding(SearchCardState s) => s.OpenedAt >= 0 && s.Clock - s.OpenedAt < 0.25;

    /// <summary>The capsule, drawn here in the same shape it takes at rest on the desktop, so the
    /// card reads as it unfolding rather than a different control appearing in its place.</summary>
    /// <summary>Where the query's characters sit in the field. The field shows a window of the
    /// query that keeps the caret visible: <c>Skip</c> leading characters are hidden, and the
    /// caret is drawn at <c>CaretX</c> px from the text's left edge. Pure, so the click-to-place
    /// hit test can call it and land on the same glyph the painter drew.</summary>
    public static (int Skip, float CaretX) FieldMetrics(string query, int caret, SKTypeface face, float size, float maxW, int slot = -1)
    {
        caret = Math.Clamp(caret, 0, query.Length);
        // a query that fits needs no window - the common case, one measure
        if (CardText.Measure(query, face, size) <= maxW)
        {
            var cells = FieldCaret.Cells(query, face, size);
            return (0, FieldCaret.SlotX(cells, FieldCaret.Of(cells, query, caret, slot).Slot));
        }
        // drop leading characters until the caret's x fits; the window is re-laid out each step
        // because reordering can change once a run is cut
        int skip = 0;
        while (true)
        {
            string shown = query[skip..];
            var cells = FieldCaret.Cells(shown, face, size);
            float cx = FieldCaret.SlotX(cells, FieldCaret.Of(cells, shown, caret - skip).Slot);
            if (cx <= maxW || skip >= caret) return (skip, cx);
            skip = Math.Min(caret, skip + 2);
        }
    }

    /// <summary>The caret position a click at <paramref name="x"/> px (from the text's left edge)
    /// lands on, given the same window the painter drew.</summary>
    public static FieldCaret.Position CaretAt(string query, int caret, SKTypeface face, float size, float maxW, float x)
    {
        var (skip, _) = FieldMetrics(query, caret, face, size, maxW);
        string shown = query[skip..];
        var p = FieldCaret.AtX(FieldCaret.Cells(shown, face, size), shown, x);
        return skip == 0 ? p : new FieldCaret.Position(-1, skip + p.Caret);
    }

    /// <summary>One step left or right ON THE SCREEN, through Hebrew and Latin alike.</summary>
    public static FieldCaret.Position CaretStep(string query, int caret, int slot, SKTypeface face, float size, int dir)
    {
        var cells = FieldCaret.Cells(query, face, size);
        return FieldCaret.Move(cells, query, FieldCaret.Of(cells, query, caret, slot), dir);
    }

    public static void DrawCapsule(SKCanvas canvas, SKRect r, SKColor accent, SKColor text, Derived derived, string query, int caretIndex,
        string placeholder, SKTypeface face, bool caret, double clock, bool focused, int caretSlot = -1)
    {
        var rr = new SKRoundRect(r, r.Height / 2);
        using (var bg = new SKPaint { Color = derived.Ground.WithAlpha(190), IsAntialias = true }) canvas.DrawRoundRect(rr, bg);
        if (focused)
        {
            var halo = new SKRoundRect(new SKRect(r.Left - 3, r.Top - 3, r.Right + 3, r.Bottom + 3), r.Height / 2 + 3);
            using var hp = new SKPaint { Color = derived.AccentGlow, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
            canvas.DrawRoundRect(halo, hp);
        }
        using (var edge = new SKPaint { Color = focused ? accent : accent.WithAlpha(90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
            canvas.DrawRoundRect(rr, edge);

        // magnifier
        float cx = r.Left + r.Height * 0.55f, cy = r.MidY, rad = r.Height * 0.17f;
        using (var p = new SKPaint { Color = text.WithAlpha(focused ? (byte)200 : (byte)150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = r.Height * 0.045f, StrokeCap = SKStrokeCap.Round })
        {
            canvas.DrawCircle(cx - rad * 0.3f, cy - rad * 0.3f, rad, p);
            float d = rad * 0.72f;
            canvas.DrawLine(cx - rad * 0.3f + d, cy - rad * 0.3f + d, cx + rad * 1.05f, cy + rad * 1.05f, p);
        }

        float tx = r.Left + r.Height * 1.05f;
        float size = r.Height * 0.40f;
        float maxW = r.Right - tx - r.Height * 0.5f;
        if (query.Length > 0)
        {
            // the window of the query that keeps the caret in view; clipped so a long query ends at
            // the capsule's inner edge rather than under it
            var (skip, caretX) = FieldMetrics(query, caretIndex, face, size, maxW, caretSlot);
            canvas.Save();
            canvas.ClipRect(new SKRect(tx - 2, r.Top, tx + maxW + 2, r.Bottom));
            CardText.Draw(canvas, query[skip..], tx, cy + size * 0.36f, size, face, text);
            canvas.Restore();
            if (caret && (int)(clock * 2) % 2 == 0)
            {
                float qx = tx + caretX + 1;
                using var cp = new SKPaint { Color = accent, IsAntialias = true, StrokeWidth = 2 };
                canvas.DrawLine(qx, cy - size * 0.55f, qx, cy + size * 0.55f, cp);
            }
        }
        else
        {
            CardText.Draw(canvas, CardText.Ellipsize(placeholder, face, size, maxW), tx, cy + size * 0.36f, size, face, text.WithAlpha(90));
            if (caret && (int)(clock * 2) % 2 == 0)
            {
                using var cp = new SKPaint { Color = accent, IsAntialias = true, StrokeWidth = 2 };
                canvas.DrawLine(tx - 3, cy - size * 0.55f, tx - 3, cy + size * 0.55f, cp);
            }
        }
    }

    /// <summary>The text's left edge, its font size and the width it may use, for a capsule.</summary>
    public static (float TextLeft, float Size, float MaxW) FieldText(SKRect r)
        => (r.Left + r.Height * 1.05f, r.Height * 0.40f, r.Right - (r.Left + r.Height * 1.05f) - r.Height * 0.5f);

    private static void DrawStage(SKCanvas canvas, SearchCardState s, Derived d, SKColor accent, SKColor text, SKTypeface face)
    {
        int count = s.Rows.Count;
        var st = SearchCardLayout.StageRect(count, true);
        if (s.Highlight < 0 || s.Highlight >= count) return;
        var row = s.Rows[s.Highlight];
        var dim = text.WithAlpha(215);
        var faint = text.WithAlpha(140);

        // the picture: square for photos and files, 16:9 for video
        bool wide = row.Kind == ResultKind.Video;
        float pw = st.Width - 16;
        float ph = wide ? pw * 9 / 16 : Math.Min(pw, 190);
        var pic = new SKRect(st.Left + 8, st.Top + 8, st.Left + 8 + pw, st.Top + 8 + ph);
        var prr = new SKRoundRect(pic, 10);

        if (s.StageImage is SKImage img)
        {
            canvas.Save();
            canvas.ClipRoundRect(prr, antialias: true);
            // centre-crop, never stretch
            float scale = Math.Max(pic.Width / img.Width, pic.Height / img.Height);
            float dw = img.Width * scale, dh = img.Height * scale;
            var dst = new SKRect(pic.MidX - dw / 2, pic.MidY - dh / 2, pic.MidX + dw / 2, pic.MidY + dh / 2);
            using var ip = new SKPaint { IsAntialias = true };
            canvas.DrawImage(img, dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), ip);
            canvas.Restore();
        }
        else
        {
            DrawNoArt(canvas, prr, row, face, text, big: true);
            if (row.Kind == ResultKind.Document && row.Excerpt.Length > 0)
            {
                float y = pic.Top + 26;
                foreach (var line in CardText.Wrap(row.Excerpt, face, 12.5f, pic.Width - 24, 6))
                {
                    CardText.Draw(canvas, line, pic.Left + 12, y, 12.5f, face, text.WithAlpha(200));
                    y += 17;
                }
            }
        }
        if (row.MomentSeconds >= 0)
        {
            string ts = TimeSpan.FromSeconds(row.MomentSeconds).ToString(row.MomentSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
            float tw = CardText.Measure(ts, face, 11f) + 12;
            var tr = new SKRect(pic.Right - tw - 8, pic.Bottom - 26, pic.Right - 8, pic.Bottom - 8);
            using (var p = new SKPaint { Color = d.Stage, IsAntialias = true }) canvas.DrawRoundRect(new SKRoundRect(tr, 4), p);
            // Not SKColors.White: the badge behind this is d.Stage now, not a fixed black
            // scrim, and Ink-on-Stage is the pair Derived already guarantees >= 4.5 contrast on
            // every palette.
            CardText.Draw(canvas, ts, tr.Left + 6, tr.MidY + 4, 11f, face, text);
        }

        float y2 = pic.Bottom + 24;
        CardText.Draw(canvas, CardText.Ellipsize(row.Name, face, 15f, st.Width - 16), st.Left + 8, y2, 15f, face, text, bold: true);
        y2 += 20;

        void Kv(string k, string v)
        {
            CardText.Draw(canvas, k, st.Left + 8, y2, 11.5f, face, faint);
            CardText.Draw(canvas, CardText.Ellipsize(v, face, 11.5f, st.Width - 72, keepEnd: k == "where"), st.Left + 60, y2, 11.5f, face, dim);
            y2 += 17;
        }
        Kv("where", Folder(row.Path));
        Kv("match", row.Why);
        if (s.StageDetail.Length > 0) Kv("file", s.StageDetail);
        else if (row.Modified != default) Kv("file", (row.Size > 0 ? Human(row.Size) + " · " : "") + row.Modified.ToString("d MMM yyyy HH:mm"));
        Kv("score", $"{row.Score * 100:0}%");

        // the three actions
        string[] labels = { "Open", "Reveal", "Copy path" };
        for (int a = 0; a < 3; a++)
        {
            var ar = SearchCardLayout.ActionRect(count, true, a);
            var target = a == 0 ? SearchTarget.Open : a == 1 ? SearchTarget.Reveal : SearchTarget.Copy;
            bool hover = s.HoverTarget == target;
            bool primary = a == 0;
            var rr = new SKRoundRect(ar, ar.Height / 2);
            if (primary || hover)
                using (var p = new SKPaint { Color = d.RowSelected.WithAlpha((byte)(primary ? (hover ? 70 : 46) : 26)), IsAntialias = true }) canvas.DrawRoundRect(rr, p);
            using (var p = new SKPaint { Color = primary ? accent : text.WithAlpha(hover ? (byte)140 : (byte)70), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f })
                canvas.DrawRoundRect(rr, p);
            CardText.DrawCentred(canvas, labels[a], ar.MidX, ar.MidY + 4.5f, 12.5f, face, primary ? accent : hover ? text : dim);
        }
    }

    // A kind glyph on a tint hashed from the name: the no-picture state is the COMMON one for
    // files, and it must look composed rather than broken, so the saturation stays low.
    public static void DrawTile(SKCanvas canvas, SKRect r, SearchResult row, SKTypeface face, SKColor text, SKColor accent)
        => DrawNoArt(canvas, new SKRoundRect(r, 8), row, face, text, big: false);

    private static void DrawNoArt(SKCanvas canvas, SKRoundRect rr, SearchResult row, SKTypeface face, SKColor text, bool big)
    {
        var r = rr.Rect;
        uint hsh = 2166136261;
        foreach (char c in row.Name) { hsh ^= c; hsh *= 16777619; }
        float hue = (hsh % 360);
        var top = SKColor.FromHsl(hue, 30, big ? 30 : 27);
        var bot = SKColor.FromHsl((hue + 30) % 360, 26, big ? 17 : 15);
        using (var p = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom), new[] { top, bot }, SKShaderTileMode.Clamp) })
            canvas.DrawRoundRect(rr, p);

        string glyph = row.Kind switch
        {
            ResultKind.Folder => "",
            ResultKind.Photo => "IMG",
            ResultKind.Video => "VID",
            ResultKind.Audio => "AUD",
            _ => Ext(row.Name)
        };
        if (row.Kind == ResultKind.Folder)
        {
            // a folder: tab + body, the way every file manager draws it
            float fw = r.Width * (big ? 0.42f : 0.62f), fh = fw * 0.78f;
            float x = r.MidX - fw / 2, y = r.MidY - fh / 2 + fh * 0.08f;
            // Not `text`: this glyph sits on a name-hashed gradient that is always dark, on
            // every palette (see the HSL literals above) - it is not the card's ground, so it
            // does not take the card's ink. White is what stays legible on it either way.
            using var fp = new SKPaint { Color = SKColors.White.WithAlpha(big ? (byte)190 : (byte)235), IsAntialias = true };
            var body = new SKRect(x, y + fh * 0.22f, x + fw, y + fh);
            var tab = new SKRect(x, y, x + fw * 0.45f, y + fh * 0.36f);
            canvas.DrawRoundRect(new SKRoundRect(tab, fh * 0.08f), fp);
            canvas.DrawRoundRect(new SKRoundRect(body, fh * 0.08f), fp);
            return;
        }
        float size = big ? Math.Min(34f, r.Width * 0.16f) : 9.5f;
        // Same reasoning as the folder glyph above: the tile under this is always dark.
        CardText.DrawCentred(canvas, glyph, r.MidX, r.MidY + size * 0.36f, size, face, SKColors.White.WithAlpha(big ? (byte)150 : (byte)230), bold: true);
    }

    private static string Human(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes / 1024.0;
        if (v < 1024) return $"{v:0.#} KB";
        v /= 1024;
        if (v < 1024) return $"{v:0.#} MB";
        return $"{v / 1024:0.##} GB";
    }

    private static string Ext(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return "FILE";
        string e = name[(dot + 1)..].ToUpperInvariant();
        return e.Length > 4 ? e[..4] : e;
    }

    private static string Folder(string path)
    {
        try { return Path.GetDirectoryName(path) ?? path; } catch { return path; }
    }

    private static void DrawScrollHint(SKCanvas canvas, int count, int scroll, Derived d)
    {
        int visible = SearchCardLayout.VisibleRows(count);
        if (count <= visible) return;
        float top = SearchCardLayout.BodyTop + 6, bottom = SearchCardLayout.BodyTop + visible * SearchCardLayout.RowH - 6;
        float track = bottom - top;
        float thumb = Math.Max(18f, track * visible / count);
        float max = SearchCardLayout.MaxScroll(count);
        float y = top + (track - thumb) * (max <= 0 ? 0 : scroll / max);
        float x = SearchCardLayout.Pad + SearchCardLayout.ListW + 2;
        using var p = new SKPaint { Color = d.RowSelected.WithAlpha(90), IsAntialias = true };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(x, y, x + 2.5f, y + thumb), 1.3f), p);
    }

    private static void DrawRule(SKCanvas canvas, float y, float w, Derived d)
    {
        using var p = new SKPaint { Color = d.Edge, IsAntialias = false, StrokeWidth = 1 };
        canvas.DrawLine(SearchCardLayout.Pad * 0.6f, y, w - SearchCardLayout.Pad * 0.6f, y, p);
    }
}
