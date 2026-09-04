using System.Net.Http;
using System.Text;
using Findra;
using Xunit;

/// <summary>
/// The two ways a download ends badly that the guards used to miss: a body that stops between the
/// floor and the real size while the server said nothing about its length, and a connection or a
/// disk that fails mid-body.
/// </summary>
public class ModelDownloadFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-dlf-" + Guid.NewGuid().ToString("N"));

    public ModelDownloadFailureTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A floor of 6 against a real size of 100, which is the shape that matters: MinBytes
    /// is a generous "this cannot be the file" line, so between it and the declared size there is
    /// a wide window a truncated file used to pass through.</summary>
    private static readonly Model Wide = new("wide.bin", "https://example.invalid/wide.bin", 6, 100, "a test");

    private static byte[] Body(int n) => Encoding.ASCII.GetBytes(new string('A', n));

    /// <summary>A server that never says how long the file is - chunked transfer, a re-encoding
    /// proxy, or a handler that strips the header after decompressing all produce this.</summary>
    private static Fetch Silent(byte[] body) =>
        (url, from, ct) => Task.FromResult(new Fetched(new MemoryStream(body, writable: false), 0, from > 0));

    private string Final => Path.Combine(_dir, Wide.File);
    private string Part => Path.Combine(_dir, Wide.File + ".part");

    [Fact]
    public async Task AFileThatStopsAboveTheFloorButShortOfItsSizeIsNotPromoted()
    {
        // 50 of 100 bytes: over the floor of 6, so the floor alone said yes. Promoted, it reads as
        // installed on every surface and then fails every file that needs it, and nothing refetches
        // it because it is "there".
        DownloadOutcome o = await ModelDownloader.GetAsync(Wide, _dir, Silent(Body(50)), null, default);

        Assert.False(o.Complete);
        Assert.False(File.Exists(Final), "a truncated file was promoted under its real name");
        Assert.True(File.Exists(Part), "what arrived was thrown away instead of kept for a resume");
        Assert.Equal(50, new FileInfo(Part).Length);
    }

    [Fact]
    public async Task AFileWithinTheDeclaredSlackIsStillAccepted()
    {
        // A real file misses the table's figure by tens of kilobytes, so the guard is the declared
        // size less SizeSlack - never an equality.
        long ok = Wide.Bytes - ModelStore.SizeSlack(Wide.Bytes);
        DownloadOutcome o = await ModelDownloader.GetAsync(Wide, _dir, Silent(Body((int)ok)), null, default);

        Assert.True(o.Complete, o.Problem);
        Assert.True(File.Exists(Final));
    }

    [Fact]
    public async Task ADroppedConnectionIsAnOutcomeRatherThanAnUnhandledThrow()
    {
        // It reaches `--models install` and the first-run screen. Unhandled, the first is a stack
        // trace and whatever exit code the runtime picks, and the second is a progress bar that
        // simply stops.
        Fetch drops = (url, from, ct) => throw new HttpRequestException("the connection was lost");

        IReadOnlyList<DownloadOutcome> outcomes =
            await ModelDownloader.GetAllAsync([Wide], _dir, drops, null, default);

        DownloadOutcome only = Assert.Single(outcomes);
        Assert.False(only.Complete);
        Assert.Contains("lost", only.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADiskThatFillsMidBodyIsAnOutcomeToo()
    {
        Fetch fills = (url, from, ct) => throw new IOException("There is not enough space on the disk.");

        IReadOnlyList<DownloadOutcome> outcomes =
            await ModelDownloader.GetAllAsync([Wide], _dir, fills, null, default);

        Assert.False(Assert.Single(outcomes).Complete);
    }

    [Fact]
    public async Task QuittingStillCancelsRatherThanBecomingAnOutcome()
    {
        // Cancellation is Findra shutting down, not a fault, and the caller has to see its own
        // cancellation rather than a list of failures to report.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Fetch never = (url, from, ct) => throw new OperationCanceledException(ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ModelDownloader.GetAllAsync([Wide], _dir, never, null, cts.Token));
    }
}
