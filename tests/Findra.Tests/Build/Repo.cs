using System.Runtime.CompilerServices;

/// <summary>
/// The repository root, found by walking up from this file's own source path until a directory
/// holding <c>Findra.sln</c> turns up. The test binary lives several levels inside <c>bin/</c>
/// and its path moves with the configuration and the target framework, so it is not an anchor.
/// Every test in this plan that reads a file the build system owns - a props file, a workflow,
/// the installer script, the README - starts here.
/// </summary>
public static class Repo
{
    public static string Root { get; } = Find();

    public static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    public static bool Exists(string relative) =>
        File.Exists(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    public static string Path_(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The names a comparative claim would use, shared by the README test and the winget listing
    /// test - spec 9a's "no comparative claims against named competitors" applies to both, and
    /// one list means they cannot drift apart. It lives here rather than on either test class
    /// because the manifest test (Task 12) is written before the README test (Task 13).
    ///
    /// <para>Deliberately incomplete: one well-known desktop search tool is called "Everything",
    /// which is also an ordinary English word AND the name of one of Findra's own first-run
    /// presets, so it cannot be grepped for without failing on sentences that are perfectly fine.
    /// That one is a review item in the close-out.</para>
    /// </summary>
    public static readonly string[] Competitors =
        ["Listary", "Copernic", "Agent Ransack", "DocFetcher", "Recoll", "X1 Search", "Lookeen",
         "UltraSearch", "FileLocator", "Spotlight"];

    private static string Find([CallerFilePath] string here = "")
    {
        DirectoryInfo? d = new FileInfo(here).Directory;
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Findra.sln"))) d = d.Parent;
        return d?.FullName
            ?? throw new InvalidOperationException(
                $"no Findra.sln above '{here}' - the build tests cannot find the repository");
    }
}
