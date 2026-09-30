using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Findra.Diagnostics;

/// <summary>Everything the machine report says, gathered once so the wording can be tested.</summary>
public sealed record ReportFacts(
    string Version,
    string InstallSource,
    string Architecture,
    string Windows,
    string Cpu,
    long RamBytes,
    IReadOnlyList<GpuChoice> Adapters,
    IReadOnlyList<Capability> Installed,
    IReadOnlyList<Capability> Off,
    bool Reading);

/// <summary>
/// A few lines about this computer for somebody to paste into a bug report: Settings > About >
/// This computer copies them. Findra has run on one kind of machine, and every other kind is
/// evidence only when somebody says what they have - this makes that one press instead of a hunt
/// through Device Manager.
///
/// <para>It goes to the clipboard and nowhere else. It holds no file names, no searches and no
/// identifier: the hardware, the version and which add-ons are installed. It opens no model, so it
/// costs nothing to press; which chip each model actually runs on is <c>findra --searchmodels</c>'s
/// job, and the report says so.</para>
/// </summary>
public static class MachineReport
{
    /// <summary>The line that sends a reader to the command that loads the models.</summary>
    public const string ModelsHint = "Which chip each model runs on: run findra --searchmodels and paste what it prints.";

    /// <summary>The adapter the accelerated work goes to: the most dedicated memory, never a
    /// software one. The same rule as <see cref="GpuAdapter.Best"/>, which answers null when there
    /// is only one adapter because it then has nothing to choose; a report still names that one.</summary>
    public static GpuChoice? Chosen(IReadOnlyList<GpuChoice> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        GpuChoice? best = null;
        foreach (GpuChoice a in adapters)
            if (!a.Software && (best is null || a.DedicatedBytes > best.DedicatedBytes)) best = a;
        return best;
    }

    public static string Compose(ReportFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"Findra {f.Version}, installed by {f.InstallSource}, {f.Architecture}",
            f.Windows,
            f.RamBytes > 0
                ? $"Processor: {f.Cpu}, {Sizes.Human(f.RamBytes)} of memory"
                : $"Processor: {f.Cpu}, memory {Machine.Unknown}",
        };

        GpuChoice? chosen = Chosen(f.Adapters);
        // DXGI can list one adapter several times (once per output it drives); the LUID is the
        // adapter, so two genuinely separate cards of the same model still both appear.
        var cards = f.Adapters
            .DistinctBy(a => a.Luid.Length > 0 ? a.Luid : $"{a.Index}")
            .Select(a => a.Software
                ? $"{a.Name} (software)"
                : $"{a.Name}, {Sizes.Human(a.DedicatedBytes)}{(ReferenceEquals(a, chosen) ? " (the one Findra uses)" : "")}")
            .ToList();
        lines.Add("Graphics: " + (cards.Count == 0 ? "none reported" : string.Join("; ", cards)));

        string addOns = f.Installed.Count == 0
            ? "none (names only)"
            : string.Join(", ", f.Installed.Select(Capabilities.Title));
        var off = f.Off.Where(f.Installed.Contains).ToList();
        if (off.Count > 0) addOns += $" ({string.Join(", ", off.Select(Capabilities.Title))} turned off)";
        lines.Add("Add-ons: " + addOns);
        lines.Add("Reading inside files: " + (f.Reading ? "on" : "off"));
        lines.Add(ModelsHint);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>Read the facts from this machine. Nothing here opens a model or leaves it.</summary>
    [SupportedOSPlatform("windows")]
    public static ReportFacts Gather(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        CapabilitySet installed = CapabilitySet.Installed();
        return new ReportFacts(
            Version: Log.Version,
            InstallSource: string.IsNullOrWhiteSpace(config.InstallSource) ? "unknown" : config.InstallSource!,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            Windows: Machine.WindowsVersion(),
            Cpu: Machine.CpuName(),
            RamBytes: Machine.InstalledMemory(),
            Adapters: GpuAdapter.All(),
            Installed: [.. Capabilities.All.Where(installed.Has)],
            Off: [.. AddOns.Off(config)],
            Reading: config.IndexContent);
    }
}
