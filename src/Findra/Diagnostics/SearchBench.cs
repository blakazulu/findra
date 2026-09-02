using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Findra.Pipe;

namespace Findra.Diagnostics;

/// <summary>
/// One query, measured twice. Spec §9 asks for name latency "separating the pipe round trip from
/// the index scan", and the two sets are what makes that separation possible after the fact:
/// <c>ScanMs</c> is the helper's own timing of the scan, carried back on
/// <see cref="QueryReply.ElapsedTicks"/>, and the difference from the round trip is what the pipe
/// itself cost. One combined figure cannot tell a slow helper from a slow transport, which is the
/// only actionable thing a latency number ever says.
///
/// <para><c>Hits</c> is not decoration. An empty index answers every query in well under a
/// millisecond, and a latency table without a hit column publishes that as speed.</para>
/// </summary>
public sealed record LatencySet(string Name, IReadOnlyList<double> RoundTripMs,
                                IReadOnlyList<double> ScanMs, int Hits);

/// <summary>One volume as the helper measured it: spec §9's "cold-start MFT enumeration time and
/// the resulting name count, per volume" and "resident bytes of the name index", plus the journal
/// position that walk was taken against.</summary>
public sealed record VolumeRow(char Letter, int Names, long ResidentBytes, double EnumerateMs, long NextUsn);

/// <summary>One file of the index, sized on its own. Spec §9 says "the on-disk size of each
/// store", and the write-ahead log is real disk the user paid for - mid-index it is routinely
/// larger than the database beside it.</summary>
public sealed record StoreRow(string Name, string Path, long Bytes);

/// <summary>Extraction throughput for one kind of file. Both rates are derived from these three
/// numbers, so a reader can check the arithmetic without re-running anything.</summary>
public sealed record ThroughputRow(ResultKind Kind, int Files, double Seconds, long Bytes);

/// <summary>
/// Everything one <c>--searchbench</c> run measured. <see cref="Bench.Fragment"/> is a pure
/// function of this, which is what lets the published shape be tested with no disk, no pipe and
/// no timing at all.
///
/// <para><c>Volumes</c> and <c>Names</c> are nullable together: both come out of the same
/// <see cref="StatusReply"/>, so when the helper is unreachable neither exists and
/// <c>NamesUnavailable</c> says why in the published text. A table of zeros would be worse than
/// no table - it reads as a measurement of a very fast machine.</para>
/// </summary>
public sealed record BenchResult(MachineInfo Machine, string Version,
                                 IReadOnlyList<VolumeRow>? Volumes,
                                 IReadOnlyList<LatencySet>? Names, string? NamesUnavailable,
                                 IReadOnlyList<LatencySet> Fts,
                                 IReadOnlyList<ThroughputRow> Extraction, string CorpusNote,
                                 IReadOnlyList<StoreRow> Stores, long IndexedItems, long Segments);

/// <summary>
/// The published half of <c>--searchbench</c>: a percentile that does not lie, and the Markdown
/// fragment the README quotes verbatim (spec §9a).
///
/// <para>Both are pure. Nothing here opens a file, a pipe or a stopwatch, so every rule about the
/// fragment - its columns, its units, its sample sizes, its behaviour with no helper - is testable
/// on a fixed snapshot.</para>
///
/// <para><b>Every number goes through <see cref="CultureInfo.InvariantCulture"/>.</b> The project
/// sets <c>InvariantGlobalization=false</c>, so a bare <c>{n:N0}</c> renders <c>1.482.913</c> on a
/// German machine and <c>48,0 GB</c> on a Hebrew one, and this text is pasted onto a product page
/// from whichever machine happened to run it.</para>
/// </summary>
public static class Bench
{
    private static readonly CultureInfo Fixed = CultureInfo.InvariantCulture;

    /// <summary>
    /// Nearest rank, on a copy it sorts itself. Nearest rank rather than an interpolated
    /// percentile because every value it returns is a latency that was actually observed: a p50 of
    /// 50.5 ms over integer samples is a number no query ever took.
    ///
    /// <para>Sorting inside is deliberate - a percentile helper that silently requires sorted
    /// input is a defect waiting for its second caller. NaN rather than 0 for an empty set, for
    /// the same reason: zero milliseconds is a claim, and "we did not measure" is a different one.</para>
    /// </summary>
    public static double Percentile(IReadOnlyList<double> samples, double p)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) return double.NaN;
        double[] s = [.. samples];
        Array.Sort(s);
        int rank = Math.Clamp((int)Math.Ceiling(p * s.Length), 1, s.Length);
        return s[rank - 1];
    }

    /// <summary>
    /// How long the extraction run has to have taken before its rates are published.
    ///
    /// <para>Two runs of the same default corpus came back sixteen percent apart because the whole
    /// thing drained in under two tenths of a second: at that scale the figure is measuring one
    /// scheduler hiccup, not the machine. A files-per-minute extrapolated from a fraction of a
    /// second is noise wearing a unit, and this fragment is the only source the README is allowed
    /// to quote. Under this, the rate cells say so and the section says how to get a real
    /// one.</para>
    /// </summary>
    public const double MinThroughputSeconds = 1.0;

    /// <summary>The Markdown fragment, ready to paste into the README with no editing at all: six
    /// sections, every one a table or a plain sentence, nothing indented, and the machine first
    /// because a number without its machine is marketing rather than measurement (spec §9).
    ///
    /// <para>Its own heading is a LEVEL TWO. The README it is pasted into already has a level one
    /// of its own, and a fragment that opens with a second `#` has to be hand-demoted before it
    /// can be used - which breaks the one promise this mode makes.</para></summary>
    public static string Fragment(BenchResult r)
    {
        ArgumentNullException.ThrowIfNull(r);

        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');

        Line("## Findra benchmark");
        Line();
        Line("Produced by `findra --searchbench`. Every number below was measured on the machine");
        Line("named here, by this build, and re-running that command reproduces the whole page.");
        Line();

        // ---- 1. the machine ----------------------------------------------------------------
        Line("### Machine");
        Line();
        Line("| Part | Value |");
        Line("|---|---|");
        Line($"| CPU | {Cell(r.Machine.Cpu)} |");
        Line($"| Architecture | {Cell(r.Machine.Architecture)} |");
        // 0 bytes is not a small machine, it is a failed lookup, and the two must not print alike.
        Line($"| RAM | {(r.Machine.RamBytes > 0 ? Bytes(r.Machine.RamBytes) : Machine.Unknown)} |");
        Line($"| Disk | {Cell(r.Machine.Disk)} |");
        Line($"| Windows | {Cell(r.Machine.Windows)} |");
        Line($"| Accelerator | {Cell(r.Machine.Accelerator)} |");
        Line($"| Findra | {Cell(r.Version)} |");
        Line();

        // Both of the next two sections are fed by the SAME StatusReply, so one reason covers
        // both: if the helper could not be asked, neither block has a data source and neither may
        // print a number. Whatever Volumes happens to hold when NamesUnavailable is set was not
        // measured on this run, and publishing it would date-stamp stale numbers as fresh ones.
        bool helper = r.NamesUnavailable is null;

        // ---- 2. the volumes ----------------------------------------------------------------
        Line("### Volumes");
        Line();
        if (!helper || r.Volumes is null || r.Volumes.Count == 0)
        {
            Line(NotMeasured(r.NamesUnavailable));
        }
        else
        {
            Line("| Volume | Names | Name index resident | Cold-start enumeration | Journal position |");
            Line("|---|---|---|---|---|");
            foreach (VolumeRow v in r.Volumes)
                Line($"| {v.Letter}: | {N(v.Names)} | {Bytes(v.ResidentBytes)} | " +
                     $"{(v.EnumerateMs > 0 ? N((long)Math.Round(v.EnumerateMs)) + " ms" : "not measured")} | " +
                     $"{(v.NextUsn > 0 ? N(v.NextUsn) : "not measured")} |");
        }
        Line();

        // ---- 3. name latency ---------------------------------------------------------------
        Line("### Name query latency");
        Line();
        if (!helper || r.Names is null || r.Names.Count == 0)
        {
            Line(NotMeasured(r.NamesUnavailable));
        }
        else
        {
            Line("| Query | Round trip p50 | Round trip p95 | Index scan p50 | Pipe share p50 | Worst | Hits | Samples |");
            Line("|---|---|---|---|---|---|---|---|");
            foreach (LatencySet s in r.Names)
            {
                double rt50 = Percentile(s.RoundTripMs, 0.5);
                double scan50 = Percentile(s.ScanMs, 0.5);
                Line($"| {Cell(s.Name)} | {Ms(rt50)} | {Ms(Percentile(s.RoundTripMs, 0.95))} | " +
                     $"{Ms(scan50)} | {Ms(rt50 - scan50)} | {Ms(Percentile(s.RoundTripMs, 1.0))} | " +
                     $"{N(s.Hits)} | n={N(s.RoundTripMs.Count)} |");
            }
        }
        Line();

        // ---- 4. full-text latency ----------------------------------------------------------
        // No scan column here, and that is not an omission: this query runs in this process
        // against a local file, so the round trip IS the scan and a second column would be the
        // same number printed twice.
        Line("### Full-text query latency");
        Line();
        Line("| Query | p50 | p95 | Worst | Hits | Samples |");
        Line("|---|---|---|---|---|---|");
        foreach (LatencySet s in r.Fts)
            Line($"| {Cell(s.Name)} | {Ms(Percentile(s.RoundTripMs, 0.5))} | " +
                 $"{Ms(Percentile(s.RoundTripMs, 0.95))} | {Ms(Percentile(s.RoundTripMs, 1.0))} | " +
                 $"{N(s.Hits)} | n={N(s.RoundTripMs.Count)} |");
        Line();

        // ---- 5. extraction throughput ------------------------------------------------------
        Line("### Document extraction");
        Line();
        Line("| Kind | Files | Seconds | files/min | MB/s |");
        Line("|---|---|---|---|---|");
        bool tooShort = false;
        foreach (ThroughputRow t in r.Extraction)
        {
            // files/minute is what spec §9 asks for; the per-second byte rate is what makes two
            // machines comparable, because "files" is whatever size the corpus happened to be.
            //
            // Both are WITHHELD below the floor rather than printed with a caveat. A rate in a
            // table is quoted on its own, away from whatever sentence sat under it, and a number
            // this mode will not stand behind must not be available to quote at all.
            bool enough = t.Seconds >= MinThroughputSeconds;
            if (!enough) tooShort = true;
            string perMin = enough ? N((long)Math.Round(t.Files * 60.0 / t.Seconds)) : Short;
            string perSec = enough ? (t.Bytes / 1048576.0 / t.Seconds).ToString("0.00", Fixed) : Short;
            Line($"| {Cell(FileKinds.Label(t.Kind))} | {N(t.Files)} | {t.Seconds.ToString("0.00", Fixed)} | {perMin} | {perSec} |");
        }
        Line();
        if (tooShort)
        {
            Line($"A rate is published only for a run of at least {MinThroughputSeconds.ToString("0.0", Fixed)} s. " +
                 "One that finished faster is measuring a scheduler hiccup rather than the machine, and");
            Line("this page is quoted verbatim. Re-run with a larger corpus - `findra --searchbench out.md " +
                 $"{(SearchBench.DefaultCorpus * 4).ToString(Fixed)}` - to get one.");
            Line();
        }

        // ---- 6. the stores -----------------------------------------------------------------
        Line("### Stores");
        Line();
        Line("| Store | Path | Size |");
        Line("|---|---|---|");
        foreach (StoreRow s in r.Stores)
            Line($"| {Cell(s.Name)} | {Cell(s.Path)} | {Bytes(s.Bytes)} |");
        Line();
        Line($"Indexed items: {N(r.IndexedItems)}. Text segments: {N(r.Segments)}.");
        Line();
        Line($"Corpus for the extraction row: {Cell(r.CorpusNote)}.");

        return sb.ToString();
    }

    /// <summary>What a rate cell says when the run it came from was too short to publish one.
    /// Distinct from "not measured": the run happened, and what it produced is not fit to
    /// quote.</summary>
    public const string Short = "sample too short";

    private static string NotMeasured(string? why) =>
        "Not measured: " + (why is { Length: > 0 } ? Cell(why) : "the name helper is not running") + ".";

    private static string N(long v) => v.ToString("N0", Fixed);

    /// <summary>A latency, with its unit attached. NaN is the empty sample set, and it must never
    /// render as 0.00 ms - that is the difference between "fast" and "not run".</summary>
    private static string Ms(double v) => double.IsNaN(v) ? "not measured" : v.ToString("0.00", Fixed) + " ms";

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    private static string Bytes(long bytes)
    {
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < ByteUnits.Length - 1) { v /= 1024; i++; }
        string num = i == 0 ? v.ToString("0", Fixed) : v.ToString("0.0", Fixed);
        return $"{num} {ByteUnits[i]}";
    }

    /// <summary>A value on its way into a table cell. A newline would end the row and a pipe would
    /// invent a column, and either one turns the pasted fragment into literal pipes on the README
    /// page - so both are neutralised rather than escaped, because an escaped pipe is still a pipe
    /// to anything counting columns.</summary>
    private static string Cell(string s) =>
        s.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();
}

/// <summary>
/// <c>findra --searchbench [out.md] [corpus]</c>: measure it, and print numbers fit to publish
/// (spec §9). The output is the ONLY source the README is allowed to quote for a performance
/// claim, which is why it names the machine first and carries a unit and a sample size on every
/// figure.
///
/// <para>Needs no elevation, and says so when that costs it something: the name and volume halves
/// come from the elevated helper over the pipe, and when it is not running they are replaced by a
/// sentence rather than by a table of zeros.</para>
/// </summary>
public static class SearchBench
{
    /// <summary>Discarded, then counted. The first few runs pay for a cold SQLite page cache, a
    /// cold pipe and JIT, and publishing those as latency would describe a machine nobody has.</summary>
    private const int Warmups = 5, Runs = 50;

    /// <summary>
    /// How many .txt the generated corpus holds when the caller names no size, plus a tenth as
    /// many .docx.
    ///
    /// <para>Two hundred used to be the default and it drained in about two tenths of a second, so
    /// two runs on one idle machine reported rates sixteen percent apart - a figure that describes
    /// whatever else the scheduler was doing, not the machine. This is sized so an ordinary run
    /// clears <see cref="Bench.MinThroughputSeconds"/> with room to spare and publishes a number
    /// that reproduces; generating and deleting the files is the price, and a benchmark that takes
    /// a few seconds longer is not the problem a benchmark that lies is.</para>
    /// </summary>
    public const int DefaultCorpus = 2_500;

    private static readonly string[] NameQueries = ["report", "invoice", "sunset", "readme", "config"];
    private static readonly string[] FtsQueries = ["lease", "agreement", "invoice", "total", "report"];

    /// <summary>
    /// The user's real index, opened READ-ONLY, or null when there is nothing to measure.
    ///
    /// <para>A benchmark must not be able to change the thing it is measuring.
    /// <c>ContentDb.OpenOrRebuild</c> moves an index it cannot read aside and builds a fresh one,
    /// and any writable open runs <c>CreateSchema</c> and <c>OpenSchema</c> - so from the moment
    /// <c>ContentDb.Migrations</c> stops being empty, running this command would silently migrate
    /// someone's index and re-queue their files, and nothing in the fragment it prints would say
    /// that had happened. Everything this mode does with the connection is a read.</para>
    ///
    /// <para>The <c>File.Exists</c> guard matters as much as the mode: a read-only open of a
    /// missing file is an error, and a writable one would CREATE an index on a machine that has
    /// never run Findra, just for asking how fast it is. <c>--searchprobe</c> takes the same two
    /// steps for the same reason.</para>
    /// </summary>
    public static ContentDb? OpenIndex(string? path = null)
    {
        string p = path ?? ContentDb.DefaultPath;
        if (!File.Exists(p)) return null;
        try { return new ContentDb(p, readOnly: true); }
        catch (Exception ex)
        {
            // An index nobody can read is a fact about the machine, not a reason to rebuild it
            // from under someone in the middle of a measurement.
            Console.Error.WriteLine($"the content index could not be read ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? outPath = args.Length > 1 && args[1].Length > 0 ? args[1] : null;
        int corpus = DefaultCorpus;
        if (args.Length > 2 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            corpus = Math.Clamp(n, 1, 50_000);

        Console.Error.WriteLine("findra --searchbench: measuring, this takes a moment...");

        MachineInfo machine = Machine.Read();

        IReadOnlyList<VolumeRow>? volumes = null;
        IReadOnlyList<LatencySet>? names = null;
        string? unavailable = null;

        NameClient? client = null;
        try { client = await NameClient.ConnectAsync(TimeSpan.FromSeconds(5), default).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // A helper that is not there is a correct outcome from an ordinary terminal, not a
            // failure of this mode - the rest of the page is still worth measuring and printing.
            unavailable = "the name helper is not running, so nothing on this page was measured " +
                          "against it (" + Reason(ex) + ")";
        }

        if (client is not null)
        {
            try
            {
                StatusReply status = await client.StatusAsync(default).ConfigureAwait(false);
                volumes = [.. status.Volumes.Select(v =>
                    new VolumeRow(v.Letter, v.Count, v.BufferBytes, v.EnumerateMs, v.NextUsn))];

                var sets = new List<LatencySet>(NameQueries.Length);
                foreach (string q in NameQueries)
                    sets.Add(await MeasureNamesAsync(client, q).ConfigureAwait(false));
                names = sets;
            }
            catch (Exception ex)
            {
                // Half a measurement is not a measurement. If the helper went away partway, both
                // halves are dropped together - they come from the same connection.
                volumes = null;
                names = null;
                unavailable = "the name helper stopped answering partway through (" + Reason(ex) + ")";
            }
            finally { await client.DisposeAsync().ConfigureAwait(false); }
        }

        using ContentDb? db = OpenIndex();

        var fts = new List<LatencySet>(FtsQueries.Length);
        long items = 0, segments = 0;
        IReadOnlyList<StoreRow> stores = [];
        if (db is not null)
        {
            foreach (string q in FtsQueries) fts.Add(MeasureFts(db, q));
            (items, segments, _) = db.Stats();
            stores = Stores(db.Path);
        }
        else
        {
            // No readable index. An empty sample set renders "not measured" in every cell, which
            // is the truth; a zero would read as an unbelievably fast query against nothing.
            foreach (string q in FtsQueries) fts.Add(new LatencySet(q, [], [], 0));
        }

        (ThroughputRow row, string corpusNote) = MeasureExtraction(corpus);

        var result = new BenchResult(
            Machine: machine, Version: Log.Version,
            Volumes: volumes, Names: names, NamesUnavailable: unavailable,
            Fts: fts, Extraction: [row], CorpusNote: corpusNote,
            Stores: stores, IndexedItems: items, Segments: segments);

        string md = Bench.Fragment(result);
        Console.WriteLine(md);

        if (outPath is not null)
        {
            File.WriteAllText(outPath, md);
            Console.Error.WriteLine($"written to {Path.GetFullPath(outPath)}");
        }
        return 0;
    }

    /// <summary>An exception's own words, ready to sit inside a parenthesis in a published
    /// sentence. Its trailing full stop is dropped so the sentence keeps exactly one.</summary>
    private static string Reason(Exception ex) => ex.Message.Trim().TrimEnd('.');

    /// <summary>
    /// One name query, timed from both ends. The round trip is this process's stopwatch; the scan
    /// is the helper's own, carried back on the reply, so the difference is what the pipe cost.
    ///
    /// <para>A null reply is a generation the client discarded, which is not a measurement of
    /// anything and is left out of the sample rather than counted as a fast one.</para>
    /// </summary>
    private static async Task<LatencySet> MeasureNamesAsync(NameClient client, string query)
    {
        var round = new List<double>(Runs);
        var scan = new List<double>(Runs);
        int hits = 0;

        for (int i = 0; i < Warmups + Runs; i++)
        {
            long started = Stopwatch.GetTimestamp();
            QueryReply? reply = await client.SearchAsync(query, 50, default).ConfigureAwait(false);
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (reply is null || i < Warmups) continue;
            round.Add(ms);
            scan.Add(reply.ElapsedTicks * 1000.0 / Stopwatch.Frequency);
            hits = reply.Rows.Count;
        }
        return new LatencySet(query, round, scan, hits);
    }

    /// <summary>One full-text query against the LIVE index, through the same branch the card uses -
    /// grammar, dedupe and excerpt included. Measuring <c>ContentDb.Fts</c> alone would publish a
    /// number no keystroke can reproduce.</summary>
    private static LatencySet MeasureFts(ContentDb db, string query)
    {
        var round = new List<double>(Runs);
        int hits = 0;

        for (int i = 0; i < Warmups + Runs; i++)
        {
            long started = Stopwatch.GetTimestamp();
            SearchResults results = ContentBranch.Search(db, query, 50);
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (i < Warmups) continue;
            round.Add(ms);
            hits = results.Rows.Count;
        }
        return new LatencySet(query, round, [], hits);
    }

    /// <summary>
    /// Extraction throughput over a corpus this mode generates and then deletes.
    ///
    /// <para>It writes into a temp directory and queues into a THROWAWAY database beside it. It
    /// must never touch the real index: a benchmark that leaves two hundred generated files in
    /// someone's search results is a benchmark nobody runs twice.</para>
    ///
    /// <para>The .docx are real OOXML packages built with <see cref="ZipArchive"/>, so the zip and
    /// XML path is genuinely exercised rather than assumed from the .txt number.</para>
    /// </summary>
    private static (ThroughputRow Row, string Note) MeasureExtraction(int txtCount)
    {
        int docxCount = Math.Max(1, txtCount / 10);
        string dir = Path.Combine(Path.GetTempPath(), "findra-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = new List<string>(txtCount + docxCount);
            long txtBytes = 0, docxBytes = 0;

            string body = Lorem(8 * 1024);
            for (int i = 0; i < txtCount; i++)
            {
                string p = Path.Combine(dir, $"note-{i.ToString(CultureInfo.InvariantCulture)}.txt");
                File.WriteAllText(p, body);
                txtBytes += new FileInfo(p).Length;
                files.Add(p);
            }
            for (int i = 0; i < docxCount; i++)
            {
                string p = Path.Combine(dir, $"memo-{i.ToString(CultureInfo.InvariantCulture)}.docx");
                WriteDocx(p, paragraphs: 40);
                docxBytes += new FileInfo(p).Length;
                files.Add(p);
            }

            double seconds;
            using (var bench = new ContentDb(Path.Combine(dir, "bench.db")))
            {
                using (var tx = bench.Begin())
                {
                    // A synthetic reference number, not the real one off the filesystem. The queue
                    // only needs (volume, frn) to be unique, and asking the filesystem for a real
                    // FRN adds a failure mode - a temp directory on a share has none - to a step
                    // that is not what is being measured.
                    ulong frn = 1;
                    foreach (string f in files)
                        bench.Enqueue("B", frn++, f, FileKinds.Classify(Path.GetFileName(f), false),
                                      "searchbench corpus", tx);
                    tx.Commit();
                }

                var sw = Stopwatch.StartNew();
                Indexer.DrainOnce(bench, _ => { });
                sw.Stop();
                seconds = sw.Elapsed.TotalSeconds;
            }

            string note =
                $"{txtCount.ToString("N0", CultureInfo.InvariantCulture)} generated .txt of " +
                $"{Kb(txtBytes / Math.Max(1, txtCount))} and " +
                $"{docxCount.ToString("N0", CultureInfo.InvariantCulture)} generated .docx of " +
                $"{Kb(docxBytes / Math.Max(1, docxCount))}, indexed into a throwaway database and deleted";

            return (new ThroughputRow(ResultKind.Document, files.Count, seconds, txtBytes + docxBytes), note);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException ex) { Log.Warn("bench", "the generated corpus could not be deleted: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Warn("bench", "the generated corpus could not be deleted: " + ex.Message); }
        }
    }

    private static string Kb(long bytes) =>
        (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

    private static readonly string[] Words =
    [
        "lease", "agreement", "invoice", "total", "report", "quarter", "clause", "tenant",
        "premises", "payment", "schedule", "annex", "renewal", "notice", "party", "term",
    ];

    /// <summary>Word-shaped filler of a known size, so the corpus is reproducible from the note
    /// beside the number rather than being whatever files the machine happened to have.</summary>
    private static string Lorem(int bytes)
    {
        var sb = new StringBuilder(bytes + 16);
        int i = 0;
        while (sb.Length < bytes)
        {
            sb.Append(Words[i % Words.Length]).Append(i % 12 == 11 ? ".\n" : " ");
            i++;
        }
        return sb.ToString(0, bytes);
    }

    /// <summary>A real, minimal OOXML package: the content types, the package relationships and a
    /// document part of <paramref name="paragraphs"/> paragraphs. Enough that the extractor opens
    /// it as a zip and reads the same part it reads from a file Word wrote.</summary>
    private static void WriteDocx(string path, int paragraphs)
    {
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);

        Add(zip, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");

        Add(zip, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");

        var doc = new StringBuilder();
        doc.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>""");
        for (int i = 0; i < paragraphs; i++)
            doc.Append("<w:p><w:r><w:t>").Append(Lorem(200)).Append("</w:t></w:r></w:p>");
        doc.Append("</w:body></w:document>");
        Add(zip, "word/document.xml", doc.ToString());
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using Stream s = zip.CreateEntry(name).Open();
        using var w = new StreamWriter(s, new UTF8Encoding(false));
        w.Write(content);
    }

    /// <summary>
    /// Every file the index is actually made of, sized on its own - the database and its
    /// write-ahead log and shared-memory sidecars, which are real bytes on the user's disk and are
    /// routinely larger than the database itself while indexing is in flight.
    ///
    /// <para>The paths are published with the profile folder collapsed to <c>%LOCALAPPDATA%</c>.
    /// This fragment is pasted onto a public page, and the account name of whoever ran it is not
    /// part of the measurement.</para>
    /// </summary>
    private static IReadOnlyList<StoreRow> Stores(string dbPath)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rows = new List<StoreRow>();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string p = dbPath + suffix;
            long bytes;
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists) continue;
                bytes = fi.Length;
            }
            catch (IOException) { continue; }

            string shown = local.Length > 0 && p.StartsWith(local, StringComparison.OrdinalIgnoreCase)
                ? "%LOCALAPPDATA%" + p[local.Length..]
                : p;
            rows.Add(new StoreRow(Path.GetFileName(p), shown, bytes));
        }
        return rows;
    }
}
