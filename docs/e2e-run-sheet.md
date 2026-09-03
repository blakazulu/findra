# End-to-end run sheet

`docs/end-to-end-checklist.md` is the catalogue: every item that needs a UAC prompt, a screen, a
real disk, a sign-out or a public tag, written down as each plan discovered it. It is ordered by
where each item came from, which means it cannot be worked through top to bottom. Item 1 wants the
helper already running, item 24 wants a machine that has never run Findra, and item 33 destroys the
state items 20 to 22 need.

This is the same items in the order somebody can actually sit down and work through, in one
session, on one machine. Nothing here replaces the catalogue: every step names the catalogue item
it is, and the reasoning in the catalogue is carried over rather than summarised away, because
several of those paragraphs exist to describe a defect that actually happened.

Run `pwsh -File build/Check-E2E.ps1 -Exe <path to findra.exe>` at the start of every phase. It
does every check that can be automated and prints a pass/fail table, so what is left below is the
part that genuinely needs eyes, elevation or a real disk. It reads and reports and never changes
anything.

## Markers

| Marker | Means |
|---|---|
| **admin** | an elevated terminal, or a UAC prompt somebody has to answer |
| **sign-out** | a real Windows sign-out and sign-in; nothing else shows it |
| **eye** | nothing to run, or nothing a run can prove; a person looks |
| **destructive** | changes or removes state on this machine |
| **one way** | cannot be undone: a public tag, a winget pull request, a purge |

Phases 0 to 7 are safe to repeat. **Phase 8 destroys everything phases 2 to 6 built, and phase 9
contains the two steps nobody can take back.** Work down, never up.

## Before anything

Tail the log in a spare window. Every step's proof appears there as it happens:

    Get-Content $env:LOCALAPPDATA\Findra\logs\findra-$(Get-Date -f yyyyMMdd).log -Wait -Tail 20

---

# Phase 0 - Prep

Nine items. Nothing here changes Findra's behaviour; it records what "before" looked like so a
later step has something to disagree with.

### 0.1 Copy the models aside if you want to keep them (destructive later)

`%LOCALAPPDATA%\Findra\models\` currently holds about 900 MB: the three SigLIP-2 files and the
two e5 files. Both Whisper models are absent. **Phase 8 deletes that folder** (step 8.3, catalogue
49, `--uninstall --purge`).

    robocopy "$env:LOCALAPPDATA\Findra\models" "D:\findra-models-backup" /E

**Pass:** five files copied. **Skip only if** you are willing to re-download 900 MB. Say which you
chose out loud before phase 8 starts, because that is the point of no return for these files.

### 0.2 Record the baseline

Run the automated check and keep its output. Everything later compares against it.

    pwsh -File build/Check-E2E.ps1 -Exe publish/win-x64/findra.exe > baseline.txt

**Pass:** the table prints, and the "not set up yet" rows are: no scheduled task, no autostart
entry, no install folder. Those three are the correct answer today and the script says so rather
than failing.

**A failure here means** something is wrong before the session has started, and the run sheet
should not be begun until it is understood.

### 0.3 Record what the diagnostics say now

    publish\win-x64\findra.exe --version
    publish\win-x64\findra.exe --searchmodels
    publish\win-x64\findra.exe --searchindex
    publish\win-x64\findra.exe --content
    publish\win-x64\findra.exe --uninstall --dry-run

**Pass:** `--version` prints `findra 0.1.0` and the log folder. `--searchmodels` lists five files
present and two absent, and names DirectML as the chosen ONNX provider. `--searchindex` reports
schema 1, 10 documents indexed, no volume position recorded, and the indexer not running.
`--content` reports the transcription limit at 5 minutes.

**Note the disagreement**: `--content` says "index up to date" and `--searchindex` says "inside
files is off". That is catalogue item 27 and it is expected here, because the interface has never
been launched on this machine to write the `index:paused` row. Step 4.1 is where it must stop
being true.

### 0.4 (catalogue 46) The measured sizes are the real ones - eye

Compare the four numbers from `--uninstall --dry-run` against what Explorer reports for
`%LOCALAPPDATA%\Findra\models`, `\index`, `\logs` and `%APPDATA%\Findra`.

**Pass:** the four agree to the megabyte the report rounds to.

**A failure means** every surface in the product that quotes a size to somebody about to delete
something is quoting a wrong number, and there is only one measurement behind all of them
(`Uninstall.Measure`). Re-run it in phase 8 against the installed build, where the numbers are the
ones a person sees in the uninstall prompt.

### 0.5 Rename the config so the first-run screen appears

`%APPDATA%\Findra\config.json` exists on this machine, so first run will not happen until it is
moved. Move it rather than deleting it.

    Move-Item "$env:APPDATA\Findra\config.json" "$env:APPDATA\Findra\config.json.before-e2e"

**Pass:** `Test-Path "$env:APPDATA\Findra\config.json"` is `False`.

### 0.6 Note the stale `ui.json`

`%LOCALAPPDATA%\Findra\ui.json` is present and no Findra is running. It is written by a live
interface and removed on a clean quit, so a stale one is the record of a run that was killed.
Delete it now so that step 6.8's "`ui.json` is gone" means something.

### 0.7 Confirm no scheduled task exists

    schtasks /query /tn "Findra names helper"

**Pass:** it finds nothing. **This is a precondition, not a failure.** Catalogue item 24 says
explicitly: do NOT start the helper by hand first, because doing exactly that is what hid a bug
where `HelperTask.Register` had no callers at all, and Findra asked the scheduler to start a task
nothing had ever created. On a clean machine name search would never have worked. The whole value
of phase 2 depends on the task not existing when phase 2 starts.

### 0.8 Confirm no autostart entry

    Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name Findra

**Pass:** it errors with "property Findra does not exist". Expected today.

### 0.9 Have the two installers to hand

`installer/Output/findra-0.1.0-x64.exe` (82 MB) and `findra-0.1.0-arm64.exe` (79 MB) were compiled
on this machine. Use the one matching the architecture of the machine under test.

---

# Phase 1 - Install

Six items. **admin** throughout: the installer sets `PrivilegesRequired=admin`.

### 1.1 (catalogue 32) The installer script compiles - DONE

Already done, on this machine, with Inno Setup 6 at
`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`:

    pwsh -File build/Publish.ps1 -Rid win-x64
    iscc /DAppVersion=0.1.0 /DPublishDir=..\publish\win-x64 installer\findra.iss

Both architectures produced an installer. The compile found a real defect on the way
(`CreateCustomForm` called with the wrong number of arguments), which is exactly the class of
thing text assertions on the `.iss` cannot see. The compile on a *runner* is a different question
and is step 9.4.

### 1.2 (catalogue 33, first half) A real install - admin, destructive

Run `installer\Output\findra-0.1.0-x64.exe`. **Do not tick "Start Findra" yet** - step 1.4 is
where that tick is the thing being checked.

**Pass:**
- the wizard shows the licence page;
- it installs into `C:\Program Files\Findra` with **no version anywhere in the path**;
- the final page offers "Start Findra".

**A failure on the path means** the scheduled task, which stores an absolute path to `findra.exe`,
will point at a directory that ceases to exist at the next upgrade. That is why `AppId` is a fixed
GUID and `DefaultDirName` carries no version.

### 1.3 (catalogue 33, last paragraph) The licences travelled - eye

Look in `C:\Program Files\Findra`:

**Pass:** `LICENSE.txt`, `NOTICE.txt` and `OFL-Quicksand.txt` are all there beside `findra.exe`,
and `installed-by.txt` reads `installer`.

**A failure means** a build whose csproj stopped copying them installs without them and nothing
warns. Apache-2.0 section 4(d) makes the NOTICE travel with every distribution, the installer's
`[Files]` entry copies the publish folder and nothing else, and `LicenseFile=` only *displays* the
licence in the wizard - it puts no copy on the disk. A NOTICE that stays in the repository gives up
the whole reason Apache was chosen over MIT. OFL condition 2 says the same about the font licence.

Also run `pwsh -File build/Check-E2E.ps1 -Exe "C:\Program Files\Findra\findra.exe"` now. The
install-folder rows should all turn green.

### 1.4 (catalogue 50, launches 1 to 3) No black window - eye

Findra is a windows-subsystem binary (`OutputType` is `WinExe`). A console-subsystem binary is
given a console window every time it starts without a terminal, and Findra has five such launches.
Three of them are reachable now:

1. tick **"Start Findra"** on the installer's last page;
2. the **Start-menu shortcut**;
3. an **Explorer double-click** on `C:\Program Files\Findra\findra.exe`.

**Pass:** no console window on any of the three, and **nothing flashes**. A console that appears
and closes is still a console.

**A failure means** `OutputType` has gone back to `Exe`, or something in the launch path is
spawning a console-subsystem child. `ProjectFileTests.TheApplicationIsAWindowsSubsystemBinaryAndNotAConsoleOne`
asserts the project file, and `Check-E2E.ps1` reads subsystem 2 out of the built PE header, but
neither can see the window; only these three launches can. The remaining two launches are the two
sign-in ones, and they are step 6.10.

Launch 1 hands you straight into the first-run screen, which is phase 2. Note that the *installer*
did not create the scheduled task and was never meant to - creating it is Findra's own job, from a
consent moment rather than an elevation prompt at every launch, and that is what phase 2 checks.

### 1.5 (catalogue 51) The diagnostics still print when a person runs them

In a real terminal, from `C:\Program Files\Findra`, each on its own:

    .\findra.exe --version
    .\findra.exe --searchmodels
    .\findra.exe --searchprob

**Pass:** every one puts its text in that terminal. `--searchmodels` shows the Hebrew probe line
(`'שקיעה מעל הים'`) and the card's middle dot as themselves, not as replacement characters. The
mistyped one prints the list of real modes and exits 1.

Then run each again with output redirected to a file:

    .\findra.exe --searchmodels > out.txt

**Pass:** the file holds the text and the terminal holds none of it.

**Expect the prompt to come back before the text does.** A shell does not wait for a
windows-subsystem process. That is the one visible cost of step 1.4 and confirming it is only
cosmetic is part of this step.

**A failure on the redirect means** `ParentConsole` is attaching before it reads the standard
handles, so a redirected run writes to the window and hands its caller nothing - which would fail
every headless check at once, because `build/Check-Diagnostics.ps1` pipes every mode it runs.
**A failure on the Hebrew means** `Borrow()` and `UseUtf8OnTheConsole()` have been reordered: a
process that has not joined a console has no code page to set, the exception is caught, and every
diagnostic prints replacement characters from then on.

### 1.6 Re-run the automated check against the installed build

    pwsh -File build/Check-E2E.ps1 -Exe "C:\Program Files\Findra\findra.exe"

**Pass:** the install-folder, licence-file and PE-subsystem rows are green. The scheduled-task and
autostart rows still say "not set up yet".

---

# Phase 2 - First run

Six items, in an order that is not the catalogue's, for one reason: **the refusal is checked
first**. Catalogue item 42 asks that refusing the administrator prompt be survivable and
recoverable, and that only means anything on a machine where the task does not yet exist. Run item
29 first and the task is registered, after which a refusal changes nothing and 42 proves nothing.

Each pass needs the config renamed again:

    Move-Item "$env:APPDATA\Findra\config.json" "$env:APPDATA\Findra\config.json.pass2" -Force

### 2.1 (catalogue 42) Refusing the one prompt is survivable - admin, eye

With no `config.json`, launch Findra. Work the first-run screen to the end and answer **No** to
the UAC prompt it raises.

**Pass:**
- Findra keeps running: the capsule is on the desktop and the tray icon is there;
- the log says the helper is **not registered**, in those words, rather than going quiet;
- `findra --searchprobe` reports `helper task registered : NO`.

**A failure means** the one thing the specification says Findra cannot fix on a stranger's machine
is also invisible when it happens. Scheduled-task registration is that thing; what is being checked
here is that it is visible and has a way back.

### 2.2 (catalogue 42 second half, 27a fifth bullet, 24) The way back - admin

Open Settings from the tray, go to **Opening it**, press **Register it**, and answer **Yes**.

**Pass:**
- the row turns to "registered";
- `schtasks /query /tn "Findra names helper"` finds the task;
- **name search starts working in this session**, with no sign-out;
- `findra --searchprobe sunset` prints `pipe : ok`, a helper process id different from its own, a
  name count and a generation counter.

**A failure on the last two means** registering and starting have been split apart: registering
without starting leaves name search dead for the whole of somebody's first session, and only a real
machine can show it does not. **A failure on the task query means** catalogue item 24 has caught
the bug it was written for - `HelperTask.Register` having no callers, so Findra asks the scheduler
to start a task nothing has ever created.

### 2.3 (catalogue 29) The first screen, answered by a person - admin, eye

Rename the config again and launch. Nothing about a window can be checked headlessly;
`--searchshot firstrun`, `--searchshot firstrunspeech` and `--searchshot firstrundownloading` draw
the three surfaces and nothing else.

**Pass:**
- the screen appears **before** the capsule and the tray, and reads as the same object as the
  settings window: same width, same edge, same pills;
- the three preset tiles light **one at a time**, and touching any row moves the choice to none of
  them;
- ticking Speech ticks the document models under it; unticking Speech takes Hebrew with it;
- ticking Speech puts the transcription limit under it and unticking Speech takes it away; the
  five pills answer, the rows below move down rather than being drawn over, and the number chosen
  here is the one `--content` reports afterwards;
- every row's size stays the same number when it is ticked, and the four of them add up to the
  2.93 GB the Everything tile quotes;
- the Hebrew row is on the screen only on a machine with Hebrew installed;
- press **"Not now"**: **one** administrator prompt, the screen goes, and it does **not** come back
  at the next launch;
- the log carries both `names helper task registered` **and** `the names helper is answering`, and
  name search works in that same session with no sign-out.

The last bullet is the half that has no test at any level.

**A failure on "one prompt" means** Findra is asking for elevation more than once, which is the
thing the consent-moment design exists to avoid. **A failure on "does not come back" means** the
config was not written, and every launch will show the first-run screen forever.

### 2.4 (catalogue 30, 16, 17, 18) The second act, with the network taken away - destructive

Rename the config again, choose **Recommended**, press **"Get these"**.

On this machine the subject of the download test must be **Speech** (547 MB), not a preset that
re-fetches what is already there: five of the seven model files are on disk and are being kept.
Speech is the one capability with no bytes present, so it is the only one whose progress bar starts
empty and whose resume is a real resume.

**Pass:**
- **one progress bar per capability**, not one for the whole download, and a capability whose files
  were already on disk starts **full** rather than empty (Photos and Meaning will do this);
- pull the network cable part way through Speech. The screen must **say what went wrong and that
  what arrived was kept** - not stop silently - and the log must carry the same;
- plug it back in and launch again: the download **resumes from the bytes already fetched**, not
  from zero;
- close the window mid-download: Findra is still in the tray and still fetching;
- when a capability lands, the log says **how many files were queued** for what Findra can now read;
- `--searchmodels` afterwards reports the file sizes on disk matching what the interface claimed,
  and nothing that was already present was fetched twice.

**A failure on resume means** re-downloading up to 2.9 GB because an upgrade did not look first,
which the specification calls the worst thing this product could do to somebody.
**A failure where a truncated file is reported as installed means** `ModelDownloader`'s floor check
has gone: a response carrying no `Content-Length` gives a total of zero, the length comparison is
skipped, and the short file is promoted under its real name, after which every capability needing
it fails quietly while Findra reports it installed. The floor (`Model.MinBytes`) is the guard that
holds on what is on the disk rather than on what the other end chose to send.
**A failure on the "queued N file(s)" line means** the re-queue is not running on the flow that owns
the index's writer, which is what makes an installed capability start finding things without a
restart.

### 2.5 (catalogue 19) The accelerator is real

    findra --searchmodels

**Pass:** it names the provider it chose and every one it rejected, with a reason. On this machine
the ONNX pair (SigLIP-2, e5) should choose **DirectML** rather than the processor - the phase 0
baseline already shows that. Now that Whisper is on disk, the Whisper line must stop saying "not
tried - no model is on disk to open" and name **Vulkan** or, with a reason, CPU.

**A failure means** "it's slow on my laptop" stays unanswerable. "DirectML failed to initialise,
fell back to CPU" is a bug report; silence is not. CPU is a supported configuration, not a failure
state - what is not acceptable is not saying which one happened.

### 2.6 Restore the config you want to keep

Decide which of the `config.json.passN` files is the one to carry into the rest of the session and
put it back as `config.json`, or let the last first run's own config stand.

---

# Phase 3 - Names

Four items. The helper is registered and answering from phase 2.

### 3.1 (catalogue 1) The elevated helper answers

    findra --searchprobe sunset

**Pass:** `pipe : ok`, a helper process id **different from its own**, a name count, and a
generation counter - instead of the unreachable message it prints on a machine with no helper.

**A failure means** either the task is not running the helper, or the pipe name has drifted between
the two processes. The probe distinguishes those: it reports the task state and the pipe state
separately, on purpose, so a locked-down machine does not look identical to a fresh one.

### 3.2 (catalogue 2) The interface starts

    findra

**Pass:** four log lines in order - the palette and mode, the hotkey combination that registered,
the capsule's position, the tray icon. A capsule is visible on the desktop.

**A failure on the hotkey line means** it registered nothing and said nothing. Registration can
legitimately fail (`Alt+Space` is the system menu chord in some configurations); the rule is that
Findra walks a fallback chain, takes the first that registers, and **tells you which one it landed
on**. Never silently.

### 3.3 (catalogue 3) Real names - eye

Press the hotkey the log named. Type a word you know is on the disk.

**Pass:** rows appear within a keystroke or two, and the line under the field reads a **name count
and the helper's process id**, not "the name helper is not running".

That line is the best proof the pipe answered, because it comes from a different call than the
search does.

### 3.4 (catalogue 4) The generation counter under hammering - eye

Press Ctrl+1, Ctrl+2 and Ctrl+3 quickly, ten times, then type more.

**Pass:** rows re-sort every time and the searching indicator always comes back down.

**A failure looks like** stale rows with the indicator still spinning. That is a slow answer to an
abandoned query arriving late and overwriting a newer result - exactly what the generation counter
stamped on every reply exists to prevent. Name search is a pipe round trip, not an in-RAM
`IndexOf`, so this is a real race and not a theoretical one.

---

# Phase 4 - Content

Twelve items. This phase is where the largest amount of never-executed code runs for the first
time: everything below the gate in `Decoders` - photo, audio, video, transcription and the Meaning
branch of a document - is unexercised at runtime until it does.

### 4.1 (catalogue 27) `--content` and `--searchindex` agree

Turn reading inside files on, which is off until somebody asks, models or no models:

    findra --content on

Then run both:

    findra --content
    findra --searchindex

**Pass:** they say the same thing. Before the interface had ever run on this machine they did not:
`--content` read `config.json` and said "index up to date" while `--searchindex` read the index's
own `index:paused` row and said "off". Both were behaving exactly as specified and they diverged
only because nothing had written that row.

**A failure means** the row is not being written and `--searchindex` is describing an index nobody
has told about a setting that changed.

Also confirm the phrasing: an index nobody has asked for and a finished index have identical
counts, so each surface must say **which one** it is looking at rather than printing "up to date"
for both.

### 4.2 (catalogue 9) A real volume enumerates

Watch the first pass on a real disk.

    findra --searchindex

**Pass:** a rising indexed count, a **consumed journal position per volume**, and no failures
beyond unreadable files.

**A failure with no volume position recorded means** step 4.4 cannot pass either, because the
position is the fact a restart resumes from.

### 4.3 (catalogue 10) The journal streams one change

Create a file on C:, then delete it.

**Pass:** the helper's journal line tracks **that one change** rather than the whole disk, and the
file appears in the queue.

**A failure where the whole disk is re-read means** the USN journal position is not being used and
every change costs a full enumeration. Remember the split: the helper watches the journal, and the
UI decides what to enqueue. The parent still decides what is indexed; the parent is just no longer
the process watching.

### 4.4 (catalogue 11) A restart does not re-walk

Quit and relaunch.

**Pass:** the second start resumes from the recorded position rather than walking the disk again.

**This is the property the specification says must never be got wrong.** "Done" is a fact the index
records - schema version, consumed USN position per volume, pending queue - not a guess.
Re-indexing a finished disk because a restart did not look first is in the same class as
re-downloading 2.9 GB.

### 4.5 (catalogue 12) An edited file is re-indexed

Edit a document while Findra is closed, then start it.

**Pass:** the new contents become searchable.

**A failure means** the full pass is not comparing modification times, and every edit made while
Findra was not running is invisible forever. That comparison has never run against a real disk.

### 4.6 (catalogue 13) The indexer child dies with its parent

Kill the interface without a clean quit (Task Manager, End task).

**Pass:** the `findra --index` child disappears.

**It must be killed by the job object rather than by its own polling.** Indexing stopping when the
app quits is by construction, not by lifetime code, and a child that outlives its parent means the
job object is not being applied.

### 4.7 (catalogue 52) A capability installed while Findra is open is read without a restart

With content indexing on and the first pass finished, install photos twice, on two runs, by two
routes:

1. from the settings window;
2. from a terminal, while Findra is running: `findra --models install photos`.

Both times, **without touching Findra afterwards**:

**Pass:**
- `findra --searchindex` shows the photos moving off "no decoder for this kind yet";
- the log carries the one line saying what Findra can read has **changed while the indexer was
  running**.

**A failure means** the child captured a `CapabilitySet` at startup instead of re-reading the disk
through the `Func<CapabilitySet>` it was constructed with. The consequence is precise and quiet:
the child records every file the interface has just queued for that capability as unreadable, for
want of a model sitting on the disk, and **nothing queues them again**. `Decoders.CanRead` calls
`Refresh()` before every file it opens for exactly this reason.

Then restart and search a photo by what is in it. **The card's own half of a capability is loaded
when Findra starts**, which is the only part of this that still needs a restart. Indexing picks a
capability up without one; searching by it does not. `--models install` ends with exactly that
sentence.

### 4.8 (catalogue 31) The same line, from the settings window

Both paths run the same download controller, so installing a capability from settings must produce
the same "queued N file(s)" line, in the same session, as `--models install` does.

**A failure where it appears on one path and not the other means** the two have drifted apart
again.

### 4.9 (catalogue 23) Enabling a capability re-indexes exactly what it covers

This is the promise a plan review rejected a draft over. Turn on a capability **after** the index
is already built.

**Pass:** `--searchindex` shows the files that capability covers re-queued and actually re-read,
and **nothing else disturbed**.

**A failure where nothing is re-queued means** a stamp was taken while the backlog was still
sitting there. `CapabilityGate.StampsIn` withholds a capability's stamp whenever files of its kinds
are skipped for `Decoders.NoModel` and not queued, so `Apply` re-queues exactly those rather than
believing a record that says the debt was paid. Done is a fact the index holds, not a note somebody
left - and the same shape is what makes a machine written off by an older build recoverable.
**A failure where everything is re-queued means** the marginal re-queue has become a full one.

### 4.10 (catalogue 23a) The transcription limit is obeyed, and a long video is still read

With speech and photos both installed and the limit at its five-minute default, index a recording
well over the limit and a video well over it.

**Pass:**
- the recording is **skipped**, with "longer than the transcription limit" as its own reason;
- the video is **indexed** - its frames were read - and carries the same string as a note about
  what was not heard.

**No automated test can see either**, because both need a real model and real media.

### 4.11 (catalogue 23b) A video on a speech-only machine is still opened

This one needs a machine with speech and **without** photos, which this machine is not. To reach it
without discarding anything, move the three SigLIP-2 files out of the models folder, restart
Findra, run the check, then move them back:

    Move-Item "$env:LOCALAPPDATA\Findra\models\siglip2*" "$env:TEMP\siglip-aside\"
    # restart Findra, index a video with talking in it, search a phrase from the sound track
    Move-Item "$env:TEMP\siglip-aside\siglip2*" "$env:LOCALAPPDATA\Findra\models\"

**Pass:** the phrase from the sound track is found.

**A failure means** the gate has been written as a "which capability covers this kind" lookup
rather than as an OR. A video is covered by photos *or* by speech, and a lookup silently drops the
speech-only case, which is the one case this step exists for.

### 4.12 (catalogue 53) The limit raised from a terminal reaches a running Findra

With speech installed, the limit at five minutes, and a recording of half an hour already passed
over, run this **while Findra is running**:

    findra --content limit 60

**Pass:** the recording is transcribed **without a restart**, and its phrase is findable after one.
Then lower the limit again and confirm **no transcript is thrown away** - the reverse costs
nothing.

**A failure means** the limit is being read from the settings file, which a running interface will
not read again. `CapabilityGate.ApplyLimit` writes `index:transcribeminutes` into the index, before
its re-queue, and the child reads that row before each recording it opens - which is the same
delegate-not-snapshot shape as step 4.7.

---

# Phase 5 - Search and the card

Six items, all by eye, all needing the index phase 4 built.

### 5.1 (catalogue 14) Content search returns real answers - eye

Press the Content pill and search for a word **inside** a file rather than in its name.

**Pass:** the excerpt reads sensibly and points at the right part of the document.

### 5.2 (catalogue 15) Hebrew reads correctly - eye

Index a Hebrew document and search a word in it.

**Pass:** the excerpt reads in **logical order**, not reversed.

**A failure means** the excerpt is being built by string slicing that ignores bidirectional text,
and every Hebrew result in the product is unreadable.

### 5.3 (catalogue 20) Photos become searchable by description - eye

Index a folder of photos, then search for **what is in one** rather than its filename.

### 5.4 (catalogue 21) Speech becomes searchable - eye

Index a recording and search a phrase spoken in it. Then a Hebrew recording.

**Pass on the Hebrew one:** the general model runs **first**, for language detection, and only then
the Hebrew one. Hebrew is a second pass, never an alternative - only files turbo calls Hebrew are
re-run through the fine-tune.

### 5.5 (catalogue 22) Text inside images is found - eye

Index a screenshot containing words and search one of them.

### 5.6 (catalogue 26) The preview actually appears - eye

Open the card, search, and select a result that is a photo, a PDF or a video.

**Pass:** a picture appears on the stage rather than the fallback tile.

**This one cannot be checked headlessly at all.** `--searchshot` composes the card with no image
and never runs the asynchronous preview loader, so the renderer is not on that path on any machine.
It was stubbed to return nothing until the framework moved, and this is the first time it can draw.

---

# Phase 6 - The interface by eye

Ten items. Almost nothing here can be run; it is where the judgements get made.

### 6.1 (catalogue 27a) Every control in the settings window, clicked by a person - eye

Open Settings from the tray and work through all five sections. Seven controls reach the operating
system and **no test at any level covers what happens after the click**, because each is a call
into Avalonia, the registry, `schtasks` or the network:

- change the dark palette and watch the window, the capsule **and the tray icon** follow with no
  restart;
- press "Open the file" and confirm `palettes.json` opens in an editor;
- click the hotkey row, press Escape, confirm it stops listening; click it again, press a
  combination, confirm the row shows it and **that the new combination opens the card**;
- tick "Start Findra when I sign in", confirm the `Findra` value appears under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, then untick it and confirm it goes;
- press "Register it" on the name helper - **already done in step 2.2**; the row should read
  "registered" now;
- press "Add a folder", pick one, and confirm it appears in the list and can be removed;
- press a capability's size button and confirm a download starts and the row turns to "installed" -
  **already done in step 4.8**;
- press "Check now" and confirm the About line changes;
- drag the capsule to a far corner, press "Bring the capsule back", and confirm it moves **in this
  session** rather than at the next launch.

**A failure means** a dead control. An earlier draft of this window shipped with five of them.

### 6.2 (catalogue 27a, last line) The capsule's own menu - eye

Right-click the capsule.

**Pass:** the tick is beside the palette **actually on screen**, and the palettes offered are the
ones for the side in use - dark palettes in dark mode, light in light.

### 6.3 (catalogue 28) A hotkey rebind that is refused puts the old one back - eye

Not reachable headlessly: it needs a real window handle and a combination another application
already owns. A running screen-capture or launcher tool is the usual source. Open settings, click
the hotkey row, and press a combination something else has taken.

**Pass:** the row says the combination is taken and that Findra kept the old one, **and the old
combination still opens the card**.

**A failure means** the rebind unregistered the old hotkey and failed to restore it, which leaves
somebody with no hotkey at all and the control that would fix it behind a card the hotkey no longer
opens.

### 6.4 (catalogue 43) Settings by eye, in both modes - eye

Open all five sections in a dark palette, then again in a light one.

**Pass:** nothing overlaps, no note is clipped at the bottom of the pane, no label is cut off inside
its pill, and the pane is the **same height in every section**.

The measured tests say the rows fit. Whether the window reads as the same object as the card is a
judgement, and this is where it is made.

**If a label is tight, shorten the label.** Do not widen the tolerance and do not move the column:
the test that checks a label into its pill and the test that checks a column is no wider than it
needs to be are each other's opposites, and satisfying one by moving the geometry breaks the other.

### 6.5 (catalogue 44) The exclusions list is the only scroller, and it scrolls - eye

With the default entries in place, scroll to the end of the list in Settings > What it searches,
remove the last one.

**Pass:** the list is still usable afterwards, and `config.json` holds an **array** rather than
nothing at all. Then check that **nothing else anywhere in either surface scrolls**.

### 6.6 (catalogue 47) The shipped face is what a person actually sees - eye

On the installed build, open the card and all five settings sections.

**Pass:** they are drawn in **Quicksand**, not the system UI face. `OFL-Quicksand.txt` sits beside
`findra.exe` in the install folder, which is what the licence asks of every copy.

**A failure means** a packaging mistake, and this is the only place it shows: `Parts.Face` falls
back to the system default **silently and by design**, because a type initialiser that throws is
unreportable.

Then take a `--searchshot` PNG here and one on another machine and compare them. Identical is what
makes a README screenshot the product rather than a picture of one machine.

### 6.7 (catalogue 5, 6, 7) The capsule behaves - eye

- **z-order:** open a maximised window over where the capsule sits, then minimise it. The capsule
  stays behind, and does not jump forward when clicked.
- **no focus theft:** type in an editor, click the capsule once. The card opens and takes the
  keyboard; when dismissed, the editor's caret is where you left it. Click the capsule again while
  the card is open: it **closes** rather than reopening.
- **drag and save:** drag the capsule a few hundred pixels, release, quit, relaunch. The new
  position is in the log and in `config.json`, **written once per drag rather than per pixel**.
  Then drag a result row into an Explorer window and confirm the file copies.

**A failure on the write frequency means** `config.json` is being rewritten on every mouse-move
event.

### 6.8 (catalogue 8) The tray, and quitting - eye

**Pass:**
- the icon reads as a capsule **at its real size**;
- the tooltip carries the version, the hotkey and the update state;
- untick "Show capsule": it disappears and **the hotkey still works**;
- click "Check for updates": the menu item's own text changes;
- then Quit, and confirm the log's closing lines, that `%LOCALAPPDATA%\Findra\ui.json` is **gone**,
  and that no `findra` process survives **except the elevated helper**.

**A failure where `ui.json` survives means** the interface did not quit cleanly, and
`--searchprobe` will report a running interface that is not there. That is why step 0.6 deleted the
stale one before the session began.

### 6.9 Two dim behaviours - eye

Open the card from the capsule: the **capsule's** monitor dims. Open it with the hotkey: the
monitor **under the cursor** dims. Two open paths, two behaviours, and on a single-monitor machine
this cannot be told apart at all - do it on the two-monitor setup.

### 6.10 (catalogue 45, and catalogue 50 launches 4 and 5) Sign-out - sign-out, admin

This is the only step in the run sheet that needs a real sign-out, so it is deliberately last in
the phase.

Tick "Start Findra when I sign in", sign out, sign in.

**Pass:**
- the capsule comes back;
- **no console window** on either of the two sign-in launches, and nothing flashes. There are two:
  the autostart entry starting the interface, and the elevated logon task starting
  `findra.exe --names`. The second would have opened a **second** window on a console-subsystem
  binary. These are the fourth and fifth of the five launches from step 1.4.

Then untick it, sign out, sign in.

**Pass:** the capsule does **not** come back, and the `Findra` value is gone from
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

**Step 6.1 watches the registry value change; only a sign-out shows that Windows acts on it.**

---

# Phase 7 - Upgrade

One item, and it needs a second build.

### 7.1 (catalogue 34) An upgrade over a running copy - admin, destructive

Build a higher version locally. This is a **local** bump for the test; do not commit it unless it
is also the next release:

    # Directory.Build.props: <Version>0.1.1</Version>
    pwsh -File build/Publish.ps1 -Rid win-x64
    iscc /DAppVersion=0.1.1 /DPublishDir=..\publish\win-x64 installer\findra.iss

With Findra running, the capsule on screen and the name helper answering, run
`installer\Output\findra-0.1.1-x64.exe` over the installed copy.

**Pass:**
- `PrepareToInstall` stops all three processes before a file is replaced: **no "file in use"
  dialog, no reboot prompt**;
- the scheduled task still points at a `findra.exe` that exists;
- sign out and back in, or run the task, and the helper starts.

**This is the only way to see `PrepareToInstall` beat the file-in-use dialog.** Inno's
`CloseApplications` only closes windowed applications and two of Findra's three processes have no
window, which is why the script calls `findra.exe --stop` instead.

**A failure where the task points at a missing binary means** a version has crept into
`DefaultDirName`, which is the defect the fixed `AppId` and unversioned install directory exist to
prevent.

Put `Directory.Build.props` back to `0.1.0` afterwards, or leave it at `0.1.1` deliberately and say
so in `CHANGELOG.md`.

---

# Phase 8 - Uninstall

Three items. **Everything above is destroyed here. Nothing below phase 8 can be re-run without
starting from phase 1.**

### 8.1 (catalogue 33, second half, and 25) Keep by default - admin, destructive

First, record the numbers to compare against:

    "C:\Program Files\Findra\findra.exe" --uninstall --dry-run

Then uninstall from **Apps & features**.

**Pass:**
- the removal prompt lists the **four measured sizes and the total it would free**, and the numbers
  match what `--uninstall --dry-run` just printed on this machine. A prompt that says "this may
  free a large amount of space" is the vague warning the specification rejects;
- the checkbox is **unticked** when the prompt appears;
- click straight through it, and `%LOCALAPPDATA%\Findra\models`, `\index` and `%APPDATA%\Findra`
  are **all still there** afterwards;
- `schtasks /query /xml ONE /tn Findra*` finds **nothing**, whichever way the box was answered.

**The last one is the one the specification calls a defect rather than an inconvenience**, because
leaving it behind orphans an elevated logon task pointing at a deleted binary on a stranger's
machine. It is also the one a source-reading test cannot catch: wrapping the whole task-removal
block in `if (!quiet)` kept every token those tests grepped for and left the suite green while an
uninstall walked away from the task.

Also confirm `HKCU\...\Run` no longer holds `Findra`.

### 8.2 (catalogue 33, last bullet) Tick the box - admin, destructive, one way

Install again, tick the box this time, uninstall.

**Pass:** both folders are gone and **nothing outside them was touched**. `Uninstall.Delete`
refuses any entry that is not strictly inside one of the two roots; a root's parent is refused,
because "starts with the root" is true of the parent's own prefix in a naive comparison and
deleting `%LOCALAPPDATA%` is the worst thing this codebase could do to somebody.

### 8.3 (catalogue 49) `--purge` from the command line - admin, destructive, one way

This is the route with **no checkbox in front of it**, and it is the one somebody who built from
source takes - the `dotnet publish` route has no installer, so without this mode everyone who built
from source is left with an elevated scheduled task and no supported way to remove it.

From a **normal** terminal, on a source build:

    publish\win-x64\findra.exe --uninstall --purge

**Pass:** **one** elevation prompt, and afterwards `%LOCALAPPDATA%\Findra` and `%APPDATA%\Findra`
are both gone and nothing beside them has been touched.

**This deletes the 900 MB of models.** Step 0.1 is where you decided whether that mattered.

---

# Phase 9 - Release

Nine items. Three of them need only a browser and can be done at any point in the session; the rest
are in dependency order and two of them cannot be taken back.

### 9.1 (catalogue 35) The Actions tab - eye

The repository has been pushed and CI has run green across three runs, so most of this item is
already answered. What is left is reading the tab.

**Pass:**
- a run named **build** started on the push. If nothing had started, the trigger block would be
  wrong, and that is the one thing the text tests cannot see;
- `dotnet build --configuration Release -warnaserror` and `dotnet test` are green **on a clean
  checkout**. A restore that only works because of this machine's NuGet cache fails there and
  nowhere else;
- `build/Publish.ps1 -Rid win-x64` succeeded on a runner, where `dotnet publish` has no warmed-up
  `obj/` to lean on;
- `build/Check-Diagnostics.ps1` printed thirteen `ok` lines and `diagnostics: all modes answered`.

**The last of those is the interesting one.** It printed exactly that on this machine, but this
machine has a content index, a configured palette and a `%LOCALAPPDATA%\Findra`. A runner has none
of those, and the modes that read them - `--searchindex`, `--content`, `--models`, `--searchshot` -
are taking their empty path there for the first time. A failure means a diagnostic that works on a
developer's machine does not work on a stranger's, which is the whole reason the check runs.

### 9.2 (catalogue 54) Private vulnerability reporting - eye

`SECURITY.md` tells a reporter to use GitHub's private advisory form and to put no details in a
public issue. **That form only exists once somebody has ticked it on.** Settings > Security >
"Private vulnerability reporting".

**Pass:** load `https://github.com/blakazulu/findra/security/advisories/new` **while signed out of
the maintainer account** and the form is there.

Until it is, the page sends people to a door that is not open, which is worse than sending them to
the issue tracker.

### 9.3 (catalogue 40) Read the README on GitHub - eye

Every image in `README.md` is a repository-relative path, so nothing on the page could be checked
against a rendered view until there was one.

**Pass:** six images that load, six commands underneath them a reader could paste, and tables that
do not run off the side of the column on a phone.

### 9.4 (catalogue 36) The first tag - one way

**A public tag cannot be taken back.** Before tagging:

- move the `## [Unreleased]` entries into a numbered `## [x.y.z]` section.
  `build/Check-Release.ps1` exits 5 until that exists, which is the gate doing its job rather than
  a fault;
- check the number matches `Directory.Build.props`, which is the only place a version lives;
- no pre-release tags. The comparison cannot order `1.2.0-rc.1`, and `/releases/latest` excludes
  prereleases, so such a tag ships a release nobody's update check will ever see;
- run the gate by hand first: `pwsh -File build/Check-Release.ps1 -Tag v0.1.0`.

Then push the tag and watch for:

- the **check** job printing the changelog section **and nothing else**. Whatever it prints is the
  release body, verbatim. GitHub's generated release notes are never turned on;
- `choco install innosetup --version=6.3.3` putting `ISCC.exe` under
  `%ProgramFiles(x86)%\Inno Setup 6` on the current runner image. If the package moves, the step
  must fail **loudly** rather than build nothing;
- `findra.iss` compiling for **both** architectures. This is its first compilation on a runner.
  `ArchitecturesAllowed=x64compatible` and `arm64` are 6.3 syntax and a 6.2 compiler rejects them
  outright. Note the asymmetry: `x64compatible` is a word and `arm64compatible` is not, and pasting
  "compatible" onto the architecture the workflow passes gave the x64 leg a valid word and the
  arm64 leg one ISCC rejects - which fails that matrix leg, and so the whole release, for Intel and
  Arm alike;
- `softprops/action-gh-release` finding the notes at `artifacts/release-notes/release-notes.md`.
  `download-artifact` nests by artifact name, and **if that path is wrong the release is created
  with an empty body rather than failing**;
- two installers attached, `findra-<version>-x64.exe` and `findra-<version>-arm64.exe`, with
  `fail_on_unmatched_files` catching it if either is missing;
- the release body, the release page and the installers saying **nothing about being signed**. The
  signing step is a placeholder that prints one line and exits.

### 9.5 (catalogue 39) Regenerate the README's numbers

The fragment in `README.md` was measured through `dotnet run --project src/Findra`, which is a
Debug, framework-dependent build, on a machine whose content index held ten documents. Every number
in it is real and every one of them is a floor.

On a machine that has let Findra read inside its files for a while, from a self-contained Release
build:

    findra --searchbench readme-bench.md 10000

Replace everything from `## Findra benchmark` to the corpus note with what it prints, **whole**.

`TheBenchmarkFragmentIsTheWholeOneAndNotTheFlatteringHalfOfIt` fails if any section is dropped on
the way, and `TheThroughputFigureCameFromARunLargeEnoughToReproduce` fails if the default corpus is
used - a run of a second or two disagrees with itself by more than a published `files/min` or `MB/s`
deserves. Adjust the two sentences above the fragment, which describe that run and no other, in the
same commit. Name the machine: CPU, RAM, disk class, accelerator, Windows build. A number without
its machine is marketing, not measurement.

### 9.6 (catalogue 37) Apply to the SignPath Foundation

Once step 9.4 has produced a release to point at. `docs/code-signing-policy.md` is the application
material and carries a status note saying the arrangement is not yet in force.

When the application is accepted, the note comes out and the workflow's placeholder step becomes
real **in the same commit**:
`TheSigningPageSaysItIsNotInForceForAsLongAsTheSigningStepDoesNothing` couples the two in both
directions and fails on whichever one moves alone.

### 9.7 (catalogue 38) The first winget submission - one way

**Nothing but a person on the Actions tab publishes to the winget catalogue.** No `push`, `tag`,
`release` or `schedule` trigger may ever reach `winget.yml`: a mis-tagged GitHub release can be
deleted, and one that reaches the catalogue is on other people's machines by their next
`winget upgrade`.

Go to the Actions tab, run **publish to winget** with the version and **submit unticked**:

- the first step must find `findra-<version>-x64.exe` and `findra-<version>-arm64.exe` on the
  release and **stop if either is missing, before anything is built**;
- the manifests are uploaded as the `winget-manifests` artefact. Download and read them: the
  identifier, the installer type, the `/INSTALLSOURCE=winget` switch and the description are
  whatever `packaging/winget/` says, and **only the version and the two hashes were substituted**;
- both `InstallerSha256` values must be real hex. The repository copy carries sixty-four zeros on
  purpose and the workflow throws if either survives;
- `winget validate` runs only if the App Installer CLI is on the runner. If the log says it is not,
  that is the documented fallback and not a failure;
- both architectures under **one** `PackageVersion`, with two `Installers:` entries.

Only then re-run with **submit ticked**. That needs `WINGET_PKGS_TOKEN` to be a token with access
to a fork of the catalogue repository, and it opens a pull request somebody else reviews. **This is
the second thing that cannot be taken back.**

### 9.8 (catalogue 41) Replace the install section's first paragraph

The README currently opens the install section by saying there is no published release and nothing
has been submitted to winget. That is true today, and it is the reason
`winget install blakazulu.Findra` is written as the command for when that release exists rather
than as an instruction.

When step 9.7 succeeds, that paragraph is the thing that becomes false first.

### 9.9 (catalogue 48) winget, end to end - needs a second machine

Once the catalogue has the package: `winget install blakazulu.Findra` on a machine Findra has never
been on.

**Pass:** `installed-by.txt` beside the executable says `winget`, and Settings > About offers
`winget upgrade blakazulu.Findra` rather than a link to the release notes. Somebody who downloaded
the `.exe` directly must get the **link** instead, which is the second half of the same check and
needs a second machine or a second install.

The install source is recorded at first run, not guessed each launch, which is what makes both
halves stable.

---

# What this run sheet cannot place

- **Step 4.11 (catalogue 23b)** needs a machine with speech and without photos. This machine has
  photos installed and the models are being kept, so the step is written as a temporary move of
  three files rather than a deletion. If the move is skipped, the step is not run.
- **Step 9.9 (catalogue 48)** needs a second machine and a catalogue that has accepted the package.
  Neither exists yet.
- **Step 9.6 (catalogue 37)** is an application to a third party and its outcome is not on any
  schedule this repository controls.
- Nothing else in the catalogue is unplaceable. Every numbered item from 1 to 54, including 23a,
  23b and 27a, appears above exactly once, except catalogue 33, 42 and 50, which are each split
  across the phases where their halves become reachable.
- **Step 6.9 is not a catalogue item.** The two dim behaviours are a rule in the specification and
  in `CLAUDE.md` that the catalogue never turned into a step, and they need two monitors to tell
  apart at all, so they are written down here rather than left to be noticed.
