using Findra;
using Findra.Diagnostics;
using Xunit;

/// <summary>
/// The lines Settings > About > This computer copies for a bug report. They are how a machine
/// Findra has never run on becomes evidence, so they must name the graphics adapter the work
/// goes to, and they must carry nothing about a person's files.
/// </summary>
public class MachineReportTests
{
    private static readonly GpuChoice Integrated = new(0, "AMD Radeon(TM) 780M", 512L * 1024 * 1024, false);
    private static readonly GpuChoice Discrete = new(1, "NVIDIA GeForce RTX 4060", 8L * 1024 * 1024 * 1024, false);
    private static readonly GpuChoice Basic = new(2, "Microsoft Basic Render Driver", 0, true);

    private static ReportFacts Facts(IReadOnlyList<GpuChoice>? adapters = null, IReadOnlyList<Capability>? installed = null,
                                     IReadOnlyList<Capability>? off = null, long ram = 32L * 1024 * 1024 * 1024) =>
        new("0.8.0", "winget", "Arm64", "Windows 11 Home 10.0.26100.1000", "Snapdragon X Elite", ram,
            adapters ?? [Integrated, Discrete, Basic], installed ?? [], off ?? [], Reading: true);

    [Fact]
    public void ItNamesTheVersionTheInstallAndTheArchitecture()
    {
        string text = MachineReport.Compose(Facts());
        Assert.StartsWith("Findra 0.8.0, installed by winget, Arm64", text, StringComparison.Ordinal);
        Assert.Contains("Windows 11 Home 10.0.26100.1000", text, StringComparison.Ordinal);
        Assert.Contains("Processor: Snapdragon X Elite, 32 GB of memory", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAdapterIsListedAndTheOneTheWorkGoesToIsMarked()
    {
        // The failures guarded against came from an integrated card listed first; the report has to
        // show which of the two Findra picked.
        string text = MachineReport.Compose(Facts());
        Assert.Contains("NVIDIA GeForce RTX 4060, 8 GB (the one Findra uses)", text, StringComparison.Ordinal);
        Assert.Contains("AMD Radeon(TM) 780M, 512 MB;", text, StringComparison.Ordinal);
        Assert.Contains("Microsoft Basic Render Driver (software)", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("the one Findra uses").Length);
    }

    [Fact]
    public void OneAdapterListedTwiceIsNamedOnceButTwoCardsOfOneModelAreBoth()
    {
        GpuChoice a = new(0, "NVIDIA GeForce RTX 5070 Ti", 16L << 30, false, "luid_0x0_0x1");
        GpuChoice again = a with { Index = 2 };
        GpuChoice other = a with { Index = 3, Luid = "luid_0x0_0x9" };
        string text = MachineReport.Compose(Facts([a, again]));
        Assert.Equal(2, text.Split("RTX 5070 Ti").Length);
        text = MachineReport.Compose(Facts([a, other]));
        Assert.Equal(3, text.Split("RTX 5070 Ti").Length);
    }

    [Fact]
    public void ALoneAdapterIsStillNamedAsTheOneInUse()
    {
        // GpuAdapter.Best answers null with one adapter because it has nothing to choose; the
        // report must not read that as "no card".
        Assert.Same(Integrated, MachineReport.Chosen([Integrated]));
        Assert.Contains("780M, 512 MB (the one Findra uses)", MachineReport.Compose(Facts([Integrated])), StringComparison.Ordinal);
    }

    [Fact]
    public void ASoftwareAdapterIsNeverTheOneInUse()
    {
        Assert.Null(MachineReport.Chosen([Basic]));
        Assert.Contains("Graphics: none reported", MachineReport.Compose(Facts([])), StringComparison.Ordinal);
    }

    [Fact]
    public void AddOnsAreNamedAsSettingsNamesThemWithTheOnesTurnedOff()
    {
        string text = MachineReport.Compose(Facts(installed: [Capability.Photos, Capability.Speech],
                                                  off: [Capability.Speech, Capability.Meaning]));
        // Meaning is off but not installed, so saying it is turned off would be noise.
        Assert.Contains("Add-ons: Photos and video, Speech (Speech turned off)", text, StringComparison.Ordinal);
        Assert.Contains("Add-ons: none (names only)", MachineReport.Compose(Facts()), StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadMemoryIsSaidToBeUnknownNotZero()
    {
        Assert.Contains("memory unknown", MachineReport.Compose(Facts(ram: 0)), StringComparison.Ordinal);
    }

    [Fact]
    public void ItSendsTheReaderToTheCommandThatKnowsWhichChipEachModelUses()
    {
        Assert.Contains(MachineReport.ModelsHint, MachineReport.Compose(Facts()), StringComparison.Ordinal);
    }

    [Fact]
    public void GatheringOnThisMachineFillsEveryLineAndLoadsNoModel()
    {
        ReportFacts f = MachineReport.Gather(Config.Default with { InstallSource = "source" });
        Assert.Equal("source", f.InstallSource);
        Assert.False(string.IsNullOrWhiteSpace(f.Cpu));
        Assert.False(string.IsNullOrWhiteSpace(f.Windows));
        string text = MachineReport.Compose(f);
        Assert.Equal(7, text.Trim().Split(Environment.NewLine).Length);
    }

    [Fact]
    public void TheAboutRowCopiesAndThenSaysItDid()
    {
        var s = new SettingsState(Config.Default) { Section = Section.About };
        IReadOnlyList<Control> rows = SettingsModel.Controls(s);
        int row = rows.ToList().FindIndex(c => c.Id == ControlId.Report);
        Assert.True(row >= 0);
        Assert.Equal("Copy", rows[row].Value);

        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, row, -1));
        Assert.Equal(SettingsAction.CopyReport, o.Action);
        Assert.Equal("Copied", SettingsModel.Controls(o.State).Single(c => c.Id == ControlId.Report).Value);
    }
}
