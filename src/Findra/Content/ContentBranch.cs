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
    /// <summary>Every exact-word hit is worth the same band: bm25 has already put the best first,
    /// and a score invented on top of it would only pretend to a precision this branch does not
    /// have. The value sits just under a perfect name match so the two scales stay comparable
    /// if anything ever displays them side by side.</summary>
    public const float WordScore = 0.86f;

    /// <summary>
    /// How much each place down the bm25 ranking costs a row's score.
    ///
    /// <para>It is not a judgement about the file, it is the RANK, carried where the shared
    /// finish-and-order pass can see it. That pass sorts on Score and breaks a tie by path
    /// length, so leaving every content row on one flat constant would quietly redefine "best
    /// match" as "shortest path" and throw away the only ordering bm25 produced. The step is
    /// small enough that all of them stay inside one band - a hundred rows span 0.001 - so
    /// nothing reads as a confidence this branch has not got.</para>
    /// </summary>
    public const float RankStep = 1e-5f;

    /// <summary>What the card says when a Content query carries filters but no words at all.</summary>
    public const string NoWords =
        "Content search needs a word to look for - a filter narrows what the words found, " +
        "it cannot find files on its own.";

    /// <summary>
    /// The rows a Content query answers with. <paramref name="max"/> is how many the card can
    /// show; the FTS read asks for several times that, because one file can own many chunks and
    /// only the best of each survives the dedupe below, and the stat filters can still drop rows
    /// after that.
    ///
    /// <para><paramref name="sort"/> and <paramref name="stat"/> exist so this half of the pill
    /// goes through <see cref="ResultMapper.Finish"/> exactly as the name half does: same stat
    /// filters, same sort chips, same order. <paramref name="stat"/> is injected only so tests can
    /// describe a disk without having one.</para>
    /// </summary>
    public static SearchResults Search(ContentDb db, string raw, int max,
                                       SearchSort sort = SearchSort.Best,
                                       Func<string, bool, ResultMapper.Stat>? stat = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sw = Stopwatch.StartNew();
        var q = new SearchQuery(raw);
        string text = q.ContentText;

        // A query of filters alone - `ext:pdf`, or `-draft`, or a bare glob - has no ContentText.
        // Sending the raw string instead put `ext:pdf` itself through the tokenizer, which finds
        // any document containing the words "ext" and "pdf" and presents them as matches for a
        // query that never asked for a word. There is nothing to look for, so nothing is found,
        // and the card says which half of the query is missing.
        if (text.Length == 0)
            return new SearchResults(raw, Array.Empty<SearchResult>(), NamesMs: 0,
                                     ContentMs: sw.Elapsed.TotalMilliseconds,
                                     ContentReady: true, Note: NoWords);

        var byPath = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        int rank = 0;
        foreach (ContentDb.SegmentHit h in db.Fts(text, max * 4))
        {
            // The grammar is a filter on the FILE, not on its text: `lease ext:txt` still means
            // the txt one. Skipping this would make the pill quietly ignore half the query
            // language the card advertises.
            if (!q.Allows(Path.GetFileName(h.Path), h.Path, h.Kind)) continue;
            // One row per file, and it is the file's BEST chunk. A two hundred page contract that
            // says "lease" on every page is one result; the FTS read is in bm25 order, so the
            // first hit for a path outranks every later one, and the excerpt the row carries is
            // the one bm25 chose rather than whichever chunk happened to come last.
            if (byPath.ContainsKey(h.Path)) continue;
            byPath[h.Path] = ToResult(h, WordScore - rank * RankStep, text);
            rank++;
        }

        // The same finish-and-order pass the name half runs: it is what gives these rows a size
        // and a date at all, and therefore what makes `size:`, `modified:` and the sort chips mean
        // the same thing on both sides of the pill. The store holds text, not directory entries,
        // so without this those controls would sit on the card doing nothing.
        List<SearchResult> rows = ResultMapper.Finish([.. byPath.Values], q, sort, stat);
        if (rows.Count > max) rows.RemoveRange(max, rows.Count - max);

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
