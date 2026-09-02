using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Findra;

/// <summary>
/// The query side of the model-backed capabilities: a vector store, and the two ways of turning
/// what somebody typed into a vector. Either encoder may be null, and null means "that capability
/// is not installed" - which contributes no candidates and is not an error (spec §6).
///
/// <para>Delegates rather than encoder objects, so the branch's rules can be tested against a
/// vector store filled by hand with no model on disk. Absence being a null the branch already
/// handles is what makes "an absent capability is silent" a property of the code rather than a
/// thing to remember.</para>
/// </summary>
public sealed class Semantic(VectorStore vectors, Func<string, float[]>? text, Func<string, float[]>? image,
                            params IDisposable[] owned) : IDisposable
{
    public VectorStore Vectors { get; } = vectors;
    public Func<string, float[]>? Text { get; } = text;
    public Func<string, float[]>? Image { get; } = image;

    /// <summary>Disposes only what it was HANDED to own. A store the caller opened and a store
    /// <see cref="Open"/> opened are two different lifetimes, and a type that guesses gets one of
    /// them wrong - which is how a test's store gets closed under it, or how two encoders holding
    /// a GPU device are leaked for the life of the process.</summary>
    public void Dispose() { foreach (IDisposable d in owned) d.Dispose(); }

    /// <summary>What this machine can ask, given what is installed. Null when nothing is - the
    /// card then calls the branch with no semantic half at all, which is the ordinary state of a
    /// machine that took the "Just names" preset.</summary>
    public static Semantic? Open(CapabilitySet installed, string? modelDir = null)
    {
        if (!installed.Has(Capability.Photos) && !installed.Has(Capability.Meaning)) return null;
        var store = new VectorStore();
        var own = new List<IDisposable> { store };
        Func<string, float[]>? asText = null, asImage = null;
        try
        {
            if (installed.Has(Capability.Meaning))
            {
                var e5 = new E5Encoder(wantAccelerator: false, modelDir);
                own.Add(e5);
                asText = e5.EncodeQuery;
            }
            if (installed.Has(Capability.Photos))
            {
                var clip = new ClipTextEncoder(wantAccelerator: false, modelDir);
                own.Add(clip);
                asImage = clip.Encode;
            }
        }
        catch (Exception ex)
        {
            // A model that is on disk and will not load is the one case where an absent
            // capability IS worth a log line - it is not the normal state, it is a broken file.
            // It is still not an error the user has to acknowledge: whatever loaded stays.
            Log.Error("models", "a query encoder would not load - that capability is off for this session", ex);
        }
        if (asText is null && asImage is null)
        {
            foreach (IDisposable d in own) d.Dispose();
            return null;
        }
        return new Semantic(store, asText, asImage, [.. own]);
    }
}

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

    /// <summary>Where a picture stops being unrelated. SigLIP-2 is a sigmoid model and its
    /// cosines sit LOW - unrelated is near 0 and "obviously this" is 0.10 to 0.12, measured on a
    /// real library. Do not compare these numbers to another model's.</summary>
    public const float PhotoFloor = 0.05f, PhotoSpan = 0.15f, PhotoCeiling = 0.92f;

    /// <summary>e5 puts unrelated text near 0.75 and a paraphrase near 0.9, so the interesting
    /// range is narrow and high. A floor of 0 would make every document a weak match for
    /// everything.</summary>
    public const float TextFloor = 0.78f, TextSpan = 0.12f, TextCeiling = 0.9f;

    /// <summary>What a file found by BOTH its words and its meaning gains. Exact words are what
    /// the person typed; without this a paraphrase can outrank the actual phrase.</summary>
    public const float BothBonus = 0.25f;

    public static float PhotoScore(float cosine)
        => Math.Clamp((cosine - PhotoFloor) / PhotoSpan, 0f, 1f) * PhotoCeiling;

    public static float TextScore(float cosine)
        => Math.Clamp((cosine - TextFloor) / TextSpan, 0f, 1f) * TextCeiling;

    /// <summary>What the picture encoder's vector is compared against, and what the text
    /// encoder's is. A photo and a video frame are one question; a document chunk and a line of
    /// transcript are the other, because a transcript is embedded and searched as a document.
    /// Crossing them scores an image row against a sentence vector, which is noise.</summary>
    private static readonly byte[] PictureKinds = [(byte)ContentDb.SegImage, (byte)ContentDb.SegFrame];
    private static readonly byte[] WordKinds = [(byte)ContentDb.SegText, (byte)ContentDb.SegSpeech];

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
    ///
    /// <para><paramref name="semantic"/> is the model-backed half, and null is an ordinary state:
    /// a machine that took no model still searches the words in its documents through this exact
    /// call. <paramref name="installed"/> is only ever read to decide what the card may OFFER when
    /// nothing was found - a caller that leaves it at its default is saying it does not know, and
    /// is offered nothing.</para>
    /// </summary>
    public static SearchResults Search(ContentDb db, string raw, int max,
                                       SearchSort sort = SearchSort.Best,
                                       Func<string, bool, ResultMapper.Stat>? stat = null,
                                       Semantic? semantic = null,
                                       CapabilitySet installed = default)
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
        // The paths the vector branch offered, and it is a set rather than a flag on the row
        // because the full-text pass below has to tell "this file was found both ways" from "this
        // file simply has a second matching chunk". The second must not earn a bonus.
        var fromVector = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Offer(SearchResult row)
        {
            // Best offer wins rather than the first: one file can arrive from both vector passes
            // and from several rows within one, and the match a person would name is the higher
            // score, not whichever the store happened to return first.
            if (!byPath.TryGetValue(row.Path, out SearchResult? had) || had.Score < row.Score)
                byPath[row.Path] = row;
            fromVector.Add(row.Path);
        }

        // The vector branch runs first so the full-text pass can see what it found and raise it.
        // Each half is skipped in silence when its encoder is absent: a missing model is a normal
        // state, so it contributes no candidates and is never an error (spec §6).
        if (semantic is not null)
        {
            semantic.Vectors.Reload();
            VectorPass(db, q, text, max, semantic.Vectors, semantic.Image, PictureKinds, PhotoFloor, PhotoScore, Offer);
            VectorPass(db, q, text, max, semantic.Vectors, semantic.Text, WordKinds, TextFloor, TextScore, Offer);
        }

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
            if (byPath.ContainsKey(h.Path))
            {
                // Found both ways: RAISE the offer the vector branch made rather than replacing
                // it with a fresh word row. The vector row for a transcript carries the moment
                // the card seeks to and its own explanation, and replacing it throws both away.
                // The set entry is removed as it is spent, so a file's second matching chunk
                // cannot bank the bonus twice.
                if (fromVector.Remove(h.Path))
                    byPath[h.Path] = byPath[h.Path] with
                    {
                        Score = Math.Min(1f, byPath[h.Path].Score + BothBonus),
                    };
                continue;
            }
            byPath[h.Path] = ToResult(h, WordScore - rank * RankStep, text);
            rank++;
        }

        // The same finish-and-order pass the name half runs: it is what gives these rows a size
        // and a date at all, and therefore what makes `size:`, `modified:` and the sort chips mean
        // the same thing on both sides of the pill. The store holds text, not directory entries,
        // so without this those controls would sit on the card doing nothing.
        List<SearchResult> rows = ResultMapper.Finish([.. byPath.Values], q, sort, stat);
        if (rows.Count > max) rows.RemoveRange(max, rows.Count - max);

        // "No matches", "nothing has been read yet" and "this needs a model you have not got" are
        // three different facts and only one of them is about the query. The order matters: an
        // index with nothing in it is explained by the index, never by a missing model, because
        // the second reads as an advertisement for something that would not have helped.
        //
        // `installed.Have is null` is the caller saying it does not know what is on this disk -
        // the diagnostics, which search an index they built themselves. Nothing is offered to
        // somebody who was never asked, so an offer needs a caller that named the set, even the
        // empty one.
        string note = "";
        if (rows.Count == 0)
            note = db.IndexedCount() == 0
                ? "Nothing indexed yet - Findra is still reading what is inside your files."
                : installed.Have is null ? "" : Capabilities.OfferFor(q, installed)?.Text ?? "";
        // NamesMs: 0 is not a placeholder. This path never asked the name index anything.
        return new SearchResults(raw, rows, NamesMs: 0, ContentMs: sw.Elapsed.TotalMilliseconds,
                                 ContentReady: true, Note: note);
    }

    /// <summary>
    /// One vector pass: encode the query the way this half of the store was written, keep the
    /// rows above the floor, and offer each segment they point at. A null encoder is the whole
    /// point of the seam - the capability is not installed, so this returns having offered
    /// nothing and having raised nothing.
    ///
    /// <para><paramref name="max"/> * 2 rows are asked for because one file can own many
    /// segments and only its best survives the dedupe.</para>
    /// </summary>
    private static void VectorPass(ContentDb db, SearchQuery q, string text, int max,
                                   VectorStore vectors, Func<string, float[]>? encode,
                                   ReadOnlySpan<byte> kinds, float floor, Func<float, float> band,
                                   Action<SearchResult> offer)
    {
        if (encode is null) return;
        float[] v = encode(text);
        var rows = new List<long>();
        var cosines = new Dictionary<long, float>();
        foreach (VectorStore.Match m in vectors.Search(v, max * 2, kinds))
        {
            // Below the floor is not a weak match, it is no match. Without this every photo in
            // the library is a candidate for every query.
            if (m.Score < floor) continue;
            rows.Add(m.Row);
            cosines[m.Row] = m.Score;
        }
        foreach (ContentDb.SegmentHit h in db.SegmentsByVec(rows))
        {
            // The grammar is a filter on the FILE and it applies to every branch: `lease ext:pdf`
            // means the pdf whichever half of the engine found it.
            if (!q.Allows(Path.GetFileName(h.Path), h.Path, h.Kind)) continue;
            offer(ToResult(h, band(cosines[h.Vec]), text, byMeaning: true));
        }
    }

    /// <summary>One segment hit as a card row. The kind is the ITEM's kind, already joined in by
    /// the query; the segment's own kind only decides how the row explains itself.
    ///
    /// <para><paramref name="byMeaning"/> says which branch found it, and it changes the sentence
    /// rather than the score. "Said around" and "said at" is the honest distinction: a vector hit
    /// is a window that resembles what was typed, a full-text hit is the word itself.</para>
    /// </summary>
    public static SearchResult ToResult(ContentDb.SegmentHit hit, float score, string query,
                                        bool byMeaning = false)
    {
        bool speech = hit.SegKind == ContentDb.SegSpeech;
        bool frame = hit.SegKind == ContentDb.SegFrame;
        bool picture = frame || hit.SegKind == ContentDb.SegImage;
        string why = byMeaning
            ? picture ? frame ? $"a moment at {Clock(hit.T0)} looks like it" : "looks like it"
                      : speech ? $"said around {Clock(hit.T0)}" : "says something like it"
            : speech ? $"said at {Clock(hit.T0)}" : "contains the words";
        return new SearchResult(hit.Kind, Path.GetFileName(hit.Path), hit.Path, score, why,
            // A moment is what makes the answer seekable: without it the card opens an hour of
            // audio at the beginning and somebody scrubs for the sentence by hand.
            MomentSeconds: speech || frame ? hit.T0 : -1,
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
