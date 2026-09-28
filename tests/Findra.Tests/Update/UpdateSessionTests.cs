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
        public List<(TaskCompletionSource<DownloadResult> Result, Action<long, long> Progress, CancellationToken Token)> DownloadCalls = [];
        public int Installs, Wingets, Releases, Closes;
        public List<UpdateView> Shown = [];
        public List<string> Notes = [];
        public UpdateSession Session;
        private readonly BlockingCollection<Action> _posted = [];

        public Harness(string source = "installer", Func<CancellationToken, Task<Handoff>>? runWinget = null)
        {
            Session = new UpdateSession(
                UpdateFlow.Start("1.0.0", source),
                check: _ => Check.Task,
                download: (_, progress, ct) =>
                {
                    var tcs = new TaskCompletionSource<DownloadResult>();
                    lock (DownloadCalls) DownloadCalls.Add((tcs, progress, ct));
                    return tcs.Task;
                },
                runInstaller: (_, _, _) => { Installs++; return new TaskCompletionSource<Handoff>().Task; },
                runWinget: runWinget ?? (_ => { Wingets++; return new TaskCompletionSource<Handoff>().Task; }),
                openReleases: () => Releases++,
                show: v => Shown.Add(v),
                close: () => Closes++,
                post: a => _posted.Add(a),
                note: n => { lock (Notes) Notes.Add(n); });
        }

        public int Downloads { get { lock (DownloadCalls) return DownloadCalls.Count; } }
        public TaskCompletionSource<DownloadResult> Download { get { lock (DownloadCalls) return DownloadCalls[^1].Result; } }
        public CancellationToken DownloadToken { get { lock (DownloadCalls) return DownloadCalls[^1].Token; } }

        /// <summary>Run what the session posted back, on this thread, as the dispatcher would.
        /// Waits for the first, because the continuation that posts it may run on another
        /// thread.</summary>
        public void Pump()
        {
            Assert.True(_posted.TryTake(out Action? first, TimeSpan.FromSeconds(5)), "the session posted nothing back");
            first();
            while (_posted.TryTake(out Action? next)) next();
        }

        /// <summary>Wait until the session has asked for <paramref name="n"/> downloads; a new one
        /// starts only once the one before it has let go of the file.</summary>
        public void WaitForDownloads(int n)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Downloads < n && clock.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
            Assert.Equal(n, Downloads);
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
    public void ALateAnswerFromACancelledDownloadDoesNotOverwriteTheNewOne()
    {
        // Update now, Cancel, Update now again: the first download's own "cancelled", and its
        // progress, arrive after the second has begun, and must not move the window back to the
        // offer or move its bar.
        var h = new Harness();
        h.Session.Begin();
        h.Offer();
        h.Session.PressGo();
        var first = h.DownloadCalls[0];
        h.Session.PressClose();
        h.Session.PressGo();

        first.Progress(50, 100);
        first.Result.SetResult(DownloadResult.Failed(DownloadFailure.Cancelled, "cancelled"));
        h.Pump();

        Assert.Equal(UpdateStep.Downloading, h.Session.View.Step);
        Assert.Equal(0, h.Session.View.Got);
        h.WaitForDownloads(2);
    }

    [Fact]
    public void ProgressRepaintsOnlyWhenTheWholePercentMoves()
    {
        // A download reports every 80 KB, about a thousand times for an installer; each repaint
        // re-measures the window and pulls it back inside the screen.
        var h = new Harness();
        h.Session.Begin();
        h.Offer();
        h.Session.PressGo();
        int before = h.Shown.Count;

        var progress = h.DownloadCalls[0].Progress;
        for (long got = 1; got <= 9; got++) progress(got, 1000);   // all under one percent
        progress(10, 1000);                                         // one percent
        h.Pump();

        Assert.Equal(before + 1, h.Shown.Count);
        Assert.Equal(10, h.Session.View.Got);
    }

    [Fact]
    public void ADownloadThatThrowsSaysSoRatherThanHanging()
    {
        var h = new Harness();
        h.Session.Begin();
        h.Offer();
        h.Session.PressGo();

        h.Download.SetException(new UnauthorizedAccessException("the updates folder is read-only"));
        h.Pump();

        Assert.Equal(UpdateStep.DownloadFailed, h.Session.View.Step);
    }

    [Fact]
    public void AHandOffThatThrowsLeavesAWayOut()
    {
        // No buttons and no close while winget runs: a hand-off that faults instead of answering
        // would leave that window up until Findra quit.
        var h = new Harness("winget", _ => Task.FromException<Handoff>(new AggregateException(new InvalidOperationException("access denied"))));
        h.Session.Begin();
        h.Check.SetResult(new UpdateResult(UpdateState.Available, "1.1.0", null, Config.Default));
        h.Pump();
        h.Session.PressGo();
        h.Pump();

        Assert.Equal(UpdateStep.WingetFailed, h.Session.View.Step);
        Assert.True(UpdateFlow.Closable(h.Session.View));
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

    [Fact]
    public void UpdateNowLeavesATrailInTheLog()
    {
        // The log is what arrives from somebody else's machine. Without these lines the only sign
        // that Update now was pressed is the installer stopping Findra.
        var h = new Harness();
        h.Session.Begin();
        h.Offer();
        h.Session.PressGo();
        h.Download.SetResult(DownloadResult.Ok(@"C:\u\findra-setup-x64.exe"));
        h.Pump();

        Assert.Collection(h.Notes,
            n => Assert.StartsWith("Update now: downloading findra-setup-x64.exe", n),
            n => Assert.StartsWith("downloaded findra-setup-x64.exe", n),
            n => Assert.StartsWith("starting the installer findra-setup-x64.exe", n));
    }

    [Fact]
    public void ARefusedDownloadSaysWhyInTheLog()
    {
        var h = new Harness();
        h.Session.Begin();
        h.Offer();
        h.Session.PressGo();
        h.Download.SetResult(DownloadResult.Failed(DownloadFailure.Digest, "the checksum does not match"));
        h.Pump();

        Assert.Contains(h.Notes, n => n.Contains("Digest") && n.Contains("the checksum does not match"));
    }

    [Fact]
    public void AHandOffThatReturnsIsLoggedAsInstallingNothing()
    {
        var h = new Harness("winget", _ => Task.FromResult(new Handoff(HandoffOutcome.WingetFailed, "winget exited 1")));
        h.Session.Begin();
        h.Check.SetResult(new UpdateResult(UpdateState.Available, "1.1.0", null, Config.Default));
        h.Pump();
        h.Session.PressGo();
        h.Pump();

        Assert.Collection(h.Notes,
            n => Assert.StartsWith("Update now: starting winget upgrade", n),
            n => Assert.Equal("the hand-off returned, so nothing was installed (WingetFailed): winget exited 1", n));
    }
}
