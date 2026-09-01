using Findra;
using Xunit;

public class PathsTests
{
    [Fact]
    public void ConfigRoams_AndBulkDoesNot()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(roaming, Paths.Config);
        Assert.StartsWith(local,   Paths.Models);
        Assert.StartsWith(local,   Paths.Index);
        Assert.StartsWith(local,   Paths.Logs);
    }

    [Fact]
    public void ModelsAreNeverUnderRoaming()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.DoesNotContain(roaming, Paths.Models);
    }

    [Fact]
    public void ModelsAreNeverBesideTheExecutable()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Assert.DoesNotContain(exeDir, Paths.Models);
    }

    [Fact]
    public void FileNamesAreWhereTheSpecSaysTheyAre()
    {
        Assert.EndsWith(Path.Combine("Findra", "config.json"),   Paths.ConfigFile);
        Assert.EndsWith(Path.Combine("Findra", "palettes.json"), Paths.PalettesFile);
    }
}
