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
    /// parse, including null - unparseable never wins), positive means
    /// <paramref name="running"/> is newer.</summary>
    public static int Compare(string? running, string? latest)
    {
        Version? r = ParseVersion(running);
        Version? l = ParseVersion(latest);
        if (r is null || l is null) return 0;
        return r.CompareTo(l);
    }

    private static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
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
    /// command; a downloaded installer and a source build get a link to the releases page; an
    /// unrecognised source gets both, since it might be any of them. Matched case-insensitively -
    /// nothing writes <see cref="Config.InstallSource"/> yet, so its casing convention is not
    /// fixed.</summary>
    public static string Advice(string installSource, string version) => installSource.ToLowerInvariant() switch
    {
        "winget" => $"Findra {version} is available. Run winget upgrade blakazulu.Findra to update.",
        // An installed copy and a source build get the same place to go and different reasons for
        // going there: one downloads the new installer, the other pulls and rebuilds. Neither can
        // act on a winget command, which is why they are not in the default arm.
        "installer" => $"Findra {version} is available. Download it from " +
                       "https://github.com/blakazulu/findra/releases.",
        "source" => $"Findra {version} is available. See https://github.com/blakazulu/findra/releases for the release notes.",
        _ => $"Findra {version} is available. Run winget upgrade blakazulu.Findra, or see " +
             "https://github.com/blakazulu/findra/releases for the release notes.",
    };

    /// <summary>Runs the check against a caller-supplied fetch delegate, never the network
    /// directly, so every test runs offline. Short-circuits when disabled (no call to
    /// <paramref name="fetch"/> either way, <paramref name="force"/> included - off means off
    /// even from a forced tray check) or, unless <paramref name="force"/> bypasses the gate,
    /// not due yet. Catches everything the fetch can throw because a broken network is a log
    /// line, not a dialog - except <paramref name="ct"/> itself being cancelled, which means
    /// the app is quitting rather than that the network failed, and is reported without
    /// touching <c>LastUpdateCheck</c> so a quit during startup does not silently use up
    /// today's check. Every other outcome, including the fetch's own internal timeout, stamps
    /// the new <c>LastUpdateCheck</c> so a dead network is not retried every launch.</summary>
    public static async Task<UpdateResult> CheckAsync(
        Config config, Func<CancellationToken, Task<string?>> fetch, DateTime utcNow, CancellationToken ct,
        bool force = false)
    {
        if (!config.CheckForUpdates)
            return new UpdateResult(UpdateState.Disabled, null, null, config);

        if (!force && !IsDue(config, utcNow))
            return new UpdateResult(UpdateState.NotDue, null, null, config);

        string? latest;
        try
        {
            latest = await fetch(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Warn("startup", "update check cancelled (shutdown), not counted against the daily check");
            return new UpdateResult(UpdateState.Unknown, null, null, config);
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

        if (ParseVersion(latest) is null)
        {
            // Compare(...) would return 0 for an unparseable tag, and 0 used to be routed
            // straight into "Current" below - telling the user they are up to date on no
            // information, which spec 9b calls out as worse than not checking at all. So an
            // unparseable tag is caught here, before the comparison, and reported as Unknown.
            Log.Warn("startup", $"update check: latest tag \"{latest}\" does not parse as a version");
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

    /// <summary>Builds the <see cref="HttpClient"/> this file's "no machine or install
    /// identifier" guarantee actually depends on. A default-constructed <c>HttpClient</c>
    /// keeps cookies on with a per-client <c>CookieContainer</c> and follows redirects; the
    /// first response from <c>api.github.com</c> sets a tracking cookie (<c>_octo</c>), and a
    /// default client would echo it straight back on every check after the first, which is
    /// exactly the kind of identifier spec 9b promises never to send. This client turns both
    /// off, and caps the buffered response at 64 KB - comfortably larger than a release JSON
    /// payload - so a captive portal or a MITM response cannot buffer unboundedly inside the
    /// fetch's own 10 second timeout.</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
    })
    {
        MaxResponseContentBufferSize = 64 * 1024,
    };

    /// <summary>The real fetch (spec 9b): a single anonymous GET to the GitHub Releases API
    /// for this repository, User-Agent only, no query parameters, no machine or install
    /// identifier - provided <paramref name="client"/> came from <see cref="CreateClient"/>;
    /// see its doc comment for why a default <c>HttpClient</c> would not hold that guarantee.
    /// Not called by any test beyond <see cref="CreateClient"/>'s own, and not wired into the
    /// app - a later task passes <c>CreateClient()</c>'s result and this method as the
    /// <c>fetch</c> delegate to <see cref="CheckAsync"/>.
    ///
    /// GitHub's <c>/releases/latest</c> endpoint already excludes drafts and prereleases, so
    /// there is no separate prerelease filter to apply here.
    ///
    /// Note: spec 9b also says prereleases are ignored "unless the running build is itself a
    /// prerelease". That half is deferred, not implemented: <see cref="Log.Version"/> emits
    /// only <c>Major.Minor.Build</c>, so a prerelease running build currently has no tag to
    /// carry that fact, and there is nothing here to key the filter off.</summary>
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
