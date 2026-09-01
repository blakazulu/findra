using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Findra;

/// <summary>
/// Reorders a logical string into the order it must be DRAWN in, left to right.
///
/// Skia has no bidi: every <c>DrawText</c> in Findra lays code points out left to right, so
/// "שיר אהבה" arrives on screen as "הבהא ריש" - every surface, not just the card. This is the
/// single place that is fixed.
///
/// Reordering is the whole fix *for Hebrew* because Hebrew is not cursive: its letters do not join
/// or change shape by position, so the glyphs were always right and only their order was wrong.
/// Arabic, Thai and the Indic scripts additionally need real shaping (HarfBuzz) and are NOT
/// correct here - they will be reordered but not shaped. Do not read this class as making Findra
/// script-complete; it makes it correct for the scripts whose glyphs stand alone.
///
/// Deliberately a pure function over strings rather than a change to the draw calls: every caller
/// then measures and draws the same string, so widths, alignment, tracking, ellipsis and hit
/// testing all stay consistent for free.
/// </summary>
public static class BidiText
{
    private enum Dir { L, R, N }

    /// <summary>
    /// The visual-order string. Text with no right-to-left characters is returned **unchanged and
    /// by reference** - which is what makes this safe to call from every text path in Findra: for
    /// the Latin content that is 99% of what Findra draws, nothing whatsoever changes.
    /// </summary>
    public static string ToVisual(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!HasRtl(text)) return text;

        var sb = new StringBuilder(text.Length);
        foreach (var c in Layout(text)) sb.Append(text, c.Start, c.Length);
        return sb.ToString();
    }

    /// <summary>One grapheme cluster of the logical text, as it lands in the visual order.</summary>
    public readonly record struct Cluster(int Start, int Length, bool Rtl);

    /// <summary>
    /// The clusters of <paramref name="text"/> in the order they are DRAWN, left to right, each
    /// with its logical position and direction. <see cref="ToVisual"/> is this joined back into a
    /// string; a caret needs the mapping itself, because "after the third letter" is a different
    /// x depending on which way that letter's run reads.
    /// </summary>
    public static List<Cluster> Layout(string text)
    {
        var result = new List<Cluster>();
        if (string.IsNullOrEmpty(text)) return result;
        var clusters = Clusters(text);
        var starts = new int[clusters.Count];
        for (int i = 0, pos = 0; i < clusters.Count; i++) { starts[i] = pos; pos += clusters[i].Length; }
        if (!HasRtl(text))
        {
            for (int i = 0; i < clusters.Count; i++) result.Add(new Cluster(starts[i], clusters[i].Length, false));
            return result;
        }
        foreach (var (start, end, rtl) in Runs(clusters))
        {
            if (rtl) for (int i = end - 1; i >= start; i--) result.Add(new Cluster(starts[i], clusters[i].Length, true));
            else for (int i = start; i < end; i++) result.Add(new Cluster(starts[i], clusters[i].Length, false));
        }
        return result;
    }

    // The runs in visual order (UAX #9 in the small: base direction from the first strong
    // character, neutrals joining same-direction neighbours, runs reversed for an RTL base).
    private static List<(int Start, int End, bool Rtl)> Runs(List<string> clusters)
    {
        int n = clusters.Count;
        var dirs = new Dir[n];
        for (int i = 0; i < n; i++) dirs[i] = Classify(clusters[i]);

        // Base direction is the first STRONG character's (UAX #9 P2/P3). A title that opens with a
        // Latin word is a left-to-right title with Hebrew in it, and vice versa.
        bool baseRtl = false;
        for (int i = 0; i < n; i++)
        {
            if (dirs[i] == Dir.R) { baseRtl = true; break; }
            if (dirs[i] == Dir.L) break;
        }
        Dir baseDir = baseRtl ? Dir.R : Dir.L;

        // Neutrals (spaces, punctuation) between two runs of the same direction join them;
        // otherwise they fall back to the base direction. Without this the space in "שיר אהבה"
        // splits the phrase into two runs and lands between them in the wrong place.
        for (int i = 0; i < n; i++)
        {
            if (dirs[i] != Dir.N) continue;
            int j = i;
            while (j < n && dirs[j] == Dir.N) j++;
            Dir before = i > 0 ? dirs[i - 1] : baseDir;
            Dir after = j < n ? dirs[j] : baseDir;
            Dir fill = before == after ? before : baseDir;
            for (int k = i; k < j; k++) dirs[k] = fill;
            i = j - 1;
        }

        var runs = new List<(int Start, int End, bool Rtl)>();
        for (int i = 0; i < n;)
        {
            bool rtl = dirs[i] == Dir.R;
            int j = i;
            while (j < n && (dirs[j] == Dir.R) == rtl) j++;
            runs.Add((i, j, rtl));
            i = j;
        }
        if (baseRtl) runs.Reverse();
        return runs;
    }

    public static bool HasRtl(string s)
    {
        foreach (char c in s) if (IsRtlChar(c)) return true;
        return false;
    }

    // Hebrew, Arabic, Syriac, Thaana and the Arabic presentation forms.
    private static bool IsRtlChar(char c) =>
        (c >= '\u0590' && c <= '\u05FF') ||     // Hebrew
        (c >= '\u0600' && c <= '\u06FF') ||     // Arabic
        (c >= '\u0700' && c <= '\u074F') ||     // Syriac
        (c >= '\u0780' && c <= '\u07BF') ||     // Thaana
        (c >= '\uFB1D' && c <= '\uFDFF') ||     // Hebrew/Arabic presentation forms A
        (c >= '\uFE70' && c <= '\uFEFF');       // Arabic presentation forms B

    private static Dir Classify(string cluster)
    {
        char c = cluster[0];
        if (IsRtlChar(c)) return Dir.R;
        // European digits read LEFT TO RIGHT even inside Hebrew, so "2024" must not be reversed
        // along with the words around it.
        if (char.IsDigit(c)) return Dir.L;
        if (char.IsLetter(c)) return Dir.L;
        return Dir.N;
    }

    // Grapheme clusters, not chars: reversing raw chars would tear a Hebrew letter away from its
    // niqqud and leave the vowel point sitting on the wrong letter.
    private static List<string> Clusters(string text)
    {
        var list = new List<string>(text.Length);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }
}