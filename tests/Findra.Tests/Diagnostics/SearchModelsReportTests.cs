using System.Globalization;
using Findra.Diagnostics;

using Findra;

[Collection("culture")]
public class SearchModelsReportTests
{
    private static ModelsSnapshot Sample(bool anyPresent = true) => new(
        Dir: @"C:\Users\x\AppData\Local\Findra\models",
        Models:
        [
            new ModelRow("siglip2-vision.onnx", "photos and video frames", anyPresent, 372_034_764, anyPresent ? 372_034_764 : 0),
            new ModelRow("siglip2-text-q.onnx", "what you type", false, 283_430_092, 0),
            new ModelRow("e5-base.onnx", "meaning", false, 1_110_043_033, 0),
        ],
        Capabilities:
        [
            new CapabilityRow(Capability.Photos, false, anyPresent ? 1 : 0, 3, 659_659_160),
            new CapabilityRow(Capability.Meaning, false, 0, 2, 1_115_055_717),
        ],
        Onnx:
        [
            new ProviderTry("DirectML", false, "DllNotFoundException: DirectML.dll"),
            new ProviderTry("CPU", true, ""),
        ],
        Whisper: [new ProviderTry("Vulkan", true, "")],
        Notes: []);

    [Fact]
    public void EveryModelIsListedIncludingTheOnesThatAreNotThere()
    {
        // "Why are no photos being indexed" is unanswerable if the report only lists what it
        // found. The absent rows are the answer.
        string text = ModelsReport.Render(Sample());

        Assert.Contains("siglip2-vision.onnx", text, StringComparison.Ordinal);
        Assert.Contains("siglip2-text-q.onnx", text, StringComparison.Ordinal);
        Assert.Contains("e5-base.onnx", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APresentModelsSizeOnDiskIsPrintedBesideTheOneItShouldBe()
    {
        // Spec §9a: the README's model sizes come from the real files, not from the declared
        // table. Printing only one of the two numbers makes that impossible to check.
        string text = ModelsReport.Render(Sample());

        Assert.Contains("354.8 MB", text, StringComparison.Ordinal);   // declared, from the table
        Assert.Contains("on disk", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsThereButTheWrongSizeIsFlagged()
    {
        ModelsSnapshot s = Sample() with
        {
            Models = [new ModelRow("siglip2-vision.onnx", "photos", true, 372_034_764, 12_345)],
        };

        string text = ModelsReport.Render(s);

        Assert.Contains("12,345", text, StringComparison.Ordinal);
        Assert.Contains("expected", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileTheRightSizeForTheTableIsNotCalledWrong()
    {
        // The five sizes below are the real files a real install fetched, against the real
        // figures in ModelStore. Every one of them differs from its declared size, four of them
        // upward, because the declared size is the specification's table in megabytes to one
        // decimal place and was never going to equal a byte count. A report that compares the
        // two for equality calls all five WRONG SIZE - in the one mode the README's measured
        // sizes are supposed to come from.
        ModelsSnapshot s = Sample() with
        {
            Models =
            [
                new ModelRow("siglip2-vision.onnx", "photos", true, 372_036_403, 371_992_072),   // 44,331 smaller
                new ModelRow("siglip2-text-q.onnx", "what you type", true, 283_430_092, 283_438_275),
                new ModelRow("siglip2.spm", "its vocabulary", true, 4_194_304, 4_241_003),       // 46,699 larger
                new ModelRow("e5-base.onnx", "meaning", true, 278_601_728, 278_647_662),
                new ModelRow("e5-small.spm", "their vocabulary", true, 5_033_164, 5_069_051),
            ],
        };

        string text = ModelsReport.Render(s);

        Assert.DoesNotContain("WRONG SIZE", text, StringComparison.Ordinal);
        Assert.Equal(5, text.Split('\n').Count(l => l.Contains("matches the", StringComparison.Ordinal)));
    }

    [Fact]
    public void AFileThatArrivedShortIsStillCalledWrongHoweverTheComparisonIsLoosened()
    {
        // The other side of the same fence, and the reason the comparison exists at all. A
        // connection that closed at four fifths of the way through leaves a file that is present,
        // is above ModelStore's completeness floor - 250,000,000 for this one - and will not
        // load. A tolerance wide enough to swallow that reports nothing and is worse than no
        // comparison, because it looks like one.
        ModelsSnapshot s = Sample() with
        {
            Models = [new ModelRow("e5-base.onnx", "meaning", true, 278_601_728, 260_000_000)],
        };

        string text = ModelsReport.Render(s);

        Assert.Contains("WRONG SIZE", text, StringComparison.Ordinal);
        Assert.Contains("260,000,000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACapabilityWithSomeOfItsFilesIsNotReportedAsReady()
    {
        // One of three is not "photos work". An Any() where an All() belongs lights the
        // capability up and then fails on the first query.
        string text = ModelsReport.Render(Sample());

        Assert.Contains("1 of 3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Photos and video : ready", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACapabilityThatIsOffSaysWhatItWouldCostToTurnOn()
    {
        string text = ModelsReport.Render(Sample());
        Assert.Contains("629 MB", text, StringComparison.Ordinal);
        Assert.Contains("1.04 GB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChosenProviderAndEveryRejectedOneAppearWithItsReason()
    {
        // Spec §6, the whole point of the mode: report the chosen provider AND every one it
        // rejected, with reasons. A report that prints only the winner loses the half that
        // answers "why is this slow".
        string text = ModelsReport.Render(Sample());

        Assert.Contains("DirectML", text, StringComparison.Ordinal);
        Assert.Contains("DirectML.dll", text, StringComparison.Ordinal);
        Assert.Contains("CPU", text, StringComparison.Ordinal);
        Assert.Contains("Vulkan", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderThatWasNeverTriedIsNotClaimedAsRejected()
    {
        // The sample tried three providers in total: DirectML (rejected) and CPU (chosen) for
        // ONNX, and Vulkan (chosen) for whisper. The whisper chain stopped at its first rung, so
        // the report must not invent a rejected CPU row to fill the declared chain out.
        //
        // Counted, not sliced. An earlier draft split the rendered text on the lowercase word
        // "whisper" and inspected the tail; every other surface in this plan capitalises it, so
        // the split found nothing, `[^1]` was the whole report, and the ONNX section's own "CPU"
        // failed the assertion for a reason that had nothing to do with the rule.
        string[] lines = ModelsReport.Render(Sample()).Split('\n');
        int rows = lines.Count(l => l.Contains(ModelsReport.Chosen, StringComparison.Ordinal)
                                 || l.Contains(ModelsReport.Rejected, StringComparison.Ordinal));

        Assert.Equal(3, rows);
    }

    [Fact]
    public void AMachineWithNoModelsAtAllProducesAWholeReportAndNotAnError()
    {
        // A missing model is a NORMAL state (spec §6). The report has to be complete and
        // readable on a machine that took the "Just names" preset, which is most of them.
        string text = ModelsReport.Render(Sample(anyPresent: false));

        Assert.Contains("siglip2-vision.onnx", text, StringComparison.Ordinal);
        Assert.Contains("3.7 GB", text, StringComparison.Ordinal);    // what everything would cost
        Assert.DoesNotContain("ERROR", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FAIL", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFreeCapabilityIsNamedSoNobodyThinksSearchIsOff()
    {
        string text = ModelsReport.Render(Sample(anyPresent: false));
        Assert.Contains("words in documents", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("free", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheReportReadsTheSameOnEveryMachine()
    {
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string de = ModelsReport.Render(Sample());
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(ModelsReport.Render(Sample()), de);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }

    [Fact]
    public void ANoteFromTheRunItselfIsCarriedThrough()
    {
        // Where "this model is on disk and would not load" ends up: a note, not a crash, and
        // not a silent omission either.
        ModelsSnapshot s = Sample() with { Notes = ["e5-base-q.onnx is present but would not load: InvalidProtobuf"] };
        Assert.Contains("would not load", ModelsReport.Render(s), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTitleComesBeforeTheProbeRatherThanAfterIt()
    {
        // The probe used to be written straight to the console while the encoders were open,
        // which put a headerless block of vector norms above the title that explains what the
        // block is. The data was right and the reading order was wrong: whatever the run
        // measured belongs under the report, in a section of its own.
        ModelsSnapshot s = Sample() with
        {
            Probe = ["'a sunset over the sea'  clip |v|=1.000", "  image-text similarity:"],
        };
        string text = ModelsReport.Render(s);

        int title = text.IndexOf("findra --searchmodels", StringComparison.Ordinal);
        int heading = text.IndexOf("probe:", StringComparison.Ordinal);
        int firstLine = text.IndexOf("a sunset over the sea", StringComparison.Ordinal);

        Assert.True(title >= 0, "the report has no title");
        Assert.True(heading > title, $"the probe heading is at {heading} and the title at {title}");
        Assert.True(firstLine > heading, "a probe line is printed above its own heading");
    }

    [Fact]
    public void ARunThatProbedNothingPrintsNoProbeHeading()
    {
        // A machine with no model on disk runs no probe at all, and an empty section with a
        // heading over it reads as a probe that found nothing rather than one that never ran.
        Assert.DoesNotContain("probe:", ModelsReport.Render(Sample()), StringComparison.Ordinal);
    }
}
