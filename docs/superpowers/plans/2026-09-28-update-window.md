# The update window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pressing Check for updates (tray) or Check now (Settings) opens one update window that checks, offers Update now, and either downloads, verifies and runs the installer or runs the winget upgrade; after a silent install Findra starts again.

**Architecture:** Four pure or seam-driven units (`UpdateCheck` changes, `UpdateDownload`, `UpdateHandoff`, `UpdateFlow`) carry all the logic and all the tests. `UpdateSession` glues them to a small Skia window (`UpdateWindow`, painted by `UpdatePainter` from `UpdatePrompt`'s wording and layout) through delegates, so the shell (`App.axaml.cs`) only wires. The installer learns from `--stop`'s exit code whether to start Findra again, through `explorer.exe` so it never runs elevated.

**Tech Stack:** .NET 10, Avalonia 11 (borderless windows painted with SkiaSharp through `ISkiaSharpApiLeaseFeature`), xUnit, Inno Setup 6, winget.

**Spec:** `docs/superpowers/specs/2026-09-28-update-window-design.md`

## Global Constraints

- `dotnet build -warnaserror -t:Rebuild` has zero warnings after every task; `dotnet test` is green after every task.
- TDD: every test in a task is written and seen failing before the code that passes it.
- `CHANGELOG.md` changes in every commit, under `## [Unreleased]` (Keep a Changelog 1.1.0), written for a person reading release notes.
- No em-dashes or en-dashes and no emojis in any user-facing text, docs or UI copy; use a hyphen.
- No Claude or AI co-author line in any commit message.
- A shipped comment names the thing, never a plan, task number or review.
- Every `switch` over one of this feature's enums writes each arm out and throws in the default arm.
- The background check does not change: at most once per 24 hours, no request while `CheckForUpdates` is false, never opens a window.
- A manual check (the window) ignores both the 24-hour gate and `CheckForUpdates`.
- The check's request does not change: one anonymous GET to `UpdateCheck.ReleasesUrl` or, for `InstallSource == "winget"`, `UpdateCheck.CatalogueUrl`.
- Installer asset names are exactly `findra-setup-x64.exe` and `findra-setup-arm64.exe`, chosen from `RuntimeInformation.ProcessArchitecture`; any other architecture has no asset.
- Downloads: HTTPS only; every URL and redirect on `github.com` or a host ending in `.githubusercontent.com`; declared size at most 512 MB (`UpdateDownload.MaxBytes`); length and SHA-256 must both match; a release entry with no `sha256:` digest offers no download.
- Downloads go to `%LOCALAPPDATA%\Findra\updates\` (`Paths.Updates`).
- The installer is started with exactly `/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-`.
- winget is started as `winget` with exactly `upgrade --id blakazulu.Findra --exact --accept-source-agreements --accept-package-agreements --disable-interactivity`, no console window, both streams read as they arrive, killed after 20 minutes.
- `--stop` exits 2 when it stopped a running interface, 0 otherwise.
- Findra never runs elevated: the installer starts it again through `explorer.exe`, and only after a silent install that stopped a running interface.
- The release after this plan is 0.6.0 (not part of this plan's tasks).

## Review Focus

1. **Two presses of Update now, or Check now pressed in Settings while the tray's window is downloading** - a person expects one window and one download. Pinned in Task 6 (`AGoPressedWhileDownloadingStartsNothingNew`); the second open is the shell's `UpdateWindow.Open` check, which brings the open window forward.
2. **The window closed with Esc while the check or the download is still running** - the late answer must not reopen or repaint a closed window, and a download must stop and delete its partial file. Pinned in Task 6 (`AnAnswerThatArrivesAfterTheWindowClosedIsDropped`, `ClosingTheWindowCancelsTheDownload`).
3. **GitHub writing the digest in upper case, or with a different prefix case** - the file must still match. Pinned in Task 1 (`AnUpperCaseDigestIsReadAsLowerCase`) and Task 2 (`TheDigestIsComparedWithoutCaringAboutCase`).
4. **A redirect whose `Location` is relative** - resolved against the hop it came from, and still held to the host rule. Pinned in Task 2 (`ARelativeRedirectIsResolvedAgainstTheHopItCameFrom`).
5. **winget's output full of progress bars (`\r`, backspaces, block characters)** - the failure message must be the last readable line. Pinned in Task 3 (`TheLastLineIsWhatFollowsTheLastCarriageReturn`).

---

## File map

| File | Responsibility |
|---|---|
| `src/Findra/Update/LatestRelease.cs` (create) | `ReleaseAsset`, `LatestRelease` records |
| `src/Findra/App/UpdateCheck.cs` (modify) | `manual` flag, `InstallerName`, `InstallerOf`, fetch returns `LatestRelease` |
| `src/Findra/Core/Paths.cs` (modify) | `Paths.Updates` |
| `src/Findra/Update/UpdateDownload.cs` (create) | download, host rule, cap, verify, sweep |
| `src/Findra/Update/UpdateHandoff.cs` (create) | installer and winget start info, run, outcomes, real process runner |
| `src/Findra/Update/UpdateFlow.cs` (create) | the window's states and transitions, pure |
| `src/Findra/Update/UpdatePrompt.cs` (move from `Settings/`, rewrite) | wording, labels and layout per `UpdateView` |
| `src/Findra/Update/UpdatePainter.cs` (create) | paints the window from an `UpdateView` |
| `src/Findra/Update/UpdateSession.cs` (create) | drives the flow through delegates; knows nothing about Avalonia |
| `src/Findra/Update/UpdateWindow.cs` (create) | the Avalonia window |
| `src/Findra/Diagnostics/SearchShot.cs` (modify) | `update*` states replace `settingsuptodate`, `settingsupdate`, `settingsasking` |
| `src/Findra/Settings/SettingsWindow.cs`, `SettingsPainter.cs`, `SettingsModel.cs`, `SettingsActions.cs` (modify) | the in-pane panel, `UpdateNow` and the Off state go |
| `src/Findra/App/App.axaml.cs` (modify) | tray, Settings host, background check, `LastRunVersion`, sweep, window wiring |
| `src/Findra/App/Config.cs` (modify) | `LastRunVersion` |
| `src/Findra/Startup/Uninstall.cs` (modify) | `StopExitCode`, `StopAll` returns it |
| `installer/findra.iss` (modify) | `WasRunning`, `RestartAfterSilentInstall`, the `explorer.exe` `[Run]` entry |
| Tests under `tests/Findra.Tests/...` | as named per task |
| Docs | spec §9b, `PRIVACY.md`, README, site, `FirstRun.Disclosure`, CLAUDE.md, e2e docs, CHANGELOG |

---

### Task 1: A manual check, and the installer in the release record

**Files:**
- Create: `src/Findra/Update/LatestRelease.cs`
- Modify: `src/Findra/App/UpdateCheck.cs`
- Modify: `src/Findra/App/App.axaml.cs` (the one `CheckAsync` call site in `RunUpdateCheck`)
- Test: `tests/Findra.Tests/App/UpdateCheckTests.cs`

**Interfaces:**
- Produces: `record ReleaseAsset(string Name, string Url, long Size, string Sha256)`; `record LatestRelease(string Tag, ReleaseAsset? Installer)`; `UpdateResult(..., ReleaseAsset? Installer = null)`; `UpdateCheck.CheckAsync(Config, Func<CancellationToken, Task<LatestRelease?>>, DateTime, CancellationToken, bool manual = false)`; `UpdateCheck.InstallerName(Architecture) : string?`; `UpdateCheck.InstallerOf(JsonElement, Architecture) : ReleaseAsset?`; `UpdateCheck.FetchLatestAsync(HttpClient, string version, string? installSource, Architecture arch, CancellationToken) : Task<LatestRelease?>`.

- [ ] **Step 1: Switch the existing tests to the new fetch type, and write the new tests**

In `UpdateCheckTests.cs`, add at the top of the class:

```csharp
    private static Task<LatestRelease?> Release(string? tag) =>
        Task.FromResult(tag is null ? null : new LatestRelease(tag, null));
```

Replace every `Task.FromResult<string?>(X)` in the file with `Release(X)` (a mechanical replace; `Task.FromResult<string?>(null)` becomes `Release(null)`). Rename `ForceBypassesTheDailyGate` to `AManualCheckBypassesTheDailyGate` and change its `force: true` to `manual: true`. Delete `ForceDoesNotBypassCheckForUpdatesBeingOff`. Add:

```csharp
    [Fact]
    public async Task AManualCheckAsksEvenWithTheSwitchOff()
    {
        // The person pressed Check now. Off stops the daily check; it does not refuse an answer
        // to somebody who asked.
        int calls = 0;
        Config off = Config.Default with { CheckForUpdates = false };

        UpdateResult r = await UpdateCheck.CheckAsync(off,
            _ => { calls++; return Release("9.9.9"); }, DateTime.UtcNow, default, manual: true);

        Assert.Equal(1, calls);
        Assert.Equal(UpdateState.Available, r.State);
    }

    [Fact]
    public async Task AnAvailableUpdateCarriesThisMachinesInstaller()
    {
        var asset = new ReleaseAsset("findra-setup-x64.exe", "https://github.com/x/y.exe", 10, new string('a', 64));
        UpdateResult r = await UpdateCheck.CheckAsync(Config.Default,
            _ => Task.FromResult<LatestRelease?>(new LatestRelease("9.9.9", asset)), DateTime.UtcNow, default, manual: true);

        Assert.Equal(asset, r.Installer);
    }

    private const string ReleaseJson = """
        {"tag_name":"v0.6.0","assets":[
          {"name":"findra-setup-arm64.exe","browser_download_url":"https://github.com/blakazulu/findra/releases/download/v0.6.0/findra-setup-arm64.exe","size":82456001,"digest":"sha256:d8c8d89f22335ff99cd053aa882e7e4273ec03838d1b568d1391f6fff06b1ea3"},
          {"name":"findra-setup-x64.exe","browser_download_url":"https://github.com/blakazulu/findra/releases/download/v0.6.0/findra-setup-x64.exe","size":85727274,"digest":"SHA256:B4AE568C139F6FD3563EA1CC4AA7DC5B43AAAA695308090692A6679ACAF1B0B4"}]}
        """;

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, "findra-setup-x64.exe", 85727274L)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, "findra-setup-arm64.exe", 82456001L)]
    public void TheInstallerIsTheOneForThisMachinesArchitecture(
        System.Runtime.InteropServices.Architecture arch, string name, long size)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(ReleaseJson);
        ReleaseAsset? a = UpdateCheck.InstallerOf(doc.RootElement, arch);

        Assert.NotNull(a);
        Assert.Equal(name, a.Name);
        Assert.Equal(size, a.Size);
        Assert.EndsWith("/" + name, a.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpperCaseDigestIsReadAsLowerCase()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(ReleaseJson);
        ReleaseAsset? a = UpdateCheck.InstallerOf(doc.RootElement, System.Runtime.InteropServices.Architecture.X64);
        Assert.Equal("b4ae568c139f6fd3563ea1cc4aa7dc5b43aaaa695308090692a6679acaf1b0b4", a!.Sha256);
    }

    [Fact]
    public void AnotherArchitectureHasNoInstaller()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(ReleaseJson);
        Assert.Null(UpdateCheck.InstallerOf(doc.RootElement, System.Runtime.InteropServices.Architecture.X86));
    }

    [Theory]
    [InlineData("""{"name":"findra-setup-x64.exe","browser_download_url":"https://github.com/a.exe","size":10}""")]
    [InlineData("""{"name":"findra-setup-x64.exe","browser_download_url":"https://github.com/a.exe","size":10,"digest":null}""")]
    [InlineData("""{"name":"findra-setup-x64.exe","browser_download_url":"https://github.com/a.exe","size":10,"digest":"md5:abc"}""")]
    [InlineData("""{"name":"findra-setup-x64.exe","browser_download_url":"https://github.com/a.exe","size":10,"digest":"sha256:zz"}""")]
    [InlineData("""{"name":"findra-setup-x64.exe","browser_download_url":"https://github.com/a.exe","size":"10","digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    public void AnEntryWithNoUsableDigestOrSizeOffersNoDownload(string entry)
    {
        using var doc = System.Text.Json.JsonDocument.Parse($$"""{"tag_name":"v1.0.0","assets":[{{entry}}]}""");
        Assert.Null(UpdateCheck.InstallerOf(doc.RootElement, System.Runtime.InteropServices.Architecture.X64));
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateCheckTests"`
Expected: build errors (`LatestRelease`, `ReleaseAsset`, `InstallerOf`, `manual` do not exist).

- [ ] **Step 3: Write the records**

`src/Findra/Update/LatestRelease.cs`:

```csharp
namespace Findra;

/// <summary>One installer attached to a release, as GitHub's release record lists it: its file
/// name, where it downloads from, its size in bytes, and the SHA-256 GitHub computed when it was
/// uploaded, as lower-case hex.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string Sha256);

/// <summary>What a check learned: the newest version's tag, and the installer for this machine
/// when the source lists files at all. The winget catalogue lists versions only, so a winget copy
/// never has one; neither does a release with no installer for this architecture, or one GitHub
/// gives no digest for.</summary>
public sealed record LatestRelease(string Tag, ReleaseAsset? Installer);
```

- [ ] **Step 4: Change `UpdateCheck`**

In `UpdateCheck.cs`: add `using System.Runtime.InteropServices;`. Change the result record to

```csharp
public sealed record UpdateResult(UpdateState State, string? Latest, string? Advice, Config Config,
                                  ReleaseAsset? Installer = null);
```

Replace `CheckAsync`'s signature and its first two guards, its fetch and its last return:

```csharp
    public static async Task<UpdateResult> CheckAsync(
        Config config, Func<CancellationToken, Task<LatestRelease?>> fetch, DateTime utcNow, CancellationToken ct,
        bool manual = false)
    {
        // A manual check is somebody pressing Check now. Off stops the daily check and nothing
        // else: refusing to answer a person who asked is the silence the update window replaces.
        if (!manual && !config.CheckForUpdates)
            return new UpdateResult(UpdateState.Disabled, null, null, config);

        if (!manual && !IsDue(config, utcNow))
            return new UpdateResult(UpdateState.NotDue, null, null, config);

        LatestRelease? found;
        try
        {
            found = await fetch(ct).ConfigureAwait(false);
        }
```

keep both `catch` blocks as they are, then add `string? latest = found?.Tag;` directly after the try/catch (before `Config checkedConfig = ...`), and make the final return

```csharp
        return new UpdateResult(UpdateState.Available, latest, advice, checkedConfig, found!.Installer);
```

Update `CheckAsync`'s doc comment: replace "Short-circuits when disabled (no call to <paramref name="fetch"/> either way, <paramref name="force"/> included - off means off even from a forced tray check) or, unless <paramref name="force"/> bypasses the gate, not due yet." with "Short-circuits, unless <paramref name="manual"/>, when disabled or not due yet: a manual check is somebody pressing Check now, and it asks whatever the switch and the clock say."

Add below `NewestInCatalogue`:

```csharp
    /// <summary>The installer this machine runs, by the architecture of the running process, never
    /// assumed. Null for any architecture no installer is built for.</summary>
    public static string? InstallerName(Architecture arch) => arch switch
    {
        Architecture.X64 => "findra-setup-x64.exe",
        Architecture.Arm64 => "findra-setup-arm64.exe",
        _ => null,
    };

    /// <summary>This machine's installer from a release record, or null when the record has none,
    /// or has one without a size or a SHA-256 digest: a file that cannot be checked is never
    /// offered, because nothing unchecked is ever run.</summary>
    public static ReleaseAsset? InstallerOf(JsonElement release, Architecture arch)
    {
        if (InstallerName(arch) is not { } want) return null;
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array) return null;

        foreach (JsonElement a in assets.EnumerateArray())
        {
            if (Text(a, "name") != want) continue;
            if (Text(a, "browser_download_url") is not { Length: > 0 } url) return null;
            if (!a.TryGetProperty("size", out JsonElement size) || size.ValueKind != JsonValueKind.Number ||
                !size.TryGetInt64(out long bytes) || bytes <= 0) return null;
            if (Sha256Of(Text(a, "digest")) is not { } hex) return null;
            return new ReleaseAsset(want, url, bytes, hex);
        }
        return null;
    }

    private static string? Text(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out JsonElement v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // GitHub writes "sha256:<64 hex>". Anything else - another algorithm, a short value, no value -
    // is no digest at all.
    private static string? Sha256Of(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string hex = digest[prefix.Length..].ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }
```

Change `FetchLatestAsync`:

```csharp
    public static async Task<LatestRelease?> FetchLatestAsync(HttpClient client, string version, string? installSource,
                                                              Architecture arch, CancellationToken ct)
```

and its last line to

```csharp
        if (url == CatalogueUrl)
            return NewestInCatalogue(doc.RootElement) is { } newest ? new LatestRelease(newest, null) : null;
        return TagOf(doc.RootElement) is { } tag ? new LatestRelease(tag, InstallerOf(doc.RootElement, arch)) : null;
```

- [ ] **Step 5: Keep the shell compiling**

In `App.axaml.cs`, `RunUpdateCheck`, replace the `CheckAsync` call with

```csharp
            UpdateResult result = await UpdateCheck.CheckAsync(
                _config,
                ct => UpdateCheck.FetchLatestAsync(Http, Log.Version, _config.InstallSource,
                                                   System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture, ct),
                DateTime.UtcNow, _shutdown.Token, manual: force).ConfigureAwait(false);
```

- [ ] **Step 6: Run the tests to see them pass**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateCheckTests"`
Expected: PASS. Then `dotnet build -warnaserror -t:Rebuild` (0 warnings) and `dotnet test` (all green; the Settings panel's `Off` arm is now unreachable from Check now, which is intended).

- [ ] **Step 7: CHANGELOG and commit**

Add under `## [Unreleased]`, in a `### Changed` section (create it after `### Added` if missing):

```markdown
- **Check now checks, even with the daily check switched off.** Turning the switch off stops the
  once-a-day check in the background; pressing Check now still asks, once, because you asked.
```

```bash
git add src/Findra/Update/LatestRelease.cs src/Findra/App/UpdateCheck.cs src/Findra/App/App.axaml.cs tests/Findra.Tests/App/UpdateCheckTests.cs CHANGELOG.md
git commit -m "Check now asks even with the daily check off, and the check keeps this machine's installer"
```

---

### Task 2: Downloading and checking the installer

**Files:**
- Create: `src/Findra/Update/UpdateDownload.cs`
- Modify: `src/Findra/Core/Paths.cs`
- Test: `tests/Findra.Tests/Update/UpdateDownloadTests.cs` (create)

**Interfaces:**
- Consumes: `ReleaseAsset` (Task 1).
- Produces: `enum DownloadFailure { None, Host, TooLarge, Short, Digest, Network, Cancelled }`; `record DownloadResult(string? Path, DownloadFailure Failure, string? Message)` with `Ok(string)` and `Failed(DownloadFailure, string)`; `UpdateDownload.MaxBytes`; `UpdateDownload.AllowedHost(Uri) : bool`; `UpdateDownload.CreateClient() : HttpClient`; `UpdateDownload.GetAsync(HttpClient, ReleaseAsset, string dir, string version, Action<long, long>? progress, CancellationToken) : Task<DownloadResult>`; `UpdateDownload.Verify(string path, long size, string sha256) : DownloadFailure`; `UpdateDownload.Sweep(string dir)`; `Paths.Updates`.

- [ ] **Step 1: Write the failing tests**

`tests/Findra.Tests/Update/UpdateDownloadTests.cs`:

```csharp
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

        Assert.Equal(DownloadFailure.Short, r.Failure);
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
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateDownloadTests"`
Expected: build errors (`UpdateDownload`, `DownloadResult`, `DownloadFailure` do not exist).

- [ ] **Step 3: Add `Paths.Updates`**

In `src/Findra/Core/Paths.cs`, after `Logs`:

```csharp
    /// <summary>Where an update's installer is downloaded to. Emptied at every start.</summary>
    public static string Updates => Path.Combine(Local, "updates");
```

- [ ] **Step 4: Write `UpdateDownload`**

`src/Findra/Update/UpdateDownload.cs`:

```csharp
using System.Net.Http;
using System.Security.Cryptography;

namespace Findra;

/// <summary>Why a download did not produce a file that may be run.</summary>
public enum DownloadFailure { None, Host, TooLarge, Short, Digest, Network, Cancelled }

/// <summary>A downloaded installer that checked out, or why there is not one.</summary>
public sealed record DownloadResult(string? Path, DownloadFailure Failure, string? Message)
{
    public static DownloadResult Ok(string path) => new(path, DownloadFailure.None, null);
    public static DownloadResult Failed(DownloadFailure why, string message) => new(null, why, message);
}

/// <summary>
/// Fetches a release's installer and checks it before anything may run it.
///
/// <para>Only over HTTPS, and only from GitHub: <c>github.com</c> answers a release download with a
/// redirect to its file host, so redirects are followed here one at a time and every hop is held
/// to the same rule, rather than handed to a client that would follow them anywhere. The file must
/// then be exactly the declared size and hash to the SHA-256 GitHub published for it. That catches
/// a broken, truncated or swapped download; it cannot catch somebody who controls the account and
/// publishes a bad installer with its own matching digest. Only code signing closes that.</para>
/// </summary>
public static class UpdateDownload
{
    /// <summary>No Findra installer is within a factor of five of this. A larger declared size is
    /// not an installer, and nothing that size is written to the disk to find out.</summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    private const int MaxRedirects = 10;

    public static bool AllowedHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>No cookies, and no redirects followed behind this file's back. No overall timeout
    /// either: an 86 MB download on a slow line outlives the default 100 seconds, and the caller's
    /// token (Cancel, or quitting) is what ends it.</summary>
    public static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public static async Task<DownloadResult> GetAsync(HttpClient client, ReleaseAsset asset, string dir, string version,
                                                      Action<long, long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.Size <= 0 || asset.Size > MaxBytes)
            return DownloadResult.Failed(DownloadFailure.TooLarge, $"declared as {asset.Size:N0} bytes");
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out Uri? uri) || !AllowedHost(uri))
            return DownloadResult.Failed(DownloadFailure.Host, "refused " + asset.Url);

        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, asset.Name);
        try
        {
            HttpResponseMessage response;
            for (int hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd($"findra/{version}");
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
                if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } next) break;

                response.Dispose();
                if (hop >= MaxRedirects)
                    return DownloadResult.Failed(DownloadFailure.Network, "too many redirects");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                if (!AllowedHost(uri))
                    return DownloadResult.Failed(DownloadFailure.Host, "refused a redirect to " + uri.Host);
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[81920];
                long got = 0;
                int n;
                while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (got + n > asset.Size)
                    {
                        file.Close();
                        TryDelete(path);
                        return DownloadResult.Failed(DownloadFailure.Short, "longer than declared");
                    }
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    got += n;
                    progress?.Invoke(got, asset.Size);
                }
            }

            DownloadFailure verdict = Verify(path, asset.Size, asset.Sha256);
            if (verdict == DownloadFailure.None) return DownloadResult.Ok(path);
            TryDelete(path);
            return DownloadResult.Failed(verdict, verdict == DownloadFailure.Digest ? "checksum mismatch" : "incomplete");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(path);
            return DownloadResult.Failed(DownloadFailure.Cancelled, "cancelled");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            TryDelete(path);
            return DownloadResult.Failed(DownloadFailure.Network, ex.Message);
        }
    }

    /// <summary>Exactly <paramref name="size"/> bytes, hashing to <paramref name="sha256"/>. The
    /// digest is compared without regard to case: GitHub's is lower case today, and a file is not
    /// a different file because its hash was written in capitals.</summary>
    public static DownloadFailure Verify(string path, long size, string sha256)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != size) return DownloadFailure.Short;
        using FileStream s = File.OpenRead(path);
        string hex = Convert.ToHexStringLower(SHA256.HashData(s));
        return string.Equals(hex, sha256, StringComparison.OrdinalIgnoreCase) ? DownloadFailure.None : DownloadFailure.Digest;
    }

    /// <summary>Empty the folder. A file still held by a running installer stays until next time.</summary>
    public static void Sweep(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.EnumerateFiles(dir))
        {
            try { File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("update", $"could not delete {System.IO.Path.GetFileName(f)}: {ex.Message}");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("update", "could not delete a refused download: " + ex.Message);
        }
    }
}
```

- [ ] **Step 5: Run the tests to see them pass**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateDownloadTests"`
Expected: PASS. Then `dotnet build -warnaserror -t:Rebuild` and `dotnet test`.

- [ ] **Step 6: CHANGELOG and commit**

Add under `## [Unreleased]`, `### Added` (create the section if missing):

```markdown
- **Update now installs the new version for you.** Check for updates in the tray, or Check now in
  Settings, opens a small window that checks straight away. When there is a newer version, Update
  now downloads the installer from the release page, checks it against the checksum GitHub
  publishes for it, and runs it; a copy installed with winget runs the winget upgrade instead.
  Windows asks for permission, Findra closes while the new version installs, and it starts again
  when the installer is done.
```

```bash
git add src/Findra/Update/UpdateDownload.cs src/Findra/Core/Paths.cs tests/Findra.Tests/Update/UpdateDownloadTests.cs CHANGELOG.md
git commit -m "Update downloads: GitHub only, over HTTPS, checked against the published digest"
```

---

### Task 3: Handing off to the installer and to winget

**Files:**
- Create: `src/Findra/Update/UpdateHandoff.cs`
- Test: `tests/Findra.Tests/Update/UpdateHandoffTests.cs` (create)

**Interfaces:**
- Produces: `enum HandoffOutcome { DidNotRun, WingetFailed, WingetNothingNewer, WingetMissing }`; `record Handoff(HandoffOutcome Outcome, string Message)`; `delegate Task<(int ExitCode, string Output)> RunProcess(ProcessStartInfo start, CancellationToken ct)`; `UpdateHandoff.InstallerArguments`, `WingetArguments`, `WingetLimit`, `WingetNoUpgrade`, `PermissionRefused`, `NothingNewer`, `WingetMissing`; `UpdateHandoff.InstallerStart(string)`, `WingetStart()`; `RunInstallerAsync(string, RunProcess, CancellationToken) : Task<Handoff>`; `RunWingetAsync(RunProcess, CancellationToken) : Task<Handoff>`; `LastLine(string) : string?`; `Real(TimeSpan limit) : RunProcess`.

- [ ] **Step 1: Write the failing tests**

`tests/Findra.Tests/Update/UpdateHandoffTests.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;

using Findra;
using Xunit;

public class UpdateHandoffTests
{
    private static RunProcess Returns(int code, string output, List<ProcessStartInfo>? started = null) =>
        (start, _) => { started?.Add(start); return Task.FromResult((code, output)); };

    private static RunProcess Throws(Exception ex) => (_, _) => throw ex;

    [Fact]
    public async Task TheInstallerIsStartedSilentlyWithAProgressWindow()
    {
        var started = new List<ProcessStartInfo>();
        await UpdateHandoff.RunInstallerAsync(@"C:\u\findra-setup-x64.exe", Returns(5, "", started), default);

        ProcessStartInfo s = Assert.Single(started);
        Assert.Equal(@"C:\u\findra-setup-x64.exe", s.FileName);
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-", s.Arguments);
        Assert.True(s.UseShellExecute);
    }

    [Fact]
    public async Task AnInstallerThatReturnsWhileFindraRunsInstalledNothing()
    {
        Handoff h = await UpdateHandoff.RunInstallerAsync("x.exe", Returns(5, ""), default);
        Assert.Equal(HandoffOutcome.DidNotRun, h.Outcome);
    }

    [Fact]
    public async Task RefusingThePermissionPromptIsSaidPlainly()
    {
        Handoff h = await UpdateHandoff.RunInstallerAsync("x.exe", Throws(new Win32Exception(1223)), default);
        Assert.Equal(HandoffOutcome.DidNotRun, h.Outcome);
        Assert.Equal(UpdateHandoff.PermissionRefused, h.Message);
    }

    [Fact]
    public async Task TheWingetCommandIsExactlyThisAndHasNoWindow()
    {
        var started = new List<ProcessStartInfo>();
        await UpdateHandoff.RunWingetAsync(Returns(1, "", started), default);

        ProcessStartInfo s = Assert.Single(started);
        Assert.Equal("winget", s.FileName);
        Assert.Equal("upgrade --id blakazulu.Findra --exact --accept-source-agreements " +
                     "--accept-package-agreements --disable-interactivity", s.Arguments);
        Assert.False(s.UseShellExecute);
        Assert.True(s.CreateNoWindow);
        Assert.True(s.RedirectStandardOutput);
        Assert.True(s.RedirectStandardError);
    }

    [Fact]
    public async Task WingetWithNothingNewerSaysSo()
    {
        Handoff h = await UpdateHandoff.RunWingetAsync(
            Returns(UpdateHandoff.WingetNoUpgrade, "No available upgrade found.\r\n"), default);
        Assert.Equal(HandoffOutcome.WingetNothingNewer, h.Outcome);
        Assert.Equal(UpdateHandoff.NothingNewer, h.Message);
    }

    [Fact]
    public async Task AWingetFailureShowsItsOwnLastLine()
    {
        Handoff h = await UpdateHandoff.RunWingetAsync(
            Returns(-1978335226, "Found Findra [blakazulu.Findra]\r\nInstaller failed with exit code: 1\r\n"), default);
        Assert.Equal(HandoffOutcome.WingetFailed, h.Outcome);
        Assert.Equal("Installer failed with exit code: 1", h.Message);
    }

    [Fact]
    public async Task NoWingetOnTheMachineIsSaid()
    {
        Handoff h = await UpdateHandoff.RunWingetAsync(Throws(new Win32Exception(2)), default);
        Assert.Equal(HandoffOutcome.WingetMissing, h.Outcome);
        Assert.Equal(UpdateHandoff.WingetMissing, h.Message);
    }

    [Fact]
    public async Task AHungWingetIsStoppedAtTheLimit()
    {
        Handoff h = await UpdateHandoff.RunWingetAsync(Throws(new TimeoutException()), default);
        Assert.Equal(HandoffOutcome.WingetFailed, h.Outcome);
        Assert.Contains("20 minutes", h.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastLineIsWhatFollowsTheLastCarriageReturn()
    {
        // winget draws its progress bar by rewriting one line with carriage returns and backspaces.
        string output = "Downloading https://x\r\n  \u2588\u2588\u2592\u2592  1.00 MB / 82.0 MB\r  \u2588\u2588\u2588\u2588  82.0 MB / 82.0 MB\r\n" +
                        "Starting package install...\r\n\b\b-\b\\\b|Installer failed with exit code: 5\r\n\r\n";
        Assert.Equal("Installer failed with exit code: 5", UpdateHandoff.LastLine(output));
    }

    [Fact]
    public void NothingReadableIsNoLine() => Assert.Null(UpdateHandoff.LastLine("\r\n \b \r\n"));

    [Fact]
    public async Task TheRealRunnerReadsBothStreamsAndTheExitCode()
    {
        var start = new ProcessStartInfo("cmd.exe", "/c echo out & echo err 1>&2 & exit 3")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };

        (int code, string output) = await UpdateHandoff.Real(TimeSpan.FromSeconds(30))(start, default);

        Assert.Equal(3, code);
        Assert.Contains("out", output, StringComparison.Ordinal);
        Assert.Contains("err", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRealRunnerKillsWhatOutlivesItsLimit()
    {
        var start = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => UpdateHandoff.Real(TimeSpan.FromMilliseconds(300))(start, default));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the process was waited on rather than killed");
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateHandoffTests"`
Expected: build errors (`UpdateHandoff`, `RunProcess`, `Handoff` do not exist).

- [ ] **Step 3: Write `UpdateHandoff`**

`src/Findra/Update/UpdateHandoff.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Findra;

/// <summary>What a hand-off came back with. There is no "succeeded": an install that worked stopped
/// Findra on its way through, so this process only ever hears about the ones that did not.</summary>
public enum HandoffOutcome { DidNotRun, WingetFailed, WingetNothingNewer, WingetMissing }

public sealed record Handoff(HandoffOutcome Outcome, string Message);

/// <summary>Start a process and wait for it: its exit code and everything it printed. Throws
/// <see cref="Win32Exception"/> when it cannot start and <see cref="TimeoutException"/> when it
/// outlived its limit and was killed.</summary>
public delegate Task<(int ExitCode, string Output)> RunProcess(ProcessStartInfo start, CancellationToken ct);

/// <summary>
/// Starting the upgrade: the installer, or winget. Findra replaces none of its own files. The
/// installer or winget does, and the installer stops Findra before it touches anything, so a
/// hand-off that returns at all is one that installed nothing.
/// </summary>
public static class UpdateHandoff
{
    /// <summary>A progress window and no wizard pages; no message boxes; no reboot.</summary>
    public const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-";

    /// <summary>No <c>--silent</c>: winget's default runs the installer with its progress window,
    /// which is what the person watches once Findra has closed.</summary>
    public const string WingetArguments =
        "upgrade --id blakazulu.Findra --exact --accept-source-agreements --accept-package-agreements --disable-interactivity";

    public static readonly TimeSpan WingetLimit = TimeSpan.FromMinutes(20);

    /// <summary>winget's own code for "no newer version of this package".</summary>
    public const int WingetNoUpgrade = unchecked((int)0x8A15002B);

    /// <summary>ERROR_CANCELLED: the permission prompt was answered No.</summary>
    private const int Cancelled = 1223;

    public const string PermissionRefused = "Windows was not given permission to install it. Nothing was installed.";
    public const string NothingNewer = "winget found nothing newer to install. The catalogue can take a few days to list a new release.";
    public const string WingetMissing = "winget is not installed on this computer, so it could not run the upgrade.";

    public static ProcessStartInfo InstallerStart(string installer) =>
        new(installer, InstallerArguments) { UseShellExecute = true };

    public static ProcessStartInfo WingetStart() =>
        new("winget", WingetArguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

    public static async Task<Handoff> RunInstallerAsync(string installer, RunProcess run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            (int code, _) = await run(InstallerStart(installer), ct).ConfigureAwait(false);
            return new Handoff(HandoffOutcome.DidNotRun,
                $"The installer stopped before installing anything (exit code {code}). Nothing was installed.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return new Handoff(HandoffOutcome.DidNotRun, PermissionRefused);
        }
        catch (Win32Exception ex)
        {
            return new Handoff(HandoffOutcome.DidNotRun, "The installer could not be started: " + ex.Message);
        }
    }

    public static async Task<Handoff> RunWingetAsync(RunProcess run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            (int code, string output) = await run(WingetStart(), ct).ConfigureAwait(false);
            if (code == WingetNoUpgrade || code == 0)
                return new Handoff(HandoffOutcome.WingetNothingNewer, NothingNewer);
            return new Handoff(HandoffOutcome.WingetFailed,
                LastLine(output) ?? $"winget stopped with code 0x{code:X8}.");
        }
        catch (Win32Exception)
        {
            return new Handoff(HandoffOutcome.WingetMissing, WingetMissing);
        }
        catch (TimeoutException)
        {
            return new Handoff(HandoffOutcome.WingetFailed, "winget did not finish within 20 minutes, so it was stopped.");
        }
    }

    /// <summary>The last line of <paramref name="output"/> a person can read. winget redraws its
    /// progress with carriage returns and spins with backspaces, so a line is what follows the last
    /// carriage return, with control characters dropped.</summary>
    public static string? LastLine(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (string raw in output.Split('\n').Reverse())
        {
            string line = raw;
            int cr = line.TrimEnd('\r').LastIndexOf('\r');
            if (cr >= 0) line = line[(cr + 1)..];
            var sb = new StringBuilder(line.Length);
            foreach (char c in line)
                if (!char.IsControl(c)) sb.Append(c);
            string clean = sb.ToString().Trim().TrimStart('-', '\\', '|', '/').Trim();
            if (clean.Length > 0) return clean;
        }
        return null;
    }

    /// <summary>The real runner. Both streams are read as they arrive, never one after the other,
    /// so a process that fills one pipe while this waits on the other cannot hang both; past
    /// <paramref name="limit"/> the process tree is killed.</summary>
    public static RunProcess Real(TimeSpan limit) => async (start, ct) =>
    {
        using Process p = Process.Start(start) ?? throw new Win32Exception("the process did not start");
        Task<string> stdout = start.RedirectStandardOutput ? p.StandardOutput.ReadToEndAsync(ct) : Task.FromResult("");
        Task<string> stderr = start.RedirectStandardError ? p.StandardError.ReadToEndAsync(ct) : Task.FromResult("");

        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
        within.CancelAfter(limit);
        try
        {
            await p.WaitForExitAsync(within.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{start.FileName} did not finish within {limit}");
        }

        return (p.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    };
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateHandoffTests"`
Expected: PASS. Then `dotnet build -warnaserror -t:Rebuild` and `dotnet test`.

- [ ] **Step 5: CHANGELOG and commit**

Extend the Added entry from Task 2 with a sentence at its end:

```markdown
  If Windows is not given permission, or winget cannot run the upgrade, the window says so and
  nothing has changed.
```

```bash
git add src/Findra/Update/UpdateHandoff.cs tests/Findra.Tests/Update/UpdateHandoffTests.cs CHANGELOG.md
git commit -m "Update hand-off: the installer with a progress window, or winget with none"
```

---

### Task 4: The window's states, as a pure flow

**Files:**
- Create: `src/Findra/Update/UpdateFlow.cs`
- Test: `tests/Findra.Tests/Update/UpdateFlowTests.cs` (create)

**Interfaces:**
- Consumes: `UpdateResult`, `ReleaseAsset` (Task 1); `DownloadResult`, `DownloadFailure` (Task 2); `Handoff`, `HandoffOutcome` (Task 3).
- Produces: `enum UpdateStep { Checking, UpToDate, Unreachable, Available, Downloading, DownloadFailed, Installing, InstallerDidNotRun, Winget, WingetFailed }`; `enum UpdateRoute { Installer, Winget, Releases }`; `enum UpdateAction { None, Check, Download, CancelDownload, RunInstaller, RunWinget, OpenReleases, Close }`; `record UpdateView(UpdateStep Step, string Version, string? InstallSource)` with init properties `Latest`, `Installer`, `DownloadedTo`, `Got`, `Total`, `Problem`; `readonly record struct UpdateMove(UpdateView View, UpdateAction Action)`; `UpdateFlow.Start`, `Route`, `Checked`, `Close`, `Go`, `Progress`, `Downloaded`, `HandedOff`, `Closable`.

- [ ] **Step 1: Write the failing tests**

`tests/Findra.Tests/Update/UpdateFlowTests.cs`:

```csharp
using Findra;
using Xunit;

public class UpdateFlowTests
{
    private static readonly ReleaseAsset Asset =
        new("findra-setup-x64.exe", "https://github.com/a.exe", 85_727_274, new string('a', 64));

    private static UpdateView Available(string? source, ReleaseAsset? installer) =>
        UpdateFlow.Checked(UpdateFlow.Start("1.0.0", source),
            new UpdateResult(UpdateState.Available, "1.1.0", null, Config.Default, installer));

    [Fact]
    public void ItOpensChecking() =>
        Assert.Equal(UpdateStep.Checking, UpdateFlow.Start("1.0.0", "installer").Step);

    [Theory]
    [InlineData(UpdateState.Current, UpdateStep.UpToDate)]
    [InlineData(UpdateState.Available, UpdateStep.Available)]
    [InlineData(UpdateState.Unknown, UpdateStep.Unreachable)]
    public void TheCheckDecidesTheNextStep(UpdateState state, UpdateStep step) =>
        Assert.Equal(step, UpdateFlow.Checked(UpdateFlow.Start("1.0.0", null),
            new UpdateResult(state, "1.1.0", null, Config.Default)).Step);

    [Theory]
    [InlineData(UpdateState.Disabled)]
    [InlineData(UpdateState.NotDue)]
    public void AManualCheckNeverAnswersOffOrNotDue(UpdateState state) =>
        Assert.Throws<InvalidOperationException>(() => UpdateFlow.Checked(UpdateFlow.Start("1.0.0", null),
            new UpdateResult(state, null, null, Config.Default)));

    [Theory]
    [InlineData("installer", true, UpdateRoute.Installer)]
    [InlineData(null, true, UpdateRoute.Installer)]
    [InlineData("unknown", true, UpdateRoute.Installer)]
    [InlineData("winget", false, UpdateRoute.Winget)]
    [InlineData("WINGET", false, UpdateRoute.Winget)]
    [InlineData("source", true, UpdateRoute.Releases)]
    [InlineData("installer", false, UpdateRoute.Releases)]
    public void UpdateNowFollowsHowThisCopyWasInstalled(string? source, bool hasInstaller, UpdateRoute route) =>
        Assert.Equal(route, UpdateFlow.Route(Available(source, hasInstaller ? Asset : null)));

    [Fact]
    public void UpdateNowOnAnInstalledCopyDownloads()
    {
        UpdateMove m = UpdateFlow.Go(Available("installer", Asset));
        Assert.Equal(UpdateAction.Download, m.Action);
        Assert.Equal(UpdateStep.Downloading, m.View.Step);
        Assert.Equal(Asset.Size, m.View.Total);
    }

    [Fact]
    public void UpdateNowOnAWingetCopyRunsWinget()
    {
        UpdateMove m = UpdateFlow.Go(Available("winget", null));
        Assert.Equal((UpdateStep.Winget, UpdateAction.RunWinget), (m.View.Step, m.Action));
    }

    [Fact]
    public void OpenReleasesOpensThePageAndLeavesTheViewAlone()
    {
        UpdateView v = Available("source", Asset);
        Assert.Equal(new UpdateMove(v, UpdateAction.OpenReleases), UpdateFlow.Go(v));
    }

    [Fact]
    public void ACheckedDownloadGoesStraightToTheInstaller()
    {
        UpdateView downloading = UpdateFlow.Go(Available("installer", Asset)).View;
        UpdateMove m = UpdateFlow.Downloaded(downloading, DownloadResult.Ok(@"C:\u\findra-setup-x64.exe"));

        Assert.Equal((UpdateStep.Installing, UpdateAction.RunInstaller), (m.View.Step, m.Action));
        Assert.Equal(@"C:\u\findra-setup-x64.exe", m.View.DownloadedTo);
    }

    [Theory]
    [InlineData(DownloadFailure.Digest, "checksum")]
    [InlineData(DownloadFailure.Short, "incomplete")]
    [InlineData(DownloadFailure.Host, "other than GitHub")]
    [InlineData(DownloadFailure.TooLarge, "larger")]
    [InlineData(DownloadFailure.Network, "did not get through")]
    public void AFailedDownloadSaysWhyAndThatNothingWasInstalled(DownloadFailure why, string words)
    {
        UpdateView downloading = UpdateFlow.Go(Available("installer", Asset)).View;
        UpdateView v = UpdateFlow.Downloaded(downloading, DownloadResult.Failed(why, "")).View;

        Assert.Equal(UpdateStep.DownloadFailed, v.Step);
        Assert.Contains(words, v.Problem, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was installed.", v.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelGoesBackToTheOfferAndStopsTheDownload()
    {
        UpdateView downloading = UpdateFlow.Go(Available("installer", Asset)).View;
        UpdateMove m = UpdateFlow.Close(downloading);

        Assert.Equal((UpdateStep.Available, UpdateAction.CancelDownload), (m.View.Step, m.Action));
    }

    [Fact]
    public void ADownloadThatEndsAfterCancelChangesNothing()
    {
        UpdateView offer = UpdateFlow.Close(UpdateFlow.Go(Available("installer", Asset)).View).View;
        Assert.Equal(new UpdateMove(offer, UpdateAction.None),
            UpdateFlow.Downloaded(offer, DownloadResult.Failed(DownloadFailure.Cancelled, "")));
    }

    [Fact]
    public void TryAgainAfterUnreachableChecksAgain() =>
        Assert.Equal(UpdateAction.Check, UpdateFlow.Go(UpdateFlow.Checked(UpdateFlow.Start("1.0.0", null),
            new UpdateResult(UpdateState.Unknown, null, null, Config.Default))).Action);

    [Theory]
    [InlineData(UpdateStep.DownloadFailed)]
    [InlineData(UpdateStep.InstallerDidNotRun)]
    public void TryAgainAfterAFailedDownloadOrInstallDownloadsAgain(UpdateStep step)
    {
        UpdateView v = Available("installer", Asset) with { Step = step, Problem = "x" };
        UpdateMove m = UpdateFlow.Go(v);

        Assert.Equal((UpdateStep.Downloading, UpdateAction.Download), (m.View.Step, m.Action));
        Assert.Null(m.View.Problem);
    }

    [Theory]
    [InlineData(HandoffOutcome.DidNotRun, UpdateStep.InstallerDidNotRun)]
    [InlineData(HandoffOutcome.WingetFailed, UpdateStep.WingetFailed)]
    [InlineData(HandoffOutcome.WingetNothingNewer, UpdateStep.WingetFailed)]
    [InlineData(HandoffOutcome.WingetMissing, UpdateStep.WingetFailed)]
    public void AHandOffThatComesBackIsAProblemToShow(HandoffOutcome outcome, UpdateStep step)
    {
        UpdateView v = UpdateFlow.HandedOff(Available("installer", Asset) with { Step = UpdateStep.Installing },
                                            new Handoff(outcome, "said"));
        Assert.Equal((step, "said"), (v.Step, v.Problem));
    }

    [Theory]
    [InlineData(UpdateStep.Checking, true)]
    [InlineData(UpdateStep.Available, true)]
    [InlineData(UpdateStep.Downloading, true)]
    [InlineData(UpdateStep.Installing, false)]
    [InlineData(UpdateStep.Winget, false)]
    public void OnlyAHandOffInFlightCannotBeClosed(UpdateStep step, bool closable) =>
        Assert.Equal(closable, UpdateFlow.Closable(UpdateFlow.Start("1.0.0", null) with { Step = step }));

    [Theory]
    [InlineData(UpdateStep.Checking)]
    [InlineData(UpdateStep.Installing)]
    [InlineData(UpdateStep.Winget)]
    [InlineData(UpdateStep.UpToDate)]
    [InlineData(UpdateStep.WingetFailed)]
    [InlineData(UpdateStep.Downloading)]
    public void GoDoesNothingWhereThereIsNoGoButton(UpdateStep step)
    {
        UpdateView v = Available("installer", Asset) with { Step = step };
        Assert.Equal(UpdateAction.None, UpdateFlow.Go(v).Action);
    }

    [Fact]
    public void ProgressMovesOnlyADownload()
    {
        UpdateView downloading = UpdateFlow.Go(Available("installer", Asset)).View;
        Assert.Equal(1234, UpdateFlow.Progress(downloading, 1234, Asset.Size).Got);

        UpdateView offer = Available("installer", Asset);
        Assert.Equal(offer, UpdateFlow.Progress(offer, 1234, Asset.Size));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateFlowTests"`
Expected: build errors (`UpdateFlow`, `UpdateView`, `UpdateStep` do not exist).

- [ ] **Step 3: Write `UpdateFlow`**

`src/Findra/Update/UpdateFlow.cs`:

```csharp
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
    public static UpdateRoute Route(UpdateView v) => (v.InstallSource ?? "unknown").ToLowerInvariant() switch
    {
        "winget" => UpdateRoute.Winget,
        "source" => UpdateRoute.Releases,
        _ => v.Installer is null ? UpdateRoute.Releases : UpdateRoute.Installer,
    };

    public static UpdateView Checked(UpdateView v, UpdateResult r) => r.State switch
    {
        UpdateState.Current => v with { Step = UpdateStep.UpToDate, Latest = r.Latest },
        UpdateState.Available => v with { Step = UpdateStep.Available, Latest = r.Latest, Installer = r.Installer },
        UpdateState.Unknown => v with { Step = UpdateStep.Unreachable },
        UpdateState.Disabled or UpdateState.NotDue =>
            throw new InvalidOperationException($"a check somebody asked for never answers {r.State}"),
        _ => throw new ArgumentOutOfRangeException(nameof(r), r.State, "no step for this update state"),
    };

    /// <summary>The left-hand button, or the only one.</summary>
    public static UpdateMove Close(UpdateView v) => v.Step switch
    {
        UpdateStep.Downloading => new(v with { Step = UpdateStep.Available, Got = 0 }, UpdateAction.CancelDownload),
        UpdateStep.UpToDate or UpdateStep.Unreachable or UpdateStep.Available or UpdateStep.DownloadFailed
            or UpdateStep.InstallerDidNotRun or UpdateStep.WingetFailed => new(v, UpdateAction.Close),
        UpdateStep.Checking or UpdateStep.Installing or UpdateStep.Winget => new(v, UpdateAction.None),
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no close move for this step"),
    };

    /// <summary>The right-hand button: Update now, Open releases or Try again.</summary>
    public static UpdateMove Go(UpdateView v) => v.Step switch
    {
        UpdateStep.Available => Route(v) switch
        {
            UpdateRoute.Installer => Download(v),
            UpdateRoute.Winget => new(v with { Step = UpdateStep.Winget, Problem = null }, UpdateAction.RunWinget),
            UpdateRoute.Releases => new(v, UpdateAction.OpenReleases),
            _ => throw new ArgumentOutOfRangeException(nameof(v), Route(v), "no route"),
        },
        UpdateStep.Unreachable => new(Start(v.Version, v.InstallSource), UpdateAction.Check),
        UpdateStep.DownloadFailed or UpdateStep.InstallerDidNotRun => Download(v),
        UpdateStep.Checking or UpdateStep.UpToDate or UpdateStep.Downloading or UpdateStep.Installing
            or UpdateStep.Winget or UpdateStep.WingetFailed => new(v, UpdateAction.None),
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no go move for this step"),
    };

    private static UpdateMove Download(UpdateView v) =>
        new(v with { Step = UpdateStep.Downloading, Got = 0, Total = v.Installer!.Size, Problem = null, DownloadedTo = null },
            UpdateAction.Download);

    public static UpdateView Progress(UpdateView v, long got, long total) =>
        v.Step == UpdateStep.Downloading ? v with { Got = got, Total = total } : v;

    /// <summary>A download that ends after Cancel was pressed changes nothing: the view has already
    /// gone back to the offer.</summary>
    public static UpdateMove Downloaded(UpdateView v, DownloadResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (v.Step != UpdateStep.Downloading) return new(v, UpdateAction.None);
        return r.Failure switch
        {
            DownloadFailure.None => new(v with { Step = UpdateStep.Installing, DownloadedTo = r.Path }, UpdateAction.RunInstaller),
            DownloadFailure.Cancelled => new(v with { Step = UpdateStep.Available, Got = 0 }, UpdateAction.None),
            DownloadFailure.Digest or DownloadFailure.Short or DownloadFailure.Host or DownloadFailure.TooLarge
                or DownloadFailure.Network => new(v with { Step = UpdateStep.DownloadFailed, Problem = Problem(r.Failure) },
                                                  UpdateAction.None),
            _ => throw new ArgumentOutOfRangeException(nameof(r), r.Failure, "no step for this download failure"),
        };
    }

    public static UpdateView HandedOff(UpdateView v, Handoff h)
    {
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
    public static bool Closable(UpdateView v) => v.Step is not (UpdateStep.Installing or UpdateStep.Winget);

    private static string Problem(DownloadFailure why) => why switch
    {
        DownloadFailure.Digest => "The download did not match the checksum GitHub published for it, so it was deleted. Nothing was installed.",
        DownloadFailure.Short => "The download arrived incomplete, so it was deleted. Nothing was installed.",
        DownloadFailure.Host => "The download pointed somewhere other than GitHub, so it was refused. Nothing was installed.",
        DownloadFailure.TooLarge => "The installer was far larger than any Findra installer, so it was refused. Nothing was installed.",
        DownloadFailure.Network => "The download did not get through. Nothing was installed.",
        _ => throw new ArgumentOutOfRangeException(nameof(why), why, "no problem text for this failure"),
    };
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateFlowTests"`
Expected: PASS. Then `dotnet build -warnaserror -t:Rebuild` and `dotnet test`.

- [ ] **Step 5: CHANGELOG and commit**

Extend the Added entry with:

```markdown
  Cancel stops a download part-way and deletes what arrived.
```

```bash
git add src/Findra/Update/UpdateFlow.cs tests/Findra.Tests/Update/UpdateFlowTests.cs CHANGELOG.md
git commit -m "The update window's states and moves, with no window in them"
```

---

### Task 5: The window's wording and drawing replace the Settings panel

**Files:**
- Move: `src/Findra/Settings/UpdatePrompt.cs` to `src/Findra/Update/UpdatePrompt.cs` (`git mv`), then rewrite its wording and layout on `UpdateView`
- Create: `src/Findra/Update/UpdatePainter.cs`
- Modify: `src/Findra/Diagnostics/SearchShot.cs`
- Modify: `src/Findra/Settings/SettingsWindow.cs`, `SettingsPainter.cs`, `SettingsModel.cs`, `SettingsActions.cs`
- Modify: `src/Findra/App/App.axaml.cs` (`ISettingsHost.UpdateNow` removed; `CheckNow` and `NoteUpdate` call sites)
- Modify: `README.md` (surface count), `CLAUDE.md` (states list and count)
- Test: `tests/Findra.Tests/Settings/UpdatePromptTests.cs` (move to `tests/Findra.Tests/Update/UpdatePromptTests.cs` and rewrite), `tests/Findra.Tests/Settings/SettingsActionTests.cs`

**Interfaces:**
- Consumes: `UpdateView`, `UpdateStep`, `UpdateRoute`, `UpdateFlow` (Task 4); `Sizes.Human`; `ProgressPill.Paint(SKCanvas, SKRect, IndexProgress, Derived, SKTypeface)`; `Parts.Wrap`, `Parts.Note`, `Parts.NoteHeight`, `Parts.NoteSize`, `Parts.Pill`; `CardText.Draw`.
- Produces: `UpdatePrompt.Title(UpdateView)`, `Body(UpdateView)`, `CloseLabel(UpdateView)`, `GoLabel(UpdateView)`, `Buttons(UpdateView)`, `HasBar(UpdateView)`, `Count(UpdateView)`, `Height(int bodyLines, int buttons, bool bar)`, `Panel(int bodyLines, int buttons, bool bar) : SKRect` (at the origin), `Bar(SKRect panel, int bodyLines) : SKRect`, `Button(SKRect, int, int)` and `HitTest(float, float, SKRect, int)` unchanged, constants `Width`, `Pad`, `ButtonW`, `ButtonH`, `ButtonGap`, `TitleSize`, `Radius` unchanged; `UpdatePainter.BodyLines(UpdateView, SKTypeface)`, `Surface(UpdateView, SKTypeface) : SKRect`, `Paint(SKCanvas, UpdateView, UpdatePromptTarget hover, Derived, SKTypeface)`. `UpdatePromptTarget` unchanged. `UpdatePromptState` is deleted.

- [ ] **Step 1: Rewrite the prompt tests against `UpdateView`**

`git mv tests/Findra.Tests/Settings/UpdatePromptTests.cs tests/Findra.Tests/Update/UpdatePromptTests.cs`, then replace its contents with:

```csharp
using Findra;
using SkiaSharp;
using Xunit;

public class UpdatePromptTests
{
    private static readonly ReleaseAsset Asset =
        new("findra-setup-x64.exe", "https://github.com/a.exe", 85_727_274, new string('a', 64));

    private static UpdateView At(UpdateStep step, string? source = "installer", ReleaseAsset? installer = null) =>
        UpdateFlow.Start("1.0.0", source) with { Step = step, Latest = "v1.1.0", Installer = installer ?? Asset, Problem = "Something went wrong." };

    public static TheoryData<UpdateStep> Steps()
    {
        var d = new TheoryData<UpdateStep>();
        foreach (UpdateStep s in Enum.GetValues<UpdateStep>()) d.Add(s);
        return d;
    }

    [Theory, MemberData(nameof(Steps))]
    public void EveryStepHasATitleABodyAndItsButtonsLabelled(UpdateStep step)
    {
        UpdateView v = At(step);
        Assert.False(string.IsNullOrWhiteSpace(UpdatePrompt.Title(v)));
        Assert.False(string.IsNullOrWhiteSpace(UpdatePrompt.Body(v)));
        int buttons = UpdatePrompt.Buttons(v);
        if (buttons >= 1) Assert.False(string.IsNullOrWhiteSpace(UpdatePrompt.CloseLabel(v)));
        if (buttons == 2) Assert.False(string.IsNullOrWhiteSpace(UpdatePrompt.GoLabel(v)));
    }

    [Theory]
    [InlineData(UpdateStep.Checking, 0)]
    [InlineData(UpdateStep.UpToDate, 1)]
    [InlineData(UpdateStep.Unreachable, 2)]
    [InlineData(UpdateStep.Available, 2)]
    [InlineData(UpdateStep.Downloading, 1)]
    [InlineData(UpdateStep.DownloadFailed, 2)]
    [InlineData(UpdateStep.Installing, 0)]
    [InlineData(UpdateStep.InstallerDidNotRun, 2)]
    [InlineData(UpdateStep.Winget, 0)]
    [InlineData(UpdateStep.WingetFailed, 1)]
    public void EachStepHasTheButtonsTheSpecGivesIt(UpdateStep step, int buttons) =>
        Assert.Equal(buttons, UpdatePrompt.Buttons(At(step)));

    [Theory]
    [InlineData("installer", true, "Update now")]
    [InlineData("winget", false, "Update now")]
    [InlineData("source", true, "Open releases")]
    [InlineData("installer", false, "Open releases")]
    public void TheOfferSaysWhatThisCopyCanDo(string source, bool hasInstaller, string go)
    {
        UpdateView v = At(UpdateStep.Available, source) with { Installer = hasInstaller ? Asset : null };
        Assert.Equal(go, UpdatePrompt.GoLabel(v));
        Assert.Equal("Not now", UpdatePrompt.CloseLabel(v));
    }

    [Fact]
    public void AnInstalledCopysOfferNamesTheDownloadSizeAndThePermissionPrompt()
    {
        string body = UpdatePrompt.Body(At(UpdateStep.Available));
        Assert.Contains("82 MB", body, StringComparison.Ordinal);
        Assert.Contains("permission", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AWingetCopysOfferNamesTheCommand() =>
        Assert.Contains("winget upgrade blakazulu.Findra", UpdatePrompt.Body(At(UpdateStep.Available, "winget", null)),
                        StringComparison.Ordinal);

    [Fact]
    public void TheHeadingNamesTheNewVersionWithoutTheTagsV() =>
        Assert.Equal("Findra 1.1.0 is available", UpdatePrompt.Title(At(UpdateStep.Available)));

    [Fact]
    public void AWingetCopyIsUpToDateWithTheCatalogueAndIsToldSo() =>
        Assert.Contains("winget", UpdatePrompt.Body(At(UpdateStep.UpToDate, "winget")), StringComparison.Ordinal);

    [Theory]
    [InlineData(UpdateStep.DownloadFailed)]
    [InlineData(UpdateStep.InstallerDidNotRun)]
    [InlineData(UpdateStep.WingetFailed)]
    public void AProblemIsShownInTheWordsItCameWith(UpdateStep step) =>
        Assert.Equal("Something went wrong.", UpdatePrompt.Body(At(step)));

    [Fact]
    public void TheDownloadCountsInMegabytes()
    {
        UpdateView v = At(UpdateStep.Downloading) with { Got = 36_000_000, Total = Asset.Size };
        Assert.Equal("34 MB of 82 MB", UpdatePrompt.Count(v));
        Assert.True(UpdatePrompt.HasBar(v));
        Assert.False(UpdatePrompt.HasBar(At(UpdateStep.Available)));
    }

    [Fact]
    public void TheAffirmativeButtonIsAlwaysTheRightmostOne()
    {
        SKRect panel = UpdatePrompt.Panel(2, 2, bar: false);
        Assert.True(UpdatePrompt.Button(panel, 1, 2).Left > UpdatePrompt.Button(panel, 0, 2).Right);
        Assert.Equal(UpdatePrompt.Button(panel, 1, 2).Right, UpdatePrompt.Button(panel, 0, 1).Right);
    }

    [Fact]
    public void OnlyTheButtonsAnswerAClick()
    {
        SKRect panel = UpdatePrompt.Panel(2, 2, bar: false);
        SKRect go = UpdatePrompt.Button(panel, 1, 2);
        Assert.Equal(UpdatePromptTarget.Go, UpdatePrompt.HitTest(go.MidX, go.MidY, panel, 2));
        Assert.Equal(UpdatePromptTarget.None, UpdatePrompt.HitTest(panel.MidX, panel.Top + 10, panel, 2));
    }

    [Fact]
    public void TheWindowIsTallerWithButtonsAndTallerStillWithTheBar()
    {
        float none = UpdatePrompt.Panel(2, 0, bar: false).Height;
        float buttons = UpdatePrompt.Panel(2, 1, bar: false).Height;
        float bar = UpdatePrompt.Panel(2, 1, bar: true).Height;
        Assert.True(none < buttons && buttons < bar);
        Assert.Equal(0f, UpdatePrompt.Panel(2, 1, bar: true).Left);
        Assert.Equal(0f, UpdatePrompt.Panel(2, 1, bar: true).Top);
    }
}
```

In `tests/Findra.Tests/Settings/SettingsActionTests.cs` delete `public void UpdateNow() => Calls.Add("update");` and the `[SettingsAction.UpdateNow] = "update",` line.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdatePromptTests"`
Expected: build errors (`UpdatePrompt.Title(UpdateView)` and friends do not exist).

- [ ] **Step 3: Move and rewrite `UpdatePrompt`**

`git mv src/Findra/Settings/UpdatePrompt.cs src/Findra/Update/UpdatePrompt.cs`. Replace the whole file with:

```csharp
using System;

using SkiaSharp;

namespace Findra;

/// <summary>What the update window's right-hand button does when there is one.</summary>
public enum UpdatePromptTarget { None, Close, Go }

/// <summary>
/// What the update window says, and where each part of it is.
///
/// <para>The window is only ever opened by a person: the tray's Check for updates or Settings'
/// Check now. The daily background check never opens it, because a person who did not ask a
/// question is not waiting for an answer (spec §3).</para>
///
/// <para>Findra still replaces none of its own files. Update now runs the installer or winget,
/// and they do; the body of each offer says which, because the honest button depends on how this
/// copy arrived.</para>
/// </summary>
public static class UpdatePrompt
{
    // ---- what it says -------------------------------------------------------------------------

    public static string Title(UpdateView v) => v.Step switch
    {
        UpdateStep.Checking => "Checking for updates",
        UpdateStep.UpToDate => "You have the latest version",
        UpdateStep.Unreachable => "Could not reach GitHub",
        UpdateStep.Available => $"Findra {Clean(v.Latest)} is available",
        UpdateStep.Downloading => $"Downloading Findra {Clean(v.Latest)}",
        UpdateStep.DownloadFailed => "The download did not work",
        UpdateStep.Installing => $"Installing Findra {Clean(v.Latest)}",
        UpdateStep.InstallerDidNotRun => "The installer did not run",
        UpdateStep.Winget => $"Updating Findra to {Clean(v.Latest)}",
        UpdateStep.WingetFailed => "winget did not update Findra",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no title for this step"),
    };

    public static string Body(UpdateView v) => v.Step switch
    {
        UpdateStep.Checking => "Asking GitHub whether there is a newer release.",
        UpdateStep.UpToDate => Winget(v)
            ? $"Findra {v.Version} is the newest version winget has."
            : $"Findra {v.Version} is the newest release.",
        UpdateStep.Unreachable => "The request did not get through. Nothing is wrong with this copy.",
        UpdateStep.Available => UpdateFlow.Route(v) switch
        {
            UpdateRoute.Installer =>
                $"You have {v.Version}. Update now downloads the installer ({Sizes.Human(v.Installer!.Size)}), " +
                "checks it, and runs it. Findra closes while it installs and opens again when it is done. " +
                "Windows will ask for permission.",
            UpdateRoute.Winget =>
                $"You have {v.Version}. Update now runs winget upgrade blakazulu.Findra. Findra closes while " +
                "it installs and opens again when it is done. Windows will ask for permission.",
            UpdateRoute.Releases when Source(v) == "source" =>
                $"You have {v.Version}. Open releases shows the notes for it. Pull and rebuild to take it.",
            UpdateRoute.Releases =>
                $"You have {v.Version}. This release has no installer Findra can check for this computer, " +
                "so Open releases takes you to the release page.",
            _ => throw new ArgumentOutOfRangeException(nameof(v), UpdateFlow.Route(v), "no offer for this route"),
        },
        UpdateStep.Downloading => "Checked against the checksum GitHub publishes before anything runs.",
        UpdateStep.Installing => "Findra will close now and open again when the installer is done.",
        UpdateStep.Winget => "winget is installing it. Findra will close while it installs and open again when it is done.",
        UpdateStep.DownloadFailed or UpdateStep.InstallerDidNotRun or UpdateStep.WingetFailed =>
            v.Problem ?? "Something went wrong. Nothing was installed.",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no body for this step"),
    };

    /// <summary>The left button, or the only one. Empty where there is none.</summary>
    public static string CloseLabel(UpdateView v) => v.Step switch
    {
        UpdateStep.Available => "Not now",
        UpdateStep.Downloading => "Cancel",
        UpdateStep.UpToDate or UpdateStep.Unreachable or UpdateStep.DownloadFailed
            or UpdateStep.InstallerDidNotRun or UpdateStep.WingetFailed => "Close",
        UpdateStep.Checking or UpdateStep.Installing or UpdateStep.Winget => "",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no close label for this step"),
    };

    /// <summary>The right button. Empty where there is none.</summary>
    public static string GoLabel(UpdateView v) => v.Step switch
    {
        UpdateStep.Available => UpdateFlow.Route(v) == UpdateRoute.Releases ? "Open releases" : "Update now",
        UpdateStep.Unreachable or UpdateStep.DownloadFailed or UpdateStep.InstallerDidNotRun => "Try again",
        UpdateStep.Checking or UpdateStep.UpToDate or UpdateStep.Downloading or UpdateStep.Installing
            or UpdateStep.Winget or UpdateStep.WingetFailed => "",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no go label for this step"),
    };

    public static int Buttons(UpdateView v) => v.Step switch
    {
        UpdateStep.Available or UpdateStep.Unreachable or UpdateStep.DownloadFailed or UpdateStep.InstallerDidNotRun => 2,
        UpdateStep.UpToDate or UpdateStep.Downloading or UpdateStep.WingetFailed => 1,
        UpdateStep.Checking or UpdateStep.Installing or UpdateStep.Winget => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no button count for this step"),
    };

    public static bool HasBar(UpdateView v) => v.Step == UpdateStep.Downloading;

    /// <summary>"34 MB of 82 MB", in the formatter every other size in Findra uses.</summary>
    public static string Count(UpdateView v) => $"{Sizes.Human(v.Got)} of {Sizes.Human(v.Total)}";

    private static bool Winget(UpdateView v) => Source(v) == "winget";

    private static string Source(UpdateView v) => (v.InstallSource ?? "unknown").ToLowerInvariant();

    /// <summary>Tags carry a leading v and the window does not.</summary>
    private static string Clean(string? tag) =>
        tag is { Length: > 0 } t && (t[0] == 'v' || t[0] == 'V') ? t[1..] : tag ?? "";

    // ---- where it is ---------------------------------------------------------------------------

    public const float Width = 460f;
    public const float Pad = 24f;
    public const float ButtonW = 116f;
    public const float ButtonH = 34f;
    public const float ButtonGap = 10f;
    public const float TitleSize = 17f;
    public const float Radius = 14f;
    private const float BarGap = 14f;

    /// <summary>How tall the window is: measured body lines, the download bar when there is one,
    /// the buttons when there are any.</summary>
    public static float Height(int bodyLines, int buttons, bool bar) =>
        Pad + TitleSize + 14f + Parts.NoteHeight(Math.Max(1, bodyLines))
        + (bar ? BarGap + ProgressPillLayout.Height : 0f)
        + (buttons > 0 ? 18f + ButtonH : 0f) + Pad;

    /// <summary>The window's whole surface, at the origin: the window is the panel.</summary>
    public static SKRect Panel(int bodyLines, int buttons, bool bar) =>
        new(0, 0, Width, Height(bodyLines, buttons, bar));

    /// <summary>The download bar, under the body.</summary>
    public static SKRect Bar(SKRect panel, int bodyLines)
    {
        float top = panel.Top + Pad + TitleSize + 14f + Parts.NoteHeight(Math.Max(1, bodyLines)) + BarGap;
        return new SKRect(panel.Left + Pad, top, panel.Right - Pad, top + ProgressPillLayout.Height);
    }

    /// <summary>Button <paramref name="i"/> from the RIGHT, so the affirmative one is always the
    /// rightmost whether there are one or two. Index 0 is Close, index 1 is Go.</summary>
    public static SKRect Button(SKRect panel, int i, int buttons)
    {
        float right = panel.Right - Pad;
        int fromRight = buttons == 2 ? (i == 1 ? 0 : 1) : 0;
        float x = right - (fromRight + 1) * ButtonW - fromRight * ButtonGap;
        return new SKRect(x, panel.Bottom - Pad - ButtonH, x + ButtonW, panel.Bottom - Pad);
    }

    /// <summary>What is under the pointer. Only the buttons answer; everything else moves the
    /// window.</summary>
    public static UpdatePromptTarget HitTest(float x, float y, SKRect panel, int buttons)
    {
        if (buttons >= 1 && Button(panel, 0, buttons).Contains(x, y)) return UpdatePromptTarget.Close;
        if (buttons == 2 && Button(panel, 1, buttons).Contains(x, y)) return UpdatePromptTarget.Go;
        return UpdatePromptTarget.None;
    }
}
```

`RemovePrompt` keeps using `UpdatePrompt.Width`, `Pad`, `TitleSize`, `Radius`, `ButtonH` and `UpdatePrompt.Button(panel, i, 2)`, all unchanged.

- [ ] **Step 4: Write `UpdatePainter`**

`src/Findra/Update/UpdatePainter.cs`:

```csharp
using SkiaSharp;

namespace Findra;

/// <summary>Paints the update window from an <see cref="UpdateView"/>. The window and
/// <c>--searchshot</c> both call it, so the shot is the window.</summary>
public static class UpdatePainter
{
    public static int BodyLines(UpdateView v, SKTypeface face) =>
        Parts.Wrap(UpdatePrompt.Body(v), face, Parts.NoteSize, UpdatePrompt.Width - 2 * UpdatePrompt.Pad).Count;

    public static SKRect Surface(UpdateView v, SKTypeface face) =>
        UpdatePrompt.Panel(BodyLines(v, face), UpdatePrompt.Buttons(v), UpdatePrompt.HasBar(v));

    public static void Paint(SKCanvas canvas, UpdateView v, UpdatePromptTarget hover, Derived d, SKTypeface face)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(v);
        int lines = BodyLines(v, face);
        int buttons = UpdatePrompt.Buttons(v);
        SKRect panel = UpdatePrompt.Panel(lines, buttons, UpdatePrompt.HasBar(v));

        using (var fill = new SKPaint { Color = d.Tile, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(panel, UpdatePrompt.Radius), fill);
        using (var edge = new SKPaint
        { Color = d.Accent.WithAlpha(96), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
            canvas.DrawRoundRect(new SKRoundRect(SKRect.Inflate(panel, -0.7f, -0.7f), UpdatePrompt.Radius), edge);

        float x = panel.Left + UpdatePrompt.Pad;
        CardText.Draw(canvas, UpdatePrompt.Title(v), x, panel.Top + UpdatePrompt.Pad + UpdatePrompt.TitleSize,
                      UpdatePrompt.TitleSize, face, d.Ink);
        Parts.Note(canvas, UpdatePrompt.Body(v),
                   new SKRect(x, panel.Top + UpdatePrompt.Pad + UpdatePrompt.TitleSize + 14f,
                              panel.Right - UpdatePrompt.Pad, panel.Bottom - UpdatePrompt.Pad),
                   d, face);

        if (UpdatePrompt.HasBar(v))
        {
            float fraction = v.Total > 0 ? Math.Clamp(v.Got / (float)v.Total, 0f, 1f) : 0f;
            ProgressPill.Paint(canvas, UpdatePrompt.Bar(panel, lines),
                               new IndexProgress("Downloading", UpdatePrompt.Count(v), fraction, Show: true), d, face);
        }

        if (buttons >= 1)
            Parts.Pill(canvas, UpdatePrompt.Button(panel, 0, buttons), UpdatePrompt.CloseLabel(v),
                       chosen: buttons == 1, hovered: hover == UpdatePromptTarget.Close, d, face);
        if (buttons == 2)
            Parts.Pill(canvas, UpdatePrompt.Button(panel, 1, buttons), UpdatePrompt.GoLabel(v),
                       chosen: true, hovered: hover == UpdatePromptTarget.Go, d, face);
    }
}
```

- [ ] **Step 5: Take the panel out of Settings**

`SettingsModel.cs`:
- In `enum SettingsAction`, delete the `UpdateNow,` member (line ~57) and its doc comment if it has one.
- In `SettingsState`, delete the `Prompt` and `PromptHover` properties and their doc comments (lines ~217-236).
- In `About(SettingsState s)`, replace the CheckUpdates note with
  `note: "One anonymous request to GitHub, at most once every 24 hours, in the background. " + "No query parameters, no machine or install identifier, nothing about your files. " + "Off stops it; Check now still asks when you press it."`
  Leave the CheckNow row as it is: the shell no longer marks it waiting (the window shows the check instead), and `SettingsModelTests.AWaitingRowSaysSoRatherThanLookingUntouched` holds every row's generic waiting behaviour, CheckNow included.
- Replace `AboutUpdateLine`'s doc comment with `/// <summary>What About says about updates: the last answer, in a sentence. Check now opens the update window, which is where anything is done about it.</summary>`.

`SettingsActions.cs`: delete `void UpdateNow();` and its doc comment from `ISettingsHost`, and the `case SettingsAction.UpdateNow: host.UpdateNow(); return;` arm.

`SettingsWindow.cs`:
- Replace `NoteUpdate` with
  ```csharp
      /// <summary>What the last check found, for the About row.</summary>
      public void NoteUpdate(UpdateState update, string? latest) =>
          _canvas.Refresh(s => s with { Update = update, Latest = latest });
  ```
- Delete `ShowUpdatePrompt`, `Prompted` and their doc comments, `PromptPanel()`, `PromptAt(Point)`, the `if (_state.Prompt != UpdatePromptState.None) { ... }` block at the top of `OnPointerMoved`, and the `if (_state.Prompt != UpdatePromptState.None) { switch ... }` block at the top of `OnPointerPressed`.

`SettingsPainter.cs`: delete the line `if (s.Prompt != UpdatePromptState.None) Prompt(canvas, s, d, face);` and the whole `Prompt(...)` method with its doc comment.

`App.axaml.cs`:
- Delete the whole `void ISettingsHost.UpdateNow() { ... }` method.
- Replace `ISettingsHost.CheckNow()`'s body with `_ = RunUpdateCheck(force: true);` (Task 6 replaces this with the window).
- In `RunUpdateCheck`, change `SettingsWindow.Open?.NoteUpdate(result.State, result.Latest, raise: force);` to `SettingsWindow.Open?.NoteUpdate(result.State, result.Latest);` and delete the comment block above it that describes raising the panel.

Search the tests for anything still naming the removed members: `grep -rn "PromptHover\|UpdatePromptState\|ShowUpdatePrompt\|SettingsAction.UpdateNow" tests src` must print nothing.

- [ ] **Step 6: The shots**

In `SearchShot.cs`, replace `"settingsuptodate", "settingsupdate", "settingsasking",` in `States` with

```csharp
        "updatechecking", "updateuptodate", "updateunreachable", "updateavailable", "updateavailablewinget",
        "updatedownloading", "updatedownloadfailed", "updateinstalling", "updatedidnotrun", "updatewinget",
        "updatewingetfailed",
```

In `Render`, add a branch before the `firstrun` one:

```csharp
            : state.StartsWith("update", StringComparison.Ordinal) ? RenderUpdate(state, d, face)
```

In `RenderSettings`, change `"settingsabout" or "settingsuptodate" or "settingsupdate" or "settingsasking" => Section.About,` to `"settingsabout" => Section.About,`, delete the `UpdatePromptState prompt = state switch { ... };` block with its comment, and delete the `Prompt = prompt,` and `PromptHover = ...` initialisers with their comment.

Add, below `RenderSettings`:

```csharp
    /// <summary>
    /// The update window in every state it can show, each from the real flow: problems come through
    /// <see cref="UpdateFlow.Downloaded"/> and <see cref="UpdateFlow.HandedOff"/> rather than being
    /// typed here, so a shot cannot say what the product would not. The installer and winget offers
    /// are separate states because their bodies differ.
    /// </summary>
    private static SKBitmap RenderUpdate(string state, Derived d, SKTypeface face)
    {
        var installer = new ReleaseAsset("findra-setup-x64.exe",
            "https://github.com/blakazulu/findra/releases/download/v1.4.0/findra-setup-x64.exe", 85_727_274, new string('0', 64));
        bool winget = state.Contains("winget", StringComparison.Ordinal);
        UpdateView start = UpdateFlow.Start("1.3.0", winget ? "winget" : "installer");
        UpdateView offer = start with { Step = UpdateStep.Available, Latest = "1.4.0", Installer = winget ? null : installer };
        UpdateView downloading = UpdateFlow.Go(offer).View with { Got = 36_000_000 };

        UpdateView v = state switch
        {
            "updatechecking" => start,
            "updateuptodate" => start with { Step = UpdateStep.UpToDate, Latest = "1.3.0" },
            "updateunreachable" => start with { Step = UpdateStep.Unreachable },
            "updateavailable" or "updateavailablewinget" => offer,
            "updatedownloading" => downloading,
            "updatedownloadfailed" => UpdateFlow.Downloaded(downloading, DownloadResult.Failed(DownloadFailure.Digest, "")).View,
            "updateinstalling" => downloading with { Step = UpdateStep.Installing },
            "updatedidnotrun" => UpdateFlow.HandedOff(downloading with { Step = UpdateStep.Installing },
                                     new Handoff(HandoffOutcome.DidNotRun, UpdateHandoff.PermissionRefused)),
            "updatewinget" => UpdateFlow.Go(offer).View,
            "updatewingetfailed" => UpdateFlow.HandedOff(UpdateFlow.Go(offer).View,
                                        new Handoff(HandoffOutcome.WingetFailed, "Installer failed with exit code: 1")),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "no update view for this shot"),
        };

        // Hovered on the button somebody would press, as the other surfaces' shots are.
        int buttons = UpdatePrompt.Buttons(v);
        UpdatePromptTarget hover = buttons == 2 ? UpdatePromptTarget.Go
                                 : buttons == 1 ? UpdatePromptTarget.Close : UpdatePromptTarget.None;

        SKRect surface = UpdatePainter.Surface(v, face);
        var info = new SKImageInfo((int)Math.Ceiling(surface.Width), (int)Math.Ceiling(surface.Height),
                                   SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface s = SKSurface.Create(info);
        UpdatePainter.Paint(s.Canvas, v, hover, d, face);
        s.Canvas.Flush();
        var bmp = new SKBitmap(info);
        s.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }
```

Update the state count wherever it is written. `ReadmeTests.Word` stops at 32 and throws above it, so extend its switch with `33 => "thirty-three", 34 => "thirty-four", 35 => "thirty-five", 36 => "thirty-six", 37 => "thirty-seven", 38 => "thirty-eight", 39 => "thirty-nine", 40 => "forty",` (in `tests/Findra.Tests/Build/ReadmeTests.cs`, and add that file to this task's commit). In `README.md` change `draws thirty-two surfaces` to `draws forty surfaces`; in `CLAUDE.md` change `# thirty-two states, listed below` to `# forty states, listed below`, change "Fourteen draw the card, ten the settings window and eight the first-run screen" to "Fourteen draw the card, seven the settings window, eight the first-run screen and eleven the update window", and in the states block replace the line `settingsuptodate  settingsupdate  settingsasking` with the two lines
```
updatechecking  updateuptodate  updateunreachable  updateavailable  updateavailablewinget  updatedownloading
updatedownloadfailed  updateinstalling  updatedidnotrun  updatewinget  updatewingetfailed
```

- [ ] **Step 7: Run everything**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdatePromptTests|FullyQualifiedName~ShotTests|FullyQualifiedName~ReadmeTests|FullyQualifiedName~Settings"`
Expected: PASS. `ShotTests.EveryStateRendersInEveryPalette` requires at least 280 distinct colours per shot; the window's text, antialiased edge and rounded corners over the shot's checkerboard should clear it. If an `update*` state does not, open its PNG: a flat render is a painter bug to fix, and the threshold is not lowered. Then `dotnet build -warnaserror -t:Rebuild` and `dotnet test`.

- [ ] **Step 8: CHANGELOG and commit**

Add under `### Changed`:

```markdown
- **Settings no longer puts an update panel over its page.** Check now opens the update window,
  the same one the tray opens.
```

```bash
git add -A src/Findra/Update src/Findra/Settings src/Findra/Diagnostics/SearchShot.cs src/Findra/App/App.axaml.cs tests/Findra.Tests/Update tests/Findra.Tests/Settings tests/Findra.Tests/Build/ReadmeTests.cs README.md CLAUDE.md CHANGELOG.md
git commit -m "The update window's wording and drawing, and the Settings panel retired"
```

---

### Task 6: The window, the session, and the shell

**Files:**
- Create: `src/Findra/Update/UpdateSession.cs`, `src/Findra/Update/UpdateWindow.cs`
- Modify: `src/Findra/App/App.axaml.cs`, `src/Findra/App/Config.cs`
- Test: `tests/Findra.Tests/Update/UpdateSessionTests.cs` (create), `tests/Findra.Tests/App/TrayTextTests.cs` (`UpdateMemoryTests`), `tests/Findra.Tests/Look/ScreenFitTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-5.
- Produces: `UpdateSession(UpdateView first, Func<CancellationToken, Task<UpdateResult>> check, Func<ReleaseAsset, Action<long, long>, CancellationToken, Task<DownloadResult>> download, Func<string, CancellationToken, Task<Handoff>> runInstaller, Func<CancellationToken, Task<Handoff>> runWinget, Action openReleases, Action<UpdateView> show, Action close, Action<Action> post)` with `View`, `Begin()`, `PressClose()`, `PressGo()`, `Closed()`; `UpdateWindow(UpdateSession session, Palette palette)` with `static UpdateWindow? Open`, `ShowView(UpdateView)`; `Config.LastRunVersion`; `UpdateMemory.JustUpdated(string? lastRun, string running) : bool`, `UpdateMemory.TrayHeader(string? updatedTo) : string`. `UpdateMemory.CheckedHeader` is deleted.

- [ ] **Step 1: Write the failing tests**

`tests/Findra.Tests/Update/UpdateSessionTests.cs`:

```csharp
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
                post: a => a());
        }

        public void Offer() =>
            Check.SetResult(new UpdateResult(UpdateState.Available, "1.1.0", null, Config.Default, Asset));
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

        Assert.Equal(UpdateStep.Unreachable, h.Session.View.Step);
    }
}
```

In `tests/Findra.Tests/App/TrayTextTests.cs`, inside `UpdateMemoryTests`, add:

```csharp
    [Theory]
    [InlineData("0.5.1", "0.6.0", true)]
    [InlineData("0.6.0", "0.6.0", false)]
    [InlineData("0.7.0", "0.6.0", false)]
    [InlineData(null, "0.6.0", false)]
    [InlineData("nightly", "0.6.0", false)]
    public void UpdatedToIsSaidOnlyWhenTheLastRunWasAnOlderVersion(string? lastRun, string running, bool updated) =>
        Assert.Equal(updated, UpdateMemory.JustUpdated(lastRun, running));

    [Fact]
    public void TheTrayItemSaysWhatItWasUpdatedTo()
    {
        Assert.Equal("Check for updates", UpdateMemory.TrayHeader(null));
        Assert.Equal("Updated to 0.6.0", UpdateMemory.TrayHeader("0.6.0"));
    }
```

In `tests/Findra.Tests/Look/ScreenFitTests.cs` add:

```csharp
    [Theory]
    [MemberData(nameof(Scalings))]
    public void TheUpdateWindowFits(double scaling)
    {
        // The tallest thing it shows: a download with its bar and a button.
        UpdateView v = UpdateFlow.Start("1.0.0", "installer") with
        {
            Step = UpdateStep.Downloading, Latest = "1.1.0", Got = 1, Total = 2,
            Installer = new ReleaseAsset("findra-setup-x64.exe", "https://github.com/a", 2, new string('a', 64)),
        };
        SKRect surface = UpdatePainter.Surface(v, Parts.Face);
        AssertFits("the update window", surface.Width, surface.Height, scaling);
    }
```

(Add `using SkiaSharp;` to that file if it is not already there.)

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateSessionTests|FullyQualifiedName~UpdateMemoryTests|FullyQualifiedName~ScreenFitTests|FullyQualifiedName~ConfigTests"`
Expected: build errors (`UpdateSession`, `JustUpdated`, `TrayHeader` do not exist).

- [ ] **Step 3: Write `UpdateSession`**

`src/Findra/Update/UpdateSession.cs`:

```csharp
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
    private readonly Func<string, CancellationToken, Task<Handoff>> _runInstaller;
    private readonly Func<CancellationToken, Task<Handoff>> _runWinget;
    private readonly Action _openReleases;
    private readonly Action<UpdateView> _show;
    private readonly Action _close;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _gone = new();
    private CancellationTokenSource? _downloading;
    private bool _closed;

    public UpdateView View { get; private set; }

    public UpdateSession(UpdateView first,
                         Func<CancellationToken, Task<UpdateResult>> check,
                         Func<ReleaseAsset, Action<long, long>, CancellationToken, Task<DownloadResult>> download,
                         Func<string, CancellationToken, Task<Handoff>> runInstaller,
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
            case UpdateAction.Download: _ = DownloadAsync(); return;
            case UpdateAction.CancelDownload: _downloading?.Cancel(); return;
            case UpdateAction.RunInstaller: _ = HandOffAsync(ct => _runInstaller(View.DownloadedTo!, ct)); return;
            case UpdateAction.RunWinget: _ = HandOffAsync(_runWinget); return;
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

    private async Task DownloadAsync()
    {
        _downloading?.Dispose();
        _downloading = CancellationTokenSource.CreateLinkedTokenSource(_gone.Token);
        CancellationToken ct = _downloading.Token;
        DownloadResult r = await _download(View.Installer!,
            (got, total) => _post(() => { if (!_closed) { View = UpdateFlow.Progress(View, got, total); _show(View); } }),
            ct).ConfigureAwait(false);
        _post(() => { if (!_closed) Apply(UpdateFlow.Downloaded(View, r)); });
    }

    private async Task HandOffAsync(Func<CancellationToken, Task<Handoff>> run)
    {
        Handoff h = await run(_gone.Token).ConfigureAwait(false);
        _post(() => { if (!_closed) Apply(new UpdateMove(UpdateFlow.HandedOff(View, h), UpdateAction.None)); });
    }
}
```

- [ ] **Step 4: Write `UpdateWindow`**

`src/Findra/Update/UpdateWindow.cs`:

```csharp
using System;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Findra;

/// <summary>
/// The update window: the painted panel <see cref="UpdatePainter"/> draws, in a borderless window
/// of exactly its size, with the pointer and the keyboard wired to an <see cref="UpdateSession"/>.
/// One at a time (<see cref="Open"/>). Esc closes it, and so does Alt+F4, except while a hand-off
/// is in flight: the installer or winget is already running and cannot be called back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateWindow : Window
{
    public static UpdateWindow? Open { get; private set; }

    private readonly UpdateCanvas _canvas;
    private readonly UpdateSession _session;

    public UpdateWindow(UpdateSession session, Palette palette)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        AppIcon.Apply(this);
        _canvas = new UpdateCanvas(session, Derived.From(palette ?? throw new ArgumentNullException(nameof(palette))), this);
        Content = _canvas;

        Title = "Findra update";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowView(session.View);

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !UpdateFlow.Closable(_session.View)) return;
            e.Handled = true;
            Close();
        };
        Closing += (_, e) => { if (!UpdateFlow.Closable(_session.View)) e.Cancel = true; };
        Opened += (_, _) => { Open = this; Fit(); Activate(); _canvas.Focus(); };
        Closed += (_, _) => { if (ReferenceEquals(Open, this)) Open = null; _session.Closed(); };
    }

    /// <summary>Paint a new view and take its height: the window grows for the download bar and
    /// shrinks when the buttons go.</summary>
    public void ShowView(UpdateView v)
    {
        _canvas.View = v;
        Fit();
        _canvas.InvalidateVisual();
    }

    private void Fit()
    {
        SKRect surface = UpdatePainter.Surface(_canvas.View, Parts.Face);
        double k = ScreenFit.For(this, surface.Width, surface.Height);
        _canvas.Fit = k;
        Width = surface.Width * k;
        Height = surface.Height * k;
        if (IsVisible) ScreenFit.KeepInside(this);
    }

    // Fully qualified: a bare `Control` in this namespace binds to the settings model's record.
    private sealed class UpdateCanvas : Avalonia.Controls.Control
    {
        private readonly UpdateSession _session;
        private readonly Derived _derived;
        private readonly Window _owner;
        private UpdatePromptTarget _hover = UpdatePromptTarget.None;

        public UpdateView View { get; set; }
        public double Fit { get; set; } = 1.0;

        public UpdateCanvas(UpdateSession session, Derived derived, Window owner)
        {
            _session = session;
            _derived = derived;
            _owner = owner;
            View = session.View;
            Focusable = true;
        }

        private Point At(PointerEventArgs e)
        {
            Point p = e.GetPosition(this);
            return new Point(p.X / Fit, p.Y / Fit);
        }

        private UpdatePromptTarget HitAt(Point p) =>
            UpdatePrompt.HitTest((float)p.X, (float)p.Y, UpdatePainter.Surface(View, Parts.Face), UpdatePrompt.Buttons(View));

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            UpdatePromptTarget over = HitAt(At(e));
            if (over == _hover) return;
            _hover = over;
            Cursor = PointerCursor.Of(Pointers.ForPrompt(over));
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _hover = UpdatePromptTarget.None;
            Cursor = PointerCursor.Of(Pointers.ForPrompt(UpdatePromptTarget.None));
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            switch (HitAt(At(e)))
            {
                case UpdatePromptTarget.Close: _session.PressClose(); return;
                case UpdatePromptTarget.Go: _session.PressGo(); return;
                case UpdatePromptTarget.None:
                    // A borderless window is picked up by its body.
                    try { _owner.BeginMoveDrag(e); }
                    catch (Exception ex) { Log.Warn("update", "the window would not move: " + ex.Message); }
                    return;
                default: throw new ArgumentOutOfRangeException(nameof(e), "no press for this target");
            }
        }

        public override void Render(DrawingContext context) =>
            context.Custom(new DrawOp(new Rect(Bounds.Size), this));

        private sealed class DrawOp(Rect bounds, UpdateCanvas c) : ICustomDrawOperation
        {
            public Rect Bounds { get; } = bounds;
            public bool HitTest(Point p) => true;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
                using ISkiaSharpApiLease lease = feature.Lease();
                SKCanvas canvas = lease.SkCanvas;
                canvas.Save();
                canvas.Scale((float)c.Fit);
                UpdatePainter.Paint(canvas, c.View, c._hover, c._derived, Parts.Face);
                canvas.Restore();
            }
        }
    }
}
```

- [ ] **Step 5: `Config.LastRunVersion` and `UpdateMemory`**

`Config.cs`: after `InstallSource` add

```csharp
    /// <summary>The version that last started, so the first start on a new one can say what it
    /// was updated to. Null before the first start that records it.</summary>
    public string? LastRunVersion { get; init; }
```

and add `&& LastRunVersion == other.LastRunVersion` to `Equals` after the `InstallSource` line, and `h.Add(LastRunVersion);` to `GetHashCode` after `h.Add(InstallSource);`.

`App.axaml.cs`, class `UpdateMemory`: delete `CheckedHeader` and its doc comment, and add

```csharp
    /// <summary>Whether this start is the first on a newer version than the last one that ran.
    /// Not on a first run ever (nothing ran before), and not when either side does not parse.</summary>
    public static bool JustUpdated(string? lastRun, string running) =>
        !string.IsNullOrWhiteSpace(lastRun) && Remembered(lastRun, running) == UpdateState.Available;

    /// <summary>The tray item's words: what it was updated to on the first start after an update,
    /// and Check for updates every other time. It opens the update window either way.</summary>
    public static string TrayHeader(string? updatedTo) =>
        updatedTo is null ? "Check for updates" : $"Updated to {updatedTo}";
```

- [ ] **Step 6: Wire the shell**

In `App.axaml.cs` (class `Shell`):

1. Fields, next to `_update` and `_latest`:
   ```csharp
       private string? _updatedTo;
       private static readonly HttpClient DownloadHttp = UpdateDownload.CreateClient();
   ```
2. Startup, directly after the `InstallSource` block (`install source recorded as ...`):
   ```csharp
               // The first start on a new version says so in the tray, once. Then the installer
               // this update downloaded, or any left over from one that did not run, goes.
               if (UpdateMemory.JustUpdated(_config.LastRunVersion, Log.Version))
               {
                   _updatedTo = Log.Version;
                   Log.Info("startup", $"updated from {_config.LastRunVersion} to {Log.Version}");
               }
               if (_config.LastRunVersion != Log.Version)
               {
                   _config = _config with { LastRunVersion = Log.Version };
                   _config.Save();
               }
               UpdateDownload.Sweep(Paths.Updates);
   ```
3. Tray: replace
   ```csharp
           var check = new NativeMenuItem("Check for updates");
           check.Click += (_, _) => _ = RunUpdateCheck(force: true);
   ```
   with
   ```csharp
           var check = new NativeMenuItem(UpdateMemory.TrayHeader(_updatedTo));
           check.Click += (_, _) => OpenUpdateWindow();
   ```
   and delete the `_checkForUpdatesItem` field and the line `_checkForUpdatesItem = check;`.
4. `ISettingsHost.CheckNow()`: replace its body and comment with
   ```csharp
       // The same window the tray opens: one place to ask, one place the answer and Update now are.
       void ISettingsHost.CheckNow() => OpenUpdateWindow();
   ```
5. Replace `RunUpdateCheck(bool force)` with a version that returns what it found:
   ```csharp
       /// <summary>The check behind both the daily background run (<paramref name="manual"/> false:
       /// the 24 hour gate and the switch hold) and the update window (true: they do not). Either
       /// way what it found is remembered, the tooltip and Settings' About row follow it, and nothing
       /// else is opened: only the window shows an answer, and only a person opens the window.</summary>
       private async Task<UpdateResult> RunUpdateCheck(bool manual, CancellationToken ct)
       {
           try
           {
               UpdateResult result = await UpdateCheck.CheckAsync(
                   _config,
                   c => UpdateCheck.FetchLatestAsync(Http, Log.Version, _config.InstallSource,
                                                     System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture, c),
                   DateTime.UtcNow, ct, manual).ConfigureAwait(false);

               await Dispatcher.UIThread.InvokeAsync(() =>
               {
                   // Only the timestamp and the tag are taken back, and both are merged onto the
                   // CURRENT config on the UI thread. The config may have moved on since the request
                   // started - a capsule dragged, the capsule toggled off - and writing the whole
                   // record back would undo it.
                   Config merged = _config;
                   if (result.Config.LastUpdateCheck != merged.LastUpdateCheck)
                       merged = merged with { LastUpdateCheck = result.Config.LastUpdateCheck };

                   bool answered = result.State is UpdateState.Current or UpdateState.Available;
                   if (answered && !string.IsNullOrWhiteSpace(result.Latest) &&
                       result.Latest != merged.LatestKnownVersion)
                       merged = merged with { LatestKnownVersion = result.Latest };

                   if (!ReferenceEquals(merged, _config))
                   {
                       _config = merged;
                       _config.Save();
                   }

                   // A not-due or failed check carries no tag, and must not erase what the last
                   // successful one found - remembering it is the whole point of writing it down.
                   if (answered)
                   {
                       _update = result.State;
                       _latest = result.Latest;
                   }

                   SettingsWindow.Open?.NoteUpdate(result.State, result.Latest);
                   RefreshTooltip();
                   if (result.Advice is { } advice) Log.Info("startup", advice);
               });
               return result;
           }
           catch (Exception ex)
           {
               Log.Warn("startup", "the update check could not run: " + ex.Message);
               return new UpdateResult(UpdateState.Unknown, null, null, _config);
           }
       }
   ```
6. The background call in `StartupStep.UpdateCheck`: `await RunUpdateCheck(force: false).ConfigureAwait(false);` becomes `await RunUpdateCheck(manual: false, _shutdown.Token).ConfigureAwait(false);`.
7. Add:
   ```csharp
       // ---- the update window -----------------------------------------------------------------------

       /// <summary>Open the update window, or bring the open one forward. A second window would be a
       /// second check and possibly a second download of the same file.</summary>
       private void OpenUpdateWindow()
       {
           if (TheWelcomeScreenIsInTheWay()) return;
           if (UpdateWindow.Open is { } open) { open.Activate(); return; }
           try
           {
               UpdateWindow? window = null;
               var session = new UpdateSession(
                   UpdateFlow.Start(Log.Version, _config.InstallSource),
                   check: async ct =>
                   {
                       using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
                       return await RunUpdateCheck(manual: true, both.Token).ConfigureAwait(false);
                   },
                   download: (asset, progress, ct) => UpdateDownload.GetAsync(DownloadHttp, asset, Paths.Updates, Log.Version, progress, ct),
                   runInstaller: (path, ct) => UpdateHandoff.RunInstallerAsync(path, UpdateHandoff.Real(Timeout.InfiniteTimeSpan), ct),
                   runWinget: ct => UpdateHandoff.RunWingetAsync(UpdateHandoff.Real(UpdateHandoff.WingetLimit), ct),
                   openReleases: OpenReleasesPage,
                   show: v => window?.ShowView(v),
                   close: () => window?.Close(),
                   post: a => Dispatcher.UIThread.Post(a));
               window = new UpdateWindow(session, _palette);
               window.Show();
               session.Begin();
           }
           catch (Exception ex) { Log.Error("app", "the update window could not open", ex); }
       }

       private static void OpenReleasesPage()
       {
           try
           {
               Process.Start(new ProcessStartInfo("https://github.com/blakazulu/findra/releases/latest") { UseShellExecute = true });
           }
           catch (Exception ex) { Log.Warn("update", "could not open the releases page: " + ex.Message); }
       }
   ```
   (`UpdateHandoff.Real(Timeout.InfiniteTimeSpan)` for the installer: the person may take their time over the permission prompt, and killing the installer under it would be worse than waiting.)

`grep -n "_checkForUpdatesItem\|CheckedHeader\|force:" src/Findra/App/App.axaml.cs` must print nothing.

- [ ] **Step 7: Run everything**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UpdateSessionTests|FullyQualifiedName~UpdateMemoryTests|FullyQualifiedName~ScreenFitTests|FullyQualifiedName~ConfigTests"`
Expected: PASS (`ConfigTests.EveryPropertyIsPartOfEquality` passes only with both `Equals` and `GetHashCode` edits). Then `dotnet build -warnaserror -t:Rebuild` and `dotnet test`.

- [ ] **Step 8: CHANGELOG and commit**

Extend the Added entry with:

```markdown
  After an update, the tray menu says what Findra was updated to until it is next closed.
```

```bash
git add -A src/Findra/Update src/Findra/App tests/Findra.Tests CHANGELOG.md
git commit -m "The update window opens from the tray and from Settings, and Update now runs"
```

---

### Task 7: Findra starts again after a silent install

**Files:**
- Modify: `src/Findra/Startup/Uninstall.cs`
- Modify: `installer/findra.iss`
- Modify: `CLAUDE.md` (the `--stop` line in the modes list)
- Test: `tests/Findra.Tests/Startup/UninstallTests.cs`, `tests/Findra.Tests/Build/InstallerScriptTests.cs`

**Interfaces:**
- Produces: `Uninstall.StopExitCode(Running running, IReadOnlyList<int> spare) : int` (2 or 0); `StopAll()` returns it.

- [ ] **Step 1: Write the failing tests**

In `UninstallTests.cs`:

```csharp
    [Fact]
    public void StopSaysWhetherItStoppedARunningInterface()
    {
        // The installer reads this to decide whether to start Findra again after a silent install.
        Assert.Equal(2, Uninstall.StopExitCode(new Running(Interface: 200, Helper: null, Others: [300]), spare: [999]));
        Assert.Equal(0, Uninstall.StopExitCode(new Running(Interface: null, Helper: null, Others: [300]), spare: [999]));
        Assert.Equal(0, Uninstall.StopExitCode(new Running(Interface: 999, Helper: null, Others: []), spare: [999]));
    }
```

In `InstallerScriptTests.cs`:

```csharp
    [Fact]
    public void ASilentInstallStartsFindraAgainOnlyIfItHadBeenRunning()
    {
        // Every winget upgrade and every Update now is silent, and "Start Findra" is skipped when
        // silent, so without this Findra stayed closed after every update. Started through
        // explorer.exe, so it runs as the signed-in user: an elevated Findra cannot drag a result
        // into Explorer, and this installer runs elevated.
        Match run = Regex.Match(Script, @"(?m)^Filename:\s*""\{win\}\\explorer\.exe"";.*$");
        Assert.True(run.Success, "no [Run] entry starts Findra through explorer.exe");
        Assert.Contains(@"Parameters: """"""{app}\findra.exe""""""", run.Value, StringComparison.Ordinal);
        Assert.Contains("Check: RestartAfterSilentInstall", run.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("runascurrentuser", run.Value, StringComparison.OrdinalIgnoreCase);

        string check = Body("RestartAfterSilentInstall");
        Assert.Contains("WizardSilent", check, StringComparison.Ordinal);
        Assert.Contains("WasRunning", check, StringComparison.Ordinal);
        Assert.Contains("WasRunning := code = 2", Body("StopFindra"), StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~StopSaysWhether|FullyQualifiedName~ASilentInstallStartsFindraAgain"`
Expected: FAIL (`StopExitCode` does not exist; the script has no such entry).

- [ ] **Step 3: `--stop`'s exit code**

In `Uninstall.cs`, add above `StopAll()`:

```csharp
    /// <summary>What <c>--stop</c> exits with: 2 when it stopped a running interface, 0 otherwise.
    /// The installer reads it to start Findra again after a silent install, and only then.</summary>
    public static int StopExitCode(Running running, IReadOnlyList<int> spare)
    {
        ArgumentNullException.ThrowIfNull(spare);
        return running.Interface is { } ui && !spare.Contains(ui) ? 2 : 0;
    }
```

and change the private `StopAll(IReadOnlyList<int> spare)` to

```csharp
    private static int StopAll(IReadOnlyList<int> spare)
    {
        Running running = Discover();
        foreach (int pid in StopOrder(running, spare))
        {
            // (the existing loop body, unchanged)
        }
        return StopExitCode(running, spare);
    }
```

keeping the existing `try`/`catch` loop body verbatim.

In the same file, the production `delete` lambda inside `Run(string[] args)`: change
`IReadOnlyList<Removal> removed = Delete(deletes, Roots());` to

```csharp
                // The downloaded installers go with a purge. They are nobody's data, so they are
                // never priced in the report, and an empty "updates" folder left behind would keep
                // %LOCALAPPDATA%\Findra standing after a purge that said it was gone.
                IReadOnlyList<Removal> removed = Delete([.. deletes, new DataSize("updates", Paths.Updates, 0)], Roots());
```

(`Delete` holds every path inside `Roots()`, and `Paths.Updates` is inside `%LOCALAPPDATA%\Findra`; a folder that is not there is already done.)

- [ ] **Step 4: The installer**

In `installer/findra.iss`:

- `[Run]`: after the existing `Start Findra` line, add
  ```
  ; A silent install (every winget upgrade, and Update now) skips the line above, so Findra would stay
  ; closed after every update. This puts back what the install stopped, and only that, through the
  ; shell so it runs as the signed-in user: this installer runs elevated, and Findra must not.
  Filename: "{win}\explorer.exe"; Parameters: """{app}\findra.exe"""; Flags: nowait; Check: RestartAfterSilentInstall
  ```
- `[Code]`: add `WasRunning: Boolean;` to the `var` block beside `Purge: Boolean;`. In `StopFindra`, replace the `Exec(...)` line with
  ```pascal
    begin
      Exec(ExpandConstant('{app}\findra.exe'), '--stop', '', SW_HIDE, ewWaitUntilTerminated, code);
      // 2: an interface was running and has been stopped. An older findra.exe always answers 0.
      WasRunning := code = 2;
    end;
  ```
  (turning the `if FileExists(...) then` into `if FileExists(...) then begin ... end;`). Below `PrepareToInstall`, add
  ```pascal
  function RestartAfterSilentInstall(): Boolean;
  begin
    // An interactive install asks with its own "Start Findra" checkbox instead.
    Result := WizardSilent() and WasRunning;
  end;
  ```

- [ ] **Step 5: Run the tests to see them pass, and compile the script**

Run: `dotnet test tests/Findra.Tests --filter "FullyQualifiedName~UninstallTests|FullyQualifiedName~InstallerScriptTests"`
Expected: PASS. Then compile the script to prove it parses:
`& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" "/DAppVersion=0.0.1" "/DPublishDir=..\publish\win-x64" "/DArch=x64" installer\findra.iss`
Expected: `Successful compile` (the output folder is git-ignored). Then `dotnet build -warnaserror -t:Rebuild` and `dotnet test`.

- [ ] **Step 6: CLAUDE.md, CHANGELOG and commit**

In `CLAUDE.md`, change the `findra.exe --stop` line to
`findra.exe --stop                     # stop the interface, the indexer and the name helper; exits 2`
and add on the next line `                                      # when it stopped a running interface (the installer reads it)`.

Add under `### Fixed`:

```markdown
- **Findra starts again after an update.** A winget upgrade, or any installer run without its
  wizard, closed Findra and left it closed until the next sign-in. It now starts again when the
  installer is done, if it had been running. This takes effect from the update after this one,
  because it is the version being replaced that reports whether it was running.
```

```bash
git add src/Findra/Startup/Uninstall.cs installer/findra.iss tests/Findra.Tests/Startup/UninstallTests.cs tests/Findra.Tests/Build/InstallerScriptTests.cs CLAUDE.md CHANGELOG.md
git commit -m "A silent install starts Findra again if it stopped a running one"
```

---

### Task 8: The words everywhere else

**Files:**
- Modify: `docs/superpowers/specs/2026-09-01-findra-design.md` (§9b), `PRIVACY.md`, `README.md`, `website/content/about.md`, `website/content/pages/faq.html`, `website/content/pages/faq.md`, `website/public/llms.txt` (if it says the same), `src/Findra/First/FirstRun.cs` (`Disclosure`), `CLAUDE.md` (*The update panel*, *Versions and updates*), `docs/end-to-end-checklist.md`, `docs/e2e-run-sheet.md`, `CHANGELOG.md`
- Regenerate: `node build/Make-Pages.mjs`

- [ ] **Step 1: Find every sentence that is now untrue**

Run: `grep -rniE "never (install|update|download)s? (anything|an update|itself)|installs nothing itself|updates itself|self-updat|off means|request is not made|downloads or installs|never updates" README.md PRIVACY.md CLAUDE.md website/content website/public/llms.txt src/Findra docs/end-to-end-checklist.md docs/e2e-run-sheet.md`
Every hit about updates changes; the two about codecs (`SettingsActions.cs`, `SettingsModel.cs`, `guides/find-photos-by-description.md`: "Findra installs nothing itself" for a Store codec) stay.

- [ ] **Step 2: Rewrite each, with this wording**

- The privacy sentence (PRIVACY.md bullet, README's privacy paragraph, FAQ answer, About page, `FirstRun.Disclosure`): "It can be switched off. Off stops the daily check; pressing Check now still asks, once, because you asked."
- The install sentence (PRIVACY.md, README, FAQ, About): "Findra downloads and installs an update only when you press Update now. It downloads the installer from the release page and checks it against the checksum GitHub publishes for it before running it; a copy installed with winget runs the winget upgrade instead. The installer or winget replaces the files, never Findra itself."
- `FirstRun.Disclosure` becomes: `"Findra asks GitHub for the newest version at most once every 24 hours. It is one anonymous request with no query parameters, no machine or install identifier, and nothing about your files or your searches. It installs an update only when you press Update now, and turning this off stops the daily check."`
- Spec §9b: replace "Findra knows its own version, learns whether a newer one exists, and tells you. It does not install anything by itself." with "Findra knows its own version, learns whether a newer one exists, tells you, and starts the upgrade when you press Update now."; replace the bullet "**It can be switched off** in settings, and off means off: no request is made." with "**It can be switched off** in settings. Off stops the daily check; pressing Check now still asks, once, because the person asked."; replace everything under *What it does with the answer* (the paragraph, the table and the self-updater paragraph, up to *Version identity*) with:

  ```markdown
  Findra **never replaces its own files**. The installer and winget do, because replacing a
  running executable and re-registering an elevated scheduled task are the two operations in this
  product most likely to leave a machine broken, and both already do them correctly. What Findra
  does is start them, when the person presses **Update now** in the update window:

  | Installed via | Update now |
  |---|---|
  | the installer, or unknown | downloads this machine's installer from the release, checks its size and SHA-256 against the release record, and runs it silently |
  | winget | runs `winget upgrade --id blakazulu.Findra` with no console window |
  | `git clone` + `dotnet publish` | opens the release page |

  The window, its states and the download rules are in
  `docs/superpowers/specs/2026-09-28-update-window-design.md`. The install source is recorded at
  first run, not guessed at every launch.
  ```
- CLAUDE.md: replace the whole *The update panel* section (heading and bullets) with:

  ```markdown
  ## The update window

  **Check for updates** in the tray and **Check now** in Settings open one small window
  (`src/Findra/Update/`); nothing else does, and the daily background check never does.

  - **A manual check ignores the 24 hour gate and the switch** (`CheckAsync(manual: true)`); the
    background check keeps both, and off still means it makes no request.
  - **`UpdateFlow` is the window as a list of states**, pure and tested; `UpdateSession` starts
    what each move names through delegates, and the window only paints and reports presses.
  - **The check keeps this machine's installer from the same response** (`InstallerOf`, by
    `ProcessArchitecture`); an entry with no `sha256:` digest is never offered.
  - **Downloads are GitHub-only, over HTTPS, capped at 512 MB, and checked** (size and SHA-256)
    before anything runs. Redirects are followed one hop at a time, each held to the host rule.
  - **Findra never replaces its own files.** The installer runs with
    `/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-`; winget with exactly
    `upgrade --id blakazulu.Findra --exact --accept-source-agreements --accept-package-agreements
    --disable-interactivity`, no window, both streams drained, killed at 20 minutes. A hand-off
    that returns at all installed nothing, because the installer stops Findra first.
  - **`--stop` exits 2 when it stopped a running interface**, and the installer starts Findra again
    through `explorer.exe` (never elevated) only after a silent install that stopped one.
  - **While the installer or winget runs, the window cannot be closed**: they cannot be called back.
  ```

  In *Versions and updates*, replace "**Findra never installs anything by itself** - no self-updater, background installer or elevation; winget does it correctly." with "**Findra never replaces its own files.** Update now runs the installer or winget, and they do; nothing happens without the press." and replace "off means no request." with "off stops the daily check; Check now still asks."
- `docs/end-to-end-checklist.md`: in item 8 change `click "Check for updates": the menu item's own text changes` to `click "Check for updates": the update window opens, checks and answers`; after item 69 add:

  ```markdown
  70. **Update now installs the newer release.** Install a build stamped older than the latest
      release (`dotnet publish` with `-p:Version=0.0.1`, then the installer from it), then Check for
      updates in the tray, then Update now. The installer downloads with a bar, Windows asks for
      permission, Findra closes, the release installs, and `findra --version` names it. The log has
      `updated from 0.0.1`. From the release after 0.6.0, Findra also starts again by itself once
      the installer is done.
  ```
- `docs/e2e-run-sheet.md`: in 6.8 make the same change as item 8, and after 9.9 add:

  ```markdown
  ### 9.10 (catalogue 70) Update now, end to end - admin

  **Do:** install a build stamped `0.0.1` from this code, start it, open Check for updates in the
  tray, press Update now, and answer the permission prompt Yes.

  **Pass:** the window shows the bar counting up to the installer's size, then "Installing"; Findra
  closes; the release installs; `findra --version` names the release; the next start's tray item
  reads "Updated to" the release.

  **A failure at the download means** the host rule or the digest refused it: the window says
  which, and the log line under `update` names the host or the mismatch.
  ```

- [ ] **Step 3: Regenerate the site and run everything**

Run: `node build/Make-Pages.mjs`, then `dotnet test`.
Expected: PASS, including `WebsiteTests` (PRIVACY.md and `/privacy/` agree both ways) and `FirstRunTests` (update any assertion that quotes the old disclosure to quote the new one).

- [ ] **Step 4: CHANGELOG and commit**

Add under `### Changed`:

```markdown
- Documentation: the privacy policy, the README, the FAQ and the About page say that Findra
  installs an update only when you press Update now, and that off stops the daily check rather
  than refusing a check you ask for.
```

```bash
git add -A PRIVACY.md README.md CLAUDE.md docs website src/Findra/First/FirstRun.cs tests CHANGELOG.md
git commit -m "The privacy policy and the docs say what Update now does"
```

---

### Task 9: Automatic end-to-end checks

**Files:** none committed.

- [ ] **Step 1: The whole suite and the headless modes on a fresh publish**

Run: `dotnet build -warnaserror -t:Rebuild`, `dotnet test`, `pwsh -File build/Publish.ps1 -Rid win-x64`, `pwsh -File build/Check-Diagnostics.ps1 -Exe publish/win-x64/findra.exe`, and `publish/win-x64/findra.exe --searchshot <tmp>.png <state>` for each of the eleven `update*` states, looking at each PNG.
Expected: 0 warnings; all tests pass; every mode answers; every shot shows its state legibly.

- [ ] **Step 2: Against the real GitHub release**

Write a throwaway test file `tests/Findra.Tests/Update/ScratchLiveUpdate.cs` (deleted afterwards, never committed) that calls `UpdateCheck.FetchLatestAsync(UpdateCheck.CreateClient(), "0.5.0", "installer", Architecture.X64, ct)`, asserts the tag and an installer with a 64-character digest, then `UpdateDownload.GetAsync(UpdateDownload.CreateClient(), release.Installer!, <temp dir>, "0.5.0", null, ct)` and asserts `DownloadFailure.None` and a file of the declared size. Run it with `dotnet test --filter FullyQualifiedName~ScratchLiveUpdate`, then delete the file.
Expected: the latest release's real installer downloads through GitHub's redirect and matches its published digest.

- [ ] **Step 3: The one step that needs a person**

The real hand-off (the permission prompt, the installer replacing the files) is catalogue item 70; it waits for somebody at the machine.
