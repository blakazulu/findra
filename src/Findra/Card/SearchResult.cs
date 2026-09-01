using System;
using System.Collections.Generic;

namespace Findra;

public sealed record SearchResult(ResultKind Kind, string Name, string Path, float Score, string Why,
    double MomentSeconds = -1, string Excerpt = "", long Size = -1, DateTime Modified = default);

public enum SearchSort { Best, Newest, Largest }

/// <summary>One answer to one query. Immutable, swapped in whole - the card never edits it.</summary>
public sealed record SearchResults(string Query, IReadOnlyList<SearchResult> Rows, double NamesMs,
    double ContentMs, bool ContentReady, string Note = "")
{
    public static readonly SearchResults Empty = new("", Array.Empty<SearchResult>(), 0, 0, false);
}
