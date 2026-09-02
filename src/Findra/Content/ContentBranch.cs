using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Findra;

/// <summary>
/// The words-in-documents answer. It reads the local index directly - no pipe: this database is
/// the interface process's own file at normal integrity, and the elevated helper, which holds
/// names in RAM and opens volume handles, has never seen it and has no reason to.
///
/// <para>Content results are deliberately NOT merged with name results. They answer two different
/// questions, and blending them lets a file merely NAMED "lease" outrank the lease itself, found
/// by its words - which is precisely what the Content pill exists to ask for. The toggle switches
/// which question is asked; it never averages the two answers.</para>
/// </summary>
public static class ContentBranch
{
    /// <summary>Every exact-word hit is worth the same: bm25 has already put the best first, and
    /// a score invented on top of it would only pretend to a precision this branch does not
    /// have. The value sits just under a perfect name match so the two scales stay comparable
    /// if anything ever displays them side by side.</summary>
    public const float WordScore = 0.86f;

    /// <summary>
    /// The rows a Content query answers with. <paramref name="max"/> is how many the card can
    /// show; the FTS read asks for several times that, because one file can own many chunks and
    /// only the first of each survives the dedupe below.
    /// </summary>
    public static SearchResults Search(ContentDb db, string raw, int max)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sw = Stopwatch.StartNew();
        var q = new SearchQuery(raw);
        // A query of filters alone (`ext:pdf` with no words) has no ContentText, and matching the
        // raw string would send `ext:pdf` itself to the tokenizer. Fall back to the raw text only
        // when the parse found nothing to say.
        string text = q.ContentText.Length > 0 ? q.ContentText : raw;

        var byPath = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (ContentDb.SegmentHit h in db.Fts(text, max * 4))
        {
            // The grammar is a filter on the FILE, not on its text: `lease ext:txt` still means
            // the txt one. Skipping this would make the pill quietly ignore half the query
            // language the card advertises.
            if (!q.Allows(Path.GetFileName(h.Path), h.Path, h.Kind)) continue;
            // One row per file. A two hundred page contract that says "lease" on every page is
            // one result, and the FTS read is in bm25 order, so the first hit for a path is its
            // best one.
            if (byPath.ContainsKey(h.Path)) continue;
            byPath[h.Path] = ToResult(h, WordScore, text);
        }

        List<SearchResult> rows = byPath.Values.OrderByDescending(r => r.Score).Take(max).ToList();
        // "No matches" and "nothing has been read yet" are different facts, and only one of them
        // is about the query. An empty index must never read as an answer.
        string note = rows.Count == 0 && db.IndexedCount() == 0
            ? "Nothing indexed yet - Findra is still reading what is inside your files."
            : "";
        // NamesMs: 0 is not a placeholder. This path never asked the name index anything.
        return new SearchResults(raw, rows, NamesMs: 0, ContentMs: sw.Elapsed.TotalMilliseconds,
                                 ContentReady: true, Note: note);
    }

    /// <summary>One segment hit as a card row. The kind is the ITEM's kind, already joined in by
    /// the query; the segment's own kind only decides how the row explains itself.</summary>
    public static SearchResult ToResult(ContentDb.SegmentHit hit, float score, string query)
    {
        bool speech = hit.SegKind == ContentDb.SegSpeech;
        return new SearchResult(hit.Kind, Path.GetFileName(hit.Path), hit.Path, score,
            speech ? $"said at {Clock(hit.T0)}" : "contains the words",
            MomentSeconds: speech ? hit.T0 : -1,
            Excerpt: Excerpt(hit.Text, query));
    }

    /// <summary>m:ss, or h:mm:ss once there is an hour to show.</summary>
    public static string Clock(double t)
        => t < 0 ? "" : TimeSpan.FromSeconds(t).ToString(t >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    /// <summary>
    /// The sentence around the first query word, or the start of the chunk. This is what the
    /// person reads to decide whether this is the file, so it is centred on what they typed
    /// rather than cut from the top - and a word that is not in this chunk (FTS matched on a
    /// prefix, or on a different chunk of the same file) still shows the chunk's opening rather
    /// than an empty cell, which would read as a file with nothing in it.
    /// </summary>
    public static string Excerpt(string text, string query)
    {
        if (text.Length == 0) return "";
        int at = -1;
        foreach (string w in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            at = text.IndexOf(w, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) break;
        }
        int start = at < 0 ? 0 : Math.Max(0, at - 60);
        // Back up to a word boundary when one is close, so the excerpt does not open mid-word.
        if (start > 0) { int sp = text.LastIndexOf(' ', start); if (sp > 0 && start - sp < 20) start = sp + 1; }
        int len = Math.Min(220, text.Length - start);
        string ex = text.Substring(start, len).Replace('\n', ' ').Trim();
        // The ellipses are the truth about what was cut, so each is conditional on a real cut:
        // a chunk shorter than the window is shown whole, with neither.
        return (start > 0 ? "…" : "") + ex + (start + len < text.Length ? "…" : "");
    }
}
