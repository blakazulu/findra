namespace Findra;

/// <summary>Which of its states the update window is in.</summary>
public enum UpdateStep
{
    Checking, UpToDate, Unreachable, Available, Downloading, DownloadFailed,
    Installing, InstallerDidNotRun, Winget, WingetFailed,
}

/// <summary>What Update now does for this copy, by how it was installed.</summary>
public enum UpdateRoute { Installer, Winget, Releases }

/// <summary>The work a move asks the session to start.</summary>
public enum UpdateAction { None, Check, Download, CancelDownload, RunInstaller, RunWinget, OpenReleases, Close }

/// <summary>Everything the window shows, and everything the next move needs.</summary>
public sealed record UpdateView(UpdateStep Step, string Version, string? InstallSource)
{
    public string? Latest { get; init; }
    public ReleaseAsset? Installer { get; init; }
    public string? DownloadedTo { get; init; }
    public long Got { get; init; }
    public long Total { get; init; }
    public string? Problem { get; init; }
}

public readonly record struct UpdateMove(UpdateView View, UpdateAction Action);

/// <summary>
/// The update window as a list of states and the moves between them, with no window in it, so
/// every transition has a test. The session starts the work a move names; the window only paints
/// a view and reports which button was pressed.
/// </summary>
public static class UpdateFlow
{
    public static UpdateView Start(string version, string? installSource) =>
        new(UpdateStep.Checking, version, installSource);

    /// <summary>winget for a winget copy; the releases page for a source build, and for any copy
    /// whose release has no installer this machine can check; the installer for everything else,
    /// including a copy that never recorded how it arrived, since the installer works however
    /// Findra got here.</summary>
    public static UpdateRoute Route(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return (v.InstallSource ?? "unknown").ToLowerInvariant() switch
        {
            "winget" => UpdateRoute.Winget,
            "source" => UpdateRoute.Releases,
            _ => v.Installer is null ? UpdateRoute.Releases : UpdateRoute.Installer,
        };
    }

    public static UpdateView Checked(UpdateView v, UpdateResult r)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(r);
        return r.State switch
        {
            UpdateState.Current => v with { Step = UpdateStep.UpToDate, Latest = r.Latest },
            UpdateState.Available => v with { Step = UpdateStep.Available, Latest = r.Latest, Installer = r.Installer },
            UpdateState.Unknown => v with { Step = UpdateStep.Unreachable },
            UpdateState.Disabled or UpdateState.NotDue =>
                throw new InvalidOperationException($"a check somebody asked for never answers {r.State}"),
            _ => throw new ArgumentOutOfRangeException(nameof(r), r.State, "no step for this update state"),
        };
    }

    /// <summary>The left-hand button, or the only one.</summary>
    public static UpdateMove Close(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step switch
        {
            UpdateStep.Downloading => new(v with { Step = UpdateStep.Available, Got = 0 }, UpdateAction.CancelDownload),
            UpdateStep.UpToDate or UpdateStep.Unreachable or UpdateStep.Available or UpdateStep.DownloadFailed
                or UpdateStep.InstallerDidNotRun or UpdateStep.WingetFailed => new(v, UpdateAction.Close),
            UpdateStep.Checking or UpdateStep.Installing or UpdateStep.Winget => new(v, UpdateAction.None),
            _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no close move for this step"),
        };
    }

    /// <summary>The right-hand button: Update now, Open releases or Try again.</summary>
    public static UpdateMove Go(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step switch
        {
            UpdateStep.Available => Route(v) switch
            {
                UpdateRoute.Installer => Download(v),
                UpdateRoute.Winget => new(v with { Step = UpdateStep.Winget, Problem = null }, UpdateAction.RunWinget),
                UpdateRoute.Releases => new(v, UpdateAction.OpenReleases),
                _ => throw new ArgumentOutOfRangeException(nameof(v), Route(v), "no move for this route"),
            },
            UpdateStep.Unreachable => new(Start(v.Version, v.InstallSource), UpdateAction.Check),
            UpdateStep.DownloadFailed or UpdateStep.InstallerDidNotRun => Download(v),
            UpdateStep.Checking or UpdateStep.UpToDate or UpdateStep.Downloading or UpdateStep.Installing
                or UpdateStep.Winget or UpdateStep.WingetFailed => new(v, UpdateAction.None),
            _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no go move for this step"),
        };
    }

    private static UpdateMove Download(UpdateView v) =>
        new(v with { Step = UpdateStep.Downloading, Got = 0, Total = v.Installer!.Size, Problem = null, DownloadedTo = null },
            UpdateAction.Download);

    public static UpdateView Progress(UpdateView v, long got, long total)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step == UpdateStep.Downloading ? v with { Got = got, Total = total } : v;
    }

    /// <summary>A download that ends after Cancel was pressed changes nothing: the view has already
    /// gone back to the offer.</summary>
    public static UpdateMove Downloaded(UpdateView v, DownloadResult r)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(r);
        if (v.Step != UpdateStep.Downloading) return new(v, UpdateAction.None);
        return r.Failure switch
        {
            DownloadFailure.None => new(v with { Step = UpdateStep.Installing, DownloadedTo = r.Path }, UpdateAction.RunInstaller),
            DownloadFailure.Cancelled => new(v with { Step = UpdateStep.Available, Got = 0 }, UpdateAction.None),
            DownloadFailure.Digest or DownloadFailure.Short or DownloadFailure.Oversized or DownloadFailure.Host
                or DownloadFailure.TooLarge or DownloadFailure.Network => new(v with { Step = UpdateStep.DownloadFailed, Problem = Problem(r.Failure) },
                                                  UpdateAction.None),
            _ => throw new ArgumentOutOfRangeException(nameof(r), r.Failure, "no step for this download failure"),
        };
    }

    public static UpdateView HandedOff(UpdateView v, Handoff h)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(h);
        return h.Outcome switch
        {
            HandoffOutcome.DidNotRun => v with { Step = UpdateStep.InstallerDidNotRun, Problem = h.Message },
            HandoffOutcome.WingetFailed or HandoffOutcome.WingetNothingNewer or HandoffOutcome.WingetMissing =>
                v with { Step = UpdateStep.WingetFailed, Problem = h.Message },
            _ => throw new ArgumentOutOfRangeException(nameof(h), h.Outcome, "no step for this hand-off"),
        };
    }

    /// <summary>Everything but a hand-off in flight: the installer or winget is already running and
    /// cannot be called back, so the window stays to say so.</summary>
    public static bool Closable(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step is not (UpdateStep.Installing or UpdateStep.Winget);
    }

    /// <summary>Whether a close is refused: only one a person started, and only while a hand-off
    /// runs. Quitting Findra and Windows shutting down are never refused - a window that did would
    /// hold Findra open and keep Windows from shutting down.</summary>
    public static bool RefusesClose(UpdateView v, bool personClosing) => personClosing && !Closable(v);

    private static string Problem(DownloadFailure why) => why switch
    {
        DownloadFailure.Digest => "The download did not match the checksum GitHub published for it, so it was deleted. Nothing was installed.",
        DownloadFailure.Short => "The download arrived incomplete, so it was deleted. Nothing was installed.",
        DownloadFailure.Oversized => "The download was larger than GitHub said it would be, so it was deleted. Nothing was installed.",
        DownloadFailure.Host => "The download pointed somewhere other than GitHub, so it was refused. Nothing was installed.",
        DownloadFailure.TooLarge => "The installer was far larger than any Findra installer, so it was refused. Nothing was installed.",
        DownloadFailure.Network => "The download did not get through. Nothing was installed.",
        _ => throw new ArgumentOutOfRangeException(nameof(why), why, "no problem text for this failure"),
    };
}
