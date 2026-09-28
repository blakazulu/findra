using System;

namespace Findra;

/// <summary>
/// The order results go in, best first. The score decides; between two that answer equally well,
/// the one fewer folders down comes first (somebody typing ".claude" means the one in their
/// profile, not the one inside every project), then the shorter path, then the path itself so
/// the order is total. Depth only breaks ties: a shallow weak match never overtakes a deep good
/// one. The name helper merges volumes by this and the card sorts by it, so both agree.
/// </summary>
public static class Rank
{
    public static int Compare(float scoreA, string pathA, float scoreB, string pathB)
    {
        int c = scoreB.CompareTo(scoreA);
        if (c != 0) return c;
        c = Depth(pathA).CompareTo(Depth(pathB));
        if (c != 0) return c;
        c = pathA.Length.CompareTo(pathB.Length);
        return c != 0 ? c : string.CompareOrdinal(pathA, pathB);
    }

    /// <summary>Folders between the drive and the item: C:\Users\me\.claude is three, the same
    /// count <see cref="NameIndex.Depth"/> gives for that record.</summary>
    public static int Depth(string path) => path.AsSpan().Count('\\');
}
