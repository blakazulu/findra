using System;
using System.Globalization;

namespace Findra;

/// <summary>
/// One byte-to-words formatter, because every surface that shows a model size must agree with
/// every other one and with the README. Invariant always: this text is compared in tests, pasted
/// into a product page, and read on machines set to any locale.
/// </summary>
public static class Sizes
{
    private const long Mb = 1024L * 1024L;
    private const long Gb = Mb * 1024L;

    /// <summary>Whole megabytes below a gigabyte, two decimals above it with trailing zeros
    /// trimmed. The two-decimal form is not decoration: spec §6 says a two-decimal gigabyte is the number for
    /// the README", and one decimal would print 2.9, which is the conservative floor's total
    /// rather than the measured one.</summary>
    public static string Human(long bytes)
    {
        if (bytes < Gb)
            return Math.Round(bytes / (double)Mb, 0, MidpointRounding.AwayFromZero)
                       .ToString("0", CultureInfo.InvariantCulture) + " MB";
        return Math.Round(bytes / (double)Gb, 2, MidpointRounding.AwayFromZero)
                   .ToString("0.##", CultureInfo.InvariantCulture) + " GB";
    }
}
