using System.Collections.Concurrent;
using System.Net.Http;

using Findra;
using Xunit;

public class UpdateSessionTests
{
    private static readonly ReleaseAsset Asset =
        new("findra-setup-x64.exe", "https://github.com/a.exe", 100, new string('a', 64));

    private sealed class Harness
    {
        public TaskCompletionSource<UpdateResult> Check = new();
        public TaskCompletionSource<DownloadResult> Download = new();
        public int Downloads, Installs, Wingets, Releases, Closes;
        public CancellationToken DownloadToken;
        public List<UpdateView> Shown = [];
        public UpdateSession Session;
        private readonly BlockingCollection<Action> _posted = [];

        public Harness(string source = "installer")
        {
            Session = new UpdateSession(
                UpdateFlow.Start("1.0.0", source),
                check: _ => Check.Task,
                download: (_, _, ct) => { Downloads++; DownloadToken = ct; return Download.Task; },
                runInstaller: (_, _) => { Installs++; return new TaskCompletionSource<Handoff>().Task; },
                runWinget: _ => { Wingets++; return new TaskCompletionSource<Handoff>().Task; },
                openReleases: () => Releases++,
                show: v => Shown.Add(v),
                close: () => Closes++,
                post: a => _posted.Add(a));
        }

        /// <summary>Run what the session posted back, on this thread, as the dispatcher would.
        /// Waits for the first, because the continuation that posts it may run on another
        /// thread.</summary>
        public void Pump()
        {
            Assert.True(_posted.TryTake(out Action? first, TimeSpan.FromSeconds(5)), "the session posted nothing back");
            first();
            while (_posted.TryTake(out Action? next)) next();
        }

        public void Offer()
        {
            Check.SetResult(new UpdateResult(UpdateState.Available, "1.1.0", null, Config.Default, Asset));
            Pump();
        }
    }

    [Fact]
    public void BeginChecksAndShowsTheAnswer()
    {
        var h = new Harness();
        h.Session.Begin();
        h.Offer();

        Assert.Equal(UpdateStep.Available, h.Session.View.Step);
        Assert.Equal(UpdateStep.Available, h.Shown[^1].Step);
    }

    [Fact]
    public void AGoPressedWhileDownloadingStartsNothingNew()
    {
        var h = new Harness();
        h.Session.Begin();
        h.Offer();

        h.Session.PressGo();
        h.Session.PressGo();

        Assert.Equal(1, h.Downloads);
    }

    [Fact]
    public void ACheckedDownloadRunsTheInstaller()
    {
        var h = new Harness();
        h.Session.Begin();
        h.Offer();
        h.Session.PressGo();
        h.Download.SetResult(DownloadResult.Ok(@"C:\u\findra-setup-x64.exe"));
        h.Pump();

        Assert.Equal(1, h.Installs);
        Assert.Equal(UpdateStep.Installing, h.Session.View.Step);
    }

    [Fact]
    public void ClosingTheWindowCancelsTheDownload()
    {
        var h = new Harness();
        h.Session.Begin();
        h.Offer();
        h.Session.PressGo();

        h.Session.Closed();

        Assert.True(h.DownloadToken.IsCancellationRequested);
    }

    [Fact]
    public void AnAnswerThatArrivesAfterTheWindowClosedIsDropped()
    {
        var h = new Harness();
        h.Session.Begin();
        int shownBefore = h.Shown.Count;

        h.Session.Closed();
        h.Offer();

        Assert.Equal(shownBefore, h.Shown.Count);
        Assert.Equal(0, h.Closes);
    }

    [Fact]
    public void OpenReleasesOpensThePageAndClosesTheWindow()
    {
        var h = new Harness("source");
        h.Session.Begin();
        h.Offer();
        h.Session.PressGo();

        Assert.Equal((1, 1), (h.Releases, h.Closes));
    }

    [Fact]
    public void AWingetCopyRunsWinget()
    {
        var h = new Harness("winget");
        h.Session.Begin();
        h.Check.SetResult(new UpdateResult(UpdateState.Available, "1.1.0", null, Config.Default));
        h.Pump();
        h.Session.PressGo();

        Assert.Equal(1, h.Wingets);
        Assert.Equal(UpdateStep.Winget, h.Session.View.Step);
    }

    [Fact]
    public void ACheckThatThrowsIsUnreachableRatherThanAnUnhandledFault()
    {
        var h = new Harness();
        h.Session.Begin();
        h.Check.SetException(new HttpRequestException("no route"));
        h.Pump();

        Assert.Equal(UpdateStep.Unreachable, h.Session.View.Step);
    }
}
