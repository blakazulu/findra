using System.Text;

using Findra;

public class ModelDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-dl-" + Guid.NewGuid().ToString("N"));

    public ModelDownloadTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A model whose floor is small enough to satisfy with a handful of bytes, so the
    /// whole download path can be exercised without a network or a gigabyte.</summary>
    private static readonly Model Tiny = new("tiny.bin", "https://example.invalid/tiny.bin", 6, 9, "a test");

    private static readonly byte[] Content = Encoding.ASCII.GetBytes("ABCDEFGHI");   // 9 bytes

    /// <summary>A server that honours ranges, recording what it was asked for.</summary>
    private static Fetch Server(List<long> askedFrom, byte[]? body = null)
    {
        byte[] all = body ?? Content;
        return (url, from, ct) =>
        {
            askedFrom.Add(from);
            if (from > all.Length) throw new RangeRefusedException(url, from);
            var slice = new MemoryStream(all, (int)from, all.Length - (int)from, writable: false);
            return Task.FromResult(new Fetched(slice, all.Length, from > 0));
        };
    }

    private string Part => Path.Combine(_dir, Tiny.File + ".part");
    private string Final => Path.Combine(_dir, Tiny.File);

    [Fact]
    public async Task AFinishedFileIsNotFetchedAgain()
    {
        // Spec §2a. Re-downloading gigabytes because an upgrade did not look first is the
        // single most annoying thing this product could do to someone, and it gets a test.
        File.WriteAllBytes(Final, Content);
        var asked = new List<long>();

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, Server(asked), null, default);

        Assert.Empty(asked);               // nothing was requested at all
        Assert.True(r.Complete);
    }

    [Fact]
    public async Task APartialDownloadResumesFromTheByteAlreadyFetched()
    {
        // The assertion that matters is `asked` - a downloader that throws the part away and
        // starts over produces a byte-identical file, so the file alone proves nothing.
        File.WriteAllBytes(Part, Content[..3]);
        var asked = new List<long>();

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, Server(asked), null, default);

        Assert.Equal([3L], asked);                                   // it asked for the rest
        Assert.Equal(Content, File.ReadAllBytes(Final));
        Assert.False(File.Exists(Part));
        Assert.True(r.Complete);
    }

    [Fact]
    public async Task ProgressCountsTheWholeFileAndNotJustThisLeg()
    {
        // Resuming a 1.5 GB file at 60% and then showing 0% is a bar that says the download
        // restarted when it did not. The last report must be 9 of 9, not 6 of 9.
        File.WriteAllBytes(Part, Content[..3]);
        var seen = new List<DownloadProgress>();

        await ModelDownloader.GetAsync(Tiny, _dir, Server([]), seen.Add, default);

        Assert.NotEmpty(seen);
        Assert.Equal(9L, seen[^1].Got);
        Assert.Equal(9L, seen[^1].Total);
        Assert.All(seen, p => Assert.True(p.Got >= 3, $"progress went backwards to {p.Got}"));
    }

    [Fact]
    public async Task ADownloadThatEndsShortIsNotPromoted()
    {
        // The source moves whatever arrived into place. A short file above its floor then reads
        // as installed for ever: every load fails, the capability is dead, and nothing
        // re-downloads it because it is "there".
        Fetch truncating = (url, from, ct) =>
            Task.FromResult(new Fetched(new MemoryStream(Content[..5]), Content.Length, from > 0));

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, truncating, null, default);

        Assert.False(r.Complete);
        Assert.False(File.Exists(Final));                       // nothing was promoted
        Assert.Equal(5, new FileInfo(Part).Length);             // and what arrived is kept, to resume from
        Assert.Contains("5", r.Problem!, StringComparison.Ordinal);
        Assert.Contains("9", r.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStalePartAgainstARepublishedFileStartsOver()
    {
        // A .part longer than the whole file cannot be a prefix of it. Without the restart the
        // install can never complete again, on any run.
        File.WriteAllBytes(Part, Encoding.ASCII.GetBytes("ZZZZZZZZZZZZZZZ"));   // 15 > 9
        var asked = new List<long>();

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, Server(asked), null, default);

        Assert.Equal([15L, 0L], asked);                 // refused, then started over
        Assert.Equal(Content, File.ReadAllBytes(Final));
        Assert.True(r.Complete);
    }

    [Fact]
    public async Task APartThatIsAlreadyTheWholeFileIsPromotedRatherThanFetchedAgain()
    {
        // Cancelled or killed between the last write and the rename. The .part holds the whole
        // file, the next run asks for a range at the end, the server refuses - and treating that
        // the same way as a stale part costs a full re-download of something already on the disk.
        File.WriteAllBytes(Part, Content);              // exactly 9 bytes, well over the floor of 6
        var asked = new List<long>();

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, Server(asked), null, default);

        Assert.Equal([9L], asked);                      // refused once, and NOT re-fetched from 0
        Assert.True(r.Complete);
        Assert.Equal(Content, File.ReadAllBytes(Final));
        Assert.False(File.Exists(Part));
    }

    [Fact]
    public async Task CancellingLeavesThePartSoTheNextRunResumes()
    {
        using var cts = new CancellationTokenSource();
        Fetch slow = (url, from, ct) =>
            Task.FromResult(new Fetched(new CancellingStream(Content, after: 4, cts), Content.Length, from > 0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ModelDownloader.GetAsync(Tiny, _dir, slow, null, cts.Token));

        Assert.False(File.Exists(Final));
        Assert.True(File.Exists(Part));
        Assert.Equal(4, new FileInfo(Part).Length);
    }

    [Fact]
    public async Task EachFileInASetIsFetchedOnceAndTheOnesAlreadyThereAreSkipped()
    {
        var second = new Model("second.bin", "https://example.invalid/second.bin", 6, 9, "a second test");
        File.WriteAllBytes(Final, Content);              // Tiny is already installed
        var asked = new List<string>();
        Fetch f = (url, from, ct) => { asked.Add(url); return Task.FromResult(new Fetched(new MemoryStream(Content), 9, false)); };

        IReadOnlyList<DownloadOutcome> all = await ModelDownloader.GetAllAsync([Tiny, second], _dir, f, null, default);

        Assert.Equal([second.Url], asked);
        Assert.All(all, o => Assert.True(o.Complete));
    }

    [Fact]
    public void TheIndexerChildNeverDownloadsAnything()
    {
        // Spec §6 moves consent and progress onto the first-run screen, and the source engine
        // did the opposite: its indexer blocked the entire queue until all seven files existed
        // and fetched them itself. This is the guard that stops that coming back with a port.
        string content = Path.Combine(RepoRoot(), "src", "Findra", "Content");
        foreach (string file in Directory.EnumerateFiles(content, "*.cs"))
        {
            string src = File.ReadAllText(file);
            foreach (string banned in new[] { "HttpClient", "ModelDownloader", "WebClient", "HttpRequestMessage" })
                Assert.False(src.Contains(banned, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} mentions {banned}; downloads belong to the interface, not to the indexer child");
        }
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Findra.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>A body that cancels the token partway through, so the writer is interrupted
    /// mid-file exactly as a dropped connection would interrupt it.</summary>
    private sealed class CancellingStream(byte[] all, int after, CancellationTokenSource cts) : Stream
    {
        private int _at;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= after) { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }
            int n = Math.Min(count, after - _at);
            Array.Copy(all, _at, buffer, offset, n);
            _at += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => all.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
