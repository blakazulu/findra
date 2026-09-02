namespace Findra.Startup;

/// <summary>
/// How this copy got here, recorded once (spec §9b). It decides which sentence the update check
/// gives somebody, and there is no honest way to guess it later: a winget install and a manual
/// installer run leave the same files in the same place.
/// </summary>
public static class InstallSource
{
    /// <summary>Written by the installer beside the executable. The `dotnet publish` route has
    /// none, which is itself the answer.</summary>
    public const string MarkerFile = "installed-by.txt";

    private static readonly string[] Known = ["winget", "installer", "source"];

    public static string Detect(string exeDir)
    {
        ArgumentNullException.ThrowIfNull(exeDir);
        try
        {
            string path = Path.Combine(exeDir, MarkerFile);
            if (!File.Exists(path)) return "source";
            string text = File.ReadAllText(path).Trim().ToLowerInvariant();
            // Anything unrecognised is "unknown", which has a sentence of its own. Passing the raw
            // word through would put it in the About section and into a switch that would silently
            // fall to the both-ways advice.
            return Array.IndexOf(Known, text) >= 0 ? text : "unknown";
        }
        catch (Exception ex)
        {
            Log.Warn("startup", "could not read the install marker: " + ex.Message);
            return "unknown";
        }
    }

    /// <summary>What to record, given what is already recorded. Detection happens once; after that
    /// the config is the answer, because the marker file can be lost and the truth cannot change.
    /// </summary>
    public static string Resolve(Config config, string exeDir)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.IsNullOrWhiteSpace(config.InstallSource) ? Detect(exeDir) : config.InstallSource!;
    }
}
