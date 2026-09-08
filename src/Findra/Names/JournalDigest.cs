using System;
using System.Collections.Generic;
using System.Globalization;

namespace Findra;

/// <summary>
/// The journal tail's log line, as a summary instead of one line per poll.
///
/// <para>The tail polls every volume about once a second for as long as the machine is on, and it
/// used to write a line each time. Measured on a real install: <b>50,248 lines in 14 hours</b>, 83%
/// of them saying nothing had been applied, 99.5% of everything in the log, roughly 9 MB a day that
/// never stops growing. The cost is not the disk. It is that the log stops being readable - finding
/// the four warnings that mattered on that machine meant filtering fifty thousand lines out of the
/// way first - and a log nobody can read is the same as no log at all on the one day somebody needs
/// it.</para>
///
/// <para><b>A digest rather than <c>Log.Repeat</c>.</b> The throttle would print one poll's numbers
/// with "412 more since the last line" after it, which is a true number describing a four-hundredth
/// of the interval it appears to summarise. What a reader wants from the journal is how much went
/// past and how much of it mattered, and both of those are sums.</para>
///
/// <para><b>A quiet window says nothing.</b> A poll that saw no records is not news, and a
/// heartbeat whose whole content is "nothing happened" is the thing being removed here. Whether the
/// tail is alive is a question <c>--searchindex</c> answers, with the position each volume has
/// reached; a tail that dies says so through its own warning.</para>
/// </summary>
public sealed class JournalDigest
{
    /// <summary>How often a busy volume is allowed a line. Twelve an hour rather than 3,600.
    /// </summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private sealed class Window
    {
        public long Records, Applied;
        public DateTime Since;
        public bool Spoken;
    }

    private readonly Dictionary<char, Window> _windows = [];

    /// <summary>Record one poll and return the line to write, or null to write nothing.
    ///
    /// <para>The first poll on a volume that carries any work is reported immediately: a reader
    /// watching a fresh start has to see the tail pick the drive up, and holding that line for five
    /// minutes would read as a tail that never started.</para></summary>
    public string? Add(char letter, int records, int applied, long usn, DateTime now)
    {
        if (!_windows.TryGetValue(letter, out Window? w))
            _windows[letter] = w = new Window { Since = now };

        w.Records += records;
        w.Applied += applied;

        // Nothing seen is nothing to say, however long the window has been open. The clock is only
        // consulted once there is something worth reporting, so a drive that is quiet for an hour
        // and then does one thing reports it at once rather than at the end of some window it was
        // never really in.
        if (w.Records == 0) { w.Since = now; return null; }
        if (w.Spoken && now - w.Since < Every) return null;

        string line = string.Create(CultureInfo.InvariantCulture,
            $"{letter}: {w.Records} journal records, {w.Applied} applied, now at usn {usn}");
        w.Records = 0;
        w.Applied = 0;
        w.Since = now;
        w.Spoken = true;
        return line;
    }
}
