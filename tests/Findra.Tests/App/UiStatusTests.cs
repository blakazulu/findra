using System.Diagnostics;
using Findra;
using Xunit;

public class UiStatusTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"findra-ui-status-{Guid.NewGuid():N}.json");

    // No test run is ever named findra - it is testhost, or dotnet. The alive-and-ours branch can
    // only be reached at all by telling Read what name to expect, which is why the parameter is
    // there. Everything that does NOT pass it is asserting the production rule.
    private static string ThisProcess => Process.GetCurrentProcess().ProcessName;

    [Fact]
    public void WriteThenReadRoundTrips()
    {
        string path = TempPath();
        try
        {
            UiStatus.Write(Environment.ProcessId, "Ctrl+Alt+Space", path);

            UiStatus.Status? status = UiStatus.Read(path, ThisProcess);

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

            UiStatus.Status? status = UiStatus.Read(path, ThisProcess);

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
    public void ALivePidBelongingToSomeOtherProgramReadsAsNull()
    {
        // The crash case. The file outlives the process that wrote it, naming a pid Windows is
        // free to hand to anything; this test's own pid is alive and is emphatically not Findra.
        // Without the name check, --searchprobe reports "ui : running (pid N, hotkey Alt+Space)"
        // about a process that has never heard of Findra, and the first thing anyone does with
        // that answer is go hunting for a bug in the hotkey.
        string path = TempPath();
        try
        {
            UiStatus.Write(Environment.ProcessId, "Alt+Space", path);
            Assert.NotEqual(UiStatus.ProcessName, ThisProcess, StringComparer.OrdinalIgnoreCase);

            Assert.Null(UiStatus.Read(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void TheNameIsComparedWithoutRegardToCase()
    {
        string path = TempPath();
        try
        {
            UiStatus.Write(Environment.ProcessId, "Alt+Space", path);
            Assert.NotNull(UiStatus.Read(path, ThisProcess.ToUpperInvariant()));
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
