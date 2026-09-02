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
