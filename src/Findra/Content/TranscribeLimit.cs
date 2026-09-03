using System;
using System.Collections.Generic;
using System.Globalization;

namespace Findra;

/// <summary>
/// How long a recording is worth transcribing, as one number of minutes.
///
/// <para>Transcription cost scales with the LENGTH of a recording rather than the size of its
/// file, and it is the most expensive thing Findra does - on a machine with no usable
/// accelerator an hour of audio is a long stretch of real time. So the length that is worth it
/// is the user's decision and not a constant in the code (spec §6).</para>
///
/// <para><b>One number, covering audio and video together.</b> An asymmetry between them would be
/// invisible in the interface and surprising in use. Zero is off, a negative value is no limit,
/// and any positive number is the limit itself; the named choices are PRESETS OVER THAT NUMBER,
/// so a typed value and a preset are the same setting and cannot disagree. A second field
/// holding the preset name is exactly how they would.</para>
/// </summary>
public static class TranscribeLimit
{
    public const int Off = 0;
    public const int NoLimit = -1;

    /// <summary>Voice memos, messages, clips and screen recordings - cheap on any machine, which
    /// is what a default has to be.</summary>
    public const int Default = 5;

    public static readonly IReadOnlyList<int> Presets = [Off, 5, 30, 120, NoLimit];

    /// <summary>Is this recording worth transcribing at the current setting?</summary>
    public static bool Covers(int minutes, double durationSeconds)
    {
        if (minutes == Off) return false;      // off means off, whatever the length
        if (minutes < 0) return true;          // ANY negative is no limit, not just -1
        return durationSeconds <= minutes * 60.0;   // exactly at the limit is inside it
    }

    /// <summary>The preset name for this number, or null when the user typed something of their
    /// own. Derived from the number - there is nowhere else for a name to live.</summary>
    public static string? Named(int minutes) => minutes switch
    {
        Off => "Off",
        5 => "5 minutes",
        30 => "30 minutes",
        120 => "2 hours",
        < 0 => "No limit",
        _ => null,
    };

    /// <summary>Always readable: the preset's name, or the number in minutes.</summary>
    public static string Describe(int minutes)
        => Named(minutes) ?? $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes";

    /// <summary>
    /// The same setting, shortened to what a pill holds. The long form is what
    /// <see cref="Describe"/> prints beside the row and on the command line; this is what goes
    /// inside the control, wherever five choices share a row.
    ///
    /// <para>Measured in the shipped face at <c>Parts.LabelSize</c>: "Off" 19.1px, "5 min" 32.8,
    /// "30 min" 40.2, "2 hr" 23.3, "No limit" 45.6, against a pill that holds 62.8px in the
    /// settings window and 64px on the first-run screen. <see cref="Describe"/>'s own
    /// "30 minutes" is 65.3px and fits neither, which is why this exists rather than either
    /// column being re-narrowed. "2 hours" (44.5px) would fit on its own; it is short here so
    /// the five read as one register rather than four abbreviations and a spelled-out one.</para>
    ///
    /// <para>One list, two surfaces. The settings window and the first-run screen offer the same
    /// five choices, and a second table of names is how they come to disagree.</para>
    /// </summary>
    public static string ShortName(int minutes) => minutes switch
    {
        Off => "Off",
        5 => "5 min",
        30 => "30 min",
        120 => "2 hr",
        < 0 => "No limit",
        _ => minutes.ToString(CultureInfo.InvariantCulture) + " min",
    };

    /// <summary>A preset name or a bare number of minutes. Null for anything else - zero is a
    /// real setting, so falling back to it would turn speech off for somebody who mistyped.</summary>
    public static int? Parse(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        string w = word.Trim();
        foreach (int m in Presets)
            if (string.Equals(Named(m)!.Replace(" ", ""), w.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                return m;
        return int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
    }
}
