# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

**`docs/superpowers/specs/2026-09-01-findra-design.md` is the contract** - read it before
writing anything. Everything below is a summary of the parts that are easy to get wrong, not
a replacement for it.

Findra is a standalone Windows desktop search widget: a capsule on the desktop that unfolds
into a results card, plus a global hotkey. .NET 10, Avalonia, SkiaSharp, SQLite.

All six plans have landed. The tree holds the three processes and the name pipe, the palette
layer and the card, the capsule, tray and hotkey, the FTS5 content store and the indexer child,
the model store and the per-capability gates, the settings window and the first-run screen,
`--uninstall`, an Inno Setup installer, three GitHub Actions workflows, the winget manifests and
a README written out of real renders and real measurements. What is left is not a plan but
`docs/end-to-end-checklist.md`: everything that needs a UAC prompt, a sign-out, a real installer
or a public tag. The checklist is the catalogue, ordered by which plan found each item and so not
runnable top to bottom; **`docs/e2e-run-sheet.md` is the same items in ten phases somebody can
work through in one session**, and `build/Check-E2E.ps1` answers every part of it a script can.

A pass of interface fixes has landed on top of that, every one of them from somebody running
the real product: the first screen now owns the display, Settings is on the card, the Content
pill has somewhere to go, the four surfaces set a cursor, and Settings gained the two Content
rows that had no control. What each of those is holding is written down below.

Two adversarial reviews have landed since the last plan. What they found is written down below
rather than left in a commit message: the console, the seams, the capability refresh, the download
floor and the installer's architecture identifiers. Each of those is a rule that looks like a
detail from inside a diff and is a broken machine from outside one.

Findra has a face since then. One mark, generated from a single set of numbers into the icon the
linker compiles into the executable, the installer and its wizard, the tray, and the site's
favicon and header - five homes for one logo, where four of them agreeing is not the same thing
as all five. `## The mark` below is what holds them together. The same pass found the stage's
picture branch had never been rendered by any shot, which is the rule now sitting under the
`--searchshot` commands above.

## Commands

```bash
dotnet build -warnaserror -t:Rebuild           # zero warnings, always - and -t:Rebuild, because
                                               # an incremental build reports a false clean
dotnet test                                    # TDD applies to new code (see below)
dotnet publish -c Release --self-contained     # self-contained is required, not optional
pwsh -File build/Publish.ps1 -Rid win-x64      # the publish the installer and CI both use
pwsh -File build/Check-Diagnostics.ps1 -Exe publish/win-x64/findra.exe   # every headless mode
pwsh -File build/Check-Release.ps1 -Tag v0.1.0 # may this tag be released, and what are its notes
pwsh -File build/Check-E2E.ps1 -Exe publish/win-x64/findra.exe   # every part of the end-to-end
                                               # run sheet a script can answer. Reads only: it
                                               # never uninstalls, purges, registers or
                                               # unregisters a task, kills a process or deletes
                                               # anything. Three outcomes, not two - "not yet"
                                               # is a machine that has not got there yet, and is
                                               # not counted as a failure.
node build/Make-Icon.mjs                       # regenerate the mark - the .ico compiled into the
                                               # exe, both SVGs, the installer's wizard image and
                                               # the site's favicon. By hand, when the mark
                                               # changes; nothing in the build runs it.
```

Six diagnostic modes are non-negotiable and are built from day one. They are how the app is
verified without a screen, and `--searchshot` in particular is how UI gets iterated headlessly:

```bash
findra.exe --searchprobe [query]      # whole path end to end; must report which process
                                      # answered, the current generation counter, and what
                                      # the content indexer is doing
findra.exe --searchmodels             # are models present, do they load, do they agree, and
                                      # which execution provider answered for each runtime
findra.exe --searchindex [file|folder|q:query]...   # what is indexed, what is queued; given
                                      # paths it queues and drains them, given q: it queries
findra.exe --searchshot out.png <state> [palette]   # seventeen states, listed below
findra.exe --searchtest               # engine self-check
findra.exe --searchbench [out.md] [corpus]   # measured numbers, as a pasteable Markdown
                                      # fragment; `corpus` is how many files it generates
findra.exe --version                  # print the version and log location, then exit
```

The `--searchshot` states are `SearchShot.States`, and that list is the only definition of them.
Nine draw the card, five the settings window and three the first-run screen:

```
capsule  empty  typing  results  noresults  many  adv  opening  openingempty
settings  settingsopening  settingssearches  settingscontent  settingsabout
firstrun  firstrunspeech  firstrundownloading
```

`firstrunspeech` is not a variation of `firstrun`: the transcription limit is on that screen
only when Speech is ticked, and the two layouts - with the row and without it - are what a
review has to be able to compare.

Two more modes are settings a person changes rather than diagnostics. **They survive the
first-run screen and the settings window rather than being replaced by them** - they are how the
capability path is exercised on a machine with no screen, in CI, and by anybody reporting a bug:

```bash
findra.exe --models                   # what is installed, and what each capability would add
                                      # given what is already there
findra.exe --models install <preset|cap[,cap]>   # justnames | recommended | everything, or
                                      # photos, meaning, speech, hebrew
findra.exe --content [on|off]         # is Findra reading inside files at all
findra.exe --content limit <length>   # off | 5 | 30 | 2 hours | no limit | any number of minutes
```

Four modes are neither a diagnostic nor a setting, and the first two are not run by hand:

```bash
findra.exe --names                    # the elevated name-index helper, started by the task
findra.exe --index <parentPid>        # the content indexer, started by the UI
findra.exe --uninstall [--purge] [--dry-run]   # stop everything, remove the scheduled task, the
                                      # autostart entry and the program files; --purge also
                                      # deletes the models, the index and the settings;
                                      # --dry-run prints the whole plan and changes nothing
findra.exe --stop                     # stop the interface, the indexer and the name helper
```

Anything beginning with `--` that is not one of these exits 1 and prints the list. A mistyped
mode must never fall through to the greeting, because a script checks the exit code.

The `--searchbench` fragment opens at heading level two and every section below it at level
three, so it pastes under the README's own `#` with nothing to edit. It refuses to print a
throughput rate for a run shorter than a second and says how to get a longer one.

`--searchshot` must learn every new palette and every new surface as it is written - and
**a surface with every one of its states shot can still have a whole branch of its painter
that nothing has ever rendered.** The card's stage has two: a decoded picture centre-cropped
into the well, and the no-picture tile. Every state left the image unset, so for as long as
the card had existed the one surface whose whole job is showing you the file had only ever
been reviewed in its fallback - while nine states, a README image and its alt text all looked
complete. `results` and `opening` now carry a picture and `many` keeps the tile, so both are
on screen somewhere. The rule the next one needs: where a painter branches on data a state
supplies, some state has to supply it, or the branch ships unlooked at.

## The console

**`OutputType` is `WinExe`, not `Exe`, and putting it back drags a black console window behind
every launch.** Windows gives a console-subsystem binary a window every time it starts without a
terminal, and Findra has five such launches: the installer's run step, the Start-menu shortcut, an
Explorer double-click, the autostart entry at every sign-in, and the elevated logon task, which
adds a second one. `ProjectFileTests.TheApplicationIsAWindowsSubsystemBinaryAndNotAConsoleOne`
asserts the `OutputType` itself, which is the only part of this a test can see; the absence of the
window on those five launches is checklist step 50, because no run in this project has ever made
one of them.

A windows-subsystem process has no standard output at all, even when a person typed its name at a
prompt. `src/Findra/Core/ParentConsole.cs` buys it back, and four things about it are load-bearing:

- **`AttachConsole(ATTACH_PARENT_PROCESS)`, never `AllocConsole`.** Attaching joins a console the
  caller already owns and fails harmlessly, changing nothing, when there is none. Allocating would
  conjure the very window `WinExe` exists to prevent, on exactly the launches that have no
  terminal.
- **The standard handles are read BEFORE attaching and put back if they were already set.**
  Attaching resets them to the console's own, so a redirected run would write to the window and
  hand its caller nothing - and `build/Check-Diagnostics.ps1` pipes every mode it runs, so getting
  this wrong fails every headless check at once. A pipe or a file wins; only a handle that was not
  set at all is pointed at `CONOUT$`, and that one is given an auto-flushing UTF-8 writer, because
  the writer .NET would build lazily sits on the handle as it was, which was nothing.
- **`ParentConsole.Borrow()` runs before `UseUtf8OnTheConsole()`.** Setting
  `Console.OutputEncoding` sets the console's output code page, and a process that has not joined
  a console has no code page to set. Reversed, the exception is caught and every diagnostic prints
  the card's middle dot and every Hebrew string as replacement characters.
- **`--names`, `--index` and the interface do not attach.** The first two are headless children
  nobody typed and they report through the log file. The interface is a deliberate refusal, not an
  oversight: a shell does not wait for a windows-subsystem process, so its prompt is already back
  and anything Findra wrote for the next few hours would land in the middle of whatever was typed
  after it. Everything else beginning with `--` attaches, including a mistyped mode, which owes
  the caller the list of real modes and exit 1.

The visible price is that a shell does not wait for `findra.exe`: run a diagnostic directly and
the prompt returns before the text does. That is cosmetic and is the whole cost of the change.
`dotnet run --project src/Findra -- --version` waits, because the shell is waiting for `dotnet`.

## Starting up

**When the first screen is needed, it owns the display until it is answered.** `Window.Show()`
does not block, so `Start()` used to carry straight on and build the hotkey, the capsule and the
tray behind it - the whole product running behind a welcome screen somebody was still reading,
and a download they had just asked for reading as a window in the way of it.

`src/Findra/Startup/StartupOrder.cs` is the seam. Which stages a launch takes and in what order
is a LIST rather than a run of statements, because no window can be built in a test and the
ordering is the part that was wrong. `Shell.Run` switches on `StartupStep` and its default arm
throws: a step added to the enum and forgotten there is the tray icon, the hotkey or the content
index silently not existing.

- **The names helper is the one deliberate exception** and is not in
  `AfterTheScreenIsAnswered()`: the answer registers the scheduled task and starts the helper
  itself, immediately, because names are the half of Findra that works with nobody's models and
  nobody should wait on a 1.5 GB download for their filenames.
- **`_firstRunIsUp` is set AFTER `Show()`.** A screen that threw on its way up must leave a
  launch that carries on; `WhenTheScreenCouldNotBeShown()` is that path and it is real, because
  every stage is wrapped.

## The card's pill column

Three pills stack beside the field: Content, Advanced and **Settings**, which is there because
settings could be opened from exactly two hidden places - the tray icon's menu and a right-click
on the capsule - so the capability list, the transcription limit, the indexing power and the
switch that starts reading all read as features Findra does not have.

- **`SearchCardLayout.HeaderRight` is the field's right edge, not the card's.** The column reaches
  down into the header's band, so a timing right-aligned to the card is drawn straight across the
  Settings pill on every search that reports one.
- **The empty card's height is the hint's OR the column's, whichever needs more.** It was the
  hint's alone, which ended nine pixels above the bottom of the third pill.
- **The card's pills do not ellipsise.** `SearchCardPainter`'s three labels and its two empty
  hints are named constants so `CardPillTests` measures what is drawn rather than a copy of it;
  a label wider than its pill is drawn over both ends of its own outline.

**`ContentPill.Decide` owns what pressing Content means**, not `CardWindow`. Releasing the pill is
always just the search again. Otherwise: files already read and reading merely off turns reading
back on in place; nothing read at all opens Settings at Content, where the switch, the power, the
limit and the capabilities are; a count nothing has read yet searches, because a window thrown
over a card somebody has just opened is not undone by pressing anything.

## The pointer

Findra paints all four of its surfaces itself, so nothing about a rectangle tells Windows what is
under the pointer. **`src/Findra/Look/Pointers.cs` is the only place a cursor shape is decided**,
and it maps each surface's own hit-test answer, so the shape and the behaviour cannot disagree
about what is under the pointer.

- **The capsule body is the only Move cursor in the product.** It has been draggable since it was
  written and said so nowhere. Nothing that answers a click may offer to move anything.
- **The field and the advanced form's fields take the I-beam; everything else clickable takes the
  hand.**
- **Every arm is written out and the default throws.** A target added to `SearchTarget`,
  `PanelTarget` or `FirstRunTarget` and forgotten there is a control that quietly shows the plain
  arrow, which is the whole thing this removes.
- `PointerCursor.Of` builds its cursors ON DEMAND and keeps them, never in a static initialiser -
  a cursor is made through Avalonia's platform factory, and the same reasoning `Parts.Face` is
  written under applies.

## The README is a product page

It has to sell Findra to someone who has never heard of it, so it carries screenshots and
numbers - and **both must be real**.

- **Screenshots come from `--searchshot`**, which draws the actual card with the actual
  painter. Every image is the product, not a mockup. Regenerate by running the command;
  never hand-edit. Record the command next to each image so anyone can reproduce it.
- **Every number comes from `--searchbench`** pasted verbatim and whole, with the machine named
  (CPU, RAM, disk class, accelerator, Windows build). A number without its machine is marketing,
  not measurement. Model sizes come from real files on disk, never the declared floors. The
  fragment is pasted with every section it printed, never the flattering half of it.
- **Never quote a throughput rate from a default-sized `--searchbench` run.** A run of a second
  or two disagrees with itself by more than a published `files/min` or `MB/s` deserves. Quote the
  latency tables, the enumeration numbers and the store sizes, which are stable at any size; if
  an extraction rate is quoted at all, regenerate it with a corpus of at least 10,000 and say in
  the sentence above it that that is what produced it.
- **No claim appears that a reader cannot reproduce** with a command from the README itself.
- **No comparative claims against named competitors** - Findra cannot benchmark them fairly.
  `tests/Findra.Tests/Build/Repo.cs` holds the one name list the README test and the winget
  listing test share. It is deliberately incomplete: one well-known tool's name is an ordinary
  English word and also one of Findra's own presets, so it cannot be grepped for. That one is a
  reading, not a test.

The README was written last, out of the surfaces and the benchmark. Every image in it is a
`--searchshot` render with the command that produced it printed underneath; regenerate by
running the command, never by hand-editing an image.

## Architecture

**Three processes.** This split exists from the first commit; retrofitting it means
re-plumbing the entire query path.

- `findra.exe --names` - elevated, headless, started by a `HighestAvailable` logon scheduled
  task. Owns the NTFS volume handle and the in-RAM name index, and nothing else.
- `findra.exe` - the UI, at normal integrity. Owns grammar, ranking, content search, settings,
  the card, tray and hotkey.
- `findra.exe --index` - the content indexer, a child of the UI.

Exactly one call needs admin rights: `CreateFile(\\.\C:)`, serving `FSCTL_ENUM_USN_DATA` and
`FSCTL_READ_USN_JOURNAL`. Nothing else does.

**The elevated helper must never parse untrusted file content.** Decoders (PDF, ONNX,
Whisper, image codecs) run over arbitrary files found on disk and are the most likely thing
to be exploitable by a malformed input. They belong in the indexer at normal integrity, never
in the elevated process. Running the whole app elevated is also rejected because UIPI would
block dragging a result row into Explorer.

**The pipe.** Local named pipe between helper and UI. Two consequences that shape all the code:

- **Name search is async everywhere.** It is a round trip, not an in-RAM `IndexOf`. Write it
  async from the start.
- **Every query carries a generation counter, stamped on the reply, checked by the UI.**
  Without it a slow answer to an abandoned query arrives late and overwrites a newer result.
  This needs an explicit adversarial test.

The helper also streams USN journal events; the UI decides what to enqueue. The rule "the
parent decides what is indexed" still holds, but the parent is no longer the process watching
the journal.

**Indexing stops when the app quits.** The indexer is a child of the UI, so this is by
construction - no lifetime code. The UI must say so plainly rather than looking idle.

## Capabilities and models

Model-backed capabilities are **independently installable**, and they are **not peers** -
there is a dependency graph:

```
words in documents  ─  free, opt-in (FTS5, no model)
photos & video      ─  siglip2 vision + text + spm            629 MB
meaning in docs     ─  e5-base + e5-spm                       270 MB
speech              ─  whisper-turbo + [e5 pair]              550 MB (+270 if e5 not taken)
  └ hebrew          ─  whisper-ivrit, requires speech         1.5 GB
```

- Speech needs e5 because a transcript is embedded and searched exactly like a document.
- Hebrew needs the general Whisper model: turbo runs first for language detection, and only
  files it calls Hebrew are re-run through the fine-tune. Hebrew is a second pass, never an
  alternative.
- **Everything is 2.9 GB** - measured file sizes, not the conservative minimum-byte floors.
- **`Capabilities.MarginalBytes` is what the settings window and `--models` quote**, because
  there the question really is what one more capability would add to what is already on disk.
  **The first-run rows do not**: each is priced at its own files through
  `Capabilities.OwnModels`, and that number never moves. A marginal figure turns into "0 MB" the
  moment a row is ticked, so the number somebody is weighing disappears exactly when they decide
  on it - and own files are the only pricing where the column adds up, 629 + 270 + 547 + 1549 MB
  being the 2.93 GB on the Everything tile where the closed sets would total 4.08 GB. What the
  whole selection costs is the summary's job: it is `TotalBytes(Close(chosen))`, so ticking Speech
  alone shows 547 MB on the row and 818 MB along the bottom, and the bottom line is the one that
  tells the truth about the download.
- **A missing model is a normal state, not an error state.** Every capability degrades
  silently when its model is absent: the indexer skips that kind, content search contributes
  no candidates, and the card offers the download.
- Enabling a capability later re-queues **only the files it covers**.
- **The indexer asks what is installed before every file it opens.** `Decoders.CanRead` calls
  `Refresh()`, which re-reads the disk through the `Func<CapabilitySet>` the child was constructed
  with. The child is started once and a model can arrive at any moment, so a set captured at
  startup means the child records every file the interface has just queued for that capability
  unreadable, for want of a model sitting on the disk - and nothing queues them again. The
  transcription limit is a delegate for the same reason: `--content limit` writes a settings file
  a running interface will not read again, so `CapabilityGate.ApplyLimit` writes
  `index:transcribeminutes` into the index, before its re-queue, and the child reads that row
  before each recording it opens.
- **A stamp is not taken while its backlog is still sitting there.** `CapabilityGate.StampsIn`
  withholds a capability's stamp whenever files of its kinds are skipped for `Decoders.NoModel`
  and not queued, so `Apply` re-queues exactly those (`onlyBecause: [NoModel]`) rather than
  believing a record that says the debt was paid. Done is a fact the index holds, not a note
  somebody left; the same shape is what makes a machine written off by an older build recoverable.
- **Indexing picks a capability up without a restart; searching by it does not.** The query-side
  encoders are opened once when the interface starts, so the card cannot answer the new way until
  Findra is restarted. Any surface that installs a capability has to say both halves.
  `--models install` ends with exactly that sentence and the README carries it; the settings row
  and the first-run screen do not yet, and that is a gap rather than a decision. Saying the whole
  thing is live is the shorter sentence and it is false.
- **Reading inside files is off until somebody asks**, models or no models. Names are
  searchable the second Findra starts, because a name index costs seconds; looking inside
  files walks every drive and can run for hours, so it never begins on its own. `--content on`
  starts it, `--content off` stops it without discarding anything already read, and the setting
  survives a restart. An index nobody has asked for and a finished index have identical counts,
  so every surface says which one it is looking at rather than printing "up to date".
- **`IndexPowerLevels` is the one list of duty-cycle levels**, on exactly the terms
  `TranscribeLimit.ShortName` sets: the numbers and their labels come out of one table, every
  level offered is inside the clamp `Config.Load` applies, and the row writes the NUMBER at the
  chosen index rather than the index - which clamps to 10 and leaves the indexer resting nine
  tenths of the time. The setting was honoured end to end from the day it was written and had no
  control at all, which is the same shape of defect as a control that is drawn and dead.
- **"Start now" is beside the toggle rather than instead of it.** A toggle states a preference and
  never announces a start, and Findra reads only while it is open, so the button is a real request
  even when the switch is already on. It writes the configuration AND asks the shell, for the
  reason the autostart toggle needs both halves.
- **One number, in minutes, decides how long a recording is worth transcribing**, covering
  sound files and video together. Zero is off, negative is no limit, positive is minutes, five
  by default. A recording over the limit is passed over with a reason of its own, and raising
  the limit goes back for exactly those and nothing else. **It is asked on the first-run screen
  as well as in Settings**, appearing under the Speech row when Speech is ticked and going with
  it: ticking Speech is what signs somebody up for transcription, and a default of five minutes
  cuts a lecture short without saying so. `TranscribeLimit.ShortName` holds the five pill labels
  both surfaces draw - "Off", "5 min", "30 min", "2 hr", "No limit" - because `Describe`'s "30
  minutes" is 65.3px against a pill that holds 62.8px, and a second table of names is how the
  two surfaces come to disagree.
- **A file's size on disk never equals the declared size in the table.** That table is the
  spec's figure in megabytes to one decimal place; real files miss it by tens of kilobytes,
  mostly upward. `ModelStore.SizeSlack` is the only place that width is decided, and nothing
  may compare a file's length to `Model.Bytes` for equality.

## Hardware portability

Findra ships on winget and lands on machines nobody chose for it - AMD or Intel CPUs,
NVIDIA / AMD / Intel GPUs, integrated or discrete, or no usable accelerator at all.
**No capability may require a particular vendor**, and nothing may fail because of the
silicon it found.

- **Detect at runtime, never assume.** Try providers in order, take the first that
  initialises: ONNX (SigLIP-2, e5) is **DirectML → CPU**; Whisper is **Vulkan → CPU**.
  Both cover NVIDIA, AMD and Intel in one path.
- **No vendor-locked providers.** CUDA means NVIDIA only plus a large separate runtime;
  ROCm is not a Windows story. A portable path everywhere beats a fast path for a third
  of users.
- **CPU is a supported configuration, not a failure state.** Only the initial content
  index is slower - queries embed one short string. The UI says so rather than looking stuck.
- **No CPU-feature assumptions** - no AVX-512 requirement, no vendor-specific intrinsics.
- **`--searchmodels` reports the chosen provider and every one it rejected, with reasons.**
  "It's slow on my laptop" is unanswerable; "DirectML failed to initialise, fell back to
  CPU" is a bug report.
- **`--searchbench` records the accelerator** beside the CPU, RAM, disk and Windows build.
- **Never assume x64** - no hardcoded RID in source, no x64-only intrinsics. x64 ships
  first; keeping arm64 reachable costs nothing now and a lot later.

## Palettes

A palette is `name`, `accent`, `ink`, `ground`, `light`. Everything else - fills, rows, tiles,
edges, shadows, hovers - is derived. That five-field object is the entire public contract.

Six ship (dark: Mond, Brass, Verdigris; light: Paper, Blueprint, Porcelain). The user picks
one dark and one light, plus a mode: Follow Windows / Always dark / Always light - auto-follow
needs a pair, which is why it is two picks.

The card painter's derivation assumes a dark ground. **Making it ground-aware is paid exactly
once**; after that every palette in either mode is four constants. This is the largest piece
of new visual work and it lives inside inherited code.

There is deliberately no element/page manifest system. Users extend
`%APPDATA%\Findra\palettes.json`; they do not author layouts.

`Derived.Tile` and `Derived.Chip` are painted by the settings surfaces and are in the legibility
check in `--searchtest` and in `DerivedTests`. Nothing in either is "reserved for later" any more.

## The typeface

Quicksand ships inside the application, one weight, embedded from `assets/fonts/Quicksand-Regular.ttf`,
under the SIL Open Font License 1.1. `assets/fonts/OFL.txt` reaches the publish folder and
therefore the installer, because OFL condition 2 makes the licence travel with every copy.

**`Parts.Face` is the only place in `src/Findra` that resolves a typeface.** The card, the
capsule, both settings surfaces and `--searchshot` all draw through it, and a missing resource
falls back to the system default with a log line rather than stopping the application - a type
initialiser that throws is unreportable.

That single resolver is why a label's fit is **measured** rather than eyeballed: the tests that
check a label into its pill and the test that checks the column is not wider than it needs to be
are each other's opposites. **If a label is ever tight, shorten the label.** Do not widen the
tolerance and do not move the column - satisfying one of that pair by moving the geometry breaks
the other. Bold is `SKFont.Embolden` on the same face; there is no second file.

## The mark

A lens with the capsule's own search field cut out of it, in Mond's accent on Mond's ground -
solid mass rather than a stroked outline, because a 21-unit stroke is 1.3 px once the icon is
16 px across and grey mush is what a taskbar makes of it.

**`build/Make-Icon.mjs` holds the only copy of the geometry.** Everything else is emitted from it:
`assets/icon/findra.ico`, the plated and unplated SVGs, the installer's wizard image and
`website/public/favicon.svg`. Hand-editing an output is how one logo quietly becomes two, so
`IconTests` decodes the shipped bytes and holds every copy to the others - the site header's data
URI included, which is the fifth and the easiest to forget.

- **It needs node, and nothing in the build does.** `dotnet build`, `Publish.ps1`, the installer
  and all three workflows read what it produced and do not know it exists.
- **A binary `.ico` in the tree is a deliberate exception** to Findra drawing its own icons. A
  Win32 application icon is a PE resource: the linker needs a real file at build time and there is
  no runtime hook that could paint one. The tray icon, which CAN be drawn at runtime, still is.
- **The small sizes are drawn, not shrunk.** `HINTS` thickens the handle and drops the slot
  entirely at 16 px, and 20 and 40 are in the set because that is what Windows picks at 125% and
  150% scaling - without them it downscales 32 and 48 and throws the hinting away. `IconTests`
  asserts the 16 px slot is ABSENT, so losing a hint fails rather than passing quietly.
- **The tray has no plate and its slot is a real hole.** Windows composites a tray icon onto a
  taskbar whose colour it chose: a plate is a dark square nobody asked for, and a slot filled with
  Findra's own ground looks right on a dark taskbar and like a smudge on a light one. Both are
  asserted, on three palettes, through a `Render` seam that exists because a `WindowIcon` needs a
  running Avalonia and the alternative was a test that reads its own source.

## Shipping

- **The version lives in `Directory.Build.props` and nowhere else.** No `<Version>`,
  `<AssemblyVersion>`, `<FileVersion>` or `<InformationalVersion>` in any `.csproj`.
  `IncludeSourceRevisionInInformationalVersion` is off and `BuildInfo.Normalise` strips anything
  from `+` onward anyway, because `Version.TryParse` rejects `1.2.0+sha`, `UpdateCheck.Compare`
  answers 0 for what it cannot parse, and 0 is routed to "up to date" - a permanent lie.
- **A tag with no matching `CHANGELOG.md` section fails the release, and the notes are that
  section.** GitHub's generated release notes are never turned on. `build/Check-Release.ps1` is
  the gate, each refusal has its own exit code, and the workflow calls it rather than knowing it.
- **No pre-release tags.** The comparison cannot order `1.2.0-rc.1`, and `/releases/latest`
  excludes prereleases, so such a tag ships a release nobody's update check will ever see. The
  refusal in `Check-Release.ps1` is the line to delete when the comparison learns semver
  prereleases.
- **Nothing but a person on the Actions tab publishes to the winget catalogue.** No `push`,
  `tag`, `release` or `schedule` trigger may reach `winget.yml`. A mis-tagged GitHub release is
  recoverable; one that reaches the catalogue is somebody else's `winget upgrade`.
- **Both architectures ship from the first release, in one manifest.** `win-x64` and `win-arm64`
  from one matrix, two `Installers:` entries under one `PackageVersion`.
- **The signing step is a placeholder that does nothing, and nothing anywhere claims otherwise** -
  not the README, not the installer, not the release body, not the winget listing, and not
  `docs/code-signing-policy.md`, whose status note is coupled to the empty step by a test.
- **The installer is a third distribution route and `installer` a fourth install source**, beside
  `winget`, `source` and `unknown`. Spec §2 and §9b still say two and three; the plan document
  argues the amendment and this line is the record until the spec is amended.
- **Inno Setup's architecture identifiers are not a regular family, so `installer/findra.iss`
  builds them per architecture rather than by pasting a suffix on.** `x64compatible` is a word;
  `arm64compatible` is not - the identifier is plain `arm64`. The full list is `x86compatible`,
  `x86os`, `x64compatible`, `x64os`, `arm32compatible`, `arm64` and `win64`. Appending
  "compatible" to the `{#Arch}` the workflow passes gave the x64 leg a valid word and the arm64
  leg one ISCC rejects, which fails that matrix leg and so the whole release, for Intel and Arm
  alike. `InstallerScriptTests` now reads every `ArchitecturesAllowed` and
  `ArchitecturesInstallIn64BitMode` in the script against that list, and checks that the uninstall
  prompt's report variable is declared `AnsiString`, which is the exact type
  `LoadStringFromFile`'s var parameter takes.
- **No version number in the install directory**, and `AppId` is a fixed GUID: the scheduled task
  stores an absolute path to `findra.exe`, so a versioned directory points an elevated logon task
  at a binary that no longer exists after every upgrade.

## Data locations

Config roams, bulk does not - 2.9 GB of models must never sit in a roaming profile, and models
must never live in the publish folder.

| Path | Holds |
|---|---|
| `%APPDATA%\Findra\config.json`, `palettes.json` | settings |
| `%LOCALAPPDATA%\Findra\models\` | the seven model files |
| `%LOCALAPPDATA%\Findra\index\` | SQLite name, FTS5 and vector stores |
| `%LOCALAPPDATA%\Findra\logs\` | `findra-YYYYMMDD.log` |

## Versions and updates

Findra knows its version, learns whether a newer one exists, and tells you. **It never
installs anything by itself** - no self-updater, no background installer, no elevation for
updates. Replacing a running executable and re-registering an elevated scheduled task are the
two operations most likely to leave a machine broken; winget already solves that correctly.

The update check is the **one exception** to "nothing leaves the machine", and it is written
down rather than buried (spec §9b): an anonymous HTTPS GET to the GitHub Releases API, at
most once per 24 hours, on startup, in the background. No query parameters, no machine or
install identifier, nothing about files or searches. It never blocks anything - a failure is
a log line, not a dialog. On by default, disclosed on the first-run screen, and switchable
off, where off means no request is made.

It reports the action matching how the user installed: `winget upgrade blakazulu.Findra`, or
a link to the release notes for a source build. The install source is recorded at first run,
not guessed each launch.

**Compare parsed version numbers, never strings** - `1.10.0` is newer than `1.9.0`, and a
check that gets that wrong is worse than none, because it tells people they are current when
they are not.

## Install, resume and uninstall

**Installing never discards work that is still good.** On startup, inspect what is on disk
and continue from it: models present and correctly sized are kept, partial downloads resume
from the byte already fetched, an index with a current schema is used as-is and a non-empty
queue is **resumed**, an older schema is migrated with only the invalidated files re-queued.
"Done" is a fact the index records - schema version, consumed USN position per volume,
pending queue - not a guess. Re-downloading 2.9 GB or re-indexing a finished disk because an
upgrade did not look first is the worst thing this product could do to someone; it gets a test.

The name index is exempt - it lives in RAM in the helper and is rebuilt by MFT enumeration
in seconds at every logon. Only content survives restarts.

**A download that ends short is refused on a floor, not only on a length.** `ModelDownloader`
checks the bytes written against `Model.MinBytes` as well as against the response's length,
because the length-only guard was unreachable exactly when it was needed: a response carrying no
`Content-Length` gives a total of zero, the comparison is skipped, and the truncated file is
promoted under its real name - after which every capability needing it fails quietly while Findra
reports it installed. The general rule is worth more than the one fix: **a guard conditioned on
data the other end may choose not to send is not a guard.** Pair it with one that holds on what is
on the disk, and reason about the case where the header is absent rather than the case where it
disagrees.

**Uninstall always removes** the app files, **the `HighestAvailable` scheduled task**, any
autostart entry, and stops the helper and indexer first. Missing the scheduled task orphans
an elevated logon task pointing at a deleted binary on a stranger's machine - that is a
defect, not an inconvenience.

**Uninstall keeps by default** `%LOCALAPPDATA%\Findra\models\`, `index\`, and
`%APPDATA%\Findra\` config. Deleting them is opt-in via a checkbox and a flag, and the prompt
states the **measured** size it would free, not a vague warning.

**`findra.exe --uninstall` is a first-class mode** (with `--purge` to also delete data),
because the `dotnet publish` route has no installer - without it, everyone who built from
source is left with an elevated scheduled task and no supported way to remove it.

## No lineage

**Findra is a separate project, not a fork or a component.** Everything in it is owned outright:
namespaces are `Findra`, and log tags, probe markers, config paths and file names follow.

Nothing anywhere - the README, the UI, a commit message, a manifest, a build script or a code
comment - may describe Findra as derived from another project, name one, or describe behaviour
that is not Findra's. This is not a name grep: the leaks that survive one are doc comments
explaining another product's behaviour, magic constants justified by another product's history,
and vocabulary that belongs to a different object model. Read the comments; ask of each whether it
reads as written for Findra by somebody who has never seen another codebase.

The same rule covers this repository's own development vocabulary. A shipped comment may not cite
a plan, a task number or a review, because those documents are not in the tree a reader has. Name
the thing rather than the document that asked for it.

## The changelog

`CHANGELOG.md` is updated on **every commit**, in the same commit, in its pathspec. Never
leave it for a sweep at release time.

It is load-bearing rather than decorative. The release workflow reads the section matching a
tag and uses it as the release notes, and a tag with no matching section fails the release.
It is also where Findra's own update check sends someone who built from source.

The format is [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/): entries go under
`## [Unreleased]` in one of Added, Changed, Deprecated, Removed, Fixed or Security, and move
into a numbered section on release. Write each line for a person reading release notes, not
for a reviewer reading a diff.

## Testing

TDD for all new code: the pipe protocol and its generation counter, the palette layer and light
derivation, per-capability gating and the dependency graph, the hotkey fallback chain, config
load/save and migration.

The ported engine arrives working and is not rewritten test-first. It gets characterization
tests only where Findra changes its behaviour - principally the all-or-nothing model gates
becoming per-capability.

**An effect that cannot be run in a test gets a seam, not a test that reads its own source.**
`Uninstall.Run` and `HelperTask.Unregister` each have an overload taking their effects as
delegates, and the tests drive those and assert the recorded sequence: what was stopped, what was
unregistered, what was deleted, and in what order. `Autostart` takes an `IStore` for the same
reason.

The reason is that a source-reading test cannot fail for the defect it exists to catch. Wrapping
the whole task-removal block in `if (!quiet)`, with every token those tests grep for still present,
left the suite green while an uninstall walked away from a `HighestAvailable` logon task pointing
at a deleted binary - the thing the spec calls a defect rather than an inconvenience. If a test's
only evidence is that a string appears in a file, ask what edit would keep the string and break the
behaviour; there is nearly always one. Text assertions are kept only where there is no code to run
at all: `installer/findra.iss` and the workflow YAML.

**A subprocess is drained asynchronously and killed on a timeout.** `HelperTask.RunSchtasks` reads
both of `schtasks`'s streams as tasks before it waits, because reading one to the end and then the
other stops dead the moment the unread one fills its buffer - inside an elevated uninstall, with no
window and no way out but the task manager. `Register` reads the result of its own timeout instead
of discarding it, or an elevation prompt nobody answered is reported as an unrelated error.

## Gotchas

- `schtasks` CSV column headings are localized; the XML is not. Parse the XML form.
- Hotkey registration can fail (`Alt+Space` is the system menu chord in some configurations).
  Walk a fallback chain, take the first that registers, and **tell the user which one it landed
  on**. Never fail silently.
- Two open paths mean two dim behaviours: from the capsule, dim the capsule's monitor; from the
  hotkey, dim the monitor under the cursor.
- Scheduled-task registration is the one thing that can fail on a stranger's machine in a way
  Findra cannot fix. It needs a non-fatal path that still leaves names working on whatever can
  be read unelevated.

## Licence

Apache-2.0 with a `NOTICE` file. The requirement is free use, cloning and modification, with
propagating attribution to blakazulu and https://github.com/blakazulu/findra. Apache's NOTICE
is the mechanism that carries that forward; MIT would not.

**`LICENSE` and `NOTICE` are copied into the publish folder by `src/Findra/Findra.csproj`, and
that is the only reason they reach anybody who installs.** Apache-2.0 section 4(d) requires the
notice to travel with every distribution, the installer's `[Files]` entry copies the publish
folder and nothing else, and `LicenseFile=` in `installer/findra.iss` only DISPLAYS the licence in
the wizard - it puts no copy on the disk. A NOTICE that stays in the repository gives up the whole
reason Apache was chosen over MIT. `assets/fonts/OFL.txt` travels the same way and for the same
kind of reason, OFL condition 2, and `TypefaceTests` holds all three to it.
