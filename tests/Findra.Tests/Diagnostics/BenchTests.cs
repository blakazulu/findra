using System.Globalization;
using Findra;
using Findra.Diagnostics;
using Xunit;

[Collection("culture")]
public class BenchTests
{
    private static readonly MachineInfo Box = new(
        "AMD Ryzen 9 9900X3D 12-Core Processor", "X64", 51_539_607_552L,
        "NVMe SSD", "Windows 11 Pro 10.0.26200.1234", "CPU only - this build runs no models");

    private static IReadOnlyList<VolumeRow> Vols() =>
    [
        new('C', 1_482_913, 96_468_992, 1_840.0, 90_210),
        new('D', 204_881, 12_845_056, 260.0, 4_242),
    ];

    private static IReadOnlyList<LatencySet> SomeNames() =>
    [
        new("report", RoundTripMs: [0.4, 0.8, 1.2, 5.0], ScanMs: [0.1, 0.2, 0.3, 0.9], Hits: 37),
    ];

    private static BenchResult Sample(
        IReadOnlyList<LatencySet>? names = null,
        string? unavailable = null,
        IReadOnlyList<VolumeRow>? volumes = null) => new(
        Machine: Box, Version: "0.4.0",
        Volumes: volumes ?? Vols(),
        Names: names, NamesUnavailable: unavailable,
        Fts:
        [
            new("lease", [1.0, 2.0, 3.0, 4.0], [], 12),
            new("invoice", [2.0, 2.5, 3.5, 9.0], [], 0),
        ],
        Extraction: [new ThroughputRow(ResultKind.Document, 200, 4.0, 1_638_400)],
        CorpusNote: "200 generated .txt of 8 KB and 20 generated .docx of 12 KB",
        Stores:
        [
            new StoreRow("search.db", @"%LOCALAPPDATA%\Findra\index\search.db", 12_582_912),
            new StoreRow("search.db-wal", @"%LOCALAPPDATA%\Findra\index\search.db-wal", 4_194_304),
        ],
        IndexedItems: 3400, Segments: 21_000);

    // ---- percentiles ----

    [Theory]
    [InlineData(0.5, 50)]      // nearest-rank, not an average of 50 and 51
    [InlineData(0.95, 95)]
    [InlineData(1.0, 100)]
    public void PercentilesAreNearestRank(double p, double expected)
    {
        double[] s = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Assert.Equal(expected, Bench.Percentile(s, p));
    }

    [Fact]
    public void PercentilesDoNotNeedTheCallerToSortFirst()
    {
        Assert.Equal(3, Bench.Percentile([9.0, 1.0, 3.0, 2.0, 20.0], 0.5));
        // The line above cannot actually fail: nearest rank on this set lands on index 2, which
        // holds 3 sorted OR unsorted, so a helper that never sorts passes it. The quartile does
        // fail - sorted it is 2, and straight off the caller's order it is 1 - which is what
        // makes this test a proof rather than a coincidence.
        Assert.Equal(2, Bench.Percentile([9.0, 1.0, 3.0, 2.0, 20.0], 0.25));
    }

    [Fact]
    public void OneSampleIsItsOwnEveryPercentile()
    {
        Assert.Equal(7, Bench.Percentile([7.0], 0.5));
        Assert.Equal(7, Bench.Percentile([7.0], 0.95));
    }

    [Fact]
    public void NoSamplesIsNotAZero()
    {
        // Zero milliseconds is a claim. "We did not measure" is a different one, and the
        // README must never be handed the first when the second is true.
        Assert.True(double.IsNaN(Bench.Percentile([], 0.5)));
    }

    // ---- the fragment ----

    [Fact]
    public void TheMachineBlockNamesEveryPartOfTheMachine()
    {
        string md = Bench.Fragment(Sample(names: SomeNames()));

        Assert.Contains("Ryzen 9 9900X3D", md);
        Assert.Contains("X64", md);                // never assumed - printed
        Assert.Contains("48.0 GB", md);            // 51539607552 bytes
        Assert.Contains("NVMe SSD", md);
        Assert.Contains("10.0.26200.1234", md);
        Assert.Contains("Accelerator", md);
        Assert.Contains("CPU only - this build runs no models", md);
        Assert.Contains("0.4.0", md);
    }

    [Fact]
    public void TheAcceleratorLineCarriesNoPlanNumbersOrOtherInternalLanguage()
    {
        // This fragment is pasted verbatim onto a product page (§9a). "none (Plan 5 adds
        // detection)" means nothing to a reader and dates the README the moment Plan 5 ships.
        string md = Bench.Fragment(Sample(names: SomeNames()));

        Assert.DoesNotContain("Plan ", md, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO", md, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryVolumeReportsItsEnumerationTimeNameCountAndResidentBytes()
    {
        // Spec §9 asks for "cold-start MFT enumeration time and the resulting name count, per
        // volume" and "resident bytes of the name index". Without them the README cannot make
        // the claim Findra is actually built on - that names arrive in seconds.
        string md = Bench.Fragment(Sample(names: SomeNames()));

        Assert.Contains("1,482,913", md);      // C: name count
        Assert.Contains("204,881", md);        // D: name count
        Assert.Contains("1,840", md);          // C: enumeration ms
        Assert.Contains("92.0 MB", md);        // C: resident bytes of the name index
    }

    [Fact]
    public void NameLatencySeparatesThePipeFromTheScan()
    {
        // Spec §9: "separating the pipe round trip from the index scan". One combined number
        // cannot tell a slow helper from a slow transport, which is the only actionable thing
        // a latency figure can say.
        string md = Bench.Fragment(Sample(names: SomeNames()));

        Assert.Contains("scan", md, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pipe", md, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryLatencyNumberCarriesItsUnitAndItsSampleSize()
    {
        string md = Bench.Fragment(Sample(names: SomeNames()));

        Assert.Contains("p50", md);
        Assert.Contains("p95", md);
        Assert.Contains("ms", md);
        Assert.Contains("n=4", md);
    }

    [Fact]
    public void AQuerySetThatMatchedNothingSaysSoRatherThanLookingFast()
    {
        // "invoice" has 0 hits in the sample. An empty index answers every query in under a
        // millisecond, and a latency table with no hit column turns that into a boast.
        string md = Bench.Fragment(Sample(names: SomeNames()));

        Assert.Contains("hits", md, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"invoice[^\n]*\|\s*0\s*\|", md);
    }

    [Fact]
    public void WithNoHelperTheNameTablesAreReplacedBySentencesAndNoNumbers()
    {
        // A table of zeros is worse than no table: it reads as a measurement of a fast
        // machine. The absence has to be visible in the pasted fragment itself, and it takes
        // the per-volume block with it - those numbers come from the same StatusReply.
        string md = Bench.Fragment(Sample(names: null, unavailable: "the name helper is not running", volumes: null));

        Assert.Contains("the name helper is not running", md);
        Assert.DoesNotContain("p50", md.Split("## Full-text")[0]);
        Assert.DoesNotContain("1,482,913", md);
    }

    [Fact]
    public void ThroughputIsGivenPerMinuteAndPerSecondWithItsCorpusNamed()
    {
        string md = Bench.Fragment(Sample(names: SomeNames()));

        Assert.Contains("files/min", md);          // spec §9 asks for files/minute, by kind
        Assert.Contains("MB/s", md);
        Assert.Contains("3,000", md);              // 200 files in 4.0 s = 3000 files/min
        Assert.Contains("200 generated .txt of 8 KB", md);
        Assert.Contains("Doc", md);                // by kind
    }

    [Fact]
    public void EveryStoreIsSizedIndividuallyNotJustTheDatabase()
    {
        // Spec §9: "the on-disk size of EACH store". The write-ahead log is real disk the
        // user paid for, and mid-index it is routinely larger than search.db itself.
        string md = Bench.Fragment(Sample(names: SomeNames()));

        Assert.Contains("search.db", md);
        Assert.Contains("search.db-wal", md);
        Assert.Contains("12.0 MB", md);
        Assert.Contains("4.0 MB", md);
        Assert.Contains("3,400", md);
        Assert.Contains("21,000", md);
    }

    [Fact]
    public void EveryTableIsValidMarkdownWithAConsistentColumnCount()
    {
        // The whole contract of this mode is "pasteable without editing". A table whose rows
        // disagree with its header renders as literal pipes on the README page.
        string md = Bench.Fragment(Sample(names: SomeNames()));

        string[] lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        int i = 0, tables = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith('|')) { i++; continue; }
            int header = lines[i].Count(c => c == '|');
            int rows = 0;
            while (i < lines.Length && lines[i].StartsWith('|'))
            {
                Assert.Equal(header, lines[i].Count(c => c == '|'));
                i++; rows++;
            }
            Assert.True(rows >= 3, "a table needs a header, a separator and at least one row");
            tables++;
        }
        Assert.True(tables >= 5, $"expected machine, volumes, names, FTS, throughput and stores; found {tables}");
    }

    [Fact]
    public void NoLineIsIndentedIntoACodeBlock()
    {
        // Four leading spaces or a tab turns a Markdown line into a code block, which is how
        // a pasted fragment silently stops being a table on the README.
        string md = Bench.Fragment(Sample(names: SomeNames()));

        foreach (string line in md.Split('\n'))
            Assert.False(line.StartsWith('\t') || line.StartsWith("    "), $"indented: '{line}'");
    }

    [Fact]
    public void TheFragmentReadsTheSameOnEveryMachine()
    {
        // InvariantGlobalization is false in this project, so {n:N0} renders "3.000" in
        // German and "48,0 GB" in Hebrew. This fragment is a published artefact.
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string md = Bench.Fragment(Sample(names: SomeNames()));

            Assert.Contains("48.0 GB", md);
            Assert.Contains("3,000", md);
            Assert.Contains("1,482,913", md);
            Assert.DoesNotContain("48,0 GB", md);
            Assert.DoesNotContain("1.482.913", md);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }
}
