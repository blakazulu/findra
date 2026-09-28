using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

using Findra;
using Xunit;

public sealed class UpdateDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    /// <summary>Answers every request from a function, and remembers what was asked.</summary>
    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Asked.Add(r.RequestUri!);
            return Task.FromResult(answer(r));
        }
    }

    private const string Start = "https://github.com/blakazulu/findra/releases/download/v9.9.9/findra-setup-x64.exe";
    private static readonly byte[] Body = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
    private static string Hex(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static ReleaseAsset Asset(long? size = null, string? sha = null, string url = Start) =>
        new("findra-setup-x64.exe", url, size ?? Body.Length, sha ?? Hex(Body));

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private Task<DownloadResult> Get(FakeHttp http, ReleaseAsset asset, Action<long, long>? progress = null,
                                     CancellationToken ct = default) =>
        UpdateDownload.GetAsync(new HttpClient(http), asset, _dir, "1.0.0", progress, ct);

    [Fact]
    public async Task ADownloadThatMatchesItsDigestIsKept()
    {
        DownloadResult r = await Get(new FakeHttp(_ => Ok(Body)), Asset());

        Assert.Equal(DownloadFailure.None, r.Failure);
        Assert.Equal(Body, File.ReadAllBytes(r.Path!));
    }

    [Fact]
    public async Task ARedirectToGitHubsFileHostIsFollowed()
    {
        var http = new FakeHttp(q => q.RequestUri!.Host == "github.com"
            ? Redirect("https://release-assets.githubusercontent.com/x/findra-setup-x64.exe")
            : Ok(Body));

        DownloadResult r = await Get(http, Asset());

        Assert.Equal(DownloadFailure.None, r.Failure);
        Assert.Equal(2, http.Asked.Count);
    }

    [Fact]
    public async Task ARedirectOffGitHubIsRefusedBeforeAnythingIsWritten()
    {
        var http = new FakeHttp(q => q.RequestUri!.Host == "github.com" ? Redirect("https://example.com/evil.exe") : Ok(Body));

        DownloadResult r = await Get(http, Asset());

        Assert.Equal(DownloadFailure.Host, r.Failure);
        Assert.DoesNotContain(http.Asked, u => u.Host == "example.com");
        Assert.False(File.Exists(Path.Combine(_dir, "findra-setup-x64.exe")));
    }

    [Fact]
    public async Task ARelativeRedirectIsResolvedAgainstTheHopItCameFrom()
    {
        var http = new FakeHttp(q => q.RequestUri!.AbsolutePath.EndsWith("findra-setup-x64.exe", StringComparison.Ordinal)
            ? Redirect("/elsewhere/file.exe")
            : Ok(Body));

        DownloadResult r = await Get(http, Asset());

        Assert.Equal(DownloadFailure.None, r.Failure);
        Assert.Equal(new Uri("https://github.com/elsewhere/file.exe"), http.Asked[1]);
    }

    [Fact]
    public async Task APlainHttpAddressIsRefused()
    {
        var http = new FakeHttp(_ => Ok(Body));
        DownloadResult r = await Get(http, Asset(url: "http://github.com/a.exe"));

        Assert.Equal(DownloadFailure.Host, r.Failure);
        Assert.Empty(http.Asked);
    }

    [Fact]
    public async Task ADeclaredSizeOverTheCapIsRefusedWithoutARequest()
    {
        var http = new FakeHttp(_ => Ok(Body));
        DownloadResult r = await Get(http, Asset(size: UpdateDownload.MaxBytes + 1));

        Assert.Equal(DownloadFailure.TooLarge, r.Failure);
        Assert.Empty(http.Asked);
    }

    [Fact]
    public async Task AShortDownloadIsRefusedAndDeleted()
    {
        DownloadResult r = await Get(new FakeHttp(_ => Ok(Body[..1000])), Asset());

        Assert.Equal(DownloadFailure.Short, r.Failure);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task ALongerDownloadThanDeclaredIsRefusedAndDeleted()
    {
        DownloadResult r = await Get(new FakeHttp(_ => Ok([.. Body, 1, 2, 3])), Asset());

        Assert.Equal(DownloadFailure.Oversized, r.Failure);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task AWrongDigestIsRefusedAndDeleted()
    {
        DownloadResult r = await Get(new FakeHttp(_ => Ok(Body)), Asset(sha: new string('0', 64)));

        Assert.Equal(DownloadFailure.Digest, r.Failure);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task TheDigestIsComparedWithoutCaringAboutCase()
    {
        DownloadResult r = await Get(new FakeHttp(_ => Ok(Body)), Asset(sha: Hex(Body).ToUpperInvariant()));
        Assert.Equal(DownloadFailure.None, r.Failure);
    }

    [Fact]
    public async Task ANetworkFailureIsReportedAndLeavesNothingBehind()
    {
        DownloadResult r = await Get(new FakeHttp(_ => throw new HttpRequestException("no route")), Asset());

        Assert.Equal(DownloadFailure.Network, r.Failure);
        Assert.False(File.Exists(Path.Combine(_dir, "findra-setup-x64.exe")));
    }

    [Fact]
    public async Task CancellingDeletesThePartialFile()
    {
        using var cts = new CancellationTokenSource();
        DownloadResult r = await Get(new FakeHttp(_ => Ok(Body)), Asset(), progress: (_, _) => cts.Cancel(), ct: cts.Token);

        Assert.Equal(DownloadFailure.Cancelled, r.Failure);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task ProgressCountsUpToTheDeclaredSize()
    {
        var seen = new List<(long Got, long Total)>();
        await Get(new FakeHttp(_ => Ok(Body)), Asset(), progress: (g, t) => seen.Add((g, t)));

        Assert.NotEmpty(seen);
        Assert.Equal((Body.LongLength, Body.LongLength), seen[^1]);
    }

    [Fact]
    public void SweepEmptiesTheFolder()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "findra-setup-x64.exe"), "old");

        UpdateDownload.Sweep(_dir);

        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Theory]
    [InlineData("https://github.com/a", true)]
    [InlineData("https://objects.githubusercontent.com/a", true)]
    [InlineData("https://release-assets.githubusercontent.com/a", true)]
    [InlineData("http://github.com/a", false)]
    [InlineData("https://githubusercontent.com.evil.example/a", false)]
    [InlineData("https://notgithub.com/a", false)]
    public void OnlyGitHubsOwnHostsOverHttpsAreAllowed(string url, bool allowed) =>
        Assert.Equal(allowed, UpdateDownload.AllowedHost(new Uri(url)));
}
