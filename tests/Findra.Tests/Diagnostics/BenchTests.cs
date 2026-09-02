using System.Globalization;
using Findra;
using Findra.Diagnostics;
using Xunit;

[Collection("culture")]
public class BenchTests
{
    private static readonly MachineInfo Box = new(
        "AMD Ryzen 9 9900X3D 12-Core Processor", "X64", 51_539_607_552L,
        "NVMe SSD", "Windows 11 Pro 10.0.26200.1234", "ONNX: DirectML · Whisper: CPU");

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
        // Deliberately three different sample counts - names 4, lease 5, invoice 6. A single
        // shared count lets a table that lost its own "n=" pass on another table's marker, which
        // is the hole EveryLatencyNumberCarriesItsUnitAndItsSampleSize used to have.
        Fts:
        [
            new("lease", [1.0, 2.0, 3.0, 4.0, 4.5], [], 12),
            new("invoice", [2.0, 2.5, 3.5, 9.0, 9.5, 10.0], [], 0),
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
        Assert.Contains("ONNX: DirectML · Whisper: CPU", md);
        Assert.Contains("0.4.0", md);
    }

    [Fact]
    public void TheAcceleratorLineNamesBothRuntimesAndWhatEachOneGot()
    {
        // A throughput number without the silicon beside it is meaningless (spec §6), and
        // "which silicon" is now two answers, because the two runtimes choose separately: a
        // machine can run DirectML for the vision tower and fall back to the CPU for whisper.
        string line = Machine.AcceleratorLine(onnx: "DirectML", whisper: "CPU");

        Assert.Contains("ONNX", line, StringComparison.Ordinal);
        Assert.Contains("DirectML", line, StringComparison.Ordinal);
        Assert.Contains("Whisper", line, StringComparison.Ordinal);
        Assert.Contains("CPU", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AMachineWithNoModelsSaysSoRatherThanClaimingACpuFallback()
    {
        // "CPU" would be a measurement of something that never ran. Not loaded is the truth.
        string line = Machine.AcceleratorLine(onnx: null, whisper: null);
        Assert.Contains("not loaded", line, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectML", line, StringComparison.Ordinal);
    }

    [Fact]
    public void OneRuntimeWithAModelAndOneWithoutAreTwoDifferentAnswersOnOneLine()
    {
        // The state of this machine while Plan 5 was written: the picture models fetched, the
        // speech models not. A line that collapsed to one word would have to be wrong about one
        // of them, and the wrong one is the one somebody reads off a product page.
        string line = Machine.AcceleratorLine(onnx: "DirectML", whisper: null);

        Assert.Contains("DirectML", line, StringComparison.Ordinal);
        Assert.Contains("not loaded", line, StringComparison.Ordinal);
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

    /// <summary>One "## ..." section of the fragment, header line included, up to the next one.
    /// Asserting inside a section is what makes a claim about THAT table rather than about the
    /// document.</summary>
    private static string Section(string md, string heading)
    {
        string[] parts = md.Split("### " + heading + "\n");
        Assert.Equal(2, parts.Length);
        return parts[1].Split("\n### ")[0];
    }

    [Fact]
    public void EveryLatencyNumberCarriesItsUnitAndItsSampleSize()
    {
        // Scoped per TABLE, and with a different sample count in each. Asserting "n=4 appears
        // somewhere in the fragment" cannot fail while any table still carries a marker: drop
        // "n=" from the name table and the full-text table's own marker keeps the test green,
        // which is the opposite of what it claims to prove.
        string md = Bench.Fragment(Sample(names: SomeNames()));

        string names = Section(md, "Name query latency");
        Assert.Contains("p50", names, StringComparison.Ordinal);
        Assert.Contains("p95", names, StringComparison.Ordinal);
        Assert.Contains(" ms ", names, StringComparison.Ordinal);
        Assert.Contains("n=4", names, StringComparison.Ordinal);

        string fts = Section(md, "Full-text query latency");
        Assert.Contains("p50", fts, StringComparison.Ordinal);
        Assert.Contains("p95", fts, StringComparison.Ordinal);
        Assert.Contains(" ms ", fts, StringComparison.Ordinal);
        Assert.Contains("n=5", fts, StringComparison.Ordinal);   // lease
        Assert.Contains("n=6", fts, StringComparison.Ordinal);   // invoice
    }

    [Fact]
    public void TheFragmentCarriesNoTopLevelHeadingSoItPastesUnderTheReadmesOwn()
    {
        // The whole promise of this mode is "paste it in, edit nothing". A README already has one
        // level-one heading; a fragment that opens with a second has to be demoted by hand first,
        // every time, and a fragment that needs hand-editing is not a reproducible number.
        string md = Bench.Fragment(Sample(names: SomeNames()));

        Assert.StartsWith("## Findra benchmark", md, StringComparison.Ordinal);
        foreach (string line in md.Split('\n'))
            Assert.False(line.StartsWith("# ", StringComparison.Ordinal),
                         $"a top-level heading collides with the README's own: '{line}'");
    }

    [Fact]
    public void ARunTooShortToMeasureWithholdsItsRateAndSaysWhy()
    {
        // 190 ms of extraction is what two runs sixteen percent apart look like. Publishing
        // files/min off it puts a number on a product page that the next run will not reproduce,
        // so no rate is printed at all and the reader is told how to get one.
        BenchResult r = Sample(names: SomeNames()) with
        {
            Extraction = [new ThroughputRow(ResultKind.Document, 220, 0.19, 1_638_400)],
        };

        string md = Bench.Fragment(r);
        string section = Section(md, "Document extraction");

        Assert.Contains(Bench.Short, section, StringComparison.Ordinal);
        Assert.DoesNotContain("69,474", section, StringComparison.Ordinal);   // 220 files / 0.19 s
        Assert.Contains("0.19", section, StringComparison.Ordinal);           // the run is still reported
        Assert.Contains("--searchbench", section, StringComparison.Ordinal);  // and how to fix it
    }

    [Fact]
    public void ARunLongEnoughToMeasurePublishesItsRateWithNoCaveat()
    {
        string section = Section(Bench.Fragment(Sample(names: SomeNames())), "Document extraction");

        Assert.Contains("3,000", section, StringComparison.Ordinal);          // 200 files in 4.0 s
        Assert.DoesNotContain(Bench.Short, section, StringComparison.Ordinal);
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

    [Fact]
    public void AMachineWithNoIndexSaysSoRatherThanPrintingAnEmptyTable()
    {
        // Now that the benchmark refuses to create an index just for measuring one, there is a
        // real run in which no store exists. A markdown table with a header row and nothing under
        // it is what that used to render as, on a page that is pasted onto a README.
        string md = Bench.Fragment(Sample(names: SomeNames()) with { Stores = [] });
        string section = Section(md, "Stores");

        Assert.DoesNotContain("| Store | Path | Size |", section, StringComparison.Ordinal);
        Assert.Contains("no content index", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBenchmarkOpensTheRealIndexReadOnlyAndCanNeverRebuildOrCreateOne()
    {
        // A BENCHMARK. Everything it does with this connection is a read - MeasureFts, Stats,
        // Stores - and it was opening the user's index through OpenOrRebuild, which moves an
        // unreadable index aside and builds a fresh one, and which runs CreateSchema and
        // OpenSchema for any writable open. Once Migrations is non-empty a benchmark run would
        // silently migrate someone's index and re-queue their files, and nothing in the fragment
        // it prints would mention that it had happened.
        string dir = Path.Combine(Path.GetTempPath(), "findra-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string missing = Path.Combine(dir, "none.db");
            Assert.Null(SearchBench.OpenIndex(missing));
            Assert.False(File.Exists(missing), "measuring an index must not bring one into existence");

            string broken = Path.Combine(dir, "broken.db");
            File.WriteAllText(broken, "this is not a database, it is a text file");
            Assert.Null(SearchBench.OpenIndex(broken));
            Assert.False(File.Exists(broken + ".corrupt"), "a benchmark must not move a user's index aside");
            Assert.Equal("this is not a database, it is a text file", File.ReadAllText(broken));

            string good = Path.Combine(dir, "search.db");
            using (var real = new ContentDb(good)) real.Enqueue("C", 1, @"C:\a.pdf", ResultKind.Document, "new");

            using ContentDb? open = SearchBench.OpenIndex(good);
            Assert.NotNull(open);
            Assert.Equal(1, open.PendingCount());                       // it can read
            Assert.ThrowsAny<Exception>(() => open.Set("index:rebuilt", "1"));   // and only read
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
