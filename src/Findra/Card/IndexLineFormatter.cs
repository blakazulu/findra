using System.Collections.Generic;
using System.Globalization;
using Findra.Pipe;

namespace Findra;

/// <summary>
/// Turns a <see cref="StatusReply"/> into the "index:" line the card shows under the field.
/// Pulled out of <see cref="CardWindow"/> so it is pure and testable: no pipe, no helper, no
/// window. When any volume has not finished its initial USN enumeration, the line says so -
/// a sparse answer mid-enumeration should explain itself rather than look like a small disk.
/// </summary>
public static class IndexLineFormatter
{
    public static string IndexLineFor(StatusReply s)
    {
        long names = 0;
        var letters = new List<string>(s.Volumes.Count);
        bool stillReading = false;
        foreach (VolumeStatus v in s.Volumes)
        {
            names += v.Count;
            letters.Add(v.Letter + ":");
            if (!v.Live) stillReading = true;
        }

        string line = letters.Count == 0
            ? $"no volumes indexed (helper pid {s.ProcessId})"
            : $"{Count(names)} names on {string.Join(", ", letters)} (helper pid {s.ProcessId})";

        return stillReading ? line + " (still reading the drive)" : line;
    }

    /// <summary>Invariant, all three arms. "1.5M" renders as "1,5M" under de-DE, in the same
    /// footer sentence as <see cref="IndexStatus.Line"/>, which is careful to read the same on
    /// every machine - one half of one line disagreeing with the other half is the kind of thing
    /// nobody sees until it is in a screenshot.</summary>
    private static string Count(long n)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        return n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0", c) + "M"
             : n >= 1000 ? (n / 1000.0).ToString("0", c) + "k"
             : n.ToString(c);
    }
}
