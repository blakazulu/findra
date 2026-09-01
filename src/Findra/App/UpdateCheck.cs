using System.Net.Http;
using System.Text.Json;

namespace Findra;

/// <summary>Outcome of an update check, one value per thing the tray needs to say.</summary>
public enum UpdateState
{
    /// <summary>The user turned checks off. No request was made.</summary>
    Disabled,
    /// <summary>Checked within the last 24 hours; nothing new was asked.</summary>
    NotDue,
    /// <summary>A due check tried to run but the fetch failed or returned nothing usable.
    /// Not an error the user needs to acknowledge - a broken network happens.</summary>
    Unknown,
    /// <summary>The running build is the latest release, or newer.</summary>
    Current,
    /// <summary>A newer release exists. <see cref="UpdateResult.Advice"/> says what to do
    /// about it, matched to how this copy was installed.</summary>
    Available,
}

/// <summary>What a check found, plus the <see cref="Config"/> to save afterwards - the
/// caller persists it so a dead network does not retry on every launch.</summary>
public sealed record UpdateResult(UpdateState State, string? Latest, string? Advice, Config Config);

/// <summary>
/// The one thing Findra sends off this machine (spec 9b): an anonymous check against the
/// GitHub Releases API, at most once a day, that never blocks and never installs anything.
/// Every comparison here is on parsed version numbers, never string ordering, and a version
/// that fails to parse is never treated as newer than one that does.
/// </summary>
public static class UpdateCheck
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>Compares two version strings after stripping a leading `v`/`V`. Negative
    /// means <paramref name="running"/> is older, zero means equal (or either side failed to
    /// parse - unparseable never wins), positive means <paramref name="running"/> is newer.</summary>
    public static int Compare(string running, string latest)
    {
        Version? r = ParseVersion(running);
        Version? l = ParseVersion(latest);
        if (r is null || l is null) return 0;
        return r.CompareTo(l);
    }

    private static Version? ParseVersion(string s)
    {
        string trimmed = s.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V')) trimmed = trimmed[1..];
        return Version.TryParse(trimmed, out var v) ? v : null;
    }

    /// <summary>Whether a check is both wanted and overdue. Disabled is handled separately by
    /// <see cref="CheckAsync"/> so it can report <see cref="UpdateState.Disabled"/> rather than
    /// just looking like an ordinary not-due result.</summary>
    public static bool IsDue(Config config, DateTime utcNow) =>
        config.CheckForUpdates &&
        (config.LastUpdateCheck is null || utcNow - config.LastUpdateCheck.Value >= CheckInterval);

    /// <summary>The action matching how this copy was installed. winget gets the upgrade
    /// command; a source build gets a link to the release notes; an unrecognised source gets
    /// both, since it might be either.</summary>
    public static string Advice(string installSource, string version) => installSource switch
    {
        "winget" => $"Findra {version} is available. Run winget upgrade blakazulu.Findra to update.",
        "source" => $"Findra {version} is available. See https://github.com/blakazulu/findra/releases for the release notes.",
        _ => $"Findra {version} is available. Run winget upgrade blakazulu.Findra, or see " +
             "https://github.com/blakazulu/findra/releases for the release notes.",
    };

    /// <summary>Runs the check against a caller-supplied fetch delegate, never the network
    /// directly, so every test runs offline. Short-circuits when disabled or not due (no call
    /// to <paramref name="fetch"/> either way), and catches everything the fetch can throw -
    /// including a cancellation - because a broken network is a log line, not a dialog. The
    /// returned <see cref="Config"/> carries the new <c>LastUpdateCheck</c> whenever a check
    /// actually ran, success or failure, so a dead network is not retried every launch.</summary>
    public static async Task<UpdateResult> CheckAsync(
        Config config, Func<CancellationToken, Task<string?>> fetch, DateTime utcNow, CancellationToken ct)
    {
        if (!config.CheckForUpdates)
            return new UpdateResult(UpdateState.Disabled, null, null, config);

        if (!IsDue(config, utcNow))
            return new UpdateResult(UpdateState.NotDue, null, null, config);

        string? latest;
        try
        {
            latest = await fetch(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("startup", "update check failed: " + ex.Message);
            return new UpdateResult(UpdateState.Unknown, null, null, config with { LastUpdateCheck = utcNow });
        }

        Config checkedConfig = config with { LastUpdateCheck = utcNow };

        if (string.IsNullOrWhiteSpace(latest))
        {
            Log.Warn("startup", "update check returned no usable tag");
            return new UpdateResult(UpdateState.Unknown, null, null, checkedConfig);
        }

        if (Compare(Log.Version, latest) >= 0)
        {
            Log.Info("startup", $"running {Log.Version}, latest release is {latest}: up to date");
            return new UpdateResult(UpdateState.Current, latest, null, checkedConfig);
        }

        string advice = Advice(config.InstallSource ?? "unknown", latest);
        Log.Info("startup", $"running {Log.Version}, latest release is {latest}: update available");
        return new UpdateResult(UpdateState.Available, latest, advice, checkedConfig);
    }

    /// <summary>The real fetch (spec 9b): a single anonymous GET to the GitHub Releases API
    /// for this repository, User-Agent only, no query parameters, no machine or install
    /// identifier. Not called by any test and not wired into the app - a later task passes
    /// this as the <c>fetch</c> delegate to <see cref="CheckAsync"/>.
    ///
    /// GitHub's <c>/releases/latest</c> endpoint already excludes drafts and prereleases, so
    /// there is no separate prerelease filter to apply here.</summary>
    public static async Task<string?> FetchLatestTagAsync(HttpClient client, string version, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/blakazulu/findra/releases/latest");
        request.Headers.UserAgent.ParseAdd($"findra/{version}");

        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

        return doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
    }
}
