using System;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;

namespace Findra;

// The Advanced Search popup: a familiar file-search dialog, spoken in Findra's grammar. The rules
// are HELD state (the field stays the user's own words); Compose() folds them into the query that
// actually runs. While any rule is set the Advanced pill latches orange and carries a `!` badge -
// that is the whole "is it on" indicator, chosen over a badge row under the field.
//
// The popup is drawn INSIDE the card's own window - a second window would deactivate the card and
// close it. Layout is a pure function, same as the rest of the card: the painter and the hit test
// both call SearchAdvancedLayout, so there is no stored rect list.

/// <summary>Everything the popup holds. Immutable; the card swaps whole records.</summary>
public sealed record SearchAdvanced(
    string AllWords = "",
    string Phrase = "",
    string AnyWords = "",
    string NoneWords = "",
    bool MatchCase = false,
    bool WholeWords = false,
    string InFile = "",
    string In = "",
    int Kind = 0,
    string DateFrom = "",
    string DateTo = "",
    string SizeFrom = "",
    string SizeTo = "")
{
    public static readonly SearchAdvanced Empty = new();

    public static readonly string[] KindLabels = { "Anything", "Photos", "Videos", "Documents", "Audio", "Folders" };
    private static readonly string[] KindTokens = { "", "photo", "video", "doc", "audio", "folder" };

    public bool IsEmpty => AllWords.Length == 0 && Phrase.Length == 0 && AnyWords.Length == 0
        && NoneWords.Length == 0 && InFile.Length == 0 && In.Length == 0 && Kind == 0
        && DateFrom.Length == 0 && DateTo.Length == 0 && SizeFrom.Length == 0 && SizeTo.Length == 0;

    /// <summary>Does an active rule ask the inside-of-files question? Lights Content mode.</summary>
    public bool WantsContent => InFile.Trim().Length > 0;

    /// <summary>The query that actually runs: the typed query plus every rule, in the grammar.
    /// "Any of these words" distributes over the rest (`base a | base b`), because OR is
    /// top-level only.</summary>
    public string Compose(string typed)
    {
        if (IsEmpty) return typed;
        var b = new List<string>();
        string t = typed.Trim();
        if (t.Length > 0) b.Add(t);
        foreach (var w in Split(AllWords)) b.Add(Wrap(w));
        string ph = Phrase.Trim().Trim('"');
        if (ph.Length > 0) b.Add("\"" + ph + "\"");
        foreach (var w in Split(NoneWords)) b.Add("!" + w);
        foreach (var w in Split(InFile)) b.Add(Wrap(w));
        string loc = In.Trim();
        if (loc.Length > 0) b.Add("in:" + (loc.Contains(' ') ? "\"" + loc + "\"" : loc));
        if (Kind > 0 && Kind < KindTokens.Length) b.Add("type:" + KindTokens[Kind]);
        string d1 = DateFrom.Trim(), d2 = DateTo.Trim();
        if (d1.Length > 0 && d2.Length > 0) b.Add("modified:" + d1 + ".." + d2);
        else if (d1.Length > 0) b.Add("modified:" + d1 + "..2999");
        else if (d2.Length > 0) b.Add("modified:1980.." + d2);
        string s1 = SizeFrom.Trim(), s2 = SizeTo.Trim();
        if (s1.Length > 0 && s2.Length > 0) b.Add("size:" + s1 + ".." + s2);
        else if (s1.Length > 0) b.Add("size:>" + s1);
        else if (s2.Length > 0) b.Add("size:<" + s2);

        string baseQ = string.Join(" ", b);
        var any = Split(AnyWords);
        if (any.Count == 0) return baseQ;
        var parts = new List<string>();
        foreach (var a in any) parts.Add((baseQ.Length > 0 ? baseQ + " " : "") + a);
        return string.Join(" | ", parts);
    }

    private string Wrap(string w) => MatchCase ? "case:" + w : WholeWords ? "ww:" + w : w;

    private static List<string> Split(string s)
    {
        var list = new List<string>();
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) list.Add(w);
        return list;
    }

    /// <summary>Field access by index, for the focus/typing plumbing. Order is the visual order.</summary>
    public const int FieldCount = 10;
    public string Field(int i) => i switch
    {
        0 => AllWords, 1 => Phrase, 2 => AnyWords, 3 => NoneWords, 4 => InFile,
        5 => In, 6 => DateFrom, 7 => DateTo, 8 => SizeFrom, 9 => SizeTo, _ => ""
    };
    public SearchAdvanced WithField(int i, string v) => i switch
    {
        0 => this with { AllWords = v }, 1 => this with { Phrase = v }, 2 => this with { AnyWords = v },
        3 => this with { NoneWords = v }, 4 => this with { InFile = v }, 5 => this with { In = v },
        6 => this with { DateFrom = v }, 7 => this with { DateTo = v }, 8 => this with { SizeFrom = v },
        9 => this with { SizeTo = v }, _ => this
    };
}

public static class SearchAdvancedLayout
{
    public const float W = 566f;
    public const float Pad = 18f;
    public const float LabelW = 132f;
    public const float RowH = 38f;
    public const float FieldH = 30f;
    public const float HeaderH = 40f;
    public const float SectH = 26f;
    public const float BtnH = 30f;

    // Row map, top to bottom: 4 word fields, the checks row, a section, in-file, a section,
    // located-in, kind chips, a section, modified duo, size duo, then the buttons.
    private static float FieldTop(int i) => i switch
    {
        0 => HeaderH,                          // all words
        1 => HeaderH + RowH,                   // phrase
        2 => HeaderH + RowH * 2,               // any
        3 => HeaderH + RowH * 3,               // none
        4 => HeaderH + RowH * 4 + 30 + SectH,  // in file (after checks + section)
        5 => FieldTop(4) + RowH + SectH,       // located in (after section)
        6 => FieldTop(5) + RowH + 34 + SectH,  // modified from (after kind + section)
        7 => FieldTop(6),                      // modified to (same row)
        8 => FieldTop(6) + RowH,               // size from
        9 => FieldTop(8),                      // size to
        _ => 0
    };

    public static float ChecksTop => HeaderH + RowH * 4 + 2;
    public static float KindTop => FieldTop(5) + RowH + 2;
    public static float ButtonsTop => FieldTop(8) + RowH + 8;
    public static float H => ButtonsTop + BtnH + 14;

    /// <summary>The popup, anchored under the pills at the card's right edge.</summary>
    public static SKRect Panel()
        => new(SearchCardLayout.Width - SearchCardLayout.Pad - W,
               SearchCardLayout.FieldTop + SearchCardLayout.FieldH + 8,
               SearchCardLayout.Width - SearchCardLayout.Pad,
               SearchCardLayout.FieldTop + SearchCardLayout.FieldH + 8 + H);

    public static SKRect FieldRect(int i)
    {
        var p = Panel();
        float top = p.Top + FieldTop(i) + (RowH - FieldH) / 2;
        if (i is 6 or 7)   // modified from .. to
        {
            float half = (W - Pad * 2 - LabelW - 26) / 2;
            float x = p.Left + Pad + LabelW + (i == 6 ? 0 : half + 26);
            return new SKRect(x, top, x + half, top + FieldH);
        }
        if (i is 8 or 9)   // size from .. to
        {
            float half = (W - Pad * 2 - LabelW - 26) / 2;
            float x = p.Left + Pad + LabelW + (i == 8 ? 0 : half + 26);
            return new SKRect(x, top, x + half, top + FieldH);
        }
        return new SKRect(p.Left + Pad + LabelW, top, p.Right - Pad, top + FieldH);
    }

    public static SKRect CheckRect(int i)   // 0 match case, 1 whole words
    {
        var p = Panel();
        float x = p.Left + Pad + LabelW + i * 150;
        return new SKRect(x, p.Top + ChecksTop, x + 140, p.Top + ChecksTop + 24);
    }

    public static SKRect KindRect(int i)
    {
        var p = Panel();
        float x = p.Left + Pad + LabelW;
        for (int k = 0; k < i; k++) x += KindW(k) + 6;
        return new SKRect(x, p.Top + KindTop, x + KindW(i), p.Top + KindTop + 24);
    }

    private static float KindW(int i) => i switch { 0 => 66, 1 => 56, 2 => 54, 3 => 80, 4 => 50, 5 => 58, _ => 60 };

    public static SKRect ButtonRect(int i)   // 0 Clear, 1 Close, 2 Apply (right-aligned)
    {
        var p = Panel();
        float w = i == 2 ? 84 : 66, gap = 8;
        float right = p.Right - Pad;
        for (int k = 2; k > i; k--) right -= (k == 2 ? 84 : 66) + gap;
        return new SKRect(right - w, p.Top + ButtonsTop, right, p.Top + ButtonsTop + BtnH);
    }

    public static readonly string[] FieldLabels =
        { "All these words", "This exact phrase", "Any of these words", "None of these words",
          "Words in the file", "Located in", "Modified", "", "Size", "" };

    /// <summary>Hit test inside the open popup, in card coordinates. Field/check/kind/button,
    /// or None for a miss INSIDE the panel; a point outside the panel returns Target None with
    /// Index -2 so the caller can close it.</summary>
    public static SearchHit HitTest(float x, float y)
    {
        var p = Panel();
        if (!p.Contains(x, y)) return new SearchHit(SearchTarget.None, -2);
        for (int i = 0; i < SearchAdvanced.FieldCount; i++)
        {
            var r = FieldRect(i); r.Inflate(2, 4);
            if (r.Contains(x, y)) return new SearchHit(SearchTarget.AdvField, i);
        }
        for (int i = 0; i < 2; i++) if (CheckRect(i).Contains(x, y)) return new SearchHit(SearchTarget.AdvCheck, i);
        for (int i = 0; i < SearchAdvanced.KindLabels.Length; i++)
        {
            var r = KindRect(i); r.Inflate(2, 3);
            if (r.Contains(x, y)) return new SearchHit(SearchTarget.AdvKind, i);
        }
        for (int i = 0; i < 3; i++) if (ButtonRect(i).Contains(x, y)) return new SearchHit(SearchTarget.AdvButton, i);
        return new SearchHit(SearchTarget.None, -1);
    }
}

public static class SearchAdvancedPainter
{
    public static void Paint(SKCanvas canvas, SearchCardState s, Derived d, SKTypeface face)
    {
        SKColor accent = d.Accent; SKColor text = d.Ink;
        var p = SearchAdvancedLayout.Panel();
        // Every reduced ink in the popup goes through d.Fade, not WithAlpha, for the same
        // reason the card's does: a raw alpha is a mix fraction wearing a different hat, and it
        // needs the same light-ground correction or the whole popup reads a step fainter on Paper.
        var dim = d.Fade(200);
        var faint = d.Fade(120);

        using (var sh = new SKPaint { Color = d.Shadow, IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
            canvas.DrawRoundRect(new SKRoundRect(p, 14), sh);
        using (var bg = new SKPaint { Color = d.Ground.WithAlpha(252), IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(p, 14), bg);
        using (var edge = new SKPaint { Color = accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
            canvas.DrawRoundRect(new SKRoundRect(p, 14), edge);

        CardText.Draw(canvas, "ADVANCED SEARCH", p.Left + SearchAdvancedLayout.Pad, p.Top + 26, 12.5f, face, accent, bold: true);

        // the section captions, each above the group its row budget reserved space for
        void Sect(string label, int aboveField)
        {
            float y = SearchAdvancedLayout.FieldRect(aboveField).Top - 12;
            CardText.Draw(canvas, label, p.Left + SearchAdvancedLayout.Pad, y, 10.5f, face, faint, bold: true);
            using var line = new SKPaint { Color = d.Edge, StrokeWidth = 1 };
            float lx = p.Left + SearchAdvancedLayout.Pad + CardText.Measure(label, face, 10.5f) + 10;
            canvas.DrawLine(lx, y - 3.5f, p.Right - SearchAdvancedLayout.Pad, y - 3.5f, line);
        }
        Sect("INSIDE THE FILE", 4);
        Sect("WHERE & WHAT", 5);
        Sect("DATE & SIZE", 6);

        var adv = s.Adv;
        for (int i = 0; i < SearchAdvanced.FieldCount; i++)
        {
            var r = SearchAdvancedLayout.FieldRect(i);
            string label = SearchAdvancedLayout.FieldLabels[i];
            if (label.Length > 0)
                CardText.Draw(canvas, label, p.Left + SearchAdvancedLayout.Pad, r.MidY + 4.5f, 12f, face, dim);
            if (i is 7 or 9)
                CardText.Draw(canvas, "to", r.Left - 20, r.MidY + 4f, 11.5f, face, faint);

            bool focus = s.AdvFocus == i;
            var rr = new SKRoundRect(r, 8);
            using (var fb = new SKPaint { Color = d.Row, IsAntialias = true }) canvas.DrawRoundRect(rr, fb);
            using (var fe = new SKPaint { Color = focus ? accent : d.Fade(56), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f })
                canvas.DrawRoundRect(rr, fe);

            string v = adv.Field(i);
            float size = 12.5f, tx = r.Left + 9;
            float maxW = r.Width - 18;
            if (v.Length > 0)
            {
                // keep the tail visible while typing
                string shown = v;
                while (shown.Length > 1 && CardText.Measure(shown, face, size) > maxW) shown = shown[1..];
                CardText.Draw(canvas, shown, tx, r.MidY + 4.5f, size, face, text);
                if (focus && (int)(s.Clock * 2) % 2 == 0)
                {
                    float cx = tx + CardText.Measure(shown, face, size) + 1;
                    using var cp = new SKPaint { Color = accent, StrokeWidth = 1.6f, IsAntialias = true };
                    canvas.DrawLine(cx, r.Top + 7, cx, r.Bottom - 7, cp);
                }
            }
            else
            {
                string ph = i switch
                {
                    0 => "blue lake photo", 1 => "summer holiday", 2 => "jpg heic raw", 3 => "draft copy",
                    4 => "switches Content on", 5 => @"Downloads or C:\Users\you\Pictures",
                    6 => "2026-01-01 · week · 2025", 7 => "2026-03-15", 8 => "1mb · huge", 9 => "100mb", _ => ""
                };
                CardText.Draw(canvas, CardText.Ellipsize(ph, face, size, maxW), tx, r.MidY + 4.5f, size, face, d.Fade(70));
                if (focus && (int)(s.Clock * 2) % 2 == 0)
                {
                    using var cp = new SKPaint { Color = accent, StrokeWidth = 1.6f, IsAntialias = true };
                    canvas.DrawLine(tx, r.Top + 7, tx, r.Bottom - 7, cp);
                }
            }
        }

        // the checks
        string[] checks = { "Match case", "Whole words" };
        for (int i = 0; i < 2; i++)
        {
            var r = SearchAdvancedLayout.CheckRect(i);
            bool on = i == 0 ? adv.MatchCase : adv.WholeWords;
            var box = new SKRect(r.Left, r.MidY - 8, r.Left + 16, r.MidY + 8);
            using (var be = new SKPaint { Color = on ? accent : d.Fade(90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f })
                canvas.DrawRoundRect(new SKRoundRect(box, 4), be);
            if (on)
            {
                using var tick = new SKPaint { Color = accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round };
                canvas.DrawLine(box.Left + 3.5f, box.MidY + 1, box.MidX - 1, box.Bottom - 4, tick);
                canvas.DrawLine(box.MidX - 1, box.Bottom - 4, box.Right - 3, box.Top + 4, tick);
            }
            CardText.Draw(canvas, checks[i], box.Right + 8, r.MidY + 4.5f, 12f, face, on ? text : dim);
        }

        // the kind chips
        CardText.Draw(canvas, "Kind", p.Left + SearchAdvancedLayout.Pad, SearchAdvancedLayout.KindRect(0).MidY + 4.5f, 12f, face, dim);
        for (int i = 0; i < SearchAdvanced.KindLabels.Length; i++)
        {
            var r = SearchAdvancedLayout.KindRect(i);
            bool on = adv.Kind == i;
            var rr = new SKRoundRect(r, r.Height / 2);
            // Opaque d.RowSelected, the same fill the card's active filter chip takes. Diluting
            // it to alpha 46 over the ground gave the chosen Kind a 2.2-2.8 L* signal where the
            // card's equivalent has 13-15.6; the surface is already derived to be exactly one
            // step off the ground, so drawing it at a fifth of its weight throws that away.
            if (on) using (var f = new SKPaint { Color = d.RowSelected, IsAntialias = true }) canvas.DrawRoundRect(rr, f);
            using (var e = new SKPaint { Color = on ? accent : d.Fade(56), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f })
                canvas.DrawRoundRect(rr, e);
            CardText.DrawCentred(canvas, SearchAdvanced.KindLabels[i], r.MidX, r.MidY + 3.8f, 10.8f, face, on ? accent : dim);
        }

        // the composed query, live: the popup teaches the grammar as it is used
        string composed = adv.Compose(s.Query.Trim());
        var pv = new SKRect(p.Left + SearchAdvancedLayout.Pad, p.Top + SearchAdvancedLayout.ButtonsTop,
            SearchAdvancedLayout.ButtonRect(0).Left - 12, p.Top + SearchAdvancedLayout.ButtonsTop + SearchAdvancedLayout.BtnH);
        CardText.Draw(canvas, CardText.Ellipsize(composed.Length > 0 ? composed : "…", face, 11f, pv.Width), pv.Left, pv.MidY + 3.5f, 11f, face, faint);

        // the buttons
        string[] btns = { "Clear", "Close", "Apply" };
        for (int i = 0; i < 3; i++)
        {
            var r = SearchAdvancedLayout.ButtonRect(i);
            bool primary = i == 2;
            bool hover = s.HoverTarget == SearchTarget.AdvButton && s.HoverIndex == i;
            var rr = new SKRoundRect(r, r.Height / 2);
            // Three levels, three derived surfaces, no alpha: the row ladder is built so that
            // ground < Row < RowHover < RowSelected each clear about 2 L*, which is exactly the
            // hierarchy these buttons want. Apply rests one step up and takes the top step when
            // the pointer is on it; Clear and Close light only on hover, at the bottom step.
            // The old 26/46/70 dilution gave all three a 1.2-4.3 L* spread against the ground.
            if (primary || hover)
                using (var f = new SKPaint { Color = primary ? (hover ? d.RowSelected : d.RowHover) : d.Row, IsAntialias = true }) canvas.DrawRoundRect(rr, f);
            using (var e = new SKPaint { Color = primary ? accent : d.Fade(hover ? (byte)140 : (byte)70), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f })
                canvas.DrawRoundRect(rr, e);
            CardText.DrawCentred(canvas, btns[i], r.MidX, r.MidY + 4.2f, 12.5f, face, primary ? accent : hover ? text : dim);
        }
    }
}
