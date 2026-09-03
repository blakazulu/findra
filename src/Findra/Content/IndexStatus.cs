using System;
using System.Globalization;

namespace Findra;

/// <summary>What the capsule's progress pill draws: a label on the left, a track across the
/// middle, and a count on the right. <c>Show</c> false is "draw nothing at all", which is not the
/// same as a bar sitting at zero.</summary>
public readonly record struct IndexProgress(string Label, string Count, float Fraction, bool Show)
{
    public string Label { get; init; } = Label ?? "";
    public string Count { get; init; } = Count ?? "";
}

/// <summary>
/// What the content index is doing, in the two shapes the product needs: one sentence for the
/// card's footer and the tray's tooltip, and the same facts split for the capsule's progress pill.
/// Both live here so no two surfaces can disagree about it.
/// </summary>
public static class IndexStatus
{
    // Every number goes through this. The project sets InvariantGlobalization=false, so a bare
    // {n:N0} renders "9.000" on a German machine, and this line is compared in tests and read by
    // users on machines set to any locale.
    private static readonly CultureInfo Fixed = CultureInfo.InvariantCulture;

    /// <summary>
    /// The capsule's progress pill, split into the three things it draws.
    ///
    /// <para><see cref="Line"/> is one sentence for a footer and a tooltip; this is the same facts
    /// laid out as label, track and count, because the pill puts them at opposite ends of itself
    /// and a sentence cannot be cut in half. Both come from this file so the capsule and the card
    /// cannot disagree about what the index is doing.</para>
    ///
    /// <para><see cref="IndexProgress.Show"/> is false wherever the answer would be a bar with
    /// nothing behind it: reading off, no live indexer, or an empty queue. A permanently visible
    /// progress pill makes an idle widget feel busy, which is the thing spec §3 says the capsule
    /// must not do.</para>
    /// </summary>
    public static IndexProgress Pill(bool contentEnabled, string kind, long pending, long indexed, bool alive)
    {
        if (!contentEnabled || !alive || pending <= 0) return default;

        long total = indexed + pending;
        return new IndexProgress(
            Doing(kind),
            indexed.ToString("N0", Fixed) + " of " + total.ToString("N0", Fixed),
            total <= 0 ? 0f : (float)(indexed / (double)total),
            Show: true);
    }

    /// <summary>
    /// "indexing photos" and not "indexing Photo". The kind comes off the queue row the indexer is
    /// working, and an enum name is a token rather than a word - it would be the only place in the
    /// product where an identifier reached the screen.
    ///
    /// <para>An unknown or absent kind falls back to the bare verb rather than to a guess: the row
    /// is written by the child a second at a time, and a capsule that read it mid-write would
    /// otherwise name the wrong thing with complete confidence.</para>
    /// </summary>
    public static string Doing(string kind) =>
        Enum.TryParse(kind, out ResultKind k) && Enum.IsDefined(k) ? Doing(k) : "indexing";

    /// <summary>
    /// The same, on the enum, which is the only form that can be checked.
    ///
    /// <para>This switch was written on STRINGS - "Photo", "Video", "Audio", "Doc" - and "Doc" is
    /// not a member of <see cref="ResultKind"/>. It is the column heading <c>--searchindex</c>
    /// prints, copied from one surface into a comparison on another, so every document on the
    /// machine fell through to the default and the pill read "indexing" with no noun. Documents
    /// are most of what a first pass finds, so the label was wrong nearly all of the time and
    /// looked merely terse.</para>
    ///
    /// <para>The <c>_</c> arm is only there because an enum can hold a value no member names and
    /// the compiler insists (CS8524). It is not the guard: a seventh KIND would fall into it
    /// silently, so <c>EveryKindWhoseContentsAreReadHasAWordForIt</c> holds this to
    /// <see cref="FileKinds.HasContent"/> instead - every kind the indexer can queue must have a
    /// noun, and a new one fails that test rather than shipping a verb with nothing after it.
    /// </para>
    /// </summary>
    public static string Doing(ResultKind kind) => "indexing" + kind switch
    {
        ResultKind.Photo => " photos",
        ResultKind.Video => " video",
        ResultKind.Audio => " recordings",
        ResultKind.Document => " documents",
        // A file with no kind of its own and a folder are both indexed by name alone. There is no
        // honest noun for them, and the bare verb is the fallback rather than a special case.
        ResultKind.File => "",
        ResultKind.Folder => "",
        _ => "",
    };

    /// <summary>
    /// <paramref name="contentEnabled"/> is whether anybody has asked for the inside of files to
    /// be read at all, <paramref name="state"/> is what the indexer last wrote about itself,
    /// <paramref name="alive"/> whether it is running at all, and <paramref name="rebuilt"/>
    /// whether this index was thrown away and started again because it could not be read.
    ///
    /// <para>Off is the FIRST question, because an index nobody asked for is byte-for-byte what a
    /// finished one looks like - an empty queue and a still child - and the counts alone would
    /// say "up to date · 0 files" about a machine that has never read anything (spec §6). It is
    /// also a different sentence from the closed-app pause below it, because the two have
    /// opposite answers: one is "turn it on", the other is "leave Findra open".</para>
    /// </summary>
    public static string Line(bool contentEnabled, string state, long pending, long indexed, bool alive, bool rebuilt)
    {
        string N(long v) => v.ToString("N0", Fixed);

        if (!contentEnabled)
            return indexed > 0
                ? $"searching inside files is off · {N(indexed)} files already read"
                : "searching inside files is off - turn it on to read what is in them";

        // Checked first, and it survives the queue draining: "why did this take an hour" is a
        // question someone asks after it finishes, and an index that was unreadable is rebuilt
        // AND said so.
        if (rebuilt && pending > 0) return $"rebuilding the index - {N(pending)} to go";
        if (rebuilt) return $"index rebuilt · {N(indexed)} files";

        // Nothing read and nothing waiting, with reading TURNED ON, is not silence - it is the
        // second after somebody pressed "Start now", before the walk has put anything in the
        // queue. Returning "" there is what the user saw: they asked for it, and no surface said
        // a word. A live child gets said so; without one this is a switch that is on in a session
        // where nothing is reading, which is the honest and more useful sentence.
        if (pending == 0 && indexed == 0)
            return alive
                ? "reading inside your files - nothing found yet"
                : "reading inside files is on, but nothing is reading yet";
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
