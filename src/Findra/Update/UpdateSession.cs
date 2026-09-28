namespace Findra;

/// <summary>
/// Drives one update window: asks <see cref="UpdateFlow"/> for each move, starts the work the move
/// names, and shows the view it leads to. Everything with an effect arrives as a delegate and
/// every continuation comes back through <c>post</c>, so this runs in a test without a window, a
/// network or a process, and on the interface thread in the product.
/// </summary>
public sealed class UpdateSession
{
    private readonly Func<CancellationToken, Task<UpdateResult>> _check;
    private readonly Func<ReleaseAsset, Action<long, long>, CancellationToken, Task<DownloadResult>> _download;
    private readonly Func<string, ReleaseAsset, CancellationToken, Task<Handoff>> _runInstaller;
    private readonly Func<CancellationToken, Task<Handoff>> _runWinget;
    private readonly Action _openReleases;
    private readonly Action<UpdateView> _show;
    private readonly Action _close;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _gone = new();
    private CancellationTokenSource? _downloading;
    private Task? _lastDownload;
    private bool _closed;

    public UpdateView View { get; private set; }

    public UpdateSession(UpdateView first,
                         Func<CancellationToken, Task<UpdateResult>> check,
                         Func<ReleaseAsset, Action<long, long>, CancellationToken, Task<DownloadResult>> download,
                         Func<string, ReleaseAsset, CancellationToken, Task<Handoff>> runInstaller,
                         Func<CancellationToken, Task<Handoff>> runWinget,
                         Action openReleases, Action<UpdateView> show, Action close, Action<Action> post)
    {
        View = first ?? throw new ArgumentNullException(nameof(first));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _runInstaller = runInstaller ?? throw new ArgumentNullException(nameof(runInstaller));
        _runWinget = runWinget ?? throw new ArgumentNullException(nameof(runWinget));
        _openReleases = openReleases ?? throw new ArgumentNullException(nameof(openReleases));
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _close = close ?? throw new ArgumentNullException(nameof(close));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    public void Begin() => Apply(new UpdateMove(View, UpdateAction.Check));

    public void PressClose() => Apply(UpdateFlow.Close(View));

    public void PressGo() => Apply(UpdateFlow.Go(View));

    /// <summary>The window has gone. A download in flight stops and deletes what arrived; an answer
    /// still on its way is dropped rather than painted onto a window nobody can see.</summary>
    public void Closed()
    {
        _closed = true;
        _downloading?.Cancel();
        _gone.Cancel();
    }

    private void Apply(UpdateMove m)
    {
        if (_closed) return;
        View = m.View;
        _show(View);
        switch (m.Action)
        {
            case UpdateAction.None: return;
            case UpdateAction.Check: _ = CheckAsync(); return;
            case UpdateAction.Download: _lastDownload = DownloadAsync(_lastDownload); return;
            case UpdateAction.CancelDownload: _downloading?.Cancel(); return;
            case UpdateAction.RunInstaller:
            {
                string path = View.DownloadedTo!;
                ReleaseAsset asset = View.Installer!;
                _ = HandOffAsync(ct => _runInstaller(path, asset, ct), HandoffOutcome.DidNotRun);
                return;
            }
            case UpdateAction.RunWinget: _ = HandOffAsync(_runWinget, HandoffOutcome.WingetFailed); return;
            case UpdateAction.OpenReleases: _openReleases(); _close(); return;
            case UpdateAction.Close: _close(); return;
            default: throw new ArgumentOutOfRangeException(nameof(m), m.Action, "no work for this action");
        }
    }

    private async Task CheckAsync()
    {
        UpdateResult r;
        try { r = await _check(_gone.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("update", "the check could not run: " + ex.Message);
            r = new UpdateResult(UpdateState.Unknown, null, null, Config.Default);
        }
        _post(() => { if (!_closed) Apply(new UpdateMove(UpdateFlow.Checked(View, r), UpdateAction.None)); });
    }

    /// <summary>
    /// One download. Its progress and its answer count only while it is still the current one:
    /// Update now, Cancel, Update now again leaves the first download's own "cancelled" and its
    /// last progress on their way, and they must not move the window back to the offer or move the
    /// new bar. It also waits for the download before it to let go of the file, which it opens
    /// exclusively and which this one is about to write.
    /// </summary>
    private async Task DownloadAsync(Task? before)
    {
        var mine = CancellationTokenSource.CreateLinkedTokenSource(_gone.Token);
        _downloading = mine;
        ReleaseAsset asset = View.Installer!;
        if (before is not null)
        {
            try { await before.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }   // it answered for itself
        }

        DownloadResult r;
        try
        {
            r = await _download(asset,
                (got, total) => _post(() =>
                {
                    if (!Current(mine)) return;
                    // Repainted only when the whole percent moves: a download reports about a
                    // thousand times, and each repaint re-measures the window.
                    int before = Percent(View);
                    View = UpdateFlow.Progress(View, got, total);
                    if (Percent(View) != before) _show(View);
                }),
                mine.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("update", "the download failed: " + ex.Message);
            r = DownloadResult.Failed(DownloadFailure.Network, ex.Message);
        }
        _post(() => { if (Current(mine)) Apply(UpdateFlow.Downloaded(View, r)); });
    }

    private static int Percent(UpdateView v) => v.Total > 0 ? (int)(v.Got * 100 / v.Total) : 0;

    private bool Current(CancellationTokenSource download) => !_closed && ReferenceEquals(download, _downloading);

    /// <summary>A hand-off that faults rather than answering still answers: the window has no
    /// buttons and cannot be closed while one runs, so silence would keep it up until Findra
    /// quit.</summary>
    private async Task HandOffAsync(Func<CancellationToken, Task<Handoff>> run, HandoffOutcome whenItFaults)
    {
        Handoff h;
        try { h = await run(_gone.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("update", "the hand-off failed: " + ex.Message);
            h = new Handoff(whenItFaults, "It could not be started: " + ex.Message);
        }
        _post(() => { if (!_closed) Apply(new UpdateMove(UpdateFlow.HandedOff(View, h), UpdateAction.None)); });
    }
}
