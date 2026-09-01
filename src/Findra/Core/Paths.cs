namespace Findra;

/// <summary>
/// Settings roam; models, index and logs do not. 2.9 GB of model files must never
/// end up in a roaming profile, and never beside the executable.
/// </summary>
public static class Paths
{
    private static string Roaming =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Findra");

    private static string Local =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Findra");

    public static string Config => Roaming;
    public static string Models => Path.Combine(Local, "models");
    public static string Index  => Path.Combine(Local, "index");
    public static string Logs   => Path.Combine(Local, "logs");

    public static string ConfigFile   => Path.Combine(Roaming, "config.json");
    public static string PalettesFile => Path.Combine(Roaming, "palettes.json");

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
