using Findra;
using Xunit;

public class LogPlacementTests
{
    [Fact]
    public void TheTestSuiteNeverWritesIntoTheInstalledFindrasLogFolder()
    {
        // Every test that runs a path which logs used to write into %LOCALAPPDATA%\Findra\logs -
        // the folder the Findra installed on the same machine writes to - so a log read for a bug
        // report carried test fixtures ("a.bin could not be fetched", journal 'beef', an indexer
        // "restarting" four times in a millisecond), and the log's own seven-day pruning ran over
        // that folder from inside the test run.
        string mine = Path.GetFullPath(Log.Dir).TrimEnd('\\') + "\\";
        string installed = Path.GetFullPath(Paths.Logs).TrimEnd('\\') + "\\";

        Assert.False(mine.StartsWith(installed, StringComparison.OrdinalIgnoreCase),
            $"the test suite logs to {mine}");
    }
}
