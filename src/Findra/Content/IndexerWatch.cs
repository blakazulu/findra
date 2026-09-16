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

    /// <summary>Is a live, reading child with work to do overdue enough that it should be killed
    /// and restarted? A paused reader, an idle queue, or no child at all are none of this rule's
    /// business - each already writes its status every couple of seconds, so none of them can trip
    /// it - and a beat from the future is a clock that resynced rather than evidence of a stall.
    /// </summary>
    public static bool ShouldRestart(bool hostRunning, bool reading, long pending, string? beat, long nowUnix)
    {
        if (!hostRunning || !reading || pending <= 0) return false;
        if (!long.TryParse(beat, NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) return false;
        return nowUnix - at > StallSeconds;
    }
}
