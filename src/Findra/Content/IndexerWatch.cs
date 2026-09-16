using System;
using System.Globalization;

namespace Findra;

/// <summary>
/// When a child that is alive has stopped being useful.
///
/// <para><b>The attempts counter cannot see this.</b> It is spent before a file is opened so that
/// a decoder which takes the process down is written off - but a HANG is not a crash. The attempt
/// never ends, the child never exits, nothing restarts it, and the queue stops at that file for as
/// long as the machine is on. The only thing that used to spend an attempt was somebody restarting
/// Findra.</para>
///
/// <para>Pure, and asked by the interface's pump, which already comes round every 400 ms.</para>
/// </summary>
public static class IndexerWatch
{
    /// <summary>How long a live child may go without reporting progress before it is restarted.
    /// Every decoder beats as it works, and the queue's own rests are seconds, so three minutes is
    /// far past anything healthy - and a false restart costs one attempt out of three, while a
    /// missed stall costs the whole queue.</summary>
    public const int StallSeconds = 180;

    /// <summary>
    /// Is a live, reading child with work to do overdue enough that it should be killed and
    /// restarted? A paused reader, an idle queue, or no child at all are none of this rule's
    /// business - each already writes its status every couple of seconds, so none of them can trip
    /// it - and a beat from the future is a clock that resynced rather than evidence of a stall.
    ///
    /// <para><b>A child younger than <see cref="StallSeconds"/> is never judged</b>, and
    /// <paramref name="sinceStart"/> is how that is known. The beat row is written by the CHILD and
    /// outlives the child that wrote it, so the row a replacement would be read against belongs to
    /// the one just killed - and a watchdog kill restarts immediately, on purpose. Without this,
    /// one turn of the pump kills a stalled child and every turn after it kills the replacement
    /// microseconds after starting it: a process storm every 400 ms in place of the stall, and the
    /// file is never written off, because an attempt is only committed once a child has taken a row
    /// off the queue and this one never gets that far. A child with no age at all is refused for the
    /// same reason - not knowing how long it has been there is not evidence that it has stalled.
    /// </para>
    /// </summary>
    public static bool ShouldRestart(bool hostRunning, bool reading, long pending, string? beat, long nowUnix,
                                     TimeSpan? sinceStart)
    {
        if (!hostRunning || !reading || pending <= 0) return false;
        if (sinceStart is not { } age || age.TotalSeconds < StallSeconds) return false;
        if (!long.TryParse(beat, NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) return false;
        return nowUnix - at > StallSeconds;
    }
}
