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
/// The facts beyond the counts that change what the status says: a kind waiting for the card
/// while others are read, reading moved to the processor, a restart after a crash, and the files
/// that were left out or could not be read. All optional, so a caller that knows none of them
/// gets the plain answer. <c>default</c> carries nulls for the two strings, so they are read with
/// <c>IsNullOrEmpty</c>, never <c>.Length</c>.
/// </summary>
public readonly record struct IndexExtra(
    string Around = "", string Processor = "", int RestartIn = 0, long LeftOut = 0, long Failed = 0,
    bool Held = false);

/// <summary>What the dot beside the Settings sentence says at a glance: moving, waiting for
/// something, stuck, or finished. Encoded in shape as well as colour (a ring waits, a filled dot
/// moves), so it reads for somebody who cannot tell the colours apart.</summary>
public enum StatusTone { None, Reading, Waiting, Problem, Done }

/// <summary>
/// What the content index is doing, in the shapes the product needs: one line for the card's
/// footer and the tray's tooltip, the same facts split for the progress pill, and a sentence for
/// Settings. All of them live here so no two surfaces can disagree about it.
///
/// <para><b>Written for somebody who has never heard of an index, a GPU or a codec.</b> Every
/// wait says what it is waiting for in words a person recognises, and whether they have to do
/// anything (almost never: "Findra will carry on by itself").</para>
/// </summary>
public static class IndexStatus
{
    // Every number goes through this. The project sets InvariantGlobalization=false, so a bare
    // {n:N0} renders "9.000" on a German machine, and this line is compared in tests and read by
    // users on machines set to any locale.
    private static readonly CultureInfo Fixed = CultureInfo.InvariantCulture;

    private static string N(long v) => v.ToString("N0", Fixed);

    /// <summary>The indexer's state token for a failure it cannot get past.</summary>
    public const string Stuck = "stuck";

    /// <summary>
    /// The progress pill, split into the three things it draws.
    ///
    /// <para><see cref="IndexProgress.Show"/> is false wherever the answer would be a bar with
    /// nothing behind it: reading off, no live indexer, or an empty queue. A permanently visible
    /// progress pill makes an idle widget feel busy, which is the thing spec §3 says the capsule
    /// must not do. The one exception is a reader that crashed and is about to be started again:
    /// that is work in hand, and a pill that vanished would say it was finished.</para>
    ///
    /// <para><b>The count is files DONE</b>, which includes the ones left out and the ones that
    /// could not be read. Counting only files read made a pass through skipped files look frozen
    /// at "1 of 278,067" while it was moving.</para>
    /// </summary>
    public static IndexProgress Pill(bool contentEnabled, string kind, long pending, long indexed,
                                     bool alive, string state = "", IndexExtra extra = default)
    {
        long done = indexed + extra.LeftOut + extra.Failed;
        long total = done + pending;
        string count = N(done) + " of " + N(total);
        float fraction = total <= 0 ? 0f : (float)(done / (double)total);

        if (contentEnabled && !alive && pending > 0 && extra.RestartIn > 0)
            return new IndexProgress("a problem · trying again soon", count, fraction, Show: true);

        // Work in hand. The only state either surface draws, and the only one with a moving bar.
        if (contentEnabled && alive && pending > 0)
            return new IndexProgress(PillLabel(state, kind, extra), count, fraction, Show: true);

        // Everything else is a settled index: reading off, no indexer behind the queue, nothing
        // queued at all. There is no work to draw a picture of, so nothing is drawn - which is not
        // a bar at zero and not a bar at 100%. --searchprobe names which of those it is.
        return default;
    }

    private static string PillLabel(string state, string kind, IndexExtra extra)
    {
        if (Waiting(state) is { } waiting) return waiting;
        if (!string.IsNullOrEmpty(extra.Around)) return Waits(extra.Around ?? "") + " waiting · " + Doing(kind);
        if (!string.IsNullOrEmpty(extra.Processor)) return Doing(kind) + ", slowly";
        return Doing(kind);
    }

    /// <summary>The pill's words for an indexer that is holding back or stuck, or null for any
    /// state that is not one. As short as "reading recordings", which is what the pill is
    /// measured against.</summary>
    public static string? Waiting(string? state) => state switch
    {
        IndexGate.GpuBusy => "paused · another app is busy",
        IndexGate.Fullscreen => "paused · full-screen app",
        Stuck => "a problem · see Settings",
        _ => null,
    };

    /// <summary>What waits while the card is busy, from the kind held back: photos and videos, or
    /// recordings. Anything unrecognised is "photos", the commonest.</summary>
    private static string Waits(string kind) =>
        Enum.TryParse(kind, out ResultKind k) && k == ResultKind.Audio ? "recordings" : "photos";

    /// <summary>
    /// "reading photos" and not "indexing Photo". The kind comes off the queue row the indexer is
    /// working, and an enum name is a token rather than a word - it would be the only place in the
    /// product where an identifier reached the screen.
    ///
    /// <para>An unknown or absent kind falls back to the bare verb rather than to a guess: the row
    /// is written by the child a second at a time, and a capsule that read it mid-write would
    /// otherwise name the wrong thing with complete confidence.</para>
    /// </summary>
    public static string Doing(string kind) =>
        Enum.TryParse(kind, out ResultKind k) && Enum.IsDefined(k) ? Doing(k) : "reading";

    /// <summary>
    /// The same, on the enum, which is the only form that can be checked.
    ///
    /// <para>The <c>_</c> arm is only there because an enum can hold a value no member names and
    /// the compiler insists (CS8524). It is not the guard: a seventh KIND would fall into it
    /// silently, so <c>EveryKindWhoseContentsAreReadHasAWordForIt</c> holds this to
    /// <see cref="FileKinds.HasContent"/> instead.</para>
    /// </summary>
    public static string Doing(ResultKind kind) => "reading" + kind switch
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

    /// <summary>The meta row the interface writes how long until a crashed reader is started
    /// again. Written by the one process that knows (the host is the interface's), read by every
    /// surface that describes the index.</summary>
    public const string RestartInKey = "index:restartin";

    /// <summary>
    /// The extra facts, read from the rows the indexer and the interface write. One reader, so the
    /// card and the capsule cannot take them differently. <paramref name="get"/> is a meta lookup.
    /// </summary>
    public static IndexExtra ExtraFrom(Func<string, string?> get, long leftOut, long failed, bool held = false)
    {
        ArgumentNullException.ThrowIfNull(get);
        int restart = int.TryParse(get(RestartInKey), NumberStyles.Integer, Fixed, out int r) ? r : 0;
        return new IndexExtra(get("indexer:around") ?? "", get("indexer:processor") ?? "", restart, leftOut, failed, held);
    }

    /// <summary>The dot for the same facts <see cref="Sentence"/> reads, arm for arm, so the two
    /// cannot disagree.</summary>
    public static StatusTone Tone(bool on, string state, long pending, long indexed, bool alive, IndexExtra extra = default)
    {
        if (!on) return StatusTone.None;
        if (extra.Held) return StatusTone.Waiting;
        long done = indexed + extra.LeftOut + extra.Failed;
        if (pending == 0) return done > 0 ? StatusTone.Done : StatusTone.Waiting;
        if (!alive) return extra.RestartIn > 0 ? StatusTone.Problem : StatusTone.Waiting;
        if (state == Stuck) return StatusTone.Problem;
        if (state is IndexGate.Fullscreen or IndexGate.GpuBusy) return StatusTone.Waiting;
        if (!string.IsNullOrEmpty(extra.Around)) return StatusTone.Waiting;
        return StatusTone.Reading;
    }

    /// <summary>"in 2 minutes", "in a minute", "in a moment" - never a number of seconds.</summary>
    public static string InAWhile(int seconds) =>
        seconds <= 45 ? "in a moment"
        : seconds <= 90 ? "in a minute"
        : $"in {((seconds + 59) / 60).ToString(Fixed)} minutes";

    /// <summary>
    /// <paramref name="contentEnabled"/> is whether reading inside files is on right now,
    /// <paramref name="state"/> is what the indexer last wrote about itself,
    /// <paramref name="alive"/> whether it is running at all, and <paramref name="rebuilt"/>
    /// whether this index was thrown away and started again because it could not be read.
    ///
    /// <para>Off is the FIRST question, because an index nobody asked for is byte-for-byte what a
    /// finished one looks like - an empty queue and a still child - and the counts alone would
    /// say "all done · 0 files" about a machine that has never read anything (spec §6).</para>
    /// </summary>
    public static string Line(bool contentEnabled, string state, long pending, long indexed, bool alive, bool rebuilt,
                              IndexExtra extra = default)
    {
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
        // queue.
        if (pending == 0 && indexed == 0)
            return alive ? "looking for files to read" : "reading inside files is on, but nothing is reading yet";
        if (pending == 0) return $"all done · {N(indexed)} files ready to search";

        string toGo = $"{N(pending)} files to go";
        // Paused first: it is a fact recorded in the index by whoever paused it, and a paused
        // index has no child by design, so asking "is a child alive" first blamed the wrong cause.
        if (state == "paused") return $"paused · {toGo}";
        if (!alive)
            return extra.RestartIn > 0
                ? $"something went wrong - Findra will try again {InAWhile(extra.RestartIn)}"
                : $"paused while Findra is closed · {toGo}";
        if (state == Stuck) return "Findra can't save what it reads - your disk may be full";
        if (state == IndexGate.GpuBusy) return $"paused while another app is busy · {toGo}";
        if (state == IndexGate.Fullscreen) return $"paused while you're in a full-screen app · {toGo}";
        if (!string.IsNullOrEmpty(extra.Around)) return $"{Waits(extra.Around ?? "")} wait while another app is busy · reading documents · {toGo}";
        if (!string.IsNullOrEmpty(extra.Processor)) return $"reading more slowly, the graphics card is nearly full · {toGo}";
        return $"reading your files · {N(indexed + extra.LeftOut + extra.Failed)} done · {N(pending)} to go";
    }

    /// <summary>
    /// The sentence under "Reading now" in Settings: what reading is doing, in plain words, and
    /// whether anybody has to do anything. Empty while reading is off - the switch's own sentence
    /// says that.
    /// </summary>
    public static string Sentence(bool on, string state, long pending, long indexed, bool alive, IndexExtra extra = default)
    {
        if (!on) return "";
        if (extra.Held) return "Waiting until you finish setting up Findra.";
        long done = indexed + extra.LeftOut + extra.Failed;
        if (pending == 0) return done > 0 ? "All done. Your files are ready to search." : "Looking for files to read.";
        if (!alive)
            return extra.RestartIn > 0
                ? $"Something went wrong. Findra will try again {InAWhile(extra.RestartIn)}."
                : "Getting ready to read your files.";
        if (state == Stuck) return "Findra can't save what it reads. Your disk may be full.";
        if (state == IndexGate.Fullscreen) return "Paused while you're in a full-screen app.";
        if (state == IndexGate.GpuBusy) return "Paused while another app is busy. Findra will carry on by itself.";
        if (!string.IsNullOrEmpty(extra.Around))
            return Waits(extra.Around ?? "") == "recordings"
                ? "Recordings will wait while another app is busy. Documents are still being read."
                : "Photos will wait while another app is busy. Documents are still being read.";
        if (!string.IsNullOrEmpty(extra.Processor)) return "Reading more slowly, because the graphics card is nearly full.";

        string read = $"{N(indexed)} files read";
        if (extra.LeftOut > 0) read += $" · {N(extra.LeftOut)} left out";
        if (extra.Failed > 0) read += $" · {N(extra.Failed)} could not be read";
        return read + ".";
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
    public static bool Alive(string? beat, string? pid, int thisProcess, long nowUnixSeconds,
                             Func<int, bool>? stillRunning = null)
    {
        // An index written before this row existed has no pid to compare, and reading that as
        // "it must have been me" would call every such index idle for ever.
        bool haveePid = int.TryParse(pid, NumberStyles.Integer, Fixed, out int wrote);
        if (haveePid && wrote == thisProcess) return false;

        if (!long.TryParse(beat, NumberStyles.Integer, Fixed, out long at)) return false;
        // A beat dated in the future is a clock that resynced under a running child, not a dead
        // one, so only an OLD beat counts as gone.
        if (nowUnixSeconds - at <= BeatStaleSeconds) return true;

        // A stale beat from a process that is STILL THERE is a busy child, not a dead one, and
        // this is the difference between the two questions these rows answer. The beat is written
        // between files; one long file - a thirty-minute recording, a 180 MB document, frames out
        // of a long video - takes minutes with no chance to write anything, and fifteen seconds in
        // the capsule's pill vanished, the card said "paused", the card's footer said indexing was
        // paused because Findra was closed, and both diagnostics said no indexer was running. All
        // four went quiet during exactly the operation people most often ask about, and they said
        // it was not happening while it was happening.
        return haveePid && (stillRunning ?? IsRunning)(wrote);
    }

    /// <summary>Is there a process with this id? Injected in tests; the real one asks the OS.
    /// A pid can be reused, which is why it is asked ONLY about a stale beat that a live child
    /// would otherwise have refreshed - never as the whole answer.</summary>
    private static bool IsRunning(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }        // no such process
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
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
