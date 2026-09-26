using System;

namespace Findra;

/// <summary>The three things the card's reading pill can say.</summary>
public enum ReadingShown { Start, Starting, Stop }

/// <summary>What a press on the reading pill asks for.</summary>
public enum ReadingPress { Start, Stop, Nothing }

/// <summary>
/// The fourth pill on the card: Settings' "Start reading now" button, where the search is.
///
/// <para>The card reads the index through a read-only connection and learns what reading is
/// doing from the status rows about once a second, so a press is believed for a few seconds
/// (<see cref="Hold"/>) rather than contradicted by a row that has not caught up. A start that
/// never shows work in hand - a disk that is already finished - goes back to offering Start once
/// the wait is over, rather than saying Starting for ever.</para>
/// </summary>
public static class ReadingPill
{
    public const string StartLabel = "Start now";
    public const string StartingLabel = "Starting...";

    /// <summary>"Stop" and no count: the card's pills are a quarter the width of the Settings
    /// button, and the count is already in the progress pill under the card while there is
    /// work in hand.</summary>
    public const string StopLabel = "Stop";

    /// <summary>How long a press is believed over the status rows.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(5);

    /// <summary>Is Findra reading inside files right now: the switch on, and an indexer alive with
    /// work in hand. The switch is part of it because stopping PAUSES the indexer and leaves its
    /// queue as it was, so alive with a backlog is still true straight after a stop.</summary>
    public static bool Reading(bool readingOn, bool indexerAlive, long pending)
        => readingOn && SettingsModel.Reading(indexerAlive, pending);

    /// <param name="reading">What the status rows say (<see cref="Reading"/>).</param>
    /// <param name="asked">What the last press asked for and is still being believed: true a
    /// start, false a stop, null nothing.</param>
    public static ReadingShown Shown(bool reading, bool? asked) => asked switch
    {
        true when !reading => ReadingShown.Starting,
        false => ReadingShown.Start,
        _ => reading ? ReadingShown.Stop : ReadingShown.Start,
    };

    /// <summary>What is still believed after <paramref name="since"/> has passed since the press:
    /// nothing once the index agrees or the wait is over.</summary>
    public static bool? Settle(bool reading, bool? asked, TimeSpan since)
        => asked is null || asked == reading || since >= Hold ? null : asked;

    public static ReadingPress Press(ReadingShown shown) => shown switch
    {
        ReadingShown.Start => ReadingPress.Start,
        ReadingShown.Stop => ReadingPress.Stop,
        ReadingShown.Starting => ReadingPress.Nothing,
        _ => throw new ArgumentOutOfRangeException(nameof(shown), shown, "no press for this shape"),
    };

    /// <summary>Starting takes no second press, and is drawn faded with the plain arrow like any
    /// other pill that is not offering.</summary>
    public static bool Offers(ReadingShown shown) => shown != ReadingShown.Starting;

    public static string Label(ReadingShown shown) => shown switch
    {
        ReadingShown.Start => StartLabel,
        ReadingShown.Starting => StartingLabel,
        ReadingShown.Stop => StopLabel,
        _ => throw new ArgumentOutOfRangeException(nameof(shown), shown, "no label for this shape"),
    };
}
