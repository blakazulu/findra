using System;
using System.Collections.Generic;
using System.IO;
using Findra.Pipe;

namespace Findra;

/// <summary>
/// The helper answers in <see cref="NameRow"/>s - a name, a path, the raw attribute word and a
/// match class - because that is all it has: the name index lives in RAM and has never touched a
/// directory entry. The card paints <see cref="SearchResult"/>s. This is the single place that
/// turns one into the other, so the card, the diagnostics and the tests all agree on what a match
/// class means, where Size and Modified come from, and who enforces the filters that need a stat.
/// </summary>
public static class ResultMapper
{
    /// <summary>FILE_ATTRIBUTE_DIRECTORY, the one bit of the attribute word this side reads.</summary>
    public const uint DirectoryAttribute = 0x10;

    public static bool IsDirectory(uint attributes) => (attributes & DirectoryAttribute) != 0;

    /// <summary>
    /// A match class in words, for the stage's "match" line. The classes are NameIndex.Hit.Match
    /// (NameIndex.cs:23): 0 the whole name, 1 a prefix, 2 the start of a word, 3 anywhere,
    /// 4 within one typo, 5 a wildcard pattern, 6 filters only. Anything else is a helper that
    /// knows a class this build does not, and still has to read as something.
    /// </summary>
    public static string Why(int match) => match switch
    {
        0 => "exact name",
        1 => "name starts with it",
        2 => "a word in the name",
        4 => "close to the name",
        5 => "matches the pattern",
        6 => "matches the filters",
        _ => "in the name",
    };

    /// <summary>What one directory entry says. <see cref="Missing"/> is the honest answer for a
    /// file that has gone, or a path this process may not stat - not an exception and not a zero.</summary>
    public readonly record struct Stat(long Size, DateTime Modified, DateTime Created, DateTime Accessed)
    {
        public static readonly Stat Missing = new(-1, default, default, default);
        public bool Found => Modified != default;
    }

    /// <summary>The one call in here that touches the disk. Never on the UI thread.</summary>
    public static Stat StatOf(string path, bool isDirectory)
    {
        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists) return Stat.Missing;
            return new Stat(info is FileInfo f ? f.Length : 0,
                            info.LastWriteTime, info.CreationTime, info.LastAccessTime);
        }
        catch { return Stat.Missing; }   // a stat is a nicety; a row without one still opens
    }

    public static SearchResult Map(NameRow row, Stat stat)
    {
        bool dir = IsDirectory(row.Attributes);
        return new SearchResult(FileKinds.Classify(row.Name, dir), row.Name, row.Path,
            Math.Clamp(row.Score, 0f, 1f), Why(row.Match),
            Size: stat.Found ? stat.Size : -1,
            Modified: stat.Found ? stat.Modified : default);
    }

    /// <summary>
    /// A whole reply, mapped, filtered and ordered. <paramref name="stat"/> is injected so the
    /// tests can describe a disk without having one; production passes <see cref="StatOf"/>.
    /// </summary>
    public static SearchResults Build(string query, IReadOnlyList<NameRow> rows, SearchQuery parsed,
                                      SearchSort sort, double namesMs, Func<string, bool, Stat>? stat = null)
    {
        var mapped = new List<SearchResult>(rows.Count);
        foreach (NameRow row in rows) mapped.Add(Map(row, Stat.Missing));
        return new SearchResults(query, Finish(mapped, parsed, sort, stat), namesMs, 0, false);
    }

    /// <summary>
    /// The last step of every search, whichever half answered it: give each row its directory
    /// entry, drop what the stat filters exclude, and put what is left in the order the sort chips
    /// ask for.
    ///
    /// <para><b>Both halves come through here, and that is the point.</b> A full-text hit arrives
    /// from the index with no size and no date on it - the store holds text, not directory
    /// entries - so a Content search that skipped this step would leave `size:&gt;1mb`,
    /// `modified:week` and the Newest and Largest chips silently doing nothing, while the card
    /// went on showing them as applied. The grammar has to mean the same thing on both sides of
    /// the pill, and one shared pass is the only way it stays that way.</para>
    ///
    /// <para>`size:` and `modified:` are ours to enforce either way. The helper holds names in RAM
    /// and no stats at all, and the content store holds text, so neither source can answer those
    /// filters - dropping this shows every sunset on the disk for `sunset size:&gt;1mb`. A row
    /// that cannot be statted cannot be shown to satisfy such a filter, so it does not survive
    /// one; with no stat filter in the query it is kept, with a size of -1 that the card reads as
    /// "unknown".</para>
    ///
    /// <para>One stat per row, on whatever thread the caller is on - never the UI thread.</para>
    /// </summary>
    public static List<SearchResult> Finish(IReadOnlyList<SearchResult> rows, SearchQuery parsed,
                                            SearchSort sort, Func<string, bool, Stat>? stat = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(parsed);
        stat ??= StatOf;
        var list = new List<SearchResult>(rows.Count);
        foreach (SearchResult r in rows)
        {
            Stat st = stat(r.Path, r.Kind == ResultKind.Folder);
            if (parsed.NeedsStat && (!st.Found || !parsed.AllowsStat(st.Size, st.Modified, st.Created, st.Accessed)))
                continue;
            list.Add(st.Found ? r with { Size = st.Size, Modified = st.Modified } : r);
        }
        Order(list, sort);
        return list;
    }

    // A total order, not just a key: List.Sort is unstable, so equal scores would otherwise
    // shuffle between two runs of the same query and the card would appear to flicker.
    private static void Order(List<SearchResult> rows, SearchSort sort)
    {
        switch (sort)
        {
            case SearchSort.Newest:
                rows.Sort(static (a, b) => b.Modified.CompareTo(a.Modified) is var c && c != 0 ? c : Tie(a, b));
                break;
            case SearchSort.Largest:
                rows.Sort(static (a, b) => b.Size.CompareTo(a.Size) is var c && c != 0 ? c : Tie(a, b));
                break;
            default:
                rows.Sort(static (a, b) => Tie(a, b));
                break;
        }
    }

    // Score first, then the shorter path: a hit near the top of the disk explains itself.
    private static int Tie(SearchResult a, SearchResult b)
    {
        int c = b.Score.CompareTo(a.Score);
        if (c != 0) return c;
        c = a.Path.Length.CompareTo(b.Path.Length);
        return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
    }
}
