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
    /// Is there an indexer child running? Two meta rows answer it, and both halves are needed.
    ///
    /// <para><c>indexer:beat</c> is the evidence that something is alive: a stale one means the
    /// opposite of what it says - the queue is not moving, and the line above has to explain that
    /// rather than show progress that never advances. An absent or unparseable row is an indexer
    /// that has never run, which is not a running one.</para>
    ///
    /// <para><c>indexer:pid</c> is the evidence that it is somebody ELSE.
    /// <see cref="Indexer.DrainOnce"/> writes exactly the same rows a running <c>--index</c> child
    /// writes, so any process that queues and drains in place - <c>--searchindex</c> given a
    /// folder, <c>--searchtest</c>'s end-to-end check - leaves a fresh heartbeat behind and would
    /// then read its own finished one-shot work back as a live child, with a state and a rate.</para>
    ///
    /// <para>This lives here, and nowhere else, because four surfaces describe the same two rows:
    /// the card's footer, the capsule's progress line, <c>--searchprobe</c> and
    /// <c>--searchindex</c>. Three of them used to answer this question the weaker way. There is
    /// deliberately no overload that omits the pid - the point is that one answer exists.</para>
    /// </summary>
    public static bool Alive(string? beat, string? pid, int thisProcess, long nowUnixSeconds)
    {
        if (!long.TryParse(beat, NumberStyles.Integer, Fixed, out long at)) return false;
        // A beat dated in the future is a clock that resynced under a running child, not a dead
        // one, so only an OLD beat counts as gone.
        if (nowUnixSeconds - at > BeatStaleSeconds) return false;

        // An index written before this row existed has no pid to compare, and reading that as
        // "it must have been me" would call every such index idle for ever.
        if (!int.TryParse(pid, NumberStyles.Integer, Fixed, out int wrote)) return true;
        return wrote != thisProcess;
    }

    /// <summary>The same rule against this process and the wall clock now.</summary>
    public static bool Alive(string? beat, string? pid)
        => Alive(beat, pid, Environment.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    /// The one sentence that describes a live indexer child, shared by <c>--searchindex</c> and
    /// <c>--searchprobe</c> so the two cannot disagree about the rows they both read.
    ///
    /// <para>Every field is optional in practice and the ordinary steady state has two of them
    /// empty: a child that has drained the queue has no current file and no rate. Interpolating
    /// them regardless printed <c>idle -  ()</c> - a dash, two spaces and an empty pair of
    /// brackets - which is the line most people ever see.</para>
    /// </summary>
    public static string Running(string? pid, string? state, string? current, string? rate)
    {
        string head = pid is { Length: > 0 } p ? $"running (pid {p})" : "running";
        string what = state is { Length: > 0 } s ? s : "working";
        string where = current is { Length: > 0 } c ? " " + c : "";
        string fast = rate is { Length: > 0 } r ? ", " + r : "";
        return $"{head} - {what}{where}{fast}";
    }
}
