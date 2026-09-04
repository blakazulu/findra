using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Findra.Diagnostics;

/// <summary>
/// Everything the index knows about ONE file, and what it would score for a query.
///
/// <para><b>Why this exists.</b> Every other diagnostic answers a question about the whole index:
/// what is queued, what failed, what matched. None of them could answer the only question anybody
/// actually asks - "I can see this file, I searched for it, why did nothing come back" - and the
/// facts needed to answer it were all recorded and unreachable. Explaining one real result took
/// reading source and reasoning about it from the outside, which is exactly the shape of defect
/// <c>--searchprobe</c> was written to remove for the progress pill.</para>
///
/// <para>It is a pure function of an index and a path so that every sentence in it can be
/// asserted without a disk, a model or a screen.</para>
/// </summary>
public static class ExplainFile
{
    /// <summary>How a file stands with the index, before any query is considered.</summary>
    public readonly record struct Standing(
        string Path,
        bool OnDisk, long Bytes, DateTime? Modified,
        ResultKind Kind, bool ContentKind, bool Excluded,
        bool Known, int State, string Recorded, DateTime? ReadAt, long StaleBy,
        string? QueuedFor, int Attempts,
        IReadOnlyList<(int SegKind, int Count)> Segments);

    /// <summary>What one segment scored, and whether that was enough.</summary>
    public readonly record struct SegmentScore(int SegKind, long Vec, float Cosine, float Floor, bool Live, string Text);

    public static Standing Look(ContentDb db, string path, IReadOnlyList<string> exclusions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(path);

        var info = new FileInfo(path);
        bool onDisk = info.Exists;
        ResultKind kind = FileKinds.Classify(Path.GetFileName(path), isDirectory: false);

        ContentDb.ItemRow? row = db.ItemByPath(path);
        (string Reason, int Attempts)? queued = db.QueuedAs(path);

        var segments = new List<(int, int)>();
        long staleBy = 0;
        if (row is { } r)
        {
            foreach (var g in db.SegmentsOf(r.Id).GroupBy(s => s.SegKind).OrderBy(g => g.Key))
                segments.Add((g.Key, g.Count()));
            // A file read before it was last edited is a stale answer, and the index says so
            // itself rather than making somebody compare two timestamps by eye.
            if (onDisk && r.Mtime != 0 && info.LastWriteTimeUtc.Ticks > r.Mtime)
                staleBy = info.LastWriteTimeUtc.Ticks - r.Mtime;
        }

        return new Standing(
            Path: path,
            OnDisk: onDisk, Bytes: onDisk ? info.Length : 0,
            Modified: onDisk ? info.LastWriteTimeUtc : null,
            Kind: kind,
            ContentKind: FileKinds.HasContent(kind),
            Excluded: FileKinds.Excluded(path, exclusions),
            Known: row is not null,
            State: row?.State ?? ContentDb.StateQueued,
            Recorded: row?.Error ?? "",
            ReadAt: row is { IndexedAt: > 0 } k ? DateTimeOffset.FromUnixTimeSeconds(k.IndexedAt).UtcDateTime : null,
            StaleBy: staleBy,
            QueuedFor: queued?.Reason,
            Attempts: queued?.Attempts ?? 0,
            Segments: segments);
    }

    /// <summary>The single sentence that answers "is this file findable at all". Ordered by what
    /// actually decides, so it never names a reason that is not THE reason - the rule
    /// <c>--searchprobe</c> is written under.</summary>
    public static string Verdict(Standing s)
    {
        if (!s.OnDisk) return "this file is not on the disk";
        if (!s.ContentKind) return $"a {FileKinds.Label(s.Kind).ToLowerInvariant()} holds no content Findra reads - it is findable by name only";
        if (s.Excluded) return "a skipped-folder rule covers this path, so nothing reads it";
        if (s.QueuedFor is { } why) return $"waiting to be read ({why})";
        if (!s.Known) return "the index has never been offered this file";

        return s.State switch
        {
            ContentDb.StateSkipped => s.Recorded.Length > 0
                ? "passed over: " + s.Recorded
                : "passed over, with no reason recorded",
            ContentDb.StateFailed => s.Recorded.Length > 0
                ? "could not be read: " + s.Recorded
                : "could not be read, with no reason recorded",
            ContentDb.StateIndexed when s.Segments.Count == 0 =>
                "read, but nothing searchable came out of it" +
                (s.Recorded.Length > 0 ? " - " + s.Recorded : ""),
            ContentDb.StateIndexed when s.StaleBy > 0 =>
                "read, but it has been edited since - the index answers for the older copy",
            ContentDb.StateIndexed => "read and searchable",
            _ => "queued",
        };
    }

    /// <summary>What each of this file's vectors scores against an encoded query, and whether it
    /// cleared the floor its kind is judged on. This is the half that answers "why did this
    /// picture not come back": <c>VectorStore.Search</c> can only report the rows that WON, and a
    /// file somebody asks about is nearly always one that lost.</summary>
    public static List<SegmentScore> Against(ContentDb db, ContentDb.ItemRow row, VectorStore vectors,
                                             float[]? picture, float[]? words)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(vectors);
        var scored = new List<SegmentScore>();

        foreach (ContentDb.SegmentHit seg in db.SegmentsOf(row.Id))
        {
            if (seg.Vec < 0) { scored.Add(new SegmentScore(seg.SegKind, -1, 0f, 0f, Live: true, seg.Text)); continue; }

            bool isPicture = seg.SegKind is ContentDb.SegImage or ContentDb.SegFrame;
            float[]? q = isPicture ? picture : words;
            float floor = isPicture ? ContentBranch.PhotoFloor : ContentBranch.TextFloor;
            if (q is null) { scored.Add(new SegmentScore(seg.SegKind, seg.Vec, 0f, floor, Live: true, seg.Text)); continue; }

            (float cos, _, bool live) = vectors.ScoreOf(seg.Vec, q);
            scored.Add(new SegmentScore(seg.SegKind, seg.Vec, cos, floor, live, seg.Text));
        }
        return scored;
    }

    public static string SegName(int segKind) => segKind switch
    {
        ContentDb.SegImage => "picture",
        ContentDb.SegText => "words",
        ContentDb.SegSpeech => "speech",
        ContentDb.SegFrame => "frame",
        _ => segKind.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>What one scored segment means, in a sentence. The comparison is against the floor
    /// the real search uses, from the same constants, so this cannot describe a threshold the
    /// engine does not apply.</summary>
    public static string Says(SegmentScore s)
    {
        if (s.Vec < 0) return "not embedded - searchable by its words only";
        if (!s.Live) return "this vector has been discarded and matches nothing";
        if (s.Floor <= 0) return "no encoder for this kind on this machine, so it was not scored";

        string n = s.Cosine.ToString("0.000", CultureInfo.InvariantCulture);
        string f = s.Floor.ToString("0.000", CultureInfo.InvariantCulture);
        return s.Cosine >= s.Floor
            ? $"{n} against a floor of {f} - a match"
            : $"{n} against a floor of {f} - below it, so not a match";
    }
}
