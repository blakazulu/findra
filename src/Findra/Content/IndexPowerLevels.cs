using System.Collections.Generic;
using System.Globalization;

namespace Findra;

/// <summary>
/// How much of the machine the content indexer is allowed to take, as one number.
///
/// <para>A duty cycle rather than a thread count or a priority: at 50 the child rests as long as
/// it worked, at 100 it does not rest at all. It is read out of the index's <c>index:power</c>
/// row before each turn, so moving it reaches a running indexer rather than waiting for the next
/// launch.</para>
///
/// <para><b>One list, wherever it is offered.</b> The named choices are PRESETS OVER THE NUMBER,
/// exactly as the transcription limit's are: a second table of labels somewhere else is how a
/// surface comes to offer a level the config will not keep.</para>
/// </summary>
public static class IndexPowerLevels
{
    /// <summary>What <see cref="Config.Load"/> clamps to. A level offered outside this is a
    /// control that writes a number the next launch quietly replaces.</summary>
    public const int Min = 10;
    public const int Max = 100;

    /// <summary>Rest as long as it worked. Enough that a first index finishes in an evening and
    /// little enough that nobody notices it running.</summary>
    public const int Default = 50;

    /// <summary>Four, not five: a scale finer than a quarter is a distinction nobody can feel,
    /// and every one of them is inside the clamp above.</summary>
    public static readonly IReadOnlyList<int> Presets = [25, Default, 75, Max];

    /// <summary>What a pill says. Invariant, because it is a number in a control and the same
    /// number is written into the index for another process to parse.</summary>
    public static string ShortName(int power) =>
        power.ToString(CultureInfo.InvariantCulture) + "%";
}
