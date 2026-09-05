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
pwsh -File build/Make-Shots.ps1 -Exe publish/win-x64/findra.exe   # redraw every screenshot the
                                               # README shows, into docs/shots, and copy each one
                                               # the website shows too. Reads the list out of the
                                               # README; keeping a second list is the bug.
node build/Make-Icon.mjs                       # regenerate the mark - the .ico compiled into the
                                               # exe, both SVGs, the installer's wizard image, the
                                               # site's favicon.svg, favicon.ico and 180px
                                               # apple-touch-icon, and the two share images with the
                                               # card's own subline written out beside them as
                                               # share/card.txt. By hand, when the mark changes;
                                               # nothing in the build runs it.
node build/Make-Pages.mjs                      # regenerate /about/, /contact/ and /privacy/ from
                                               # their Markdown, and copy each source verbatim
                                               # beside its page. By hand, when the prose changes.
node build/Ping-IndexNow.mjs [--send]          # tell Bing and Yandex the site changed, instead
                                               # of waiting to be discovered. Prints the URLs and
                                               # sends nothing without --send. By hand, AFTER the
                                               # deploy carrying the change is live - a submission
                                               # is a claim that these pages really changed, and
                                               # firing one on every push is how a site teaches an
                                               # engine to stop believing its signal.
node tests/edge/markdown.test.mjs               # the site's Accept negotiation, as a table. The
                                               # one thing in this repository CI runs node for.
```

Six diagnostic modes are non-negotiable and are built from day one. They are how the app is
verified without a screen, and `--searchshot` in particular is how UI gets iterated headlessly:

```bash
findra.exe --searchprobe [query]      # whole path end to end; must report which process
                                      # answered, the current generation counter, and what
                                      # the content indexer is doing
findra.exe --searchmodels             # are models present, do they load, do they agree, and
                                      # which execution provider answered for each runtime
findra.exe --searchindex [file|folder|q:query|why:path]...   # what is indexed, what is queued;
                                      # given paths it queues and drains them, given q: it queries,
                                      # given why:<path> it explains ONE file and changes nothing
findra.exe --searchshot out.png <state> [palette]   # twenty-six states, listed below
findra.exe --searchtest               # engine self-check
findra.exe --searchbench [out.md] [corpus]   # measured numbers, as a pasteable Markdown
                                      # fragment; `corpus` is how many files it generates
findra.exe --version                  # print the version and log location, then exit
```

The `--searchshot` states are `SearchShot.States`, and that list is the only definition of them.
Twelve draw the card, eight the settings window and six the first-run screen:

```
capsule  empty  indexing  contentmode  contentwaiting  typing  results  noresults  many  adv
opening  openingempty
settings  settingsopening  settingssearches  settingscontent  settingsabout
settingsuptodate  settingsupdate  settingsasking
firstrun  firstruninstalled  firstrunspeech  firstrundownloading
firstrunfinished  firstrunready
```

`contentwaiting` is the Content pill drawn NOT offering - reading is on and the first pass has not
finished a file, so there is nothing to search, nothing to turn on and nothing settings could add.
It is hovered on purpose: suppressing the hover fill is half of what makes a dead control read as
dead, and a state that never hovers it proves only the resting colour.

`firstrunfinished` and `firstrunready` are the last act's two shapes, and one state can only show
one of them: the first took reading and so carries the last question and two buttons, the second
did not and is the plain "Findra is ready" with one way out. A finished shot that only ever had
reading on would ship the other half of the painter unlooked at.

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

- **Nothing reads inside files until the LAST question is answered.** The first act's switch
  states the preference and is saved the moment it is answered; the content loop used to act on it
  ten seconds later, so the indexer was reading and embedding files while 2.9 GB of models came
  down the wire over the same disk - on a real install the rate fell from 57 files a minute to 9.
  `App._holdReading` holds it, `FirstRun.Asks` decides whether the last act asks, and "Start
  reading" clears the hold. "Later" leaves it set for the session and changes no setting, so the
  next launch reads without asking again - which is why the button is **Later** and not "Not now".
  Anything that explicitly starts reading clears the hold too: the settings button and the card's
  Content pill are both a person saying "now", and a hold nothing could clear would be the dead
  button all over again.
- **The names helper is the one deliberate exception** and is not in
  `AfterTheScreenIsAnswered()`: the answer registers the scheduled task and starts the helper
  itself, immediately, because names are the half of Findra that works with nobody's models and
  nobody should wait on a 1.5 GB download for their filenames.
- **The screen is pinned to the front only long enough to ARRIVE.** `Topmost` is set at
  construction, because Windows will not reliably let a still-starting process take the
  foreground and a welcome screen opening behind the desktop reads as an installer that did
  nothing - and it is released in `Opened`, the moment the window is really on the display.
  Keeping it set was the first attempt and only a real install showed why it is wrong: this is a
  screen somebody reads, thinks about and then leaves running while 2.9 GB arrives, and for all of
  that it stood over every other window on the machine. **What makes it the only door into Findra
  is the gate in `App.TheWelcomeScreenIsInTheWay`**, not a flag that outranks the whole desktop.
- **`_firstRunIsUp` AND `_firstRun` are set AFTER `Show()`.** A screen that threw on its way up
  must leave a launch that carries on; `WhenTheScreenCouldNotBeShown()` is that path and it is
  real, because every stage is wrapped. `_firstRun` is the GATE, and taking it before `Show` made
  that path worse than useless: `Closed` never fires on a window that never opened, so the
  recovery built a whole product whose hotkey, capsule, tray and Settings each called `Activate`
  on a dead window, saw "the screen is in the way", and refused for the life of the process.
- **A screen CLOSED without an answer hands the launch on too.** The X, Alt+F4 and the taskbar are
  never disabled and none of them raise `Answered`, so everything held back stayed held back: a
  process with no window, no tray icon and no hotkey, endable only from the task manager, and a
  next launch that would stack a second one behind another welcome screen.
  `StartupOrder.WhenTheScreenWasClosedUnanswered()` is that path. Nothing writes `FirstRunDone`,
  so the screen is asked again, which is the right outcome for a question nobody answered.
- **The hold on reading has THREE endings and only two of them are events.** "Start reading"
  clears it, "Later" deliberately keeps it for the session, and a screen that never ASKED - reading
  off in the first act, or closed before the last one - has nobody to clear it.
  `FirstRunWindow.AskedAboutReading` is what `WhenTheWelcomeScreenIsGone` reads to tell the third
  from the second; without it the hold outlived the screen and both Content switches read as on
  while reading nothing.
- **The hit test takes the STATE, never the five bounds.** `FirstRunLayout.HitTest(x, y, state)`
  is what the canvas calls, and it derives the row count, the band row, settled, finished and
  asking from the one state the painter and the window are already reading. Handed in by hand they
  were five chances to measure a screen that is not the one on the display, and one was already
  wrong: the band row went in as `FirstRun.LimitRow`, which names Speech's row in every act, while
  `SurfaceHeight` and the painter read `FirstRunLayout.BandRow`, which drops it the moment the
  screen is answered. On a machine offered Hebrew - where a row sits BELOW Speech - anybody who
  took Speech got a last question whose "Later" and "Start reading" were hit-tested 64px under the
  bottom edge of the window they were painted in: no hover, no cursor, nothing clickable, on the
  one screen that has to be answered. It never showed in a shot, in CI or on a machine with no
  Hebrew row, because there Speech is the last row and the two answers agree.

## The update panel

Pressing **Check now** in Settings raises a panel over the pane. It exists because the answer used
to land in a text row three lines above the button that asked for it, which is the quietest place
in the window to put the only news this product ever has.

- **Only the button raises it.** `RunUpdateCheck(force)` passes `force` through to
  `NoteUpdate(..., raise:)`, and the daily background check on startup passes false. A person who
  asked nothing is not waiting for an answer, and a dialog that appears on its own is the
  idle-widget-being-loud that spec §3 forbids the capsule.
- **Findra still installs nothing itself.** "Update now" runs `winget upgrade blakazulu.Findra` in
  a **visible** console for a winget copy and opens the releases page for anything else; winget or
  the installer replaces the binary, never Findra. `UpdatePrompt.GoLabel` and `Body` switch on the
  install source, because offering a winget command to somebody who built from source is worse
  than offering nothing. Spec §9b's rule is about WHO does the replacing, and the answer is
  unchanged.
- **`Disabled` has an arm of its own.** `UpdateCheck.CheckAsync` short-circuits when the switch is
  off *even when forced*, so pressing Check now with updates off makes no request at all -
  answering that with silence is the exact defect the panel exists to remove.
- **While it is up it is the only thing on the surface.** The hit test refuses the scrim, the
  panel's own body, the pane behind and the window's own close cross. A dimmed control that still
  takes a press is the worst of both, and a click on a question must not answer it.
- **`SettingsState.Prompt` is a field, not something derived from `Update`.** `Update` is what the
  last check FOUND whenever it ran; `Prompt` is whether somebody is standing in front of a question
  they asked. Deriving one from the other is how the background check ends up shouting.

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

## Explaining one file

**`--searchindex why:<path>` is how "I can see this file and searching does not find it" gets an
answer.** Every other diagnostic describes the whole index - what is queued, what failed, what
matched - and none of them could say anything about a FILE, which is the only thing anybody asks
about. The facts were all recorded and unreachable: explaining one real result meant reading source
and reasoning from the outside, which is the defect `--searchprobe` exists to remove for the
progress pill, one level along.

- **It reads and changes nothing.** Handing the same path in bare QUEUES it; `why:` is a question.
- **With a `q:` beside it, it scores that file's own vectors against that query** and says whether
  each cleared the floor its kind is judged on. `VectorStore.Search` can only report the rows that
  WON, and a file somebody is asking about is nearly always one that lost - so `ScoreOf` reads one
  known row instead. A tombstoned row is reported as discarded rather than scored, because its
  bytes are still there and dotting them yields a confident number for a segment that belongs to
  nothing.
- **`ExplainFile.Verdict` names THE reason, in decision order**, on the terms `--searchprobe` is
  written under: not on the disk, not a content kind, excluded, queued, never offered, passed over,
  failed, read-but-empty, read-but-edited-since, read. A diagnostic that names a reason which is
  not the reason sends somebody reinstalling.
- **The floors it prints come from `ContentBranch`'s own constants**, so it cannot describe a
  threshold the engine does not apply.

## The progress pill

Under the card and under the capsule's bar: a dial, what is being read, the count, and the
percentage at the far end. **The pill IS the bar** - the fill runs left to right underneath the
words rather than beside them, so the shape carries the number and the eye reads it without
reading it.

- **`ProgressPill` paints it and both surfaces go through it.** They show one fact seen in two
  places; two painters is two answers waiting to differ. Each surface supplies only its own
  rectangle, which is the one thing they are allowed to disagree about - the card's is the card's
  width less its padding, the capsule's is the bar's.
- **Both surfaces show it only while there is work in hand, and `IndexStatus.Pill` takes no
  argument that could say otherwise.** Reading off, no live indexer, or an empty queue is a settled
  index and draws nothing - not a bar at zero and not a bar at 100%. The card had an
  `evenWhenSettled` of its own for a while, on the reasoning that a window somebody OPENED owes an
  answer whether or not anything is moving, and it drew four more shapes nobody else drew: "up to
  date", "paused", "nothing read yet" and "not reading inside files". The card does owe that answer
  and the **Content pill in its own header** is what gives it - whether Findra is reading and
  whether it has read anything, where the eye already is. What hung under the card was a second
  answer to a question already answered, resting at 100% for the rest of the day, which is the
  idle-widget-looking-busy that spec §3 forbids the capsule. The card has no better claim to it.
  `--searchprobe` names which settled state it is, in `Pill`'s own decision order, for anybody
  going looking for a pill that is correctly absent.
- **It hangs BELOW the card, outside the card's shape**, the way the capsule's hangs under the bar.
  `SearchCardLayout.Height` is the card and `WindowHeight` is the card plus the pill's band; sizing
  a window or a bitmap takes the second, drawing or hit-testing the card takes the first. It sat
  inside the card once, between the field and the hints, and the painter drew the card's body at
  the height WITHOUT the pill while the window was sized WITH it - so the hints were painted onto
  the desktop under the card's bottom edge. `CardProgressTests` reads a column of pixels down the
  middle of a render and requires card, then nothing, then pill.
- **`IndexStatus.Doing` maps the kind to a word, and it switches on the ENUM.** It was written on
  strings, matching `"Doc"` - the column heading `--searchindex` prints - against a value that is
  `"Document"`. Documents are most of what a first pass finds, so the noun vanished for nearly
  every file and the pill read "indexing" with nothing after it. A label copied off one surface
  into a comparison on another is the shape of that mistake.
- **`--searchprobe` prints what the pill would draw.** A surface with no diagnostic is one nobody
  can be asked a question about, and "I cannot see the progress pill" had no answer that did not
  involve reading source and guessing.

## The capsule's progress pill

Under the bar, in a pill of its own: what is being read on the left, a track across the middle,
how far it has got on the right. It was a bare track and a line of floating text before - the only
thing in the product drawn with no container round it, which read as part of the desktop rather
than part of Findra.

- **`IndexStatus` owns both shapes.** `Line` is one sentence for the card's footer and the tray's
  tooltip; `Pill` is the same facts split for a label / track / count, because a sentence cannot be
  cut in half and put at opposite ends of a pill. Two composers would be two answers.
- **`Show` false draws no pill at all**, which is not a bar at zero. Reading off, no live indexer
  or an empty queue all mean nothing is happening, and a permanently visible progress pill makes an
  idle widget feel busy - the thing spec §3 says the capsule must not do.
- **The label is a word, never the `ResultKind`.** `IndexStatus.Doing` maps it, and an unrecognised
  or half-written row falls back to the bare verb rather than naming the wrong thing confidently.
- **The painter measures both ends and lays the track in what is left.** "indexing recordings" and
  a seven-figure count are the widest either side gets, and a track placed from a guess runs
  underneath one of them.
- **`CapsulePainter.Placeholder` is one constant because it used to be two.** The window drew
  "Search files, photos, words…" and `--searchshot capsule` drew "Search 1.5M files", so every
  render this project has reviewed - the README's, the site's, every palette sweep - showed a
  string the product does not use, and the one it does use had never been looked at. That is the
  same defect as an unrendered painter branch, one level up: the state was shot, with different
  data.

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

## The website

`website/public` is the whole site: plain static files, no build step, no framework, no package
manifest. **Nothing in this repository deploys it** - there is no workflow for it and `ci.yml`,
`release.yml` and `winget.yml` never touch it. Netlify watches `main` and publishes
`website/public` as it sits, so a push is a deploy and `netlify.toml` - a publish directory and
some headers - is the entire configuration. A build step would add a toolchain to a repository
that is otherwise .NET and PowerShell, and is a thing to argue for rather than introduce.

It carries the README's two rules, and it says so on the page: the shots section is headed
"Every picture below is the product". So a hand-edited image does not just mislead, it makes the
page lie about itself.

- **`docs/shots` and `website/public/shots` are two copies of the same renders.** They have to
  be two: `website/public` is published exactly as it sits and nothing under it can reach into
  `docs/`. Nothing kept them together and they drifted - the site served an `adv`, a `firstrun`
  and a `settingscontent` from an older build while printing the command that produces the
  current ones, and its Settings picture was missing "Start reading now" and "Indexing power",
  two controls the product had gained. **`build/Make-Shots.ps1` is how you regenerate them**: it
  reads the list of images out of the README rather than keeping a third copy of it, renders
  each into `docs/shots`, and copies every one the site also carries. `SiteShotTests` fails if
  the two ever disagree again, and fails again if the two pages print different commands for the
  same picture - identical bytes under contradictory commands is the same lie in a different
  place.
- **Every number is a `--searchbench` measurement** beside the machine that produced it, as in
  the README.

**IndexNow is not the page reaching out, and the distinction is the whole point.** Findra's site
is days old on a shared subdomain with nothing linking to it, so the ordinary route to being
crawled - somebody else pointing at it - is not available. `build/Ping-IndexNow.mjs` submits the
sitemap's URLs to Bing and Yandex, and Bing is the one that matters, because Copilot answers out of
Bing's index. It runs from a developer's machine, by hand, after a deploy; nothing on the page and
nothing in the browser makes that request, so the policy below is untouched by it. Two things about
it are load-bearing. **`--send` is required**, because a submission asserts that the pages really
changed, and one fired on every push - including the pushes that only touch a test - trains the
engine to discount the signal. And **the key exists only in the file the engines fetch**,
`website/public/<key>.txt`, which the script reads rather than holding a copy of: they GET that file
and compare it to the key in the submission, a mismatch is rejected silently, and a key written
down twice is a key that will one day be written down differently. `TheIndexNowKeyFileIsNamedForWhatItContains`
holds the file to its own name and asserts the key appears nowhere in the script.

**The screenshots are delivered through Netlify's Image CDN and the files behind them are
untouched.** Each shot sits in a `<picture>` offering AVIF and WebP at two widths through
`/.netlify/images`, with the original PNG left as the `<img>` fallback - 104 KB becomes about 15 KB
at phone width. Leaving the PNG in place is what keeps two promises true at once: `SiteShotTests`
still holds `website/public/shots` byte-identical to `docs/shots`, and the section headed "Every
picture below is the product" still means it, because what the CDN changes is the encoding on the
wire rather than the render. `/.netlify/images` is same-origin, so the policy below does not move.
`EveryShotIsServedThroughTheImageCdnAndNamesItsOwnFile` requires every transformed URL to name the
same file as the `<img>` beside it, which is the failure worth catching: a reader would see one
picture while a crawler fetched another, and nothing about the page would look wrong.

**The content security policy is a promise the page makes about itself.** A page selling
"nothing leaves your machine" must not itself be reaching out, so `netlify.toml` permits
`fonts.googleapis.com` for stylesheets and `fonts.gstatic.com` for the font files and nothing
else: `script-src 'self'`, `connect-src 'none'`, `img-src 'self' data:`. No analytics, no
beacons, no third-party script, no CDN. A `data:` URI is allowed, which is how the header mark is
inlined; an external image host is not. Anything that needs a new origin in that header is a
decision to put to somebody, not a detail to add.

**The site names Windows Search and the README may not.** "No comparative claims against named
competitors" above is scoped to the README and the winget listing, and `Repo.Competitors`
enforces it for both. The site is written in a different voice on purpose: its `h1` is the joke,
and the section under it is a Windows Search dialog failing to find a file. That is deliberate
and shipped - do not quietly correct it, and do not carry it back into the README or the
manifest, where a test will catch it.

**Nothing on it promises a release that does not exist**, in either direction. Before the first
tag the calls to action said Coming soon and winget appeared as the route that WOULD work; from
0.1.0 they say Get Findra and winget is the install. The same sentence turns over on the README,
`llms.txt` and `website/content/home.md`, and `ReadmeTests.TheReadmeDoesNotStillSayFindraIsNotReadyToInstall`
is the only one of those that a test holds - **coupled to a numbered section in `CHANGELOG.md`**,
which is the fact that actually moves when a release is cut. It was coupled to the winget
manifest's placeholder hashes, and that was a coupling to something that never changes:
`winget.yml` substitutes the real hashes into the manifests it SUBMITS and writes nothing back, on
purpose, so `packaging/winget/*.yaml` keeps its sixty-four zeros for ever. The guard could not have
fired. Pick the fact that moves.

**One `h1`.** The page opens on the mark, the name and the results card, which runs off the right
of the window on purpose; the ticket section below it is an `h2` and steps down in size, because
at hero size the page appeared to start twice. The `<title>` and `og:title` say what that first
section says. There is no `twitter:title` - the card falls back to `og:title` and follows it.

**Most of this is not tested.** Two things are. `IconTests` fails if `favicon.svg` drifts from
`assets/icon/findra.svg` or if the header's data URI stops carrying the generated path, and
`SiteShotTests` fails on a shot or a printed command that disagrees with the README's. Everything
else above holds because somebody read it - which is exactly why the shots drifted for as long as
they did, and why the two guards that exist are on the two things that had already gone wrong.

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

**One interface per index.** `src/Findra/Startup/OnlyOne.cs` is an exclusive handle on
`.running` in the index folder, taken in `RunUi` before Avalonia starts. Two interfaces do not
coexist: each starts a `--index` child, and the second child's vector store opens for writing over
a file the first holds, throws a sharing violation, is restarted, and throws again for ever at a
five-minute backoff. **A file handle rather than a named mutex**, and the reasons are each a bug
avoided: a mutex belongs to the THREAD that took it, so it is reentrant on one thread and a test
cannot prove the guard works; Windows closes a dead process's handles unconditionally, so a hard
kill frees the claim, where a named semaphore would not; and the index directory IS the key, so two
profiles are two Findras and one person signed in twice is one. A second launch exits 0 - Findra is
running, which is what was asked for - after saying which process has it and how to reach it.
Failing to take the claim for any reason but contention lets the launch through with a log line: a
guard that refuses to start is worse than the collision it prevents.

**A file that keeps ending the indexer is written off after `ContentDb.MaxAttempts` tries.**
`pending.attempts` is incremented AND COMMITTED before the file is opened, which is the whole
design. A managed throw was always handled - the row is recorded Failed and dequeued - but a
decoder that takes the PROCESS down never reaches that code, and `TakeNext`'s deterministic
ordering hands the same row to the restarted child, which dies again. The queue stopped for good at
that file with nothing to show but a repeating restart line. Counting afterwards records nothing
about exactly the attempt worth counting. The column is added to existing databases by
`AddColumnIfMissing` and deliberately NOT by a numbered migration: those decide which files are
stale, and a new column at its default makes no row mean anything different.

**Indexing stops when the app quits.** The indexer is a child of the UI, so this is by
construction - no lifetime code. The UI must say so plainly rather than looking idle.

## Capabilities and models

Model-backed capabilities are **independently installable**, and they are **not peers** -
there is a dependency graph:

```
words in documents  ─  free, opt-in (FTS5, no model)
photos & video      ─  siglip2 vision + text + spm            629 MB
meaning in docs     ─  e5-base + e5-spm                      1.04 GB
speech              ─  whisper-turbo + [e5 pair]              550 MB (+1.04 GB if e5 not taken)
  └ hebrew          ─  whisper-ivrit, requires speech         1.5 GB
```

- Speech needs e5 because a transcript is embedded and searched exactly like a document.
- Hebrew needs the general Whisper model: turbo runs first for language detection, and only
  files it calls Hebrew are re-run through the fine-tune. Hebrew is a second pass, never an
  alternative.
- **Everything is 3.7 GB** - measured file sizes, not the conservative minimum-byte floors.
- **The meaning model is FULL PRECISION, and the size is the point rather than a regret.** It was
  `model_quantized.onnx` at 265.7 MiB. A quantised model does not mean the same thing on the two
  execution providers: measured against DirectML on this machine it came back at 0.970 cosine with
  elements 0.8 apart, where the processor against itself is exactly 1, and no graph optimisation
  setting closes the gap - it survives `ORT_DISABLE_ALL`. Findra embeds documents on the
  accelerator and embeds the query on the processor and compares the two, so a model that answers
  differently depending on which silicon ran it is a systematic error in every score, and one that
  MOVES when somebody's driver changes. fp32 agrees to 1.000000. It is also faster than the fp16
  export on the processor - 10.9 ms against 27.7 for one query, because processors do fp32 natively
  and emulate fp16 - and search runs on the processor. `ProviderAgreementTests` holds the file to
  that floor and `--searchmodels` prints the measured cosine.
- **Documents are embedded on the ACCELERATOR and queries on the PROCESSOR.**
  `Decoders.E5()` asks for the accelerator, `ContentBranch` does not, and that pairing is the
  largest single lever on how long a first pass takes: 134 segments a second against 408. It is
  safe by the two providers AGREEING rather than by a threshold absorbing the difference, which is
  why the line above is not an optimisation note.
- **`Capabilities.MarginalBytes` is what the settings window and `--models` quote**, because
  there the question really is what one more capability would add to what is already on disk.
  **The first-run rows do not**: each is priced at its own files through
  `Capabilities.OwnModels`, and that number never moves. A marginal figure turns into "0 MB" the
  moment a row is ticked, so the number somebody is weighing disappears exactly when they decide
  on it - and own files are the only pricing where the column adds up, 629 MB + 1.04 GB + 547 MB
  + 1.51 GB being the 3.7 GB on the Everything tile where the closed sets would total 6.31 GB. What the
  whole selection costs is the summary's job: it is `TotalBytes(Close(chosen))`, so ticking Speech
  alone shows 547 MB on the row and 1.57 GB along the bottom, and the bottom line is the one that
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
- **Indexing picks a capability up without a restart; searching by it does not - with one
  exception, and it is the common case.** The query-side encoders are opened once when the
  interface starts, which on a FIRST RUN happens before the download that run just agreed to: they
  were opened against an empty folder, answered null, and nothing reopened them, so the first
  content search anybody ever ran came back empty on a machine whose screen had just said
  everything they chose had arrived. `OpenTheQueryEncodersIfThereAreNone` opens them when this
  session has NONE, which is safe because a null one is held by nobody; replacing a live one means
  disposing sessions a card may be part-way through a query on, and that case keeps the rule
  below. Otherwise the card cannot answer the new way until Any surface that installs a capability has to say both halves.
  `--models install` ends with exactly that sentence and the README carries it; the settings row
  and the first-run screen do not yet, and that is a gap rather than a decision. Saying the whole
  thing is live is the shorter sentence and it is false.
- **Reading inside files is off until somebody asks**, models or no models. Names are
  searchable the second Findra starts, because a name index costs seconds; looking inside
  files walks every drive and can run for hours, so it never begins on its own. `--content on`
  starts it, `--content off` stops it without discarding anything already read, and the setting
  survives a restart. An index nobody has asked for and a finished index have identical counts,
  so every surface says which one it is looking at rather than printing "up to date".
- **What Findra skips is what somebody asked it to skip, and nothing else.** `QueueFeeder.Eligible`
  is a content kind and the exclusion list; there is no second rule. There WAS one: any folder
  holding a `.git` was discovered by walking the disk for the marker and its contents were never
  read, on the reasoning that a checkout is mostly other people's files. It is a defensible guess
  and it was wrong about the first machine it met - 21 roots, all of them that person's own work,
  with the pictures they were searching for inside. It was also unseeable: nothing in the interface
  named it, and the only folder control there ADDS refusals, so it could be neither found nor
  overruled. **An "always read" list is the wrong fix** and was written and thrown away: a second
  list whose only job is arguing with a rule nobody asked for. What a checkout really buries an
  index with is already in `FileKinds.DefaultExclusions` - `node_modules`, `.git`, `bin`, `obj`,
  `packages`, `site-packages` - where each is a line the person it belongs to can read and delete.
- **A migration that changes WHICH FILES are eligible sets `ReWalk`.** `RequeueKinds` moves rows
  that exist; a file that was never offered to the queue has no row to move, and nothing else will
  reach it - the journal reports what changes, and a folder of finished work never changes again.
  `ReWalk` forgets every volume's journal position, which `JournalTail.ResumeFrom` reads as a full
  pass owed. It is opt-in per step because re-walking a finished disk for a change that only
  affects rows already held is the expensive mistake spec §2a names.
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
- **The first-run screen prices what is STILL TO FETCH, not what a capability costs.**
  `FirstRunState.OnDisk` carries the model files already present, by file and not by capability,
  and the rows, the preset tiles, the summary and the button's own label all read it through
  `FirstRun.NotHereYet`. Without it the screen quoted a download that was never going to happen:
  an uninstall keeps the models unless the purge box is ticked, so a reinstall met a full folder,
  was offered the whole 3.7 GB, and filled every bar the instant it was pressed. A capability whose own
  files are all present reads `installed` - the same word Settings uses for the same fact - and a
  half-present one is priced at the half that is missing, because a resumed download and Speech
  over an existing e5 pair are ordinary states rather than edge cases. **The number still never
  moves when a row is ticked**, which is the rule the own-files pricing was written under: what is
  on the disk does not depend on what is chosen.
- **`FirstRun.PresetChoice` drops Hebrew where its row is not drawn**, and the tiles go through it.
  `AlreadyChosen` had always dropped it for this reason and the three preset tiles had not, so
  "Everything" on a machine that reads no Hebrew selected a capability with nothing on screen to
  name it: the visible rows added to 2.19 GB, the tile and the bottom line said 3.7 GB, and the
  download drew a fourth progress bar for a row that did not exist. A selection holding a
  capability with no row prices a download nobody can see - and then fetches it.
- **`FirstRun.AlreadyChosen` ticks what is already there**, closed over the dependency graph. An
  unticked row beside a model that is present was a control whose two positions meant the same
  thing: the selection decides what is FETCHED and nothing else, and what Findra can read is read
  from the files. Closed rather than a bare presence test, so a machine holding Whisper but not the
  e5 pair opens with Speech ticked, its dependency ticked with it, and 1.04 GB priced - which is
  the truth. Hebrew is dropped where it is not offered: its row is not drawn there, and a selection
  holding a capability with no row prices a download nobody can see.
- **`CapabilitySet` carries the FILES it was built from, and every price is read from those.**
  A capability is all-or-nothing, so a folder holding whisper-turbo with no e5 pair beside it has
  Speech uninstalled and 550 MB counting for nothing: Settings, the card's offer, `--models` and
  `--searchmodels` all quoted the closed set's total for a Meaning-only download while
  `--models install`, which prices
  by file, said 270. That folder is ordinary - a download run carries on past a file that failed,
  so one bad leg of a Speech install leaves exactly it. A set built by hand with no files derives
  them from its capabilities, which keeps the old arithmetic where it was right.
- **A guard conditioned on the server's length needs a floor that does not need it, and the FLOOR
  is not `MinBytes` alone.** `MinBytes` is a generous "this cannot be the file" line, so with no
  `Content-Length` there was a window up to 124 MB wide where a truncated file passed it, was
  promoted under its real name, and then read as installed while failing everything that needed
  it. Where the length is absent, `Model.Bytes` less `ModelStore.SizeSlack` decides.
- **The Hebrew fine-tune is opened in a try of ITS OWN.** It is a second pass over what the general
  model called Hebrew, and it was opened inside the general model's attempt: one corrupt file threw
  for every recording on the disk, each was recorded `StateFailed`, and nothing re-queues a
  failure. A 1.5 GB file for one language took speech search away from every other language.
  `Semantic.Open` is the same shape one level up - one try per encoder, or a broken e5 file stops
  photo search loading.
- **SigLIP-2's score is not a cosine, and the floor has to be argued in the model's own units.**
  It is `sigmoid(exp(logit_scale) * cos + logit_bias)`, and those scalars are LEARNED parameters
  living on the combined model - so splitting the export into a vision file and a text file, which
  Findra must do, leaves the calibration in neither, with nothing to warn you. `ModelStore`
  records the pair for the checkpoint it ships (112.90, -16.7718), read from the checkpoint's own
  safetensors rather than quoted, and `Siglip2Probability` exists ONLY so a threshold can be
  reasoned about: the sigmoid is monotone in the cosine and can never reorder anything.
  `PhotoFloor` was 0.05, which is p=0.000015 - it rejected nothing. Measured on a real 3,097
  picture library, real matches are 0.130-0.132 and unrelated images 0.030-0.066, with the band
  between empty; the floor is 0.09 and the span moved to 0.06 with it, because a span reaching
  0.15 above a risen floor squeezes every real match into the bottom third of the scale. The
  measurement was taken over icons and screenshots, which are out of distribution for a model
  trained on photographs - the case matters on a desktop, but photographs need measuring
  separately before the same numbers are assumed.
- **A file's size on disk never equals the declared size in the table.** That table is the
  spec's figure in megabytes to one decimal place; real files miss it by tens of kilobytes,
  mostly upward. `ModelStore.SizeSlack` is the only place that width is decided, and nothing
  may compare a file's length to `Model.Bytes` for equality.

## Hardware portability

Findra ships on winget and lands on machines nobody chose for it - AMD or Intel CPUs,
NVIDIA / AMD / Intel GPUs, integrated or discrete, or no usable accelerator at all.
**No capability may require a particular vendor**, and nothing may fail because of the
silicon it found.

**Everything below has been run on exactly one configuration: an x64 AMD CPU with a discrete
NVIDIA card, plus the processor-only path on that same machine.** No AMD GPU and no Intel GPU
has ever run any of it, integrated or discrete, and neither has arm64. Every measurement in
this file carries that limit; say so wherever a number or a claim about hardware is written
down, in the README and on the site as well as here. The vendor-neutral chains are a design
decision meant to make those machines work - they are not evidence that they do, and the two
must not be reported as if they were the same thing. It is worth being blunt about the
direction of the risk: the failures this code guards against were reported on an AMD 780M, so
the hardware most likely to break is precisely the hardware never tested.

- **A provider that LOADS has not been shown to WORK.** The accelerated speech rung was accepted
  the moment `WhisperFactory.FromPath` returned, and the known integrated-GPU failures happen after
  that: Vulkan initialises, the shaders compile, the device registers, and the transcript comes
  back garbled with nonsense timestamps (whisper.cpp #2596, an AMD 780M on Windows 11 - the
  integrated GPU in a generation of Ryzen laptops). Findra would embed that through e5 and store it
  as a finished transcript, and nothing re-reads a file that succeeded. `Media.ProveItTranscribes`
  runs a second of generated tone through the factory before the rung is accepted, and
  `WhatIsWrongWith` judges the SHAPE - finite, ordered, in-range timestamps and no control
  characters - never the words, because a tone has no correct transcript and requiring one would
  fail working machines. The failure direction is chosen: a false rejection costs speed, a false
  acceptance writes nonsense into somebody's index for ever.
- **Detect at runtime, never assume.** Try providers in order, take the first that
  initialises: ONNX (SigLIP-2, e5) is **DirectML → CPU**; Whisper is **Vulkan → CPU**.
  Both cover NVIDIA, AMD and Intel in one path.
- **No vendor-locked providers.** CUDA means NVIDIA only plus a large separate runtime;
  ROCm is not a Windows story. A portable path everywhere beats a fast path for a third
  of users.
- **CPU is a supported configuration, not a failure state.** Only the initial content
  index is slower - queries embed one short string. The UI says so rather than looking stuck.
- **No CPU-feature assumptions** - no AVX-512 requirement, no vendor-specific intrinsics.
- **The picture model is checked on the PROCESSOR even on a machine with an accelerator.**
  "CPU is a supported configuration, not a failure state" was a promise nothing verified, and the
  way it breaks is not subtle. Measured on this machine, one image through SigLIP-2's vision tower:

  | | fp32 (shipped) | fp16 |
  |---|---|---|
  | CPU | 40.3 ms | throws at `ORT_ENABLE_ALL`, loads below it |
  | DirectML | 8.7 ms | 7.5 ms |

  The fp16 export of the same checkpoint throws inside ONNX Runtime's own graph optimiser on the
  CPU provider - `SimplifiedLayerNormFusion` against an inserted precision-free cast - while running
  perfectly on DirectML. **Two separate research passes recommended swapping to it** - it halves a
  354.8 MB download and is a URL change - and it would have taken photo indexing away from every
  machine with no usable GPU, reporting the capability as installed the whole time. Only running
  it found that. `--searchmodels` opens the vision tower on the processor as well and says so,
  which is the one place anybody looks before filing that report.

  **"Will not load at all" was this file's own overstatement and is corrected here.** It will not
  load with every fusion enabled; at `ORT_ENABLE_EXTENDED` and below the same file loads and runs.
  Confirmed twice - e5's fp16 export fails with the byte-identical error and then loads once the
  optimiser is turned down. A per-provider artifact split was called MANDATORY design work on the
  strength of the stronger claim, and that conclusion goes with it: measurement since says one
  full-precision file agrees on both providers, loads on both, and needs no per-machine reasoning
  at all. See `docs/superpowers/specs/2026-09-05-e5-full-precision-design.md`.

  Full precision is also what makes the two providers agree. fp32 against fp16 is a cosine of
  0.99999 for the same file, but a QUANTISED file against itself across providers is 0.970 - which
  is the finding that moved the meaning model to fp32 and is written up beside the capability
  table above.
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
`assets/icon/findra.ico`, the plated and unplated SVGs, the installer's wizard image,
`website/public/favicon.svg` and the two share images. Hand-editing an output is how one logo
quietly becomes two, so `IconTests` decodes the shipped bytes and holds every copy to the others -
the site header's data URI included, which is the fifth and the easiest to forget, and the share
card, which is the sixth and the least looked at.

- **It needs node, and nothing in the build does.** `dotnet build`, `Publish.ps1`, the installer
  and all three workflows read what it produced and do not know it exists.
- **A binary `.ico` in the tree is a deliberate exception** to Findra drawing its own icons. A
  Win32 application icon is a PE resource: the linker needs a real file at build time and there is
  no runtime hook that could paint one. The tray icon, which CAN be drawn at runtime, still is.
- **The small sizes are drawn, not shrunk.** `HINTS` thickens the handle and drops the slot
  entirely at 16 px, and 20 and 40 are in the set because that is what Windows picks at 125% and
  150% scaling - without them it downscales 32 and 48 and throws the hinting away. `IconTests`
  asserts the 16 px slot is ABSENT, so losing a hint fails rather than passing quietly.
- **The share card sets type, so `Make-Icon.mjs` reads `Quicksand-Regular.ttf` itself.** Outlines
  only - character map, advance widths and the quadratic contours - flattened to polylines and
  rasterised through the same signed distance the handle already uses, which is also what makes
  emboldening a subtraction. There is no second font file: only Regular ships, `Parts.Face`
  resolves that one file, and the application's own bold is `SKFont.Embolden` on it, so the card
  fakes bold by the same means rather than dragging a second licence along.
- **The card is 1200x630 and the square is 1080x1080, and both are opaque.** 1.91:1 is what X and
  Facebook crop to; the screenshot that used to be `og:image` was 1.31:1 and had a slice taken out
  of its middle. A share image is composited onto a ground the client chose, so a transparent pixel
  is black on Discord and white on X. The square exists only to be posted by hand - **Instagram
  reads no Open Graph at all** and renders no link preview anywhere, so nothing serves it.
- **The tray has no plate and its slot is a real hole.** Windows composites a tray icon onto a
  taskbar whose colour it chose: a plate is a dark square nobody asked for, and a slot filled with
  Findra's own ground looks right on a dark taskbar and like a smudge on a light one. Both are
  asserted, on three palettes, through a `Render` seam that exists because a `WindowIcon` needs a
  running Avalonia and the alternative was a test that reads its own source.

## The website

`website/public` is what Netlify serves, exactly as committed. The front page is written by hand;
the three written pages are generated, and that generator is **run by hand and by nothing else**.
No workflow runs it, so the failure this has to survive is somebody editing the Markdown, not
running it, and shipping a page that still says the old thing.

- **`/code-signing/` exists because somebody else requires it, and that shapes it.** The SignPath
  Foundation's terms say to use the term "Code signing policy" on the project's home page and on
  its download/release pages, as a section header or a link to a dedicated page - so the front
  page carries that exact phrase twice, once in the footer and once in the install section beside
  the sentence saying Findra is not signed. `WebsiteTests` asserts both, and asserts the count,
  because `index.html` is hand-written while every other footer comes out of `Make-Pages.mjs`. The
  page is generated from `docs/code-signing-policy.md`, which `PolicyPageTests` already holds to
  its promises - including the coupling that keeps its "Not yet in force" note and the release
  workflow's empty signing step in step. A second copy as HTML would be a second policy no test
  reads. **The application cannot be made until a release exists**: their terms require the project
  to be "already released in the form that should be signed", and the Reputation field is not a
  formality.
- **The generator's Markdown vocabulary is the union of what its sources use, and no more.** An
  unsupported construction does not fail - it renders as prose with its own marker still in it.
  The block quote arrived with the signing policy and would otherwise have put a literal `>` on
  every line of the one paragraph a reader sees first. `WebsiteTests.Strip` learns each new
  construction at the same time, or the prose comparison fails on a marker rather than on a
  sentence.
- **`PRIVACY.md` is the privacy policy and the page is emitted from it.** A second copy written out
  as HTML would be a second policy, and the only thing worse than no privacy page is two that
  disagree. `build/Make-Pages.mjs` generates `/privacy/` from it and copies the file verbatim to
  `/privacy.md`; `WebsiteTests` strips both back to prose and fails when a sentence exists in one
  and not the other, **in both directions**. It compares the SHIPPED bytes rather than reading the
  generator, because a test that read the generator would pass on the day somebody forgot to run
  it, which is the only day it matters.
- **The page's headline is not the Markdown's H1.** `# Privacy` is right for somebody who opened
  the file looking for the policy; "Nothing leaves your machine, except one request" is right for
  somebody deciding whether to trust the product. The generator drops the H1 and the tests exclude
  it on both sides.
- **The reading column sits on the ground, not in a `.panel`.** A privacy policy that looks like a
  marketing card reads as marketing, and being believed is the only job that page has.
- **There is no contact form, no telephone number and no postal address, and the site says so.**
  Findra collects nothing about the people who use it and a form would be the first thing here that
  did; a switchboard nobody answers would be the only dishonest thing on the site. This looks like
  a gap to a readiness checker, which scores an Organization node higher for carrying them, so it
  is written down as a test that refuses rather than left to somebody's judgement later.
- **Nothing but a real edge function goes in `netlify/edge-functions/`.** Netlify deploys every
  top-level file there AS one, and one with no default export fails the whole build - and a failed
  build does not break the site, it just never replaces it, so the previous commit goes on being
  served and nothing anywhere says the push did not land. A node test placed beside the function it
  tested did exactly that. `WebsiteTests` now reads every deployable file in that folder for a
  default export; the node test lives in `tests/edge/`.
- **`netlify/edge-functions/markdown.ts` is the one thing on the site that is not a file.** Accept
  negotiation cannot be done with a header rule or a redirect, because neither reads the header.
  Two things about it are load-bearing: `Vary: Accept` goes on **both** branches, or a cache serves
  whichever variant it saw first to everybody after; and the q values are compared rather than
  matched, because a browser sends `*/*;q=0.8`, which matches text/markdown, so "does anything
  match" hands raw Markdown to every human visitor. Its table is a node test, run in CI, and it is
  the only reason node appears in any workflow - nothing that builds or ships an artefact needs it.
- **`llms.txt` says what Findra is NOT.** It has no API, no accounts, no server and no MCP server.
  A readiness scan of this domain credited the hosting platform's own MCP server, CLI, SDKs and
  OAuth endpoints to Findra and scored the product on them; saying plainly that there is nothing to
  call is what stops the next one.
- **The 404 page is ours and it is a map.** Whoever is reading a 404 either mistyped or guessed a
  URL, so it lists every real one rather than apologising. Netlify's default page did neither.
- **Netlify injects a `hosting-provider` meta tag and a `netlify.new` referral comment into every
  HTML response, and no file in this repository can stop it.** It is added after the file leaves
  the repository, on the front page and the 404 alike, and it is on the current plan rather than
  being anything the site asked for. `WebsiteTests` therefore asserts what it can actually
  guarantee - that OUR sources carry no such link - and says so rather than implying the live page
  is clean. Removing it needs a plan change or a platform setting, and is not a code change.

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
they are not. **Both sides are checked for parsing, not just the tag.** `Compare` answers 0 when
EITHER side fails, and 0 was routed into "up to date": the release tag was guarded and the running
version was not, so a build whose own version cannot be read reported itself current on no
information at all.

**An uninstall clears `InstallSource` along with `FirstRunDone`.** It is recorded once because how
a COPY arrived cannot change - but an uninstall ends that copy, and the next may arrive by another
route. Kept, it told somebody who built from source once and then installed from winget to read
the release notes for ever instead of running `winget upgrade`, and Settings reported the wrong
source under About.

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

**But it always clears `FirstRunDone` in the config it keeps.** That flag means "the welcome
screen has been answered on this installation", and an uninstall ends an installation. It is not
bookkeeping: the screen's whole product is a state on the machine - the `HighestAvailable` task -
and the uninstall has just removed it, while nothing on an ordinary launch registers one.
`HelperTask.EnsureRunning` only *runs* a task that exists. So a reinstall over a kept config used
to start with the screen skipped and the task gone, which is not half a feature but half the
product: name search answers nothing because the names live in the helper, and the content queue
is fed from the USN journal through that same helper, so the feeder times out, the queue stays
empty, and "Start now" starts an indexer that drains nothing and idles in a tenth of a second.
Three complaints that look unrelated, one missing task. `Uninstall.Run` takes it as a seam like
its other effects, and it runs on every route including `--quiet`, which is the installer's.

**The uninstall itself is run from `CurUninstallStepChanged`, never from `[UninstallRun]`.**
Inno's install order says "The entries in `[UninstallRun]` are stored in the uninstall log" -
during the INSTALL, which is when their `Check` parameters are evaluated. The purge run was
conditioned on the checkbox, `Purge` is False at install time and cannot be anything else, so
that entry was never written into `unins000.dat` and no answer given weeks later could reach it.
The checkbox was drawn, ticked, read into `Purge`, and decided nothing; `PolicyPageTests`
asserted the two entries and their `Check` names and passed the whole time, because it read the
shape and the defect was in the timing. **A decision taken during the uninstall cannot be carried
by anything the installer wrote down.**

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
