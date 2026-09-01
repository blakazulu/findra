# Findra Plan 3 - The Widget

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Findra runs. A capsule sits on the desktop, a hotkey opens it from anywhere, typing searches the real NTFS index over the pipe, and Enter opens the file.

**Architecture:** Plan 1 proved the pipe with a probe. Plan 2 proved the card with a renderer. **Plan 3 is where they meet.** An Avalonia shell hosts the Skia card; the card's search calls become async round trips to the elevated helper; the capsule, tray and hotkey are how a person reaches it.

**Tech Stack:** .NET 10 (`net10.0-windows`), Avalonia 12.1.1, SkiaSharp 3.119.4, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-01-findra-design.md` - §3 processes, §7 look and surfaces, §9b versions and updates

## Global Constraints

- **Target `net10.0-windows`**; publish self-contained via the publish command, never a csproj RID.
- **Root namespace `Findra`.**
- **Avalonia 12.1.1 with SkiaSharp pinned at 3.119.4.** Verified from the nuspecs: both Avalonia 12.0.5 and 12.1.1 require exactly 3.119.4, which is already pinned. Two SkiaSharp majors in one process would mean two native `libSkiaSharp` binaries; do not let the pin drift.
- **Name search is async everywhere.** No `.Result`, no `.Wait()`. Exactly two `GetAwaiter().GetResult()` may exist, both at `Program.Main` arms. The ported card calls a synchronous service in six places and **every one becomes an await** - that is Task 5, and it is the point of the plan.
- **Every query carries a generation counter** and the client drops stale replies. That machinery exists; the card must not bypass it.
- **No lineage anywhere.** The name-grep is necessary and **not sufficient** - files in this project have passed a name-grep while keeping the source's domain material (a sibling Wi-Fi picker, a game hub, a hardware anecdote, a CLI flag Findra lacks). Read every ported comment and ask whether it reads as though written for Findra by someone who has never seen another codebase.
- **Every painted colour comes from `Derived`.** A literal `SKColor` in painting code is a defect, except pure black as a shadow's own colour and the documented always-dark tile glyphs.
- **The update check is the one thing that leaves the machine** (spec §9b). Anonymous, at most daily, never blocking, switchable off, and Findra never installs anything itself.
- **Build output pristine** - zero warnings. TDD for new code; the ported window is not rewritten test-first.
- Commit messages carry no AI/Claude attribution.

## Where this sits

| Plan | Delivers |
|---|---|
| 1 - Foundation and the name pipe | ✅ 53 tests |
| 2 - The look | ✅ 174 tests |
| **3 - The widget** ← this plan | The window, the capsule, tray, hotkey, config, light/dark follow, the update check, and the card searching the real index |
| 4 - Content | FTS5 store, text extraction, the indexer child, `--searchindex`, `--searchbench` |
| 5 - Capabilities | Models, vectors, per-capability gating, the first-run download screen, `--searchmodels` |
| 6 - Settings and shipping | Settings window, `--uninstall`/`--purge`, publish, winget, the real README |

## Port source

```
C:\Code\Personal\Prism\src\Search\SearchCardWindow.cs   847   window, interactive canvas, dim overlay, open/reveal, preview cache
C:\Code\Personal\Prism\src\App\App.axaml.cs             (tray icon pattern only, ~line 793)
```

`SearchCardWindow.cs` carries five things: the `Window` itself, `CardCanvas` (typing, keys, timers, hit-testing), `SearchDimWindow`, `SearchActions` (open/reveal), and `PreviewCache`. It references the old widget host in only three places.

---

## File structure

| File | Responsibility |
|---|---|
| `src/Findra/App/App.axaml` + `.cs` | Avalonia application, tray icon, lifetime |
| `src/Findra/App/Config.cs` | `config.json` - load, save, defaults, migration |
| `src/Findra/App/Theme.cs` | resolve a `Palette` from config + the Windows light/dark setting |
| `src/Findra/App/Hotkey.cs` | `RegisterHotKey` and the fallback chain |
| `src/Findra/App/UpdateCheck.cs` | version compare, the daily cache, the opt-out |
| `src/Findra/App/CapsuleWindow.cs` | the desktop capsule |
| `src/Findra/Card/CardWindow.cs` | ported window + canvas, searching over the pipe |
| `src/Findra/Card/DimWindow.cs` | the monitor dim behind an open card |
| `src/Findra/Card/CardActions.cs` | open, reveal, copy path |
| `src/Findra/Card/PreviewCache.cs` | thumbnail cache |
| `tests/Findra.Tests/App/ConfigTests.cs` | round-trip, defaults, malformed |
| `tests/Findra.Tests/App/ThemeTests.cs` | the three modes, the pair, user palettes |
| `tests/Findra.Tests/App/HotkeyTests.cs` | the fallback chain |
| `tests/Findra.Tests/App/UpdateCheckTests.cs` | version compare, cadence, opt-out |

---

## Task 1: Avalonia shell

**Files:** Create `src/Findra/App/App.axaml`, `App.axaml.cs`; modify `src/Findra/Findra.csproj`, `src/Findra/Program.cs`

**Produces:** `Findra.App` (an Avalonia `Application`), `Findra.Program.RunUi() : int`.

- [ ] **Step 1: Add the packages**

```bash
dotnet add src/Findra package Avalonia --version 12.1.1
dotnet add src/Findra package Avalonia.Desktop --version 12.1.1
dotnet add src/Findra package Avalonia.Themes.Fluent --version 12.1.1
```

Then confirm the SkiaSharp pin did not move:

```bash
dotnet list src/Findra package --include-transitive | grep -i skiasharp
```

**Every SkiaSharp entry must read 3.119.4.** If Avalonia pulled a different one, stop and report it - two native `libSkiaSharp` binaries in one process is the failure this pin exists to prevent, and it will not announce itself.

Add `<ApplicationManifest>app.manifest</ApplicationManifest>` with a DPI-aware manifest so the card is not bitmap-scaled on a high-DPI monitor.

- [ ] **Step 2: The application**

`src/Findra/App/App.axaml` is a minimal `Application` with the Fluent theme (only the tray menu uses it; the card is drawn by Skia).

`App.axaml.cs` overrides `OnFrameworkInitializationCompleted`, and for `IClassicDesktopStyleApplicationLifetime` sets `ShutdownMode = OnExplicitShutdown` - **Findra has no main window**. It lives in the tray with a capsule on the desktop, and a lifetime that quits when the last window closes would exit the moment the card is dismissed.

- [ ] **Step 3: Wire the arm**

In `Program.cs`, the default arm currently calls `Hello()`. Replace it with `RunUi()`:

```csharp
    private static int RunUi()
    {
        Log.Info("startup", $"findra {Log.Version} starting");
        try
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception ex) { Log.Error("startup", "the UI could not start", ex); Log.Flush(); return 1; }
    }
```

Keep `Hello()` reachable behind `--version`, and add it to the known-modes list.

- [ ] **Step 4: Verify**

`dotnet build` clean, `dotnet test` still 174. Running `dotnet run --project src/Findra` should start and stay running with no window - that is correct at this point. Confirm it exits cleanly on Ctrl+C and writes a startup line to the log.

- [ ] **Step 5: Commit** - `git commit -m "An Avalonia shell that lives in the tray, not in a window"`

---

## Task 2: Config

**Files:** Create `src/Findra/App/Config.cs`, `tests/Findra.Tests/App/ConfigTests.cs`

**Produces:** `Findra.Config` with `DarkPalette`, `LightPalette`, `Mode`, `Hotkey`, `CapsuleX`, `CapsuleY`, `ShowCapsule`, `CheckForUpdates`, `LastUpdateCheck`, `InstallSource`; `Config.Load(string? json)`, `Config.LoadFromDisk()`, `Config.Save()`, `Config.Default`.

`ThemeMode` is `FollowWindows | AlwaysDark | AlwaysLight`.

- [ ] **Step 1: Write the failing test**

```csharp
using Findra;
using Xunit;

public class ConfigTests
{
    [Fact]
    public void DefaultsAreMondAndPaperFollowingWindows()
    {
        Config c = Config.Default;
        Assert.Equal("Mond", c.DarkPalette);
        Assert.Equal("Paper", c.LightPalette);
        Assert.Equal(ThemeMode.FollowWindows, c.Mode);
        Assert.True(c.ShowCapsule);
        Assert.True(c.CheckForUpdates);
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        var c = Config.Default with
        {
            DarkPalette = "Verdigris", LightPalette = "Blueprint", Mode = ThemeMode.AlwaysDark,
            Hotkey = "Ctrl+Alt+F", CapsuleX = 120, CapsuleY = 900, ShowCapsule = false,
            CheckForUpdates = false, LastUpdateCheck = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            InstallSource = "winget",
        };

        Config back = Config.Load(c.ToJson());

        Assert.Equal(c, back);
    }

    [Fact]
    public void AMissingFileGivesTheDefaults()
    {
        Assert.Equal(Config.Default, Config.Load(null));
        Assert.Equal(Config.Default, Config.Load(""));
    }

    [Fact]
    public void BrokenJsonGivesTheDefaultsRatherThanThrowing()
    {
        // Someone's settings file must never be able to stop the app starting.
        Assert.Equal(Config.Default, Config.Load("{ not json"));
    }

    [Fact]
    public void AnUnknownFieldIsIgnoredAndTheRestSurvive()
    {
        // Forward compatibility: a newer Findra's file must not break an older one.
        Config c = Config.Load("""{ "darkPalette": "Brass", "somethingFromTheFuture": 42 }""");
        Assert.Equal("Brass", c.DarkPalette);
        Assert.Equal("Paper", c.LightPalette);
    }

    [Fact]
    public void AnUnknownModeFallsBackRatherThanThrowing()
    {
        Assert.Equal(ThemeMode.FollowWindows, Config.Load("""{ "mode": "Purple" }""").Mode);
    }
}
```

- [ ] **Step 2** Run: `dotnet test --filter ConfigTests` - FAIL, `Config` does not exist.

- [ ] **Step 3: Write Config**

A `record` with `init` properties and a `ToJson()`. Use `System.Text.Json` with `PropertyNameCaseInsensitive`, `ReadCommentHandling.Skip`, `AllowTrailingCommas`, and a `JsonStringEnumConverter` that falls back rather than throwing on an unknown mode. `LoadFromDisk` reads `Paths.ConfigFile` and returns `Config.Default` on any failure, logging under `startup`. `Save` writes indented JSON to `Paths.ConfigFile` via `Paths.Ensure(Paths.Config)`, catching and logging - **saving settings must never take the app down.**

- [ ] **Step 4** Run: PASS, 6 tests.

- [ ] **Step 5: Commit** - `git commit -m "config.json: settings that cannot stop the app starting"`

---

## Task 3: Theme resolution

**Files:** Create `src/Findra/App/Theme.cs`, `tests/Findra.Tests/App/ThemeTests.cs`

**Produces:** `Findra.Theme.Resolve(Config, bool windowsIsLight, IReadOnlyList<Palette> available) : Palette` and `Theme.WindowsIsLight() : bool`.

`Resolve` is pure and takes the Windows setting as a parameter, so it is testable without a registry. `WindowsIsLight` is the impure lookup.

- [ ] **Step 1: Write the failing test**

```csharp
using Findra;
using Xunit;

public class ThemeTests
{
    private static readonly IReadOnlyList<Palette> All = Palette.BuiltIn;

    [Fact]
    public void FollowWindowsTakesTheDarkPickWhenWindowsIsDark()
    {
        Config c = Config.Default with { DarkPalette = "Verdigris", LightPalette = "Blueprint" };
        Assert.Equal("Verdigris", Theme.Resolve(c, windowsIsLight: false, All).Name);
        Assert.Equal("Blueprint", Theme.Resolve(c, windowsIsLight: true, All).Name);
    }

    [Fact]
    public void PinnedModesIgnoreWindowsEntirely()
    {
        Config dark = Config.Default with { Mode = ThemeMode.AlwaysDark, DarkPalette = "Brass" };
        Assert.Equal("Brass", Theme.Resolve(dark, windowsIsLight: true, All).Name);

        Config light = Config.Default with { Mode = ThemeMode.AlwaysLight, LightPalette = "Porcelain" };
        Assert.Equal("Porcelain", Theme.Resolve(light, windowsIsLight: false, All).Name);
    }

    [Fact]
    public void AUserPaletteIsResolvedByName()
    {
        var mine = new Palette("Mine", new SkiaSharp.SKColor(1, 2, 3),
            new SkiaSharp.SKColor(0xEE, 0xEE, 0xEE), new SkiaSharp.SKColor(0x10, 0x10, 0x10), false);
        Config c = Config.Default with { Mode = ThemeMode.AlwaysDark, DarkPalette = "Mine" };

        Assert.Equal("Mine", Theme.Resolve(c, false, [.. All, mine]).Name);
    }

    [Fact]
    public void ADeletedPaletteFallsBackToTheDefaultOfTheRightSide()
    {
        // Someone edits palettes.json and removes the palette their config names. Findra must
        // keep the right SIDE of the light/dark line rather than flipping the whole card.
        Config c = Config.Default with { Mode = ThemeMode.AlwaysDark, DarkPalette = "Gone" };
        Palette got = Theme.Resolve(c, windowsIsLight: false, All);
        Assert.False(got.Light);
        Assert.Equal(Palette.DefaultDark.Name, got.Name);

        Config l = Config.Default with { Mode = ThemeMode.AlwaysLight, LightPalette = "Gone" };
        Assert.True(Theme.Resolve(l, windowsIsLight: true, All).Light);
    }

    [Fact]
    public void APaletteOnTheWrongSideIsHonouredButNoted()
    {
        // Nothing stops someone naming a light palette as their dark pick. Honour it - it is
        // their choice - but the card must still be drawn from that palette's own values.
        Config c = Config.Default with { Mode = ThemeMode.AlwaysDark, DarkPalette = "Paper" };
        Assert.Equal("Paper", Theme.Resolve(c, false, All).Name);
    }
}
```

- [ ] **Step 2** FAIL - `Theme` does not exist.

- [ ] **Step 3: Write Theme**

`Resolve` picks the side from `Mode` (`FollowWindows` consults `windowsIsLight`), looks the name up case-insensitively in `available`, and falls back to `Palette.DefaultDark` / `DefaultLight` **on the same side** when it is missing, logging once under `look`.

`WindowsIsLight()` reads `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`, returning `false` if the value is absent or unreadable - a missing key means a dark-mode-era default, and a registry read must never throw into startup.

- [ ] **Step 4** PASS, 5 tests.

- [ ] **Step 5: Commit** - `git commit -m "Resolve a palette from the config and the Windows setting"`

---

## Task 4: Port the card window

**Files:** Create `src/Findra/Card/CardWindow.cs`, `DimWindow.cs`, `CardActions.cs`, `PreviewCache.cs`

- [ ] **Step 1: Copy and split**

```bash
cp /c/Code/Personal/Prism/src/Search/SearchCardWindow.cs src/Findra/Card/CardWindow.cs
```

Split it into the four files above by responsibility - the window and its canvas stay together in `CardWindow.cs`; `SearchDimWindow` becomes `DimWindow`, `SearchActions` becomes `CardActions`, `PreviewCache` moves out. This split is authorised because the plan's file structure calls for it; nothing else may be restructured.

- [ ] **Step 2: Make it Findra's**

Change namespaces to `Findra`. Replace the three references to the old widget host and its theme manifest: the constructor takes a `Palette` (or a `Derived`) instead of a manifest, and `ThemeRenderer.Typeface(...)` becomes a typeface loaded from Findra's own resources - `SKTypeface.Default` is acceptable for now if the font is not yet embedded, but say so in your report.

Then do the whole-comment pass and report it separately from the name-grep.

- [ ] **Step 3: Stub the search calls, do not implement them**

The canvas calls a synchronous search service in six places. Leave a single private method `Task<QueryReply?> RunSearchAsync(string raw, CancellationToken ct)` that throws `NotImplementedException`, and route all six through it. **Task 5 makes it real.** Splitting the port from the async rewiring keeps two hard things apart.

- [ ] **Step 4** `dotnet build` clean; tests still 174.

- [ ] **Step 5: Commit** - `git commit -m "Port the card window, its canvas, the dim overlay and the actions"`

---

## Task 5: The card searches the real index

This is the plan's centre. Plan 1's rule - *name search is async everywhere* - lands in inherited synchronous code.

**Files:** Modify `src/Findra/Card/CardWindow.cs`

- [ ] **Step 1: Own a client**

The window holds a `NameClient`, connected lazily with `NameClient.ConnectAsync(TimeSpan.FromSeconds(5), ct)` and disposed with the window. A failure to connect is **not** an exception the user sees: the card shows "the name helper is not running" in its index line and stays usable, because Plan 1 made that failure a normal state with a clear message.

- [ ] **Step 2: Make `RunSearchAsync` real**

Call `SearchAsync(raw, SearchCardLayout.MaxRows * 8, ct)`. A `null` return means the reply was stale - **drop it silently and paint nothing**; that is the generation counter doing its job, and treating it as an error would defeat it.

Map `NameRow` to `SearchResult`: `Kind` from `FileKinds.Classify(name, isDirectory)` using the row's attributes, `Name`, `Path`, `Score`, and `Why` from the match offset. Keep the mapping in one place.

- [ ] **Step 3: Debounce and cancel**

Typing must cancel the previous search: hold a `CancellationTokenSource` per keystroke, cancel it on the next. The existing debounce timer stays.

**Do not cancel a token mid-frame-write.** Plan 1 fixed a defect where a cancellation landing between a frame's header and its payload desynchronised the pipe permanently; `Frame.WriteAsync` now writes one buffer, but the safest shape is still to let an in-flight request finish and discard its answer by generation rather than to cancel aggressively. Say in your report which you chose and why.

- [ ] **Step 4: The index line**

Replace the old service's status string with `StatusAsync` - name count and the answering pid. Refresh it on open, not per keystroke.

- [ ] **Step 5: Verify against a real helper**

**This needs an elevated terminal and cannot be done by an unelevated agent.** If you are not elevated, say so and stop at the build; the controller will run it. If you are elevated:

```
findra --names        (elevated terminal)
findra                (normal terminal, then press the hotkey or click the capsule)
```

Type `sunset`. Results must appear from the real index. Confirm from the log that the helper answered.

- [ ] **Step 6: Commit** - `git commit -m "The card searches the real index over the pipe"`

---

## Task 6: The capsule window

**Files:** Create `src/Findra/App/CapsuleWindow.cs`

A borderless, transparent, always-on-bottom `Window` painting `CapsulePainter` at `CapsuleLayout` size, positioned from `Config.CapsuleX/Y`, draggable, saving its position on drag end. Clicking opens the card over it via the existing `PlaceOver`.

**It must not steal focus** and must not appear in the Alt-Tab list: `ShowInTaskbar = false`, `ShowActivated = false`, and a tool-window extended style. A desktop widget that grabs focus when you click the desktop is worse than no widget.

`Config.ShowCapsule = false` means it is never created - hotkey and tray only.

- [ ] Commit - `git commit -m "The capsule: the resting look on the desktop"`

---

## Task 7: Tray

**Files:** Modify `src/Findra/App/App.axaml.cs`

An Avalonia `TrayIcon` with: **Search** (opens the card), **Show capsule** (a toggle bound to config), **Settings** (disabled until Plan 6 - present so the shape is visible), **Check for updates** (see Task 9), and **Quit**.

The tooltip carries the version. Quitting stops the indexer by construction later; for now it shuts the lifetime down cleanly and flushes the log.

- [ ] Commit - `git commit -m "A tray icon, because a widget you cannot find is a widget you cannot quit"`

---

## Task 8: The global hotkey and its fallback chain

**Files:** Create `src/Findra/App/Hotkey.cs`, `tests/Findra.Tests/App/HotkeyTests.cs`

**Produces:** `Findra.Hotkey.Parse(string) : (uint Mods, uint Vk)?`, `Hotkey.Describe(uint mods, uint vk) : string`, and `Hotkey.RegisterFirstThatWorks(IReadOnlyList<string> chain, Func<uint, uint, bool> register) : string?`.

`RegisterFirstThatWorks` takes the registrar as a delegate so the chain is testable without a window handle.

- [ ] **Step 1: Write the failing test**

```csharp
using Findra;
using Xunit;

public class HotkeyTests
{
    [Fact]
    public void ParsesTheOrdinaryForms()
    {
        Assert.NotNull(Hotkey.Parse("Alt+Space"));
        Assert.NotNull(Hotkey.Parse("Ctrl+Alt+F"));
        Assert.NotNull(Hotkey.Parse("ctrl+shift+space"));
        Assert.Null(Hotkey.Parse("Banana+Space"));
        Assert.Null(Hotkey.Parse(""));
    }

    [Fact]
    public void DescribeRoundTripsParse()
    {
        var (mods, vk) = Hotkey.Parse("Ctrl+Alt+F")!.Value;
        Assert.Equal("Ctrl+Alt+F", Hotkey.Describe(mods, vk));
    }

    [Fact]
    public void TakesTheFirstCombinationThatRegisters()
    {
        var tried = new List<string>();
        string? landed = Hotkey.RegisterFirstThatWorks(
            ["Alt+Space", "Ctrl+Alt+Space", "Ctrl+Alt+F"],
            (m, v) => { tried.Add(Hotkey.Describe(m, v)); return tried.Count == 2; });

        Assert.Equal("Ctrl+Alt+Space", landed);
        Assert.Equal(2, tried.Count);   // it stopped at the one that worked
    }

    [Fact]
    public void ReturnsNullWhenTheWholeChainIsTaken()
    {
        // Every combination refused is a real outcome on a machine loaded with other tools.
        // The caller must be able to tell the user, so this returns null rather than throwing.
        Assert.Null(Hotkey.RegisterFirstThatWorks(["Alt+Space", "Ctrl+Alt+F"], (_, _) => false));
    }

    [Fact]
    public void AnUnparseableEntryIsSkippedNotFatal()
    {
        string? landed = Hotkey.RegisterFirstThatWorks(["Banana+Space", "Alt+Space"], (_, _) => true);
        Assert.Equal("Alt+Space", landed);
    }
}
```

- [ ] **Step 2** FAIL - `Hotkey` does not exist.

- [ ] **Step 3: Write it**

`Parse` maps `Ctrl`/`Alt`/`Shift`/`Win` to `MOD_*` and a key name to a virtual-key code. `RegisterFirstThatWorks` walks the chain, skipping unparseable entries, returning the description of the first that registers or `null`.

The real registrar calls `RegisterHotKey` against the capsule window's handle and listens for `WM_HOTKEY`.

**The default chain is `Alt+Space`, `Ctrl+Alt+Space`, `Ctrl+Alt+F`, `Ctrl+Shift+Space`.** `Alt+Space` is the system menu chord in some configurations and will fail on real machines.

**Tell the user which one it landed on**, and if the whole chain fails say so plainly. Never fail silently - a hotkey that does nothing with no explanation is the worst outcome, worse than not having one.

- [ ] **Step 4** PASS, 5 tests.

- [ ] **Step 5: Two dim behaviours**

Opening from the capsule dims the capsule's monitor; opening from the hotkey dims the monitor **under the cursor**. `ShowDim` already takes a screen rectangle, so this is a caller decision, not a rewrite.

- [ ] **Step 6: Commit** - `git commit -m "A global hotkey that says which combination it got"`

---

## Task 9: The update check

Spec §9b. **This is the one thing that leaves the machine**, so it is built to the letter.

**Files:** Create `src/Findra/App/UpdateCheck.cs`, `tests/Findra.Tests/App/UpdateCheckTests.cs`

**Produces:** `Findra.UpdateCheck.Compare(string running, string latest) : int`, `UpdateCheck.IsDue(Config, DateTime utcNow) : bool`, `UpdateCheck.Advice(string installSource, string version) : string`, and `UpdateCheck.CheckAsync(Config, Func<CancellationToken, Task<string?>> fetch, DateTime utcNow, CancellationToken ct) : Task<UpdateResult>`.

`fetch` is a delegate returning the latest tag, so **every test runs without a network**.

- [ ] **Step 1: Write the failing test**

```csharp
using Findra;
using Xunit;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("1.9.0", "1.10.0", -1)]   // the one string ordering gets wrong
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("v1.2.3", "1.2.3", 0)]    // tags carry a leading v
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    public void ComparesNumbersNotStrings(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(UpdateCheck.Compare(a, b)));

    [Fact]
    public void AnUnparseableVersionIsNeverTreatedAsNewer()
    {
        // Telling someone they are current when they are not is worse than saying nothing,
        // so anything we cannot read loses.
        Assert.True(UpdateCheck.Compare("1.0.0", "not-a-version") >= 0);
        Assert.True(UpdateCheck.Compare("not-a-version", "1.0.0") <= 0);
    }

    [Fact]
    public async Task OptingOutMeansNoRequestIsMade()
    {
        bool called = false;
        Config off = Config.Default with { CheckForUpdates = false };

        UpdateResult r = await UpdateCheck.CheckAsync(off,
            _ => { called = true; return Task.FromResult<string?>("9.9.9"); },
            DateTime.UtcNow, default);

        Assert.False(called);              // off means off
        Assert.Equal(UpdateState.Disabled, r.State);
    }

    [Fact]
    public async Task ItAsksAtMostOncePerDay()
    {
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        int calls = 0;
        Config recent = Config.Default with { LastUpdateCheck = now.AddHours(-3) };

        await UpdateCheck.CheckAsync(recent, _ => { calls++; return Task.FromResult<string?>("9.9.9"); }, now, default);
        Assert.Equal(0, calls);

        Config old = Config.Default with { LastUpdateCheck = now.AddHours(-25) };
        await UpdateCheck.CheckAsync(old, _ => { calls++; return Task.FromResult<string?>("9.9.9"); }, now, default);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AFailedRequestIsSilentNotAnError()
    {
        // A broken network is not something the user needs to acknowledge.
        UpdateResult r = await UpdateCheck.CheckAsync(Config.Default with { LastUpdateCheck = default },
            _ => throw new HttpRequestException("no network"), DateTime.UtcNow, default);

        Assert.Equal(UpdateState.Unknown, r.State);
    }

    [Fact]
    public void TheAdviceMatchesHowItWasInstalled()
    {
        Assert.Contains("winget upgrade", UpdateCheck.Advice("winget", "1.2.0"));
        Assert.DoesNotContain("winget upgrade", UpdateCheck.Advice("source", "1.2.0"));
        Assert.Contains("github.com/blakazulu/findra", UpdateCheck.Advice("source", "1.2.0"));
    }
}
```

- [ ] **Step 2** FAIL - `UpdateCheck` does not exist.

- [ ] **Step 3: Write it**

`Compare` parses with `System.Version` after stripping a leading `v`, and returns "not newer" for anything unparseable. `IsDue` is `CheckForUpdates && utcNow - LastUpdateCheck >= 24h`. `CheckAsync` short-circuits when disabled or not due, calls `fetch`, catches **everything**, logs under `startup`, and records the time.

The real fetch is a single anonymous `GET` to `https://api.github.com/repos/blakazulu/findra/releases/latest` with a `User-Agent` of `findra/<version>` and **no other headers, no query parameters, no identifiers**. Read `tag_name`. Prereleases are ignored unless the running build is itself a prerelease.

It runs **after** the UI is up, never on a keystroke, never blocking.

- [ ] **Step 4** PASS, 11 tests.

- [ ] **Step 5: Surface it**

The tray tooltip and the tray's "Check for updates" item show the state. Findra **never downloads or installs anything** - it shows the version and the advice, and that is all.

- [ ] **Step 6: Commit** - `git commit -m "An update check that asks once a day, tells you, and installs nothing"`

---

## Task 10: Close-out

- [ ] **Step 1** `--searchprobe` also reports whether the UI is running and which hotkey it holds.
- [ ] **Step 2** `--searchtest` gains a check that `config.json` round-trips and that the configured palettes resolve.
- [ ] **Step 3** `--searchshot` learns the `capsule` state at the configured palette rather than only `Mond`.
- [ ] **Step 4** Update `CLAUDE.md`'s `--searchshot` state list, which is stale: it names a `panel` state that does not exist and omits `capsule`, `opening` and `openingempty`.
- [ ] **Step 5** Full suite green, zero warnings.
- [ ] **Step 6: Commit**

---

## Done when

- `findra` starts, shows a capsule, and quits from the tray.
- The hotkey opens the card on the monitor under the cursor, and Findra says which combination it registered.
- Typing searches the **real** NTFS index over the pipe; Enter opens the file; a row drags into Explorer.
- Closing the card leaves the capsule; `ShowCapsule = false` leaves only tray and hotkey.
- The update check runs once, silently, and never blocks.
- `dotnet test` green, zero warnings, `grep -ric prism src/` = 0 and a human-read comment pass done.

## What comes next

**Plan 4 - Content**: the FTS5 document store, text extraction, the indexer child process, journal-driven enqueue, `--searchindex` and `--searchbench`.
