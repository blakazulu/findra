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

    /// <summary>
    /// Work three times as long as it rests.
    ///
    /// <para>This was 50 - rest as long as you worked - chosen so that nobody would notice the
    /// indexer running. It is the wrong half of the trade for a first pass: the pass is the one
    /// time the work is genuinely urgent, it happens once, and holding the machine back by half
    /// doubles the day somebody spends waiting for their own files to become searchable. 75 still
    /// leaves a quarter of the duty cycle to everything else, and the level is a control on the
    /// Content screen for anybody who disagrees.</para>
    /// </summary>
    public const int Default = 75;

    /// <summary>Four, not five: a scale finer than a quarter is a distinction nobody can feel,
    /// and every one of them is inside the clamp above.
    ///
    /// <para>Written out rather than spelling <see cref="Default"/> in the middle of it. It used
    /// to say <c>[25, Default, 75, Max]</c>, which is fine until Default moves onto a number the
    /// list already names - and then the control has four options, two of them identical, and two
    /// pills light at once. <c>TheDefaultPowerIsOneOfTheFourLevelsAndTheListHasNoDuplicate</c> is
    /// what noticed.</para></summary>
    public static readonly IReadOnlyList<int> Presets = [25, 50, Default, Max];

    /// <summary>What a pill says. Invariant, because it is a number in a control and the same
    /// number is written into the index for another process to parse.</summary>
    public static string ShortName(int power) =>
        power.ToString(CultureInfo.InvariantCulture) + "%";
}
