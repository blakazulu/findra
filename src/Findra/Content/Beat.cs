using System;

namespace Findra;

/// <summary>
/// Saying "still working" often enough to be believed and seldom enough to be free.
///
/// <para>The indexer used to write its heartbeat only between files, so a file that took an hour
/// looked exactly like a child that had died an hour ago - and the difference between those two is
/// the whole of whether anything should be done about it. Every decoder now reports as it goes,
/// and this is what keeps that from being a database write per video frame.</para>
/// </summary>
public static class Beat
{
    public static Action Throttled(Func<long> nowUnix, Action<long> write, int everySeconds = 2)
    {
        ArgumentNullException.ThrowIfNull(nowUnix);
        ArgumentNullException.ThrowIfNull(write);
        // Not long.MinValue: `now - last` for the very first beat would overflow, and the
        // comparison that overflow produces is meaningless - the first beat of a session could
        // be silently swallowed, which a watchdog reads as the child having done nothing yet.
        long last = long.MinValue / 2;
        return () =>
        {
            long now = nowUnix();
            if (now - last < everySeconds) return;
            last = now;
            write(now);
        };
    }
}
