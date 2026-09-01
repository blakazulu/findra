using System;
using System.Collections.Concurrent;
using SkiaSharp;

namespace Findra;

// Text for the card: runs with per-glyph fallback, bidi reordering, measuring and ellipsis.
//
// Quicksand has no "·" and no Hebrew; a file name can contain anything. Drawing a string in one
// call silently drops every glyph the face lacks, so every string is split into runs by which face
// can draw it. Hebrew is reordered through BidiText first, because Skia has no bidi of its own.
public static class CardText
{
    private static readonly ConcurrentDictionary<int, SKTypeface?> Fallbacks = new();

    private static SKTypeface FaceFor(int codePoint, SKTypeface primary)
    {
        if (primary.ContainsGlyph(codePoint)) return primary;
        var fb = Fallbacks.GetOrAdd(codePoint, c =>
        {
            try { return SKFontManager.Default.MatchCharacter(c); }
            catch { return null; }
        });
        return fb ?? primary;
    }

    public static void Draw(SKCanvas canvas, string s, float x, float y, float size, SKTypeface face,
        SKColor color, bool bold = false)
    {
        if (string.IsNullOrEmpty(s)) return;
        s = BidiText.ToVisual(s);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        foreach (var (run, runFace) in Runs(s, face))
        {
            using var font = new SKFont(runFace, size) { Subpixel = true, Embolden = bold };
            canvas.DrawText(run, x, y, SKTextAlign.Left, font, paint);
            x += font.MeasureText(run);
        }
    }

    public static void DrawRight(SKCanvas canvas, string s, float right, float y, float size,
        SKTypeface face, SKColor color, bool bold = false)
        => Draw(canvas, s, right - Measure(s, face, size, bold), y, size, face, color, bold);

    public static void DrawCentred(SKCanvas canvas, string s, float cx, float y, float size,
        SKTypeface face, SKColor color, bool bold = false)
        => Draw(canvas, s, cx - Measure(s, face, size, bold) / 2, y, size, face, color, bold);

    public static float Measure(string s, SKTypeface face, float size, bool bold = false)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        float total = 0;
        foreach (var (run, runFace) in Runs(s, face))
        {
            using var font = new SKFont(runFace, size) { Subpixel = true, Embolden = bold };
            total += font.MeasureText(run);
        }
        return total;
    }

    /// <summary>Cut with an ellipsis to fit <paramref name="max"/> px. <paramref name="keepEnd"/>
    /// cuts the middle instead - for paths, where the folder and the file name are both the point.</summary>
    public static string Ellipsize(string s, SKTypeface face, float size, float max, bool keepEnd = false)
    {
        if (string.IsNullOrEmpty(s) || Measure(s, face, size) <= max) return s;
        if (keepEnd)
        {
            int keep = Math.Min(s.Length / 2, 24);
            for (int n = s.Length - keep - 1; n > 1; n--)
            {
                string cut = s[..n] + "…" + s[^keep..];
                if (Measure(cut, face, size) <= max) return cut;
            }
        }
        for (int n = s.Length - 1; n > 1; n--)
        {
            string cut = s[..n] + "…";
            if (Measure(cut, face, size) <= max) return cut;
        }
        return "…";
    }

    /// <summary>Wrap into at most <paramref name="maxLines"/> lines that fit <paramref name="width"/>;
    /// the last line is ellipsized if anything was left over.</summary>
    public static System.Collections.Generic.List<string> Wrap(string s, SKTypeface face, float size, float width, int maxLines)
    {
        var lines = new System.Collections.Generic.List<string>();
        if (string.IsNullOrEmpty(s)) return lines;
        var words = s.Replace("\r", "").Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cur = "";
        foreach (var w in words)
        {
            string trial = cur.Length == 0 ? w : cur + " " + w;
            if (Measure(trial, face, size) <= width) { cur = trial; continue; }
            if (cur.Length > 0) lines.Add(cur);
            cur = w;
            if (lines.Count == maxLines) break;
        }
        if (lines.Count < maxLines && cur.Length > 0) lines.Add(cur);
        if (lines.Count > maxLines) lines.RemoveRange(maxLines, lines.Count - maxLines);
        string joined = string.Join(" ", lines);
        if (lines.Count == maxLines && joined.Length < s.Trim().Length)
            lines[^1] = Ellipsize(lines[^1] + " " + s.Substring(Math.Min(s.Length, joined.Length)).Trim(), face, size, width);
        return lines;
    }

    private static System.Collections.Generic.IEnumerable<(string Run, SKTypeface Face)> Runs(string s, SKTypeface face)
    {
        int start = 0;
        SKTypeface runFace = FaceFor(char.ConvertToUtf32(s, 0), face);
        int i = char.IsSurrogatePair(s, 0) ? 2 : 1;
        while (i <= s.Length)
        {
            SKTypeface? next = null;
            int step = 1;
            if (i < s.Length)
            {
                int cp = char.ConvertToUtf32(s, i);
                step = char.IsSurrogatePair(s, i) ? 2 : 1;
                next = FaceFor(cp, face);
                if (next == runFace) { i += step; continue; }
            }
            yield return (s.Substring(start, i - start), runFace);
            start = i;
            runFace = next ?? face;
            i += step;
        }
    }
}
