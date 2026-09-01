using Findra;
using Xunit;

public class UiStatusTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"findra-ui-status-{Guid.NewGuid():N}.json");

    [Fact]
    public void WriteThenReadRoundTrips()
    {
        string path = TempPath();
        try
        {
            UiStatus.Write(Environment.ProcessId, "Ctrl+Alt+Space", path);

            UiStatus.Status? status = UiStatus.Read(path);

            Assert.NotNull(status);
            Assert.Equal(Environment.ProcessId, status!.Value.Pid);
            Assert.Equal("Ctrl+Alt+Space", status.Value.Hotkey);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ANullHotkeyRoundTrips()
    {
        string path = TempPath();
        try
        {
            UiStatus.Write(Environment.ProcessId, null, path);

            UiStatus.Status? status = UiStatus.Read(path);

            Assert.NotNull(status);
            Assert.Null(status!.Value.Hotkey);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void AMissingFileReadsAsNull()
    {
        string path = TempPath();
        Assert.False(File.Exists(path));
        Assert.Null(UiStatus.Read(path));
    }

    [Fact]
    public void ADeadPidReadsAsNull()
    {
        // No process on this machine plausibly holds this pid across a normal test run.
        string path = TempPath();
        try
        {
            UiStatus.Write(999_999, "Alt+Space", path);
            Assert.Null(UiStatus.Read(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void GarbageOnDiskReadsAsNull()
    {
        string path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Assert.Null(UiStatus.Read(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ClearRemovesTheFile()
    {
        string path = TempPath();
        UiStatus.Write(Environment.ProcessId, "Alt+Space", path);
        Assert.True(File.Exists(path));

        UiStatus.Clear(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ClearOnAMissingFileDoesNotThrow()
    {
        string path = TempPath();
        UiStatus.Clear(path);   // must not throw
    }
}
