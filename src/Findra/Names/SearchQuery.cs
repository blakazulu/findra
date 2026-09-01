using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Findra;

// What the person typed, taken apart: the words that match names and contents, the wildcards, the
// filters, the exclusions. A pure parse - the same string always means the same query - so the
// self-test can pin the grammar and the card can explain it.
//
//   sunset                 contains "sunset" in the name (and, once indexed, in the contents)
//   *.jpg  IMG_2024*  ?.md  a glob over the name: * anything, ? one character
//   "quarterly revenue"    the words together, in a name or in a document
//   -draft  !draft  -in:node_modules   must not contain / must not be under (! = -, Everything's)
//   report | invoice       OR: each side is its own full query, results unioned (top-level only -
//                          "a b | c" is (a AND b) OR c; distribute by hand for anything deeper)
//   ext:jpg,png  type:photo|video|doc|audio|folder|file
//   in:Downloads  in:C:\Code    under a folder (a name anywhere in the path, or a full prefix)
//   size:>10mb  size:1mb..100mb  size:huge   by size (b/kb/mb/gb, a..b range, or Everything's
//                          constants: tiny/small/medium/large/huge/gigantic)
//   modified:today|week|month|year|2025|2025-08|2026-01-01..2026-03-15   by last-write date
//   created:…  dc:…  accessed:…  da:…        same forms, by creation / last-access date
//   case:Word              the word must match with this exact casing
//   ww:word                whole word: not part of a longer run of letters
//   regex:pat.*ern         a .NET regex over the name (quote it to keep spaces); slower - it
//                          walks every name instead of scanning
public sealed class SearchQuery
{
    public string Raw { get; }
    /// <summary>Plain words (case-folded) every name must contain.</summary>
    public List<string> Words { get; } = new();
    /// <summary>Quoted phrases; a name must contain each, contents must contain the words together.</summary>
    public List<string> Phrases { get; } = new();
    /// <summary>Glob patterns (case-folded) the name must match in full.</summary>
    public List<string> Globs { get; } = new();
    /// <summary>Words a name must NOT contain.</summary>
    public List<string> NotWords { get; } = new();
    public HashSet<string> Exts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<ResultKind> Kinds { get; } = new();
    public List<string> Under { get; } = new();
    public List<string> NotUnder { get; } = new();
    public long MinBytes { get; private set; } = -1;
    public long MaxBytes { get; private set; } = -1;
    public DateTime? ModifiedAfter { get; private set; }
    public DateTime? ModifiedBefore { get; private set; }
    public DateTime? CreatedAfter { get; private set; }
    public DateTime? CreatedBefore { get; private set; }
    public DateTime? AccessedAfter { get; private set; }
    public DateTime? AccessedBefore { get; private set; }
    /// <summary>Words that must appear with this exact casing (checked against the original name).</summary>
    public List<string> CaseWords { get; } = new();
    /// <summary>Words that must be whole: not part of a longer run of letters or digits.</summary>
    public List<string> WholeWords { get; } = new();
    /// <summary>A `regex:` pattern, compiled; null when none or invalid (invalid logs once).</summary>
    public System.Text.RegularExpressions.Regex? Rx { get; private set; }
    public string PathNeedle { get; private set; } = "";

    public bool HasFilters => Exts.Count > 0 || Kinds.Count > 0 || Under.Count > 0 || NotUnder.Count > 0
                              || MinBytes >= 0 || MaxBytes >= 0 || ModifiedAfter is not null || ModifiedBefore is not null
                              || CreatedAfter is not null || CreatedBefore is not null
                              || AccessedAfter is not null || AccessedBefore is not null || NotWords.Count > 0;
    public bool NeedsStat => MinBytes >= 0 || MaxBytes >= 0 || ModifiedAfter is not null || ModifiedBefore is not null
                             || CreatedAfter is not null || CreatedBefore is not null
                             || AccessedAfter is not null || AccessedBefore is not null;
    /// <summary>Anything left to match a NAME against? A query of filters alone lists everything they allow.</summary>
    public bool HasNameTerms => Words.Count > 0 || Phrases.Count > 0 || Globs.Count > 0 || Rx is not null;
    /// <summary>Only filters, no words at all - not even a glob.</summary>
    public bool FiltersOnly => !HasNameTerms && HasFilters;

    /// <summary>The words and phrases for the content encoders and FTS, without the filters.</summary>
    public string ContentText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var w in Words) sb.Append(w).Append(' ');
            foreach (var p in Phrases) sb.Append(p).Append(' ');
            return sb.ToString().Trim();
        }
    }

    public SearchQuery(string raw)
    {
        Raw = raw;
        foreach (var tok in Tokenize(raw))
        {
            string t = tok;
            if (t == "|") continue;   // OR is split ABOVE this parse (OrParts); a stray pipe is noise
            bool phrase = t.Length >= 2 && t[0] == '"' && t[^1] == '"';
            if (phrase) { string ph = t[1..^1].Trim(); if (ph.Length > 0) Phrases.Add(ph.ToLowerInvariant()); continue; }

            bool neg = (t.StartsWith('-') || t.StartsWith('!')) && t.Length > 1;
            if (neg) t = t[1..];

            int colon = t.IndexOf(':');
            if (colon > 0 && colon < t.Length - 1 && !(colon == 1 && t.Length > 2 && t[2] == '\\'))   // not a drive letter
            {
                string key = t[..colon].ToLowerInvariant(), val = t[(colon + 1)..];
                if (Filter(key, val, neg)) continue;
            }

            string folded = t.ToLowerInvariant();
            if (neg) { NotWords.Add(folded); continue; }
            if (folded.Contains('\\') || folded.Contains('/'))
            {
                // a path query: the last segment matches the name, the whole thing the path
                PathNeedle = folded.Replace('/', '\\');
                int slash = PathNeedle.LastIndexOf('\\');
                string last = PathNeedle[(slash + 1)..];
                if (last.Length == 0) continue;
                if (IsGlob(last)) Globs.Add(last); else Words.Add(last);
                continue;
            }
            if (IsGlob(folded)) Globs.Add(folded); else Words.Add(folded);
        }
    }

    private bool Filter(string key, string val, bool neg)
    {
        switch (key)
        {
            case "ext":
                foreach (var e in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    Exts.Add(e.TrimStart('.'));
                return true;
            case "type":
            case "kind":
                foreach (var k in val.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    switch (k.ToLowerInvariant())
                    {
                        case "photo": case "photos": case "image": case "images": case "picture": case "pictures": Kinds.Add(ResultKind.Photo); break;
                        case "video": case "videos": Kinds.Add(ResultKind.Video); break;
                        case "doc": case "docs": case "document": case "documents": Kinds.Add(ResultKind.Document); break;
                        case "audio": case "music": case "sound": Kinds.Add(ResultKind.Audio); break;
                        case "folder": case "folders": case "dir": case "directory": Kinds.Add(ResultKind.Folder); break;
                        case "file": case "files": Kinds.Add(ResultKind.File); break;
                    }
                return true;
            case "in":
            case "path":
            case "folder":
                (neg ? NotUnder : Under).Add(val.Replace('/', '\\').Trim('"'));
                return true;
            case "size":
                ParseSize(val);
                return true;
            case "modified":
            case "date":
            case "changed":
            case "dm":
            {
                DateTime? a = ModifiedAfter, b = ModifiedBefore;
                ParseDate(val, ref a, ref b);
                ModifiedAfter = a; ModifiedBefore = b;
                return true;
            }
            case "created":
            case "dc":
            {
                DateTime? a = CreatedAfter, b = CreatedBefore;
                ParseDate(val, ref a, ref b);
                CreatedAfter = a; CreatedBefore = b;
                return true;
            }
            case "accessed":
            case "da":
            {
                DateTime? a = AccessedAfter, b = AccessedBefore;
                ParseDate(val, ref a, ref b);
                AccessedAfter = a; AccessedBefore = b;
                return true;
            }
            case "case":
            {
                string w = val.Trim('"');
                if (w.Length == 0) return true;
                CaseWords.Add(w);
                Words.Add(w.ToLowerInvariant());   // the fast folded scan still finds candidates
                return true;
            }
            case "ww":
            case "wholeword":
            {
                string w = val.Trim('"').ToLowerInvariant();
                if (w.Length == 0) return true;
                WholeWords.Add(w);
                Words.Add(w);
                return true;
            }
            case "regex":
            case "re":
            {
                string pat = val.Trim('"');
                if (pat.Length == 0) return true;
                try
                {
                    Rx = new System.Text.RegularExpressions.Regex(pat,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(50));   // a runaway pattern must not hang a keystroke
                }
                catch (Exception ex) { Log.Once("search|regex", "WARN", "search", $"bad regex '{pat}' :: {ex.Message}"); }
                return true;
            }
            default:
                return false;
        }
    }

    private void ParseSize(string v)
    {
        // Everything's named buckets, then ranges (`1mb..100mb`), then the single-ended forms
        switch (v.ToLowerInvariant())
        {
            case "tiny": MinBytes = 0; MaxBytes = 10L << 10; return;
            case "small": MinBytes = 10L << 10; MaxBytes = 100L << 10; return;
            case "medium": MinBytes = 100L << 10; MaxBytes = 1L << 20; return;
            case "large": MinBytes = 1L << 20; MaxBytes = 16L << 20; return;
            case "huge": MinBytes = 16L << 20; MaxBytes = 128L << 20; return;
            case "gigantic": MinBytes = 128L << 20; return;
            case "empty": case "zero": MinBytes = 0; MaxBytes = 0; return;
        }
        int dots = v.IndexOf("..", StringComparison.Ordinal);
        if (dots > 0)
        {
            long lo = ParseBytes(v[..dots]), hi = ParseBytes(v[(dots + 2)..]);
            if (lo >= 0) MinBytes = lo;
            if (hi >= 0) MaxBytes = hi;
            return;
        }
        char op = v.Length > 0 && (v[0] == '>' || v[0] == '<') ? v[0] : '>';
        long bytes = ParseBytes(v.TrimStart('>', '<', '='));
        if (bytes < 0) return;
        if (op == '<') MaxBytes = bytes; else MinBytes = bytes;
    }

    private static long ParseBytes(string num)
    {
        num = num.Trim().ToLowerInvariant();
        long mult = 1;
        if (num.EndsWith("gb")) { mult = 1L << 30; num = num[..^2]; }
        else if (num.EndsWith("mb")) { mult = 1L << 20; num = num[..^2]; }
        else if (num.EndsWith("kb")) { mult = 1L << 10; num = num[..^2]; }
        else if (num.EndsWith("b")) num = num[..^1];
        else if (num.EndsWith("g")) { mult = 1L << 30; num = num[..^1]; }
        else if (num.EndsWith("m")) { mult = 1L << 20; num = num[..^1]; }
        else if (num.EndsWith("k")) { mult = 1L << 10; num = num[..^1]; }
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return -1;
        return (long)(n * mult);
    }

    // One date grammar for modified/created/accessed. A range (`a..b`) takes each side's own
    // span: `2026-01..2026-03` runs from 1 Jan to the end of March.
    private static void ParseDate(string v, ref DateTime? after, ref DateTime? before)
    {
        v = v.ToLowerInvariant();
        int dots = v.IndexOf("..", StringComparison.Ordinal);
        if (dots > 0)
        {
            DateTime? la = null, lb = null, ra = null, rb = null;
            ParseDate(v[..dots], ref la, ref lb);
            ParseDate(v[(dots + 2)..], ref ra, ref rb);
            if (la is not null) after = la;
            if (rb is not null) before = rb; else if (ra is not null) before = ra;
            return;
        }
        var now = DateTime.Now;
        switch (v)
        {
            case "today": after = now.Date; return;
            case "yesterday": after = now.Date.AddDays(-1); before = now.Date; return;
            case "week": case "thisweek": case "7d": after = now.AddDays(-7); return;
            case "month": case "30d": after = now.AddDays(-30); return;
            case "year": case "365d": after = now.AddYears(-1); return;
        }
        if (v.EndsWith('d') && int.TryParse(v[..^1], out int days)) { after = now.AddDays(-days); return; }
        if (v.Length == 4 && int.TryParse(v, out int y)) { after = new DateTime(y, 1, 1); before = new DateTime(y + 1, 1, 1); return; }
        if (v.Length == 7 && v[4] == '-' && int.TryParse(v[..4], out int y2) && int.TryParse(v[5..], out int m) && m is >= 1 and <= 12)
        { after = new DateTime(y2, m, 1); before = new DateTime(y2, m, 1).AddMonths(1); return; }
        if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { after = d.Date; before = d.Date.AddDays(1); }
    }

    /// <summary>The top-level OR alternatives of a raw query: `report | invoice 2024` is two full
    /// queries, results unioned. A pipe splits as a bare token or inside a plain word
    /// (`sunset|beach`); pipes inside quotes and filter values (`type:photo|video`) stay.</summary>
    public static List<string> OrParts(string raw)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        void Flush() { string s = cur.ToString().Trim(); if (s.Length > 0) parts.Add(s); cur.Clear(); }
        foreach (var tok in Tokenize(raw))
        {
            if (tok == "|") { Flush(); continue; }
            if (!tok.Contains(':') && tok[0] != '"' && tok.Contains('|'))
            {
                var bits = tok.Split('|');
                for (int i = 0; i < bits.Length; i++)
                {
                    if (bits[i].Length > 0) { if (cur.Length > 0) cur.Append(' '); cur.Append(bits[i]); }
                    if (i < bits.Length - 1) Flush();
                }
                continue;
            }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(tok);
        }
        Flush();
        if (parts.Count == 0) parts.Add(raw.Trim());
        return parts;
    }

    /// <summary>Does this query carry anything beyond plain words and globs - filters, OR, regex,
    /// case or whole-word terms? The card's Advanced pill lights and badges on this, however the
    /// grammar got there (the popup or typed by hand).</summary>
    public static bool IsAdvanced(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return false;
        if (OrParts(raw).Count > 1) return true;
        var q = new SearchQuery(raw);
        return q.HasFilters || q.Rx is not null || q.CaseWords.Count > 0 || q.WholeWords.Count > 0;
    }

    /// <summary>Split on spaces, keeping quoted runs together (the quotes stay on).</summary>
    public static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool inQ = false;
        foreach (char c in s)
        {
            if (c == '"') { inQ = !inQ; cur.Append(c); continue; }
            if (c == ' ' && !inQ) { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }

    public static bool IsGlob(string s) => s.Contains('*') || s.Contains('?');

    // ---- glob matching, on the case-folded name ----

    /// <summary>Does <paramref name="name"/> (folded) match the whole pattern? `*` any run,
    /// `?` one character. Iterative with backtracking on the last `*`, so no recursion.</summary>
    public static bool GlobMatch(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern)
    {
        int n = 0, p = 0, starP = -1, starN = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n])) { n++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { starP = p++; starN = n; }
            else if (starP >= 0) { p = starP + 1; n = ++starN; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>The longest literal run in a glob - what the vectorised scan looks for before
    /// the full match is checked. Empty when the pattern is all wildcards (`*.*` has ".").</summary>
    public static string LiteralOf(string glob)
    {
        string best = "";
        foreach (var part in glob.Split(new[] { '*', '?' }, StringSplitOptions.RemoveEmptyEntries))
            if (part.Length > best.Length) best = part;
        return best;
    }

    // ---- typo tolerance ----

    /// <summary>Does <paramref name="name"/> contain something within one edit (a wrong, missing,
    /// extra or swapped letter) of <paramref name="word"/>? Windows of the word's length ±1.</summary>
    public static bool FuzzyContains(ReadOnlySpan<char> name, ReadOnlySpan<char> word)
    {
        int w = word.Length;
        if (w < 4) return false;
        for (int len = w - 1; len <= w + 1; len++)
        {
            if (len <= 0 || len > name.Length) continue;
            for (int i = 0; i + len <= name.Length; i++)
                if (WithinOneEdit(name.Slice(i, len), word)) return true;
        }
        return false;
    }

    private static bool WithinOneEdit(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length == b.Length)
        {
            int first = -1, count = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) { if (++count > 2) return false; if (first < 0) first = i; }
            if (count <= 1) return true;
            // exactly two differences, adjacent and crossed: a swap
            return first + 1 < a.Length && a[first + 1] != b[first + 1] && a[first] == b[first + 1] && a[first + 1] == b[first];
        }
        ReadOnlySpan<char> s = a.Length < b.Length ? a : b;
        ReadOnlySpan<char> l = a.Length < b.Length ? b : a;
        if (l.Length - s.Length != 1) return false;
        int si = 0, li = 0; bool skipped = false;
        while (si < s.Length && li < l.Length)
        {
            if (s[si] == l[li]) { si++; li++; continue; }
            if (skipped) return false;
            skipped = true; li++;
        }
        return true;
    }

    // ---- the filters, against a path ----

    public bool Allows(string name, string path, ResultKind kind)
    {
        if (Kinds.Count > 0 && !Kinds.Contains(kind)) return false;
        if (Exts.Count > 0)
        {
            string ext = Path.GetExtension(name).TrimStart('.');
            bool any = false;
            foreach (var e in Exts) if (e.Equals(ext, StringComparison.OrdinalIgnoreCase) || (e == "*" )) { any = true; break; }
            if (!any) return false;
        }
        string folded = name.ToLowerInvariant();
        foreach (var nw in NotWords) if (folded.Contains(nw)) return false;
        foreach (var cw in CaseWords) if (!name.Contains(cw, StringComparison.Ordinal)) return false;
        foreach (var ww in WholeWords) if (!ContainsWhole(folded, ww)) return false;
        if (PathNeedle.Length > 0 && !path.Contains(PathNeedle, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var u in Under) if (!IsUnder(path, u)) return false;
        foreach (var u in NotUnder) if (IsUnder(path, u)) return false;
        return true;
    }

    public bool AllowsStat(long size, DateTime modified, DateTime created = default, DateTime accessed = default)
    {
        if (MinBytes >= 0 && size < MinBytes) return false;
        if (MaxBytes >= 0 && size > MaxBytes) return false;
        if (ModifiedAfter is DateTime a && modified < a) return false;
        if (ModifiedBefore is DateTime b && modified >= b) return false;
        if (CreatedAfter is DateTime ca && created < ca) return false;
        if (CreatedBefore is DateTime cb && created >= cb) return false;
        if (AccessedAfter is DateTime aa && accessed < aa) return false;
        if (AccessedBefore is DateTime ab && accessed >= ab) return false;
        return true;
    }

    /// <summary>Is <paramref name="word"/> in <paramref name="folded"/> as a WHOLE word - not
    /// part of a longer run of letters or digits on either side?</summary>
    public static bool ContainsWhole(string folded, string word)
    {
        int at = 0;
        while ((at = folded.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = at == 0 || !char.IsLetterOrDigit(folded[at - 1]);
            int end = at + word.Length;
            bool rightOk = end >= folded.Length || !char.IsLetterOrDigit(folded[end]);
            if (leftOk && rightOk) return true;
            at++;
        }
        return false;
    }

    // `in:C:\Code` is a prefix; `in:Downloads` is a folder name anywhere on the path
    private static bool IsUnder(string path, string under)
    {
        if (under.Length >= 2 && under[1] == ':') return path.StartsWith(under.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string dir = Path.GetDirectoryName(path) ?? "";
        return ("\\" + dir + "\\").Contains("\\" + under.Trim('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
