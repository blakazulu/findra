# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

**`docs/superpowers/specs/2026-09-01-findra-design.md` is the contract** - read it before writing
anything. This file summarises the parts that are easy to get wrong; it does not replace it.

Findra is a standalone Windows desktop search widget: a capsule on the desktop that unfolds into a
results card, plus a global hotkey. .NET 10, Avalonia, SkiaSharp, SQLite.

All six plans have landed (three processes and the name pipe, palettes and card, capsule, tray and
hotkey, FTS5 store and indexer child, model store and per-capability gates, settings and first-run,
`--uninstall`, an Inno Setup installer, four GitHub Actions workflows, winget manifests, README),
plus interface fixes, two adversarial reviews and the mark. What is left is
`docs/end-to-end-checklist.md` (everything needing UAC, a sign-out, a real installer or a public
tag; ordered by discovery, not runnable top to bottom). **`docs/e2e-run-sheet.md` is the same items
in ten phases for one session**, and `build/Check-E2E.ps1` answers every part a script can.

## Commands

```bash
dotnet build -warnaserror -t:Rebuild           # zero warnings; -t:Rebuild, incremental lies clean
dotnet test                                    # TDD applies to new code
dotnet publish -c Release --self-contained     # self-contained is required
pwsh -File build/Publish.ps1 -Rid win-x64      # the publish the installer and CI use
pwsh -File build/Check-Diagnostics.ps1 -Exe publish/win-x64/findra.exe   # every headless mode
pwsh -File build/Check-Release.ps1 -Tag v0.1.0 # may this tag be released, and its notes
pwsh -File build/Check-E2E.ps1 -Exe publish/win-x64/findra.exe   # scriptable run-sheet checks;
                                               # read-only, never uninstalls/kills/deletes; "not
                                               # yet" is a third outcome, not a failure
pwsh -File build/Make-Shots.ps1 -Exe publish/win-x64/findra.exe   # redraw README shots into
                                               # docs/shots, copy the site's; list read from README
node build/Make-Icon.mjs                       # regenerate the mark (ico, SVGs, wizard image,
                                               # favicon.svg/.ico, apple-touch-icon, share images,
                                               # share/card.txt, Store tiles and listing icon).
                                               # By hand only
pwsh -File build/Make-Msix.ps1 -Rid win-x64    # Store MSIX for one RID (then -Rid win-arm64, then
                                               # -Bundle); refuses the identity placeholders
node build/Make-Pages.mjs                      # regenerate every written page, copy sources beside
                                               # them, stamp the version, write the sitemap. By
                                               # hand; CI re-runs it and fails on any diff
node build/Ping-IndexNow.mjs [--send]          # tell Bing/Yandex the site changed; nothing sent
                                               # without --send. site.yml after releases, else by
                                               # hand AFTER the deploy is live, never every push
node tests/edge/markdown.test.mjs              # the site's Accept negotiation table; CI runs node
                                               # for this and Make-Pages only
```

Six diagnostic modes are non-negotiable, built from day one; they verify the app without a screen,
and `--searchshot` is how UI is iterated headlessly:

```bash
findra.exe --searchprobe [query]      # end to end: which process answered, the generation
                                      # counter, what the content indexer is doing
findra.exe --searchmodels             # models present, loading, agreeing; provider per runtime
findra.exe --searchindex [file|folder|q:query|why:path]...   # indexed/queued; paths queue and
                                      # drain, q: queries, why:<path> explains ONE file, read-only
findra.exe --searchshot out.png <state> [palette]   # thirty states, listed below
findra.exe --searchtest               # engine self-check
findra.exe --searchbench [out.md] [corpus]   # measured numbers, pasteable Markdown; `corpus` is
                                      # how many files it generates
findra.exe --version                  # print the version and log location, then exit
```

The `--searchshot` states are `SearchShot.States`, and that list is the only definition of them.
Twelve draw the card, ten the settings window and eight the first-run screen:

```
capsule  empty  indexing  contentmode  contentwaiting  typing  results  noresults  many  adv
opening  openingempty
settings  settingsopening  settingssearches  settingscontent  settingsaddons  settingsremove  settingsabout
settingsuptodate  settingsupdate  settingsasking
firstrun  firstruninstalled  firstrunspeech  firstrundownloading
firstrunfinished  firstrunready  firstrunnames  firstrunworking
```

- `contentwaiting` is the Content pill NOT offering (reading on, nothing read yet), hovered on
  purpose: suppressing the hover fill is half of what makes a dead control read dead.
- Once answered, the first-run screen is the **welcome page** (`src/Findra/First/Welcome.cs`):
  where Findra lives (capsule, the hotkey that actually LANDED via `FirstRunWindow.NoteHotkey`, the
  tray), what happens now (bars, status, reading), About with two links, and "Open settings" (the
  shell opens Settings after the page closes, since the page is the only door in while up).
  `firstrunfinished` (took reading: last question, Later / Start reading) and `firstrunready`
  (plain, Done) are its two finished shapes; `firstrunnames` is "Just names" with no bars, no
  shortcut registered and update checks off. `firstrunspeech` shows the transcription-limit row that
  appears only when Speech is ticked. Each exists so both halves of a painter get reviewed.
- **Where a painter branches on data a state supplies, some state has to supply it**, or the branch
  ships unlooked at. The card's stage: `results` and `opening` carry a picture, `many` keeps the
  no-picture tile. `--searchshot` must learn every new palette and surface as it is written.

Two modes are settings, and **survive the first-run screen and settings window rather than being
replaced by them** (they drive the capability path in CI, headless, and in bug reports):

```bash
findra.exe --models                   # what is installed, and what each capability would add
findra.exe --models install <preset|cap[,cap]>   # justnames | recommended | everything, or
                                      # photos, meaning, speech, hebrew
findra.exe --models remove <cap> [--forget] [--dry-run]   # one add-on, the Settings rules
                                      # (Speech takes Hebrew; Meaning under Speech turns off)
findra.exe --content [on|off]         # is Findra reading inside files at all
findra.exe --content limit <length>   # off | 5 | 30 | 2 hours | no limit | any number of minutes
```

Four modes are neither; the first two are not run by hand:

```bash
findra.exe --names                    # the elevated name-index helper, started by the task
findra.exe --index <parentPid>        # the content indexer, started by the UI
findra.exe --uninstall [--purge] [--dry-run]   # stop everything, remove the scheduled task,
                                      # autostart entry and program files; --purge also deletes
                                      # models, index, settings; --dry-run prints the plan only
findra.exe --stop                     # stop the interface, the indexer and the name helper
```

- Any other `--` argument exits 1 and prints the list; never fall through to the greeting.
- The `--searchbench` fragment opens at heading level two, sections at three, so it pastes under the
  README's `#` unedited. It refuses a throughput rate for a run under a second.

## The console

**`OutputType` is `WinExe`, not `Exe`** - `Exe` drags a console window behind every terminal-less
launch (installer, Start menu, Explorer, autostart, the logon task).
`ProjectFileTests.TheApplicationIsAWindowsSubsystemBinaryAndNotAConsoleOne` asserts it; the absence
of the window is checklist step 50. `src/Findra/Core/ParentConsole.cs` buys stdout back:

- **`AttachConsole(ATTACH_PARENT_PROCESS)`, never `AllocConsole`** (allocating makes the window).
- **Standard handles are read BEFORE attaching and restored if already set**, or redirected output
  goes to the window (`build/Check-Diagnostics.ps1` pipes every mode). Only an unset handle is
  pointed at `CONOUT$`, with an auto-flushing UTF-8 writer.
- **`ParentConsole.Borrow()` runs before `UseUtf8OnTheConsole()`**: `Console.OutputEncoding` needs a
  joined console, or Hebrew and the middle dot print as replacement characters.
- **`--names`, `--index` and the interface do not attach** (headless children log to file; the
  shell does not wait for the interface, so its output would land in whatever is typed next). Every
  other `--` mode attaches, including a mistyped one.

The shell does not wait for `findra.exe`, so the prompt returns before the text - cosmetic.
`dotnet run --project src/Findra -- --version` waits.

## Starting up

**When the first screen is needed, it owns the display until answered.** `Window.Show()` does not
block, so nothing (hotkey, capsule, tray) may be built behind it. `src/Findra/Startup/StartupOrder.cs`
is the seam: the stages are a LIST, testable without a window. `Shell.Run` switches on `StartupStep`
and its default arm throws.

- **Nothing reads inside files until the LAST question is answered**, or indexing fights the 2.9 GB
  download for the disk. `App._holdReading` holds it, `FirstRun.Asks` decides whether the last act
  asks, "Start reading" clears it. "Later" keeps it for the session and changes no setting (so the
  next launch reads without asking - hence **Later**, not "Not now"). Anything that explicitly starts
  reading (settings button, Content pill) clears it too.
- **The names helper is the one exception**, not in `AfterTheScreenIsAnswered()`: the answer
  registers the task and starts the helper immediately; names need no models.
- **`Topmost` is set at construction and released in `Opened`** - pinned only long enough to arrive.
  **The gate in `App.TheWelcomeScreenIsInTheWay`** is what makes it the only door in.
- **`_firstRunIsUp` AND `_firstRun` are set AFTER `Show()`.** A screen that throws must leave a
  launch that carries on (`WhenTheScreenCouldNotBeShown()`); `Closed` never fires on a window that
  never opened, so an early gate would call `Activate` on a dead window for ever.
- **A screen CLOSED unanswered hands the launch on too** (X, Alt+F4, taskbar never raise
  `Answered`): `StartupOrder.WhenTheScreenWasClosedUnanswered()`. Nothing writes `FirstRunDone`, so
  it is asked again.
- **The reading hold has THREE endings, two of them events.** A screen that never ASKED has nobody
  to clear it; `WhenTheWelcomeScreenIsGone` reads `FirstRunWindow.AskedAboutReading` to tell that
  from "Later".
- **A button's work is shown, never hidden behind a frozen window.** `FirstRunState.Work` (a
  `FirstRunWork`) is a step card over the page; the hit test refuses everything while it is up.
  "Get these" shows it and POSTS `Answered`, so the card is drawn first; the shell ticks each step
  (`StepDone`), awaits `FirstRunWindow.Frame()` after each tick, and calls `EndWork()` in a
  finally - which is when the next page (and the resize) happens. Steps may finish out of order
  (name search waits for the UAC answer); the spinner stays on the first unfinished one.
- **The hit test takes the STATE, never the five bounds.** `FirstRunLayout.HitTest(x, y, state)`
  derives everything from the state the painter reads. `FirstRun.LimitRow` is not
  `FirstRunLayout.BandRow` (which `SurfaceHeight` and the painter read); mixing them made the last
  question unclickable wherever a Hebrew row sits below Speech.

## Fitting the screen

**Every window fits the screen it opens on, at every scaling.** Layouts are fixed units that Windows
multiplies by the scaling: the first-run page (928) is 1160 physical px at 125% on 1080p, and with no
title bar its buttons were unreachable (Store rejection, policy 10.1.2.10). `ScreenFit.Factor` shrinks
a surface that does not fit (never grows one, floor 0.5); the window scales the Skia canvas by it and
divides every pointer position by it, so layouts, hit tests and `--searchshot` stay in layout units.
First-run and Settings resize through their own fit method (never set `Width`/`Height` directly), refit
in `Opened`, and `KeepInside` the working area; the card folds it into its zoom
(`CardOverPlacement.FittedZoom`). `ScreenFitTests` holds every surface to 1080p at 100-175%.

## The update panel

**Check now** in Settings raises a panel over the pane.

- **Only the button raises it.** `RunUpdateCheck(force)` passes `force` to `NoteUpdate(..., raise:)`;
  the daily background check passes false (spec §3: no loud idle widget).
- **Findra installs nothing itself.** "Update now" runs `winget upgrade blakazulu.Findra` in a
  **visible** console for winget copies, else opens the releases page. `UpdatePrompt.GoLabel` and
  `Body` switch on the install source. Spec §9b: winget or the installer replaces the binary.
- **`Disabled` has its own arm.** `UpdateCheck.CheckAsync` makes no request when updates are off,
  *even when forced* - and the panel still answers.
- **While up, it is the only thing on the surface**: the hit test refuses scrim, panel body, the
  pane behind and the window's close cross.
- **`SettingsState.Prompt` is a field, not derived from `Update`** (what the last check found vs.
  whether somebody is facing a question they asked).

## The card and the pointer

Three pills stack beside the field: Content, Advanced and **Settings**.

- **`SearchCardLayout.HeaderRight` is the field's right edge, not the card's**, or a timing is drawn
  across the Settings pill.
- **The empty card's height is the hint's OR the column's, whichever is larger.**
- **The card's pills do not ellipsise.** `SearchCardPainter`'s three labels and two empty hints are
  named constants so `CardPillTests` measures what is drawn.
- **`ContentPill.Decide` owns what pressing Content means**, not `CardWindow`. Release: search
  again. Files read and reading off: turn reading back on in place. Nothing read: open Settings at
  Content. A count nothing has read yet: search.

**`src/Findra/Look/Pointers.cs` is the only place a cursor shape is decided**, mapped from each
surface's own hit-test answer.

- **The capsule body is the only Move cursor**; nothing that answers a click may offer to move.
- **The field and the advanced form's fields take the I-beam; everything else clickable the hand.**
- **Every arm is written out and the default throws** (a target added to `SearchTarget`,
  `PanelTarget` or `FirstRunTarget` and forgotten shows the plain arrow).
- `PointerCursor.Of` builds cursors ON DEMAND and keeps them, never in a static initialiser (same
  reasoning as `Parts.Face`).

## Models toggle in Settings

Settings has six sections; **Models toggle** (`Section.AddOns`, "add-on" in code) holds the four capability rows (Content keeps the switch,
"Reading now", power and the transcription limit). Not installed: "Add · <marginal size>".
Installed: a Choice of "Turn off"/"Turn on" and "Remove". `SettingsModel.AddOnRows`.

- **Off is `Config.AddOnsOff`**, written to `AddOns.OffKey` by the pump and read by the child per
  file (`Decoders.ForThisMachine(off:)` -> `CapabilitySet.Without`). Off stops NEW reading only;
  search keeps working (the query side reads what is on disk). Speech off takes Hebrew; Meaning
  off does not take Speech.
- **Every re-queue plans against what READS** (`installed.Without(off)`), or an off add-on's
  NoModel skips are re-queued on every launch for a child that skips them again.
- **Coming back catches up exactly**: `AddOns.NoteAway` records when it stopped (the earlier of two
  wins), `AddOns.CatchUp` re-queues its kinds read since then (`RequeueKinds(readSince:)`, on
  `indexed_at`). Called at startup, after an install, and on Turn on.
- **Remove asks first** (`RemovePrompt`, over the pane like the update panel; Escape cancels).
  `AddOns.FilesToDelete` keeps any file an add-on that stays still needs; Meaning under Speech
  deletes nothing, so its button says "Turn off" and that is what happens. Speech takes Hebrew.
  The reader is told first (off row), then files are deleted with retries; a file still held goes
  on `AddOns.LeftoverKey`, swept at the next start before any model opens.
- **"Keep what was found" is ticked by default.** Unticked, `AddOns.Forget` re-queues the kinds
  (videos with `Reframe`, so the transcript stays); the child drops findings it can no longer
  read. For Photos and Meaning, kept findings are NOT searchable without the model - the words
  say "so adding it back later is quick".

## Plain words

Status text is for somebody who has never heard of an index, a GPU or a codec, and says whether
they need to do anything. `IndexStatus` composes all of it from `IndexExtra` (the kind waiting for
the card `indexer:around`, processor reason `indexer:processor`, `IndexStatus.RestartInKey`,
files left out and failed, the first-run hold); `IndexStatus.Sentence` is the Settings line under
"Reading now". **Counts are files DONE** (read + left out + failed), so a pass through skipped
files moves. `default(IndexExtra)` carries null strings - read them with `IsNullOrEmpty`.
`IndexerHost.RestartIn` replaces "Findra is closed" during a crash backoff; a child that ran
`HealthyMinutes` resets the backoff.

## The progress pill

Under the card and under the capsule's bar: dial, what is being read, count, percentage. **The pill
IS the bar** - the fill runs underneath the words.

- **`ProgressPill` paints it for both surfaces**; each supplies only its rectangle.
- **`IndexStatus` owns both shapes**: `Line` (card footer, tray tooltip) and `Pill` (label / track /
  count). One composer.
- **Shown only while there is work in hand; `IndexStatus.Pill` takes no argument that could say
  otherwise.** Reading off, no live indexer or an empty queue draws nothing (`Show` false) - not a
  bar at zero or 100% (spec §3). No `evenWhenSettled`: the **Content pill in the card header**
  answers whether Findra is reading. `--searchprobe` names the settled state in `Pill`'s order.
- **It hangs BELOW the card.** `SearchCardLayout.Height` is the card; `WindowHeight` adds the pill's
  band. Size windows and bitmaps with the second, draw and hit-test the card with the first.
  `CardProgressTests` reads a pixel column: card, nothing, pill.
- **The label is a word, never the `ResultKind`.** `IndexStatus.Doing` switches on the ENUM, never
  strings; an unrecognised row falls back to the bare verb.
- **The painter measures both ends and lays the track in what is left.**
- **`--searchprobe` prints what the pill would draw.**
- **`CapsulePainter.Placeholder` is one constant**, so `--searchshot capsule` draws the real string.

## Explaining one file

**`--searchindex why:<path>` answers "I can see this file and searching does not find it".**

- **It reads and changes nothing** (a bare path QUEUES).
- **With `q:`, it scores that file's own vectors** against the floor for its kind. `VectorStore.Search`
  only reports winners, so `ScoreOf` reads one known row. Tombstoned rows are reported as discarded.
- **`ExplainFile.Verdict` names THE reason, in decision order**: not on the disk, not a content kind,
  excluded, queued, never offered, passed over, failed, read-but-empty, read-but-edited-since, read.
- **The floors it prints are `ContentBranch`'s own constants.**

## Staying out of the way

The power setting shapes how hard Findra works; **`IndexGate` decides whether others are working.**

- **Asked before every file, in `Indexer.Loop`**, after the pause switch and before the attempt is
  counted. Fullscreen first (`SHQueryUserNotificationState`; `NotPresent`, a locked screen, does not
  count), then the card. `IndexGate.Holds` decides which states hold: fullscreen app, game,
  presentation. **Quiet time (6) is NOT Focus Assist** - it is the first hour after a first
  sign-in on a clean install or upgrade, when a new machine's owner installs Findra; never hold it.
- **Two kinds of busy card (`GpuPressure`), engines checked first.** `Engine`: another process
  works one of its engines over 30% - everything that needs the card WAITS. `Memory`: other
  programs leave less than the installed models plus a margin (only once they exceed an ordinary
  desktop) - pictures and meaning run ON THE PROCESSOR instead (`IndexGate.OnProcessor`, a verdict
  that runs; `IDecoders.OnProcessor` drops loaded models on a change; `indexer:processor` carries
  the reason). Speech still waits: its runtime is chosen once per process (`IndexGate.UsesSpeech`).
  Found on a 3 GB GTX 1060 whose desktop alone held 2 GB: photos waited all afternoon. Busy at
  once, free after a minute of free readings, and a step DOWN from engine to memory takes the same
  minute (`GpuHold.Pressure`). PDH English counter paths, keyed by the adapter LUID from
  `GpuAdapter`.
- **Only a file that would load a model waits for the card**; fullscreen holds everything but deletes.
  **The queue does not stop behind a held file**: `Indexer.NotHeldBack` takes the oldest row of a
  kind the gate lets through (`ContentDb.TakeNextOf`, on `pending_kind`), so documents keep going
  while photos wait. A held file that is gone or unchanged is settled without waiting.
- **Anything that cannot be measured is not busy.**
- **Waiting means holding no models**: `IDecoders.Unload` on every wait and a minute after the queue
  empties.
- **...and no video memory, which Unload alone does not give.** The speech runtime (Vulkan) keeps a
  ~1.5 GB pool for the life of the process once it has transcribed real audio; DirectML gives every
  byte back. So after a release that let something go, `MachineGate.LeftOnCard` reads this
  process's own dedicated memory on the card, and above `Indexer.RecycleAboveBytes` (256 MB) the
  loop ends and the child exits with `IndexerHost.RecycleExitCode` (3). The host restarts that on
  the next turn with no backoff and resets the crash count. Measured, never "restart after speech":
  a child that read only pictures and documents never recycles.
- **The wait is said**: `indexer:state`, "waiting for the GPU" via `IndexStatus`, and `--searchprobe`.
- **No "wait until idle" rule** - the gate yields to other WORK, not to a person being present.
- Measured on one machine only; never on an AMD or Intel card.

## Video frames

`VideoFrames` is the one place a video is opened for pictures (indexer and preview), through the
source reader - **never `Windows.Media.Editing`** (~33 s a frame and black frames reported as
success; the source reader takes 17-24 ms).

- **Four corrections**: `VideoGeometry` crops to the container's visible area (H.264 padding is a
  green strip); frames are rotated by the file's rotation flag; `VideoFrames.Frame` reads past an
  all-zero sample (MPEG-1 after a seek); non-square pixels are squared from the pixel-aspect-ratio
  field (reasoned, not measured; tested on a 720x576 4:3 frame).
- **Empty means every pixel exactly zero** (`IsEmpty`); a dark frame is real.
- **Limits**: `VideoRead.GiveUpAfter` (three empty frames in a row, nothing decoded) and
  `VideoRead.Budget` (five minutes a video). Nothing decoded: `VideoRead.Take` records
  `Decoders.NoFrames`, a skip.
- **Attempts catch crashes; the watchdog catches hangs.** `IndexerWatch.ShouldRestart` kills a live,
  reading child with work that has gone `IndexerWatch.StallSeconds` (three minutes) without a beat;
  it is restarted **immediately** and the crash backoff resets. **A child younger than that window
  is never judged** (`indexer:beat` outlives its writer; judging by it kill-loops every 400 ms).
  `IndexerHost.SinceStart` is the age; `IndexerHostTests` drives the whole sequence.
- **Skip reasons**: `Decoders.NoVideoCodec` (names the codec in brackets), `Decoders.NoVideoStream`
  (audio-only, still transcribed), `Decoders.NoContainerReader` (container unrecognised; deliberately
  not a codec reason). `NoVideoCodec` and `NoContainerReader` rows re-queue when
  `VideoDecoders.Fingerprint` changes. **The Store listing is paid; nothing may call it free** -
  `VideoCodecStore` sends people to buy the HEVC extension.
- **A `reframe` writes frames via `ReplaceSegments`** (transcript kept), and outcomes go through
  `RecordFrameOutcome` using `Decoders.Video`'s rule: transcript left, `StateIndexed` with a note;
  nothing left, `StateSkipped`. **A failed re-read keeps the transcript** - never the ordinary
  failure path (empty segments, `StateFailed`, never re-queued).
- **`ContentDb.BlockedVideoCodecs` counts `StateIndexed` and `StateSkipped` together**, the same set
  `RequeueKinds` re-queues.
- **Extraction beats as it reads**: `DocText.Extract` calls `beat` per PDF page, per docx/pptx
  paragraph, per xlsx row and shared string, so the watchdog spares slow healthy files.
- **One machine only** (x64, AMD CPU, NVIDIA, software decoding). `IMFSourceReader::ReadSample` has
  an unfixed random hang on arm64; the watchdog bounds it at three minutes per file.

## Architecture

**Three processes**, from the first commit:

- `findra.exe --names` - elevated, headless, started by a `HighestAvailable` logon scheduled task.
  Owns the NTFS volume handle and the in-RAM name index, and nothing else.
- `findra.exe` - the UI, normal integrity: grammar, ranking, content search, settings, card, tray,
  hotkey.
- `findra.exe --index` - the content indexer, a child of the UI.

Exactly one call needs admin: `CreateFile(\\.\C:)`, for `FSCTL_ENUM_USN_DATA` and
`FSCTL_READ_USN_JOURNAL`. **The elevated helper must never parse untrusted file content** - decoders
(PDF, ONNX, Whisper, image codecs) run in the indexer. The whole app is not elevated because UIPI
would block dragging a result into Explorer.

**The pipe** (local named pipe, helper to UI; the helper also streams USN events and the UI decides
what to enqueue):

- **Name search is async everywhere** - a round trip, not an in-RAM `IndexOf`.
- **Every query carries a generation counter, stamped on the reply, checked by the UI**, so a late
  answer cannot overwrite a newer result. Needs an explicit adversarial test.

**The helper's memory is `NameIndex.ResidentBytes`, never `BufferBytes`** (the names alone, about a
third). Building doubles every array and `Trim` copies each once more, so `RunAsync` runs one
compacting collection after enumeration: 1.78M names, 502 MB working set down to 253 MB.

**One interface per index.** `src/Findra/Startup/OnlyOne.cs` holds an exclusive handle on `.running`
in the index folder, taken in `RunUi` before Avalonia starts (two interfaces mean two `--index`
children fighting over the vector store). **A file handle, not a named mutex** (mutexes are
thread-owned and untestable; a hard kill frees a handle; the index directory is the key). A second
launch exits 0 naming the owner. Failing for any reason but contention lets the launch through
with a log line.

**A file that keeps ending the indexer is written off after `ContentDb.MaxAttempts` tries.**
`pending.attempts` is incremented AND COMMITTED before the file is opened, because a crashing
decoder never reaches the handler and `TakeNext` would return the same row. Added by
`AddColumnIfMissing`, deliberately NOT a numbered migration (those decide staleness).

**Indexing stops when the app quits** (the indexer is its child); the UI must say so.

## Capabilities and models

Independently installable and **not peers**:

```
words in documents  ─  free, opt-in (FTS5, no model)
photos & video      ─  siglip2 vision + text + spm            629 MB
meaning in docs     ─  e5-base + e5-spm                      1.04 GB
speech              ─  whisper-turbo + [e5 pair]              550 MB (+1.04 GB if e5 not taken)
  └ hebrew          ─  whisper-ivrit, requires speech         1.5 GB
```

- Speech needs e5 (a transcript is embedded like a document). Hebrew needs turbo: turbo detects
  language and only Hebrew files re-run through the fine-tune - a second pass, never an alternative.
- **Everything is 3.7 GB** - measured sizes, not floors.
- **A missing model is a normal state**: the kind is skipped, search contributes nothing, the card
  offers the download. Enabling a capability re-queues **only the files it covers**.
- **The meaning model is FULL PRECISION.** Quantised e5 (`model_quantized.onnx`) gives 0.970 cosine
  between DirectML and CPU, even at `ORT_DISABLE_ALL`; fp32 agrees to 1.000000 and is faster than
  fp16 on CPU. `ProviderAgreementTests` holds the floor; `--searchmodels` prints the cosine.
- **Documents are embedded on the ACCELERATOR, queries on the PROCESSOR** (`Decoders.E5()` asks for
  the accelerator, `ContentBranch` does not; ~3x faster first pass). Safe only because the providers
  agree.
- **Shapes are rounded, because DirectML compiles per SHAPE.** `E5Encoder.Bucket` rounds length to a
  multiple of 32, `BatchBucket` batch to a power of two (80 shapes). Padding is masked, so vectors
  are unchanged; a padding ROW repeats the first row, never empty.

**Pricing**

- **`Capabilities.MarginalBytes`** is what Settings and `--models` quote (what one more adds).
- **First-run rows are priced at their own files via `Capabilities.OwnModels`** - a number that never
  moves when a row is ticked, and the only pricing that adds up to 3.7 GB. The summary is
  `TotalBytes(Close(chosen))` (Speech alone: 547 MB row, 1.57 GB bottom line).
- **The first-run screen prices what is STILL TO FETCH**: `FirstRunState.OnDisk` (by file), read via
  `FirstRun.NotHereYet` by rows, tiles, summary and button. Fully present reads `installed`;
  half-present is priced at the missing half.
- **`FirstRun.PresetChoice` drops Hebrew where its row is not drawn**, and the tiles use it. Never
  select a capability with no row.
- **`FirstRun.AlreadyChosen` ticks what is present, closed over the dependency graph**, Hebrew
  dropped where not offered. The selection decides only what is FETCHED.
- **`CapabilitySet` carries the FILES it was built from; every price reads those** (a partial folder,
  e.g. turbo without e5, is ordinary). A set built by hand derives files from its capabilities.

**Downloads and pick-up**

- **`ModelDownloader` refuses a short download on a floor**, not only the response length: bytes vs
  `Model.MinBytes`, and with no `Content-Length`, `Model.Bytes` less `ModelStore.SizeSlack`.
- **Disk size never equals the declared size.** `ModelStore.SizeSlack` is the only place that width is
  decided; never compare a length to `Model.Bytes` for equality.
- **The indexer asks what is installed before every file**: `Decoders.CanRead` calls `Refresh()`
  through the child's `Func<CapabilitySet>`. The transcription limit is likewise live:
  `CapabilityGate.ApplyLimit` writes `index:transcribeminutes` before its re-queue and the child reads
  it per recording (`--content limit` writes a file a running interface will not re-read).
- **No stamp while its backlog remains.** `CapabilityGate.StampsIn` withholds a stamp while its kinds
  are skipped for `Decoders.NoModel` and unqueued; `Apply` re-queues exactly those
  (`onlyBecause: [NoModel]`).
- **Query encoders open when the index first holds something they can match, never before**
  (e5 at full precision is ~1 GB on the processor; a reinstall keeps models, so "Just names" used to
  pay 1.7 GB for nothing). `Semantic.Wanted` decides (installed AND vectors of its kind in
  `vectors.bin.kinds`); startup opens what is wanted, and the pump
  (`OpenTheQueryEncodersTheIndexNowNeeds`) opens the rest off the loop when the kinds file grows,
  reading what is installed from disk then (`--models install` is another process). **A slot is
  filled, never replaced** (`Semantic.Supply`): a card reads it at query time, so an open card gains
  it, and nothing is disposed under a query. Each encoder is tried once a session. So both indexing
  and searching pick a capability up without a restart; replacing a live model file still needs
  one.
- **The Hebrew fine-tune opens in a try of ITS OWN**, and `Semantic.Open` uses one try per encoder,
  so one corrupt file cannot take the others down.

**Reading inside files**

- **Off until somebody asks**, models or not; names are searchable at once. `--content on` starts it,
  `--content off` stops it keeping what was read; it survives restarts. Surfaces say "not asked" vs
  "finished", never just "up to date".
- **What Findra skips is what somebody asked it to skip, and nothing else.** `QueueFeeder.Eligible` is
  a content kind and the exclusion list; no hidden second rule, and **no "always read" list**. Noise
  folders live in `FileKinds.DefaultExclusions` (`node_modules`, `.git`, `bin`, `obj`, `packages`,
  `site-packages`) where the user can see and delete them.
- **`DocText.WorthEmbedding` withholds the vector from a chunk under 400 characters, 60 words or 25
  distinct words** (short passages match every query). **Nothing is skipped**: the chunk is stored and
  full-text indexed. `Chunk` keeps anything over forty characters. With a binding DENSITY check (never
  a mere brace), this keeps `.html` safe as a document. **Transcripts are exempt.**
- **A migration that changes WHICH FILES are eligible sets `ReWalk`** (forgets journal positions;
  `JournalTail.ResumeFrom` owes a full pass). `RequeueKinds` only moves existing rows. Opt-in per
  step (spec §2a).
- **`IndexPowerLevels` is the one list of duty-cycle levels**, like `TranscribeLimit.ShortName`: one
  table, every level inside `Config.Load`'s clamp, and the row writes the NUMBER, never the index.
- **"Start now" sits beside the toggle**: it writes the configuration AND asks the shell.
- **One number, in minutes, limits transcription** (sound and video): zero off, negative no limit,
  five by default. Over-limit recordings get their own reason; raising the limit re-queues exactly
  those. **Asked on the first-run screen (under Speech, when ticked) and in Settings.**
  `TranscribeLimit.ShortName` holds the five labels both draw: "Off", "5 min", "30 min", "2 hr", "No
  limit" (`Describe`'s text does not fit the pill).

**Score scales**

- **SigLIP-2's score is `sigmoid(exp(logit_scale) * cos + logit_bias)`**, with learned scalars lost
  when vision and text are split. `ModelStore` records them for the shipped checkpoint (112.90,
  -16.7718). `Siglip2Probability` exists only to reason about thresholds (monotone). **`PhotoFloor`
  is 0.09, span 0.06** (real matches ~0.13, unrelated up to 0.066; measured on icons and screenshots,
  not photographs).
- **`TextFloor` is 0.81 and `TextSpan` 0.06** (unrelated text averages 0.780, best answers
  0.838-0.868). **Each scale must end just past its own best real match** so the two are comparable.
  `ScoreScaleTests` carries the numbers. **The ceilings were deliberately left alone** (0.90 vs 0.92).

## Hardware portability

Findra lands on AMD or Intel CPUs, NVIDIA / AMD / Intel GPUs, integrated or discrete, or none.
**No capability may require a particular vendor**, and nothing may fail because of the silicon.

**Everything has run on exactly one configuration: an x64 AMD CPU with a discrete NVIDIA card, plus
the processor-only path on it.** No AMD or Intel GPU, integrated or discrete, and no arm64. **Say so
wherever a number or hardware claim is written - README, site and here.** Vendor-neutral chains are a
design, not evidence; never report them as the same. The failures guarded against came from an AMD
780M, so the untested hardware is the likeliest to break.

- **A provider that LOADS has not been shown to WORK.** `Media.ProveItTranscribes` runs a second of
  tone through `WhisperFactory.FromPath` before accepting the accelerated rung; `WhatIsWrongWith`
  judges the SHAPE (finite, ordered, in-range timestamps, no control characters), never the words
  (whisper.cpp #2596). False rejection costs speed; false acceptance writes nonsense for ever.
- **Detect at runtime.** ONNX (SigLIP-2, e5) is **DirectML → CPU**; Whisper is **Vulkan → CPU**.
- **No vendor-locked providers** (no CUDA, no ROCm).
- **CPU is supported, not a failure state**; the UI says the first index is slower.
- **No CPU-feature assumptions** - no AVX-512 requirement, no vendor intrinsics.
- **The picture model is checked on the PROCESSOR too.** fp16 SigLIP-2 throws on CPU at
  `ORT_ENABLE_ALL` (`SimplifiedLayerNormFusion`), loads at `ORT_ENABLE_EXTENDED` and below. fp32
  ships and one full-precision file serves both providers; do not swap to fp16. `--searchmodels`
  opens the vision tower on the CPU as well. See
  `docs/superpowers/specs/2026-09-05-e5-full-precision-design.md`.
- **`--searchmodels` reports the chosen provider and every rejected one, with reasons.**
- **`--searchbench` records the accelerator** beside CPU, RAM, disk and Windows build.
- **Never assume x64** - no hardcoded RID, no x64-only intrinsics.

**Which chip does the work.** A device index is not a choice: `AppendExecutionProvider_DML(0)` and
the speech default take whatever is listed first - often the integrated GPU, which reports success
while being slower (for speech, slower than the CPU).

- **`GpuAdapter.Best()` picks by the most dedicated video memory**, and that is the whole rule (no
  vendor or device-name lists; §7). Never a software rasteriser.
- **`VulkanAdapter` hides the other devices via `GGML_VK_VISIBLE_DEVICES` on the indexer child's
  `ProcessStartInfo`**, set in `IndexerHost`. The wrapper's device option is ignored, and
  `Environment.SetEnvironmentVariable` in-process changes nothing native code reads.
- **A diagnostic that opens whisper re-execs itself**: `--searchmodels`, `--searchindex` with paths,
  `--searchtest` and `--searchbench` call `VulkanAdapter.ReExecWithDiscrete` (once, only when there is
  a card). A new mode that opens a speech model must too.
- **Both fail soft**: any enumeration problem returns null and changes nothing.
- **`--searchmodels` names the chips, even with no model on disk.**
- **This machine is not evidence** (card at 0 in both APIs).

## Palettes

A palette is `name`, `accent`, `ink`, `ground`, `light`; everything else is derived. That five-field
object is the entire public contract. Six ship (dark: Mond, Brass, Verdigris; light: Paper,
Blueprint, Porcelain). The user picks one dark, one light, and a mode: Follow Windows / Always dark /
Always light.

The derivation is ground-aware, **paid exactly once**; every palette is then four constants. No
element/page manifest system: users extend `%APPDATA%\Findra\palettes.json`, not layouts.
`Derived.Tile` and `Derived.Chip` are in the legibility check in `--searchtest` and `DerivedTests`.

## The typeface

Quicksand, one weight, embedded from `assets/fonts/Quicksand-Regular.ttf` (SIL OFL 1.1);
`assets/fonts/OFL.txt` reaches the publish folder and installer (OFL condition 2).

- **`Parts.Face` is the only place in `src/Findra` that resolves a typeface**; a missing resource
  falls back to the system default with a log line, never a throwing initialiser.
- Label fit is **measured** by opposing tests. **If a label is ever tight, shorten the label** -
  never widen the tolerance or move the column.
- Bold is `SKFont.Embolden` on the same face; no second file.

## The mark

A lens with the capsule's search field cut out, Mond's accent on Mond's ground, solid rather than
stroked. **`build/Make-Icon.mjs` holds the only copy of the geometry** and emits
`assets/icon/findra.ico`, both SVGs, the wizard image, `website/public/favicon.svg` and the share
images. Never hand-edit an output; `IconTests` holds every copy to the others, including the site
header's data URI and the share card.

- **It needs node; nothing in the build does** (`dotnet build`, `Publish.ps1`, installer and
  workflows only read its output).
- **The binary `.ico` in the tree is a deliberate exception** (a PE resource the linker needs). The
  tray icon is still drawn at runtime.
- **Small sizes are drawn, not shrunk**: `HINTS` thickens the handle and drops the slot at 16 px; 20
  and 40 exist for 125%/150% scaling. `IconTests` asserts the 16 px slot is ABSENT.
- **The share card sets type from `Quicksand-Regular.ttf` itself**; bold by emboldening.
- **Share card 1200x630, square 1080x1080, both opaque.** The square is for posting by hand
  (**Instagram reads no Open Graph**).
- **The tray has no plate and its slot is a real hole**; asserted on three palettes through a
  `Render` seam (a `WindowIcon` needs a running Avalonia).

## The README is a product page

Screenshots and numbers, and **both must be real**.

- **Screenshots come from `--searchshot`**, never mockups or hand edits; the command is printed under
  each image.
- **Every number comes from `--searchbench`**, pasted verbatim and whole, with the machine named (CPU,
  RAM, disk class, accelerator, Windows build). Model sizes from real files, never floors.
- **Never quote a throughput rate from a default-sized `--searchbench` run.** An extraction rate needs
  a corpus of at least 10,000, stated above it.
- **No claim a reader cannot reproduce** with a command from the README.
- **No comparative claims against named competitors.** `tests/Findra.Tests/Build/Repo.cs` holds the
  shared name list for the README and winget listing tests; it cannot hold one tool whose name is an
  ordinary word and a Findra preset - that one is a reading.

## The website

`website/public` is the whole site: static files, no build step, framework or package manifest (a
build step is to be argued for, not introduced). **Nothing here deploys it**: Netlify publishes
`website/public` from `main` as it sits, so a push is a deploy; `netlify.toml` (publish directory
plus headers) is the whole configuration. `ci.yml` only regenerates it to prove nothing changed,
`site.yml` only pings search engines after a release, `release.yml` and `winget.yml` never touch it.

The front page (`index.html`) is hand-written; every other page comes from `build/Make-Pages.mjs`,
run by hand. **CI runs it and `git diff --exit-code` over `website/public`**, sitemap excepted (its
date needs git history a shallow clone lacks).

**The front page is short, on purpose**: the lede, "We fixed Windows Search", the numbers (one
derived figure, a bar per query, three tiles) and the install. Everything else has a page:
`/features/`, `/why/`, `/numbers/`, `/faq/`, `/install/`. Those five are **hand-written HTML bodies
with hand-written Markdown twins** in `website/content/pages/` (the front page's own pattern, with
`home.md`); `Make-Pages.mjs` wraps each body in the shared shell (`html: true` in `PAGES`) and stamps
`data-stamp` elements in it. A new such page is a `PAGES` entry plus `WebsiteTests.Pages`, the 404
list, `llms.txt` and the edge function's routes. The FAQ's structured data lives on `/faq/`.

**The headline figure is derived, never typed**: the slowest median in the README's pasted run,
rounded up to the next whole millisecond ("under 4 ms"), with each bar drawn to that scale.
`TheSiteQuotesTheReadmesOwnBenchmarkNumbers` recomputes it, the bars, the range, the worst sample
and the tiles from the README, so a new run is: paste it, then change every figure the test names.

**Shots and numbers** (`/features/` says "Every picture below is the product"):

- **`docs/shots` and `website/public/shots` are two copies of the same renders**;
  **`build/Make-Shots.ps1` regenerates them**. `SiteShotTests` fails on differing bytes or differing
  printed commands.
- **Shots go through Netlify's Image CDN; PNGs are untouched.** A `<picture>` offers AVIF and WebP at
  two widths via `/.netlify/images` (same-origin), PNG as the `<img>` fallback.
  `EveryShotIsServedThroughTheImageCdnAndNamesItsOwnFile` requires matching file names.
- **Every number is a `--searchbench` measurement** beside its machine.

**Promises**

- **CSP permits this origin only**: `font-src 'self'`, `script-src 'self'`, `connect-src 'none'`,
  `img-src 'self' data:`. No analytics, beacons, third-party script, CDN, **or Google Fonts**.
  Quicksand and JetBrains Mono are upstream variable fonts, unmodified, rewrapped as WOFF2 in
  `website/public/fonts/` with licences beside them, **never subset** (a subset is a modified
  version). `TheSiteFetchesNothingFromAnotherOrigin` holds it. A new origin is a decision for
  somebody, not a detail.
- **IndexNow is not the page reaching out.** `build/Ping-IndexNow.mjs` submits sitemap URLs to Bing
  (Copilot's index) and Yandex. `.github/workflows/site.yml` runs it only after a successful release,
  **waiting until the live page names the new version**; otherwise by hand. **`--send` is required.**
  **The key exists only in `website/public/<key>.txt`**;
  `TheIndexNowKeyFileIsNamedForWhatItContains` asserts it is not in the script.
- **Search Console's verification file lives at the publish root, not in `docs/`**, unmodified;
  `TheSearchConsoleVerificationFileIsWhereGoogleLooksForIt` holds it.
- **The site names Windows Search and the README may not** (`Repo.Competitors` covers the README and
  winget listing). The `h1` is the joke; do not correct it or carry it back.
- **Nothing promises a release that does not exist.** From 0.1.0: Get Findra, winget is the install.
  The catalogue took the manifest on 21 September 2026 (microsoft/winget-pkgs#429668);
  `Repo.WingetIsInTheCatalogue` is true, so the hedge guard fails any surface saying the command does
  not resolve. **The catalogue trails each release by one manual submission**, so surfaces say the
  releases page has each release first. README, `llms.txt` and `website/content/home.md` turn over
  together; `ReadmeTests.TheReadmeDoesNotStillSayFindraIsNotReadyToInstall` is **coupled to a
  numbered section in `CHANGELOG.md`**, never to `packaging/winget/*.yaml`'s placeholder hashes, which
  never change (`winget.yml` substitutes only in what it submits). Couple guards to the fact that
  moves.

**Front page**

- **One `h1`**; the ticket section is an `h2`. No `twitter:title`.
- **`<title>` and `og:title` are "Findra - Fast, private desktop search for Windows"** (the Markdown
  twin's H1 too); the headline stays the joke, with a visually hidden "Findra:" and an `aria-hidden`
  lockup. `TheFrontPageTitleAndHeadingsSayWhatFindraIs` holds all three.
- **Capitals live in `styles.css`** (`text-transform: uppercase`); text is typed in sentence case.
- **The version is stamped, not typed**: `data-stamp` elements and the structured data's
  `softwareVersion` and release-notes link come from `Directory.Build.props` and CHANGELOG. **A
  release commit runs `node build/Make-Pages.mjs`**, or `TheFrontPageNamesTheVersionTheRepositoryBuilds`
  fails.
- **Structured data uses real schema.org properties**: no `privacyPolicy`; the repository is its own
  node with `codeRepository` pointing via `targetProduct`
  (`TheStructuredDataUsesOnlyRealProperties`). Never invent `aggregateRating` or `review`.
- **"Code signing policy" appears twice** (footer, and the install section beside "not signed"), per
  SignPath Foundation's terms; `WebsiteTests` asserts the count.

**Generated pages**

- **`/code-signing/` comes from `docs/code-signing-policy.md`**, held by `PolicyPageTests` (its "Not
  yet in force" note is coupled to the empty signing step). **The SignPath application needs a
  release to exist first.**
- **`PRIVACY.md` is the privacy policy**, emitted to `/privacy/` and copied verbatim to `/privacy.md`;
  `WebsiteTests` compares SHIPPED prose **in both directions**. The page headline is not the
  Markdown's `# Privacy` (the generator drops the H1). The reading column is not in a `.panel`.
- **`/changelog/`'s twin is not verbatim**: `releaseNotes` drops Unreleased, every `- Documentation:`
  bullet and link references into `changelog.md`;
  `TheChangelogPageIsTheChangelogWithoutItsDocumentationEntries` holds it to `CHANGELOG.md`.
- **Guides answer questions people type**: `/windows-search-not-finding-files/`,
  `/search-inside-pdfs/`, `/find-photos-by-description/`, `/search-recordings-by-speech/`, from
  `website/content/guides/`, linked in every footer, the 404 map, `llms.txt`, sitemap and edge
  function. First sentence is the answer; **say only what the code does today** (formats `DocText`
  reads, not `FileKinds`' list: `.doc`, `.xls`, `.ppt`, RTF and OpenDocument are found by name
  only). A new guide is a `PAGES` entry plus a `Guides` entry in
  `WebsiteTests`.
- **The generator's Markdown vocabulary is the union of what its sources use** (unsupported syntax
  renders with its marker, e.g. a literal `>`); `WebsiteTests.Strip` learns each new construction.

**Other files**

- **No contact form, phone number or postal address**, and a test refuses them.
- **Only real edge functions in `netlify/edge-functions/`** (every file there deploys as one; no
  default export fails the build, silently keeping the old site). `WebsiteTests` checks; node tests
  live in `tests/edge/`.
- **`netlify/edge-functions/markdown.ts` does Accept negotiation**: `Vary: Accept` on **both**
  branches, and q values compared, not matched (browsers send `*/*;q=0.8`). Node appears only in
  `ci.yml` and `site.yml`.
- **`llms.txt` says what Findra is NOT**: no API, accounts, server or MCP server.
- **The 404 page is ours and lists every real URL.**
- **Netlify injects a `netlify.new` comment and `hosting-provider` and `netlify-deploy` meta tags into
  every HTML response**, 404 included, and `built_with_badge_enabled` false (set 21 September 2026
  via `netlify api updateSite`; `hud_enabled` already false) does NOT stop it. **The edge function
  takes it back out** (`withoutNetlifyPromotion`, `path: '/*'` less static files, `onError:
  'bypass'`); `tests/edge/markdown.test.mjs` runs it on the markup Netlify actually served. It was
  deleted once by a commit titled as documentation and every page carried the link again the same
  day: never remove it without checking a live page. Check the live page after deploys.

Most of the site is untested beyond `IconTests` (favicon vs `assets/icon/findra.svg`, header data
URI) and `SiteShotTests`; the rest holds because somebody reads it.

## Shipping

- **The version lives in `Directory.Build.props` only** - no `<Version>`, `<AssemblyVersion>`,
  `<FileVersion>` or `<InformationalVersion>` in any `.csproj`.
  `IncludeSourceRevisionInInformationalVersion` is off and `BuildInfo.Normalise` strips from `+`
  (`Version.TryParse` rejects `1.2.0+sha`; `UpdateCheck.Compare` would route it to "up to date").
- **A tag with no matching `CHANGELOG.md` section fails the release; that section is the notes.**
  GitHub's generated notes are never on. `build/Check-Release.ps1` is the gate, one exit code per
  refusal.
- **No pre-release tags** (`1.2.0-rc.1` cannot be ordered; `/releases/latest` excludes them). The
  refusal in `Check-Release.ps1` is the line to delete when semver prereleases are supported.
- **Only a person on the Actions tab publishes to winget.** No `push`, `tag`, `release` or `schedule`
  trigger may reach `winget.yml`.
- **Both architectures from the first release, one manifest**: `win-x64` and `win-arm64` from one
  matrix, two `Installers:` entries under one `PackageVersion`.
- **The signing step is a placeholder, and nothing claims otherwise** - README, installer, release
  body, winget listing, `docs/code-signing-policy.md` (coupled by a test).
- **The installer is a third distribution route and `installer` a fourth install source** beside
  `winget`, `source`, `unknown`. Spec §2 and §9b still say two and three; this is the record.
- **Inno architecture identifiers are irregular**, so `installer/findra.iss` builds them per
  architecture, never by suffixing `{#Arch}`: `x86compatible`, `x86os`, `x64compatible`, `x64os`,
  `arm32compatible`, `arm64`, `win64` (no `arm64compatible`). `InstallerScriptTests` checks every
  `ArchitecturesAllowed` and `ArchitecturesInstallIn64BitMode`, and that the uninstall report variable
  is `AnsiString` (for `LoadStringFromFile`).
- **No version in the install directory**, and `AppId` is a fixed GUID (the task stores an absolute
  path to `findra.exe`).

## The Microsoft Store

**`docs/store.md` is the record**: the decision, registration, submission pages, listing text and
the per-release routine. **Status: submitted once, failed certification on scaling** (10.1.2.10,
fixed in 0.4.1, resubmission pending) - "Findra" is reserved on the existing developer account (the
one that publishes Scalpel PDF), Store ID `9P78Z9KT48PR`.
The Store is a fourth distribution route beside the releases page, the installer and winget, not a
replacement.

- **MSIX, not the EXE installer.** The Store does not re-sign EXE/MSI, which would need an
  Authenticode certificate Findra lacks; MSIX is re-signed by Microsoft after certification. So the
  bundle is **unsigned on purpose** - never add a signing step to it.
- **`packaging/store/Package.appxmanifest`** is a desktop-bridge package (`Windows.FullTrustApplication`)
  with `runFullTrust`, **`allowElevation`** (restricted, human-reviewed: the one UAC prompt that
  registers the names-helper task) and a `windows.startupTask`. `Version` and
  `ProcessorArchitecture` are filled by the script, never committed.
- **The identity is final and permanent**: `Name` `LirazShakaAmir.Findra`, the account's
  `CN=...` `Publisher`, `PublisherDisplayName` "Liraz Shaka Amir", all copied from Partner Center
  (case-sensitive). **Never edit or invent them.** `Make-Msix.ps1` still refuses the old placeholders
  (`__PACKAGE_IDENTITY_NAME__`, `__PACKAGE_PUBLISHER__`, `__PUBLISHER_DISPLAY_NAME__`) should one
  ever return; `StorePackagingTests` validates each field on its own.
- **`build/Make-Msix.ps1`** calls `Publish.ps1` (the publish stays its job), lays out
  `store/layout/<rid>` with the manifest and tiles, packs `store/msix/findra_<ver>_<arch>.msix`, and
  `-Bundle` makes `store/bundle/findra_<ver>.msixbundle` from both. The version is
  `Directory.Build.props` plus a fourth `.0`. `makeappx` is the newest Windows Kit found (a floor,
  not a pin).
- **`.github/workflows/store.yml` is `workflow_dispatch` only**, same rule as winget (`WorkflowTests`
  asserts it): build, test, pack both RIDs, bundle, upload the `store-msixbundle` artefact. No tag
  triggers it; a release does not touch the Store. Workflow files are committed through the web
  editor, because the fine-grained token cannot carry the `workflow` scope.
- **The tiles in `packaging/store/Assets` and `listing/AppTileIcon300.png` come from
  `build/Make-Icon.mjs`** - never hand-edit them; `StorePackagingTests` checks each is at its named
  size and that the script draws them. The four listing screenshots are `docs/shots` renders centred
  on the plate at 1366x768 (the Store's floor), unscaled.
- **Known gaps inside a package, queued as code changes** (none blocks a first submission):
  1. The helper task stores the `WindowsApps\<package>_<version>` path, which moves on every update;
     it needs a startup check that re-registers when the recorded path is not the running exe.
  2. `InstallSource` has no Store value, so the update check would send Store users to GitHub; a
     packaged build should say "updated through the Microsoft Store" and skip the check.
  3. `Autostart`'s HKCU Run write is virtualised in a package; the toggle should drive the
     StartupTask API when packaged.
- **If review refuses `allowElevation`**, the fallback is a Store build with name search disabled
  and content search intact, said in the listing - decided only after an actual refusal.

## The log

**Ask what a line costs per day, not per occurrence** - the journal tail polls about once a second.

- **`JournalDigest` sums rather than throttles** (not `Log.Repeat`).
- **A quiet window says nothing**; tail liveness is `--searchindex`'s job, and a dying tail warns.
- **The first activity on a volume is reported at once.**
- **Settings > About opens the log FOLDER** (the path is otherwise only in `--version`).

## Data locations

Config roams, bulk does not - 2.9 GB of models must never sit in a roaming profile or the publish
folder.

| Path | Holds |
|---|---|
| `%APPDATA%\Findra\config.json`, `palettes.json` | settings |
| `%LOCALAPPDATA%\Findra\models\` | the seven model files |
| `%LOCALAPPDATA%\Findra\index\` | SQLite name, FTS5 and vector stores |
| `%LOCALAPPDATA%\Findra\logs\` | `findra-YYYYMMDD.log` |

## Versions and updates

**Findra never installs anything by itself** - no self-updater, background installer or elevation;
winget does it correctly.

The update check is the **one exception** to "nothing leaves the machine" (spec §9b): an anonymous
HTTPS GET to the GitHub Releases API - for a winget install, to `UpdateCheck.CatalogueUrl`, the
winget catalogue's folder in `microsoft/winget-pkgs` (`winget upgrade` can only install what the
catalogue has, and it trails the releases page by a manual submission) - at most once per 24
hours, on startup, in the background. No
query parameters, machine or install identifier, nothing about files or searches. Never blocks; a
failure is a log line. On by default, disclosed on the first-run screen; off means no request.

It reports the action for how the user installed (`winget upgrade blakazulu.Findra`, or release
notes for a source build). The install source is recorded at first run, not guessed.

**Compare parsed versions, never strings** (`1.10.0` > `1.9.0`). **Both sides are checked for
parsing**: `Compare` answers 0 if EITHER fails, and that must not read as "up to date".

**An uninstall clears `InstallSource` along with `FirstRunDone`.**

## Install, resume and uninstall

**Installing never discards work that is still good.** Correctly sized models are kept, partial
downloads resume, a current-schema index is used and a non-empty queue **resumed**, an older schema is
migrated re-queuing only invalidated files. "Done" is recorded in the index (schema version, USN
position per volume, pending queue). Re-downloading 2.9 GB or re-indexing a finished disk is the worst
thing Findra could do; it gets a test. The name index is exempt (RAM, rebuilt by MFT enumeration at
every logon).

**A guard conditioned on data the other end may choose not to send is not a guard** - pair it with
one on what is on disk (see `ModelDownloader`).

**Uninstall always removes** the app files, **the `HighestAvailable` scheduled task** and any
autostart entry, stopping helper and indexer first. An orphaned elevated task is a defect.

**Uninstall keeps by default** `%LOCALAPPDATA%\Findra\models\`, `index\` and `%APPDATA%\Findra\`
config; deleting is opt-in (checkbox and flag), and the prompt states the **measured** size.

**It always clears `FirstRunDone` in the kept config**: the welcome screen registers the task, and
`HelperTask.EnsureRunning` only *runs* an existing one - skipping the screen on reinstall leaves no
helper, so no name search and no content queue. `Uninstall.Run` takes this as a seam, on every route
including `--quiet` (the installer's).

**The uninstall runs from `CurUninstallStepChanged`, never `[UninstallRun]`**, whose entries and
`Check` parameters are fixed into `unins000.dat` at INSTALL time, so the `Purge` checkbox could never
reach them. **A decision taken during the uninstall cannot be carried by anything the installer wrote
down.**

**`findra.exe --uninstall` is a first-class mode** (`--purge` deletes data), because the
`dotnet publish` route has no installer.

## No lineage

**Findra is a separate project, not a fork or a component.** Namespaces are `Findra`, and log tags,
probe markers, config paths and file names follow.

Nothing - README, UI, commit message, manifest, build script or code comment - may describe Findra as
derived from another project, name one, or describe behaviour that is not Findra's. This is not a
name grep: leaks survive as comments explaining another product's behaviour, constants justified by
another product's history, and another object model's vocabulary. Ask of each comment whether it
reads as written for Findra by somebody who has never seen another codebase.

A shipped comment may not cite a plan, a task number or a review either. Name the thing, not the
document that asked for it.

## The changelog

`CHANGELOG.md` is updated on **every commit**, in the same commit, in its pathspec - never in a sweep
at release time. It is load-bearing: the release uses the tag's section as notes, a missing section
fails the release, and the update check sends source builds there.

Format: [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/). Entries go under
`## [Unreleased]` in Added, Changed, Deprecated, Removed, Fixed or Security, and move to a numbered
section on release. Write for a person reading release notes, not a reviewer reading a diff.

## Testing

TDD for all new code: the pipe protocol and generation counter, palettes and light derivation,
per-capability gating and the dependency graph, the hotkey fallback chain, config load/save and
migration. The ported engine gets characterization tests only where Findra changes its behaviour
(principally per-capability gates).

**An effect that cannot be run in a test gets a seam, not a test that reads its own source.**
`Uninstall.Run` and `HelperTask.Unregister` take their effects as delegates and tests assert the
recorded sequence; `Autostart` takes an `IStore`. Ask what edit keeps the grepped string and breaks
the behaviour (e.g. wrapping task removal in `if (!quiet)`). Text assertions only where there is no
code to run: `installer/findra.iss` and the workflow YAML.

**A subprocess is drained asynchronously and killed on a timeout.** `HelperTask.RunSchtasks` reads
both of `schtasks`'s streams as tasks before waiting; `Register` reads the result of its own timeout.

## Gotchas

- `schtasks` CSV column headings are localized; the XML is not. Parse the XML form.
- Hotkey registration can fail (`Alt+Space` is the system menu chord in some configurations). Walk a
  fallback chain, take the first that registers, and **tell the user which one it landed on**.
- Two open paths, two dim behaviours: from the capsule, dim the capsule's monitor; from the hotkey,
  dim the monitor under the cursor.
- Scheduled-task registration can fail in a way Findra cannot fix; it needs a non-fatal path that
  leaves names working on whatever can be read unelevated.

## Licence

Apache-2.0 with a `NOTICE` file: free use, cloning and modification, with propagating attribution to
blakazulu and https://github.com/blakazulu/findra (NOTICE carries it forward; MIT would not).

**`LICENSE` and `NOTICE` reach installs only because `src/Findra/Findra.csproj` copies them into the
publish folder** (Apache-2.0 section 4(d)); the installer's `[Files]` copies only that folder, and
`LicenseFile=` in `installer/findra.iss` only DISPLAYS the licence. `assets/fonts/OFL.txt` travels the
same way (OFL condition 2); `TypefaceTests` holds all three.
