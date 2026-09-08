using Findra;

using Xunit;

/// <summary>
/// The journal tail's log line, collapsed into a summary.
///
/// <para>It used to write one line per poll, which is one line per second for as long as the
/// machine is on. Measured on a real install: 50,248 lines in 14 hours, 83% of them reporting that
/// nothing had been applied, 99.5% of everything in the log, about 9 MB a day and growing for as
/// long as Findra is running. The log stopped being readable - finding the four real warnings on
/// that machine meant filtering out fifty thousand lines first - and a log nobody can read is the
/// same as no log at all, on exactly the day somebody needs it.</para>
///
/// <para>What a reader wants from the journal is how much went past and how much of it mattered.
/// That is a sum, not a sample, which is why this holds totals rather than using
/// <c>Log.Repeat</c>: repeating one poll's numbers with "412 more since the last line" would print
/// a real number that describes 1/412th of what happened.</para>
/// </summary>
public class JournalDigestTests
{
    private static readonly DateTime T0 = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheFirstActivityOnAVolumeIsReportedAtOnce()
    {
        // A reader watching a fresh start has to see the tail pick the drive up. Holding the very
        // first line for five minutes would read as a tail that never started.
        var d = new JournalDigest();
        string? line = d.Add('C', records: 12, applied: 5, usn: 900, now: T0);
        Assert.NotNull(line);
        Assert.Contains("C:", line, StringComparison.Ordinal);
        Assert.Contains("12", line, StringComparison.Ordinal);
        Assert.Contains("900", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingMoreIsSaidUntilTheIntervalHasPassed()
    {
        var d = new JournalDigest();
        Assert.NotNull(d.Add('C', 12, 5, 900, T0));
        for (int i = 1; i <= 60; i++)
            Assert.Null(d.Add('C', 10, 2, 900 + i, T0.AddSeconds(i)));
    }

    [Fact]
    public void WhatIsSaidNextIsTheSumOfEverythingHeldBack()
    {
        // The property that makes this a digest rather than a throttle. 300 polls of 10 records
        // are 3,000 records, and a line saying 10 would be true of one poll and wrong about the
        // interval it claims to describe.
        var d = new JournalDigest();
        d.Add('C', 12, 5, 900, T0);
        for (int i = 1; i <= 100; i++) d.Add('C', 10, 2, 900 + i, T0.AddSeconds(i));

        string? line = d.Add('C', 10, 2, 1100, T0 + JournalDigest.Every + TimeSpan.FromSeconds(1));
        Assert.NotNull(line);
        Assert.Contains("1010", line, StringComparison.Ordinal);   // 101 polls of 10
        Assert.Contains("202", line, StringComparison.Ordinal);    // 101 polls of 2 applied
        Assert.Contains("1100", line, StringComparison.Ordinal);   // the usn it has reached
    }

    [Fact]
    public void TheTotalsResetAfterEachLine()
    {
        var d = new JournalDigest();
        d.Add('C', 12, 5, 900, T0);
        for (int i = 1; i <= 10; i++) d.Add('C', 10, 2, 900 + i, T0.AddSeconds(i));
        DateTime t1 = T0 + JournalDigest.Every + TimeSpan.FromSeconds(1);
        d.Add('C', 10, 2, 1000, t1);

        // The next window carries nothing over from the one that was just reported.
        string? line = d.Add('C', 7, 1, 1007, t1 + JournalDigest.Every + TimeSpan.FromSeconds(1));
        Assert.NotNull(line);
        Assert.Contains(" 7 ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuietIntervalSaysNothingAtAll()
    {
        // A poll that saw no records is not news, and a heartbeat that only ever says "nothing
        // happened" is what this whole change exists to remove. Whether the tail is alive is a
        // question --searchindex answers, with the position it has reached.
        var d = new JournalDigest();
        Assert.Null(d.Add('C', 0, 0, 900, T0));
        Assert.Null(d.Add('C', 0, 0, 900, T0 + JournalDigest.Every + TimeSpan.FromSeconds(1)));
        Assert.Null(d.Add('C', 0, 0, 900, T0 + JournalDigest.Every + JournalDigest.Every));
    }

    [Fact]
    public void EachDriveKeepsItsOwnCount()
    {
        // Two volumes are two tails on two schedules. Sharing a window would let a busy drive
        // silence a quiet one, and print one drive's totals under the other's letter.
        var d = new JournalDigest();
        Assert.NotNull(d.Add('C', 10, 1, 100, T0));
        string? dLine = d.Add('D', 20, 2, 200, T0.AddSeconds(1));
        Assert.NotNull(dLine);
        Assert.Contains("D:", dLine, StringComparison.Ordinal);
        Assert.Contains("20", dLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ThisIsFourOrdersOfMagnitudeFewerLines()
    {
        // The measurement that prompted it, as a test. A day of one poll a second on one volume,
        // every poll carrying work.
        var d = new JournalDigest();
        int lines = 0;
        for (int i = 0; i < 86_400; i++)
            if (d.Add('C', 10, 2, 1000 + i, T0.AddSeconds(i)) is not null) lines++;

        Assert.True(lines <= 24 * 60 / 5 + 1, $"a day still wrote {lines} lines");
        Assert.True(lines > 0, "a day of constant activity has to say something");
    }
}
