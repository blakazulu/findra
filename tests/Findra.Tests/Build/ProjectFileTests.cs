using System.Runtime.CompilerServices;
using System.Xml.Linq;

using Findra;

using Xunit;

/// <summary>
/// The project files themselves, asserted. Three rules in this plan live nowhere else: every
/// project moves to the same target framework together, no project pins a runtime identifier,
/// and the native-bearing packages are pinned rather than floating. All three are invisible to
/// every other test in the suite and all three break a stranger's machine rather than this one.
/// </summary>
public class ProjectFileTests
{
    private const string Tfm = "net10.0-windows10.0.19041.0";

    /// <summary>The repo root, found by walking up from this source file to the solution. The
    /// test binary's own directory is several levels into bin/ and moves with the configuration.
    /// </summary>
    private static string Root([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Findra.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IReadOnlyList<(string Path, XDocument Xml)> Projects()
    {
        var found = new List<(string, XDocument)>();
        foreach (string p in Directory.EnumerateFiles(Root(), "*.csproj", SearchOption.AllDirectories))
        {
            if (p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            found.Add((p, XDocument.Load(p)));
        }
        Assert.NotEmpty(found);
        return found;
    }

    [Fact]
    public void EveryProjectTargetsTheWindowsSdkFlavourTheDecodersNeed()
    {
        // The OCR, media and thumbnail projections do not exist below 10.0.19041.0. A project
        // left behind compiles until the first test touches a type marked for that platform,
        // and then fails as if a package reference were missing.
        foreach ((string path, XDocument xml) in Projects())
        {
            string? tfm = xml.Descendants("TargetFramework").FirstOrDefault()?.Value;
            Assert.Equal(Tfm, tfm);
            Assert.Empty(xml.Descendants("TargetFrameworks"));   // one flavour, not a matrix
        }
    }

    [Fact]
    public void NoProjectPinsARuntimeIdentifier()
    {
        // Windows on ARM stays reachable, and the cost of keeping it possible is that nobody
        // writes win-x64 into a csproj to make a native package restore. Self-contained is a
        // property of the publish command (spec §2, §6).
        foreach ((string path, XDocument xml) in Projects())
        {
            Assert.Empty(xml.Descendants("RuntimeIdentifier"));
            Assert.Empty(xml.Descendants("RuntimeIdentifiers"));
        }
    }

    [Fact]
    public void TheNativeBearingPackagesArePinnedToTheVersionsThisPlanTested()
    {
        var want = new Dictionary<string, string>
        {
            ["Microsoft.ML.OnnxRuntime.DirectML"] = "1.24.4",
            ["Microsoft.ML.Tokenizers"] = "2.0.0",
            ["Whisper.net"] = "1.9.1",
            ["Whisper.net.Runtime"] = "1.9.1",
            ["Whisper.net.Runtime.Vulkan"] = "1.9.1",
            ["NAudio.Core"] = "3.0.0",
            ["NAudio.Wasapi"] = "3.0.0",
            ["System.Numerics.Tensors"] = "10.0.11",
            ["SkiaSharp"] = "3.119.4",
            ["SQLitePCLRaw.bundle_e_sqlite3"] = "2.1.12",
        };

        XDocument app = Projects().Single(p => p.Path.EndsWith("Findra.csproj", StringComparison.Ordinal)).Xml;
        var have = app.Descendants("PackageReference")
                      .ToDictionary(e => e.Attribute("Include")!.Value, e => e.Attribute("Version")!.Value);

        foreach ((string name, string version) in want)
        {
            Assert.True(have.ContainsKey(name), $"Findra.csproj has no reference to {name}");
            Assert.Equal(version, have[name]);
        }
    }

    [Fact]
    public void NoVendorLockedExecutionProviderIsReferencedAnywhere()
    {
        // CUDA means NVIDIA only plus a large separate runtime, and ROCm is not a Windows story.
        // The ban is on the package list because that is where it would arrive first, quietly,
        // as "just for my machine" (spec §6).
        foreach ((string path, XDocument xml) in Projects())
            foreach (XElement r in xml.Descendants("PackageReference"))
            {
                string name = r.Attribute("Include")!.Value;
                Assert.False(name.Contains("Cuda", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("TensorRT", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("ROCm", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("OpenVino", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("CoreML", StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(path)} references {name}, which ties Findra to one vendor's silicon");
            }
    }
}
