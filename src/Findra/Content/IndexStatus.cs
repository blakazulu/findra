using System;
using System.Globalization;

namespace Findra;

/// <summary>
/// One line about the content index, or nothing at all. Drawn by the card's footer and by the
/// capsule, which paints its progress line only when this is non-empty - a permanently visible
/// empty bar is what makes an idle widget feel busy.
/// </summary>
public static class IndexStatus
{
    // Every number goes through this. The project sets InvariantGlobalization=false, so a bare
    // {n:N0} renders "9.000" on a German machine, and this line is compared in tests and read by
    // users on machines set to any locale.
    private static readonly CultureInfo Fixed = CultureInfo.InvariantCulture;

    /// <summary>
    /// <paramref name="state"/> is what the indexer last wrote about itself, <paramref name="alive"/>
    /// whether it is running at all, and <paramref name="rebuilt"/> whether this index was thrown
    /// away and started again because it could not be read.
    /// </summary>
    public static string Line(string state, long pending, long indexed, bool alive, bool rebuilt)
    {
        string N(long v) => v.ToString("N0", Fixed);

        // Checked first, and it survives the queue draining: "why did this take an hour" is a
        // question someone asks after it finishes, and an index that was unreadable is rebuilt
        // AND said so.
        if (rebuilt && pending > 0) return $"rebuilding the index - {N(pending)} to go";
        if (rebuilt) return $"index rebuilt · {N(indexed)} files";

        if (pending == 0 && indexed == 0) return "";
        // An honest imprecision: this calls an empty queue "up to date". The stricter definition
        // is a queue that is empty at a journal position the volume still recognises, and that
        // position is only checked when the journal is subscribed to. The gap is a window where
        // the journal wrapped since the last subscribe and this line is optimistic for one
        // session. Closing it properly means asking the helper on every status refresh, which is
        // a pipe round trip per second for a string.
        if (pending == 0) return $"index up to date · {N(indexed)} files";

        // Two different pauses reach this line and mean opposite things. Findra not running is
        // not the user's switch: indexing only happens while Findra is open, so a backlog left by
        // a previous session is expected and has to be explained rather than look stuck.
        if (!alive) return $"{N(pending)} waiting - indexing is paused while Findra is closed";
        if (state == "paused") return $"{N(pending)} waiting - indexing paused";
        return $"indexing {N(pending)} · {N(indexed)} done";
    }

    /// <summary>A heartbeat older than this is not an indexer, it is the last thing one wrote
    /// before it stopped. The child writes its status every couple of seconds at most, so this is
    /// several missed beats rather than a tight race.</summary>
    public const int BeatStaleSeconds = 15;

    /// <summary>
    /// Is the indexer child still there? The <c>indexer:beat</c> meta row is the only evidence,
    /// and a stale one means the opposite of what it says - the queue is not moving, and the line
    /// above has to explain that rather than show progress that never advances. An absent or
    /// unparseable row is an indexer that has never run, which is not a running one.
    /// </summary>
    public static bool Alive(string? beat, long nowUnixSeconds)
    {
        if (!long.TryParse(beat, NumberStyles.Integer, Fixed, out long at)) return false;
        // A beat dated in the future is a clock that resynced under a running child, not a dead
        // one, so only an OLD beat counts as gone.
        return nowUnixSeconds - at <= BeatStaleSeconds;
    }

    /// <summary>The heartbeat against the wall clock now.</summary>
    public static bool Alive(string? beat) => Alive(beat, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}
