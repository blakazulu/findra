# End-to-end checklist

Everything here needs an elevated terminal, a screen, or a real disk, so no automated run
in this project has ever executed it. It accumulates as plans land, and is worked through
once on a real installed build.

**This is the catalogue, not the running order.** It is ordered by which plan discovered each
item, which means it cannot be worked through top to bottom: item 1 wants the helper already
running, item 24 wants a machine that has never run Findra, and item 33 destroys the state items
20 to 22 need. `docs/e2e-run-sheet.md` is the same items in an order somebody can sit down and
work through, in ten phases, with the command, the pass and what a failure implies written out
for each. Every item below appears there exactly once. Read the run sheet to do the work; read
this one to understand why an item exists.

`build/Check-E2E.ps1` does every part of it a script can answer and prints a pass/fail table, so
the human only does what needs eyes:

    pwsh -File build/Check-E2E.ps1 -Exe publish/win-x64/findra.exe

Before starting, tail the log in a spare window so every step's proof appears as it happens:

    Get-Content $env:LOCALAPPDATA\Findra\logs\findra-$(Get-Date -f yyyyMMdd).log -Wait -Tail 20

## From Plan 3, the widget

1. **The elevated helper answers.** In an elevated terminal, `findra --names`. In a normal
   one, `findra --searchprobe sunset`. The probe should print `pipe : ok`, a helper process
   id different from its own, a name count, and a generation counter, instead of the
   unreachable message it prints today.
2. **The interface starts.** `findra`. Four log lines in order: the palette and mode, the
   hotkey combination that registered, the capsule's position, the tray icon. A capsule is
   visible on the desktop.
3. **Real names.** Press the hotkey the log named, type a word you know is on the disk.
   Rows appear within a keystroke or two, and the line under the field reads a name count
   and the helper's process id, not "the name helper is not running". That line is the best
   proof the pipe answered, because it comes from a different call than the search.
4. **The ordering fix.** Press Ctrl+1, Ctrl+2 and Ctrl+3 quickly, ten times, then type
   more. Rows re-sort every time and the searching indicator always comes back down.
   Failure looks like stale rows with the indicator still spinning.
5. **Capsule z-order.** Open a maximised window over where the capsule sits, then minimise
   it. The capsule stays behind, and does not jump forward when clicked.
6. **No focus theft.** Type in an editor, click the capsule once. The card opens and takes
   the keyboard; when dismissed, the editor's caret is where you left it. Click the capsule
   again while the card is open: it closes rather than reopening.
7. **Drag and save.** Drag the capsule a few hundred pixels, release, quit, relaunch. The
   new position is in the log and in `config.json`, written once per drag rather than per
   pixel. Then drag a result row into an Explorer window and confirm the file copies.
8. **The tray, and quitting.** The icon reads as a capsule at its real size. The tooltip
   carries the version, the hotkey and the update state. Untick "Show capsule": it
   disappears and the hotkey still works. Click "Check for updates": the menu item's own
   text changes. Then Quit, and confirm the log's closing lines, that `ui.json` is gone,
   and that no `findra` process survives except the elevated helper.

## From Plan 4, content

9. **A real volume enumerates.** With the helper running, watch the first pass on a real
   disk. `findra --searchindex` should show a rising indexed count, a consumed journal
   position per volume, and no failures beyond unreadable files.
10. **The journal streams.** Create and delete a file on C:. The helper's journal line
    should track that one change rather than the whole disk, and the file should appear in
    the queue.
11. **A restart does not re-walk.** Quit and relaunch. The second start must resume from
    the recorded position rather than walking the disk again. This is the property the
    specification says must never be got wrong.
12. **An edited file is re-indexed.** Edit a document while Findra is closed, then start
    it. The new contents must become searchable. This path only works because the full
    pass compares modification times, and that has never run against a real disk.
13. **The indexer child dies with its parent.** Kill the interface without a clean quit.
    The `findra --index` child must disappear, killed by the job object rather than by its
    own polling.
14. **Content search returns real answers.** With documents indexed, press the Content
    pill and search for a word inside a file rather than in its name. Check the excerpt
    reads sensibly and points at the right part of the document.
15. **Hebrew reads correctly.** Index a Hebrew document and search a word in it. The
    excerpt must read in logical order rather than reversed.

## From Plan 5, capabilities

The models are downloaded by Findra itself rather than placed by hand, so this section is
as much a test of the download path as of the models.

16. **The first download works, and can be interrupted.** Choose a preset and let it
    download. Watch the progress reach the end. Then, on a later capability, kill Findra
    part way through and restart: it must resume from the byte already fetched rather than
    starting the file again. A partial file left behind must never be treated as a
    complete model.
17. **A download that fails says so and recovers.** Disconnect the network mid-download.
    Findra must report it plainly, keep what it has, and continue when the network returns.
18. **Nothing is re-downloaded that is already there.** Restart after a complete download.
    No file is fetched twice, and the sizes on disk match what the interface claimed.
19. **The accelerator is real.** `findra --searchmodels` must name the provider it chose
    and every one it rejected with a reason. On this machine it should choose the discrete
    graphics card rather than the processor. If it falls back to the processor, the reason
    must say why.
20. **Photos become searchable by description.** Index a folder of photos, then search for
    what is in one rather than its filename.
21. **Speech becomes searchable.** Index a recording and search a phrase spoken in it.
    Then a Hebrew recording, which runs the general model first for detection and only
    then the Hebrew one.
22. **Text inside images is found.** Index a screenshot containing words and search one.
23. **Enabling a capability re-indexes exactly what it covers.** This is the promise the
    plan review rejected a draft over. Turn on a capability after the index is already
    built, and confirm from `--searchindex` that the files it covers are re-queued and
    actually re-read, and that nothing else is disturbed.
23a. **The transcription limit is obeyed, and a long video is still read for its frames.**
    With speech and photos both installed and the limit at its five-minute default, index a
    recording well over the limit and a video well over it. The recording must be *skipped*
    with "longer than the transcription limit" as its reason; the video must be *indexed* -
    its frames were read - and carry the same string as a note about what was not heard.
    No automated test on this plan can see either, because both need a real model and real
    media: everything below the gate in `Decoders` - photo, audio, video, transcription and
    the Meaning branch of a document - is unexercised at runtime until this step runs.
23b. **A video on a speech-only machine is still opened.** Take speech without photos, then
    index a video with talking in it and search a phrase from the sound track. This is the
    one case a "which capability covers this kind" lookup silently drops, and the gate is
    written as an OR precisely for it.

## The one that hid behind an unelevated agent

Every automated run of Findra in this project logged "the names helper is not answering",
and it was read as a consequence of agents having no administrator rights. It was not.
`HelperTask.Register` had no callers: the application asked the scheduler to start a task
that nothing had ever created. On a clean machine, name search would never have worked at
all.

Registration is wired up in Plan 6, from the first-run screen, because it needs a consent
moment rather than an elevation prompt at every launch.

24. **The scheduled task is created by Findra itself.** On a machine that has never run it,
    complete first run and then check `schtasks /query /tn "Findra names helper"` finds the
    task, and that `findra --searchprobe` reports it registered. Do NOT start the helper by
    hand first, because that is exactly what masked this.
25. **Uninstalling removes it again.** After `findra --uninstall`, the same query must find
    nothing. The specification calls leaving it behind a defect rather than an
    inconvenience, because it orphans an elevated logon task pointing at a deleted binary.

26. **The preview actually appears.** Open the card, search, and select a result that is a
    photo, a PDF or a video. A picture must appear on the stage rather than the fallback
    tile. This one cannot be checked headlessly at all: the shot command composes the card
    with no image and never runs the asynchronous preview loader, so the renderer is not on
    that path on any machine. It was stubbed to return nothing from Plan 3 until the
    framework moved, and this is the first time it can draw.

## From Plan 6, settings and shipping

27. **`--content` and `--searchindex` agree once the interface has run.** On this machine they
    do not: `--content` reads `config.json` and says "index up to date", while `--searchindex`
    reads the index's own `index:paused` row and says "off". Both are behaving exactly as
    specified, and they diverge only because the interface has never been launched here to
    write that row. After the first real launch, run both and confirm they say the same thing.
    If they still disagree, the row is not being written and `--searchindex` is describing an
    index nobody has told about a setting that changed.

27a. **Every control in the settings window, clicked by a person.** Open it from the tray's
    Settings item and work through all five sections. Seven of them reach the operating system
    and no test at any level covers what happens after the click, because each is a call into
    Avalonia, the registry, `schtasks` or the network:

    - change the dark palette and watch the window, the capsule and the tray icon follow with
      no restart;
    - press "Open the file" and confirm `palettes.json` opens in an editor;
    - click the hotkey row, press Escape, confirm it stops listening; click it again, press a
      combination, confirm the row shows it and that the new combination opens the card;
    - tick "Start Findra when I sign in", confirm the `Findra` value appears under
      `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, then untick it and confirm it goes;
    - press "Register it" on the name helper, answer the one prompt, and confirm the row turns
      to "registered" **and that name search starts working in this session**;
    - press "Add a folder", pick one, and confirm it appears in the list and can be removed;
    - press a capability's size button and confirm a download starts and the row turns to
      "installed" when it lands;
    - press "Check now" and confirm the About line changes;
    - drag the capsule to a far corner, press "Bring the capsule back", and confirm it moves
      **in this session** rather than at the next launch.

    Also right-click the capsule: the tick must be beside the palette actually on screen, and
    the palettes offered must be the ones for the side in use.

28. **A hotkey rebind that is refused puts the old one back.** Not reachable headlessly - it
    needs a real window handle and a combination another application already owns. Open
    settings, click the hotkey row, and press a combination something else has taken (a running
    screen-capture or launcher tool is the usual source). The row must say the combination is
    taken and that Findra kept the old one, and **the old combination must still open the
    card**. If it does not, the rebind unregistered the old hotkey and failed to restore it,
    which leaves somebody with no hotkey and the control that would fix it behind a card the
    hotkey no longer opens.

29. **The first screen, answered by a person.** Nothing about a window can be checked
    headlessly - `--searchshot firstrun`, `--searchshot firstrunspeech` and
    `--searchshot firstrundownloading` draw the three surfaces and nothing else. Rename
    `%APPDATA%\Findra\config.json` and launch, then:

    - the screen appears before the capsule and the tray, and reads as the same object as the
      settings window - same width, same edge, same pills;
    - the three preset tiles light one at a time, and touching any row moves the choice to none
      of them;
    - ticking Speech ticks the document models under it; unticking Speech takes Hebrew with it;
    - ticking Speech puts the transcription limit under it and unticking Speech takes it away;
      the five pills answer, the rows below move down rather than being drawn over, and the
      number chosen here is the one `--content` reports afterwards;
    - every row's size stays the same number when it is ticked, and the four of them add up to
      the 2.93 GB the Everything tile quotes;
    - the Hebrew row is on the screen only on a machine with Hebrew installed;
    - press "Not now": one administrator prompt, the screen goes, and it does **not** come back
      at the next launch;
    - the log carries both `names helper task registered` **and** `the names helper is
      answering`, and name search works in that same session with no sign-out. This is the half
      that has no test at any level: registering without starting leaves name search dead for
      the whole of somebody's first session, and only a real machine can show it does not.

30. **The second act, with the network taken away.** Start again with a renamed config, choose
    Recommended and press "Get these":

    - one progress bar per capability, not one for the whole download, and a capability whose
      files were already on disk starts full rather than empty;
    - pull the network cable mid-download. The screen must say what went wrong and that what
      arrived was kept - not stop silently - and the log must carry the same. Plug it back in,
      launch again, and the download resumes from the bytes already fetched rather than from
      zero;
    - close the window mid-download and confirm Findra is still in the tray and still fetching;
    - when a capability lands, the log says how many files were queued for what Findra can now
      read. That line is the re-queue running on the flow that owns the index's writer, and it
      is what makes an installed capability start finding things without a restart.

31. **The same line, from the settings window.** Both paths now run the same download
    controller, so installing a capability from settings must produce the same "queued N
    file(s)" line in the same session. If it appears on one path and not the other, the two
    have drifted apart again.

32. **The installer script compiles at all. DONE.** `installer/findra.iss` had never been through
    a compiler, and every earlier check reported Inno Setup missing because it looks in Program
    Files and this machine has it at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`. With that
    found:

        pwsh -File build/Publish.ps1 -Rid win-x64
        iscc /DAppVersion=0.1.0 /DPublishDir=..\publish\win-x64 installer\findra.iss

    Both architectures compiled and produced `installer/Output/findra-0.1.0-x64.exe` (82 MB) and
    `findra-0.1.0-arm64.exe` (79 MB). The compile found a real defect on the way -
    `CreateCustomForm` called with the wrong number of arguments - which is exactly the class of
    thing an assertion on the script's text cannot see, and it is fixed.

    What is still not done is the compile on a runner, which is step 36: a different Inno version
    from a chocolatey package, on a machine where `ISCC.exe` is under
    `%ProgramFiles(x86)%\Inno Setup 6`, for both architectures in one matrix.

33. **A real install, then a real removal.** Run that installer on a machine Findra has never
    been on. The wizard shows the licence, offers "Start Findra", and installs into
    `C:\Program Files\Findra` with no version anywhere in the path. Then, from Apps & features:

    - `reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" /s /v
      QuietUninstallString | findstr /i findra` prints nothing. Inno registers that value on its
      own and Windows 11's Settings prefers it, which starts the uninstaller with `/SILENT` and
      skips every bullet below this one without asking anything. The installer deletes it after
      installing, and only a real install can show that it did;
    - the removal prompt lists the four measured sizes and the total it would free, and the
      numbers match what `findra --uninstall --dry-run` prints on that machine. A prompt that
      says "this may free a large amount of space" is the vague warning the specification
      rejects;
    - the checkbox is **unticked** when the prompt appears. Click straight through it and
      `%LOCALAPPDATA%\Findra\models`, `index` and `%APPDATA%\Findra` must all still be there
      afterwards;
    - `schtasks /query /xml ONE /tn Findra*` finds nothing afterwards, whichever way the box was
      answered. This is the one the specification calls a defect rather than an inconvenience;
    - install again, tick the box this time, and confirm both folders are gone and that nothing
      outside them was touched.

    Before uninstalling, look in the install folder itself: `LICENSE.txt`, `NOTICE.txt` and
    `OFL-Quicksand.txt` must all be there. Apache-2.0 section 4(d) makes the notice travel with
    every distribution, and the installer copies the publish folder and nothing else, so a build
    whose csproj stopped copying them would install without them and nothing would warn.

34. **An upgrade over a running copy.** With Findra running, the capsule on screen and the name
    helper answering, install a build with a higher version over it. `PrepareToInstall` must
    stop all three processes before a file is replaced - no "file in use" dialog, no reboot
    prompt - and the scheduled task must still point at a findra.exe that exists when the
    machine next signs in.

35. **The first push, which is the first time GitHub Actions has ever run here. MOSTLY DONE.** The
    repository has been pushed and CI has run green across three runs, so `ci.yml` is no longer
    asserted as text alone - it has executed. What is left is reading the Actions tab by eye
    against the list below, because a green tick says the job exited zero and not that each of
    these happened. Before the push, `WorkflowTests` read its triggers, its `-warnaserror`, and
    the PowerShell inside any block it carries, and no runner had executed a line of it. On that
    push:

    - a run named **build** starts. If nothing starts, the trigger block is wrong, and that is
      the one thing the text tests cannot see;
    - `dotnet build --configuration Release -warnaserror` and `dotnet test` are both green on a
      clean checkout. A restore that only works because of this machine's NuGet cache fails here
      and nowhere else;
    - `build/Publish.ps1 -Rid win-x64` succeeds on a runner, where `dotnet publish` has no
      warmed-up obj/ to lean on;
    - `build/Check-Diagnostics.ps1` prints thirteen `ok` lines and `diagnostics: all modes
      answered`. It printed exactly that here, but on a machine with a content index, a
      configured palette and a `%LOCALAPPDATA%\Findra`. A runner has none of those, and the
      modes that read them - `--searchindex`, `--content`, `--models`, `--searchshot` - are
      taking their empty path there for the first time.

    A failure in the last of those is the interesting one: it means a diagnostic that works on a
    developer's machine does not work on a stranger's, which is the whole reason the check runs
    at all.

36. **The first tag, which is the first time `release.yml` has ever run.** Everything about it
    has been asserted as text and nothing more: no runner has executed a line of it. The script
    itself has now been compiled, by hand, on this machine (step 32); what a runner does with a
    chocolatey-installed 6.3 and both architectures in one matrix is still unknown. Before
    tagging, move the `## [Unreleased]` entries into a
    numbered `## [x.y.z]` section - `build/Check-Release.ps1` exits 5 until that exists, which
    is the gate doing its job rather than a fault - and check that the number matches
    `Directory.Build.props`. Then push the tag and watch for:

    - the **check** job printing the changelog section and nothing else. Whatever it prints is
      the release body, verbatim;
    - `choco install innosetup --version=6.3.3` putting `ISCC.exe` under
      `%ProgramFiles(x86)%\Inno Setup 6` on the current runner image. If the package moves, the
      step fails loudly rather than building nothing, which is the failure to prefer;
    - `findra.iss` compiling at all, for both architectures. This is its first compilation
      anywhere. `ArchitecturesAllowed=x64compatible` and `arm64compatible` are 6.3 syntax and a
      6.2 compiler rejects them outright;
    - `softprops/action-gh-release` finding the notes at
      `artifacts/release-notes/release-notes.md`. `download-artifact` nests by artifact name,
      and if that path is wrong the release is created with an empty body rather than failing;
    - two installers attached, `findra-<version>-x64.exe` and `findra-<version>-arm64.exe`, and
      `fail_on_unmatched_files` catching it if either is missing;
    - the release body, the release page and the installers saying nothing about being signed.
      The signing step is a placeholder that prints one line and exits.

37. **Apply to the SignPath Foundation, once step 36 has produced a release to point at.**
    `docs/code-signing-policy.md` is the application material and carries a status note saying
    the arrangement is not yet in force. When the application is accepted, the note comes out
    and the workflow's placeholder step becomes real **in the same commit**:
    `TheSigningPageSaysItIsNotInForceForAsLongAsTheSigningStepDoesNothing` couples the two in
    both directions and fails on whichever one moves alone.

38. **The first winget submission, which nobody but you can start.** `.github/workflows/winget.yml`
    is reachable by `workflow_dispatch` and by nothing else: no push, no tag, no release and no
    schedule may ever be added to it, because a mis-tagged build that reaches a GitHub release can
    be deleted and one that reaches the catalogue is on other people's machines by their next
    `winget upgrade`. Everything the workflow does has been asserted as text and nothing more.

    Once step 36 has produced a release, go to the Actions tab, run **publish to winget** with the
    version and **submit unticked**, and then:

    - the first step must find `findra-<version>-x64.exe` and `findra-<version>-arm64.exe` on the
      release and stop if either is missing, before anything is built;
    - the manifests are uploaded as the `winget-manifests` artefact. Download and read them: the
      identifier, the installer type, the `/INSTALLSOURCE=winget` switch and the description are
      whatever `packaging/winget/` says, and only the version and the two hashes were substituted;
    - both `InstallerSha256` values must be real hex. The repository copy carries sixty-four zeros
      on purpose and the workflow throws if either survives;
    - `winget validate` runs only if the App Installer CLI is on the runner. If the log says it is
      not, that is the documented fallback and not a failure.

    Only then re-run it with **submit ticked**. That needs `WINGET_PKGS_TOKEN` to be a token with
    access to a fork of the catalogue repository, and it opens a pull request that somebody else
    reviews. Nothing in this repository can do either of those, and nothing in it ever starts this
    workflow on its own.

39. **Regenerate the README's numbers from a published Release build.** The fragment in `README.md`
    was measured through `dotnet run --project src/Findra`, which is a Debug, framework-dependent
    build, on a machine whose content index held ten documents. Every number in it is real and
    every one of them is a floor. Once step 36 has produced a self-contained Release
    build, and on a machine that has actually let Findra read inside its files for a while, run

        findra --searchbench readme-bench.md 10000

    and replace everything from `## Findra benchmark` to the corpus note with what it prints, whole.
    `TheBenchmarkFragmentIsTheWholeOneAndNotTheFlatteringHalfOfIt` fails if any section is dropped
    on the way, and `TheThroughputFigureCameFromARunLargeEnoughToReproduce` fails if the default
    corpus is used. Adjust the two sentences above the fragment, which describe that run and no
    other, in the same commit.

40. **Read the README on GitHub, now that the repository has been pushed.** Every
    image is a repository-relative path, so nothing on the page can be checked against a rendered
    view until there is one. Look for six images that load, six commands underneath them that a
    reader could paste, and tables that do not run off the side of the column on a phone.

41. **Replace the install section's first paragraph the day the catalogue accepts the package.**
    It currently opens by saying there is no published release and nothing has been submitted to
    winget, which is true and is the reason `winget install blakazulu.Findra` is written as the
    command for when that release exists rather than as an instruction. When step 38 succeeds, that
    paragraph is the thing that becomes false first.

## From the close-out

Eight things the plan asked for that nothing above already covers. Six of them are judgements a
person makes by looking; two are destructive and belong last.

42. **Refusing the one prompt is survivable, and recoverable.** Rename `%APPDATA%\Findra\config.json`,
    launch, and answer **No** to the UAC prompt the first-run screen raises. Findra must keep
    running with the capsule and the tray as normal, the log must say the helper is not registered
    rather than going quiet, and Settings > Opening it > "Register it" must succeed on a second
    attempt. The specification calls scheduled-task registration the one thing Findra cannot fix on
    a stranger's machine, so what is being checked is that it is visible and has a way back.
43. **Settings by eye, in both modes.** Open all five sections in a dark palette, then again in a
    light one. Nothing overlaps, no note is clipped at the bottom of the pane, no label is cut off
    inside its pill, and the pane is the same height in every section. The measured tests say the
    rows fit; whether the window reads as the same object as the card is a judgement, and this is
    where it is made.
44. **The exclusions list is the only scroller, and it scrolls.** With the default entries in
    place, scroll to the end of the list in Settings > What it searches, remove the last one, and
    confirm the list is still usable afterwards and that `config.json` holds an array rather than
    nothing at all. Then check that nothing else anywhere in either surface scrolls.
45. **Start at sign-in, across a real sign-out.** Tick it, sign out, sign in: the capsule comes
    back. Untick it, sign out, sign in: it does not, and the `Findra` value is gone from
    `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Checklist step 27a watches the
    registry value change; only a sign-out shows that Windows acts on it.
46. **The measured sizes are the real ones.** Run `findra --uninstall --dry-run` and compare its
    four numbers against what Explorer reports for the same four folders. Everything in the product
    that quotes a size to somebody about to delete it comes through this one measurement.
47. **The shipped face is what a person actually sees.** On an installed build, open the card and
    all five settings sections and confirm they are drawn in Quicksand rather than the system UI
    face. `Parts.Face` falls back to the default silently and by design, so a packaging mistake
    shows up here and nowhere else. Check that `OFL-Quicksand.txt` sits beside `findra.exe` in the
    install folder, which is what the licence asks of every copy. Then take a `--searchshot` PNG
    here and one on another machine and compare them: identical is what makes a README screenshot
    the product rather than a picture of one machine.
48. **winget, end to end, once the catalogue has the package.** `winget install blakazulu.Findra`
    on a machine Findra has never been on. Afterwards `installed-by.txt` beside the executable says
    `winget`, and Settings > About offers `winget upgrade blakazulu.Findra` rather than a link to
    release notes. Somebody who downloaded the `.exe` directly must get the link instead, which is
    the second half of the same check and needs a second machine or a second install.
49. **`--purge` from the command line, which is the route with no checkbox in front of it.** On a
    machine you are willing to reinstall on: `findra --uninstall --purge` from a normal terminal.
    One elevation prompt, and afterwards `%LOCALAPPDATA%\Findra` and `%APPDATA%\Findra` are both
    gone and nothing beside them has been touched. Step 33 checks the installer's route; this is
    the one somebody who built from source takes.

## From the review

50. **No black window comes with the widget, on any of the five launches that open one.** Findra is
    a windows-subsystem binary now, and a console window is what a console-subsystem one was given
    every time it started without a terminal. Check all five, because they are five different
    callers and only two of them are the same code path: the installer's "run Findra now" tick, the
    Start-menu shortcut, an Explorer double-click on `findra.exe`, a sign-in with start-at-sign-in
    ticked, and a sign-in with the scheduled task registered - the last of which starts
    `findra.exe --names` elevated and would have opened a **second** window. Nothing may flash
    either: a console that appears and closes is still a console.
51. **The diagnostics still print when a person runs them by hand.** In a real terminal, run
    `findra --version`, `findra --searchmodels` and `findra --searchprob`, each on its own. Every
    one must put its text in that terminal, `--searchmodels` must show the Hebrew probe line and
    the card's middle dot rather than replacement characters, and the mistyped one must print the
    list of modes. Then run them again with the output redirected to a file and confirm the file
    holds the same text and the terminal holds none of it. A windows-subsystem process is not
    waited for by the shell, so expect the prompt to come back before the text does; that is the
    one visible cost of step 50 and it is worth confirming it is only cosmetic.
52. **A capability installed while Findra is open is read without a restart.** With content
    indexing on and the first pass finished, install photos - from the settings window, and again
    on another run from `findra --models install photos` in a terminal while Findra is running.
    Both times, without touching Findra afterwards, `findra --searchindex` must show the photos
    moving off "no decoder for this kind yet" and the log must carry the one line saying what
    Findra can read has changed while the indexer was running. Then restart and search a photo by
    what is in it: the card's own half of a capability is loaded when Findra starts, which is the
    only part of this that still needs a restart, and the closing sentence of `--models install`
    says exactly that. No automated test can see this step, because the gate it turns on needs
    629 MB of real model files.
53. **The transcription limit raised from a terminal reaches a running Findra.** With speech
    installed, the limit at five minutes and a recording of half an hour already passed over, run
    `findra --content limit 60` while Findra is running. The recording must be transcribed without
    a restart, and its phrase must be findable after one. Then confirm the reverse costs nothing:
    lowering the limit again throws no transcript away.
54. **Private vulnerability reporting is switched on, the first time the repository is public.**
    `SECURITY.md` tells a reporter to use GitHub's private advisory form and to put no details in
    a public issue, and that form only exists once somebody has ticked Settings, Security,
    "Private vulnerability reporting". Do it in the same sitting as step 35, then load
    `https://github.com/blakazulu/findra/security/advisories/new` while signed out of the
    maintainer account and confirm the form is there. Until it is, the page sends people to a
    door that is not open, which is worse than sending them to the issue tracker.

## From the interface pass

55. **The welcome screen is the only thing on the display until it is answered.** Rename
    `%APPDATA%\Findra\config.json` and launch Findra. While the screen is up there must be no
    capsule on the desktop, no tray icon, and the hotkey must do nothing. Press "Get these" with a
    capability ticked: the capsule, the tray icon and the hotkey appear then, and the download
    carries on in front of you rather than behind a product that is already running. Closing the
    screen mid-download must leave the download running and Findra in the tray, which is what the
    screen itself promises. `StartupOrderTests` asserts which stages a launch takes and in what
    order; that they are the stages a person sees is this step.
56. **Names work while the screen is still up.** In the same launch, before answering, the
    scheduled task does not exist yet - that is the point of the consent moment. Immediately after
    answering, and without waiting for any download, `findra --searchprobe` must report the helper
    registered and a name query must answer. Nobody waits on a 1.5 GB file for their filenames.
57. **The pointer says what each surface is.** Move over the capsule: a four-way move cursor, and
    dragging really does move it, which is the thing nothing on screen ever said. Over the card:
    an I-beam in the field, a hand over every pill, chip, row and action, and the plain arrow in
    between. The same hand over every row and pill of Settings and of the welcome screen. This is
    by eye because a cursor is a platform object with no pixels of its own in any render.
58. **Settings can be reached from the card.** Open the card and press Settings under Advanced: the
    settings window opens and the card gets out of the way. Then press Content with nothing
    indexed - it must open Settings at the Content section rather than emptying the card. Turn
    reading on, let something be read, turn it off again, and press Content: this time it turns
    reading back on in place and answers the query.
59. **The two new Content rows do what they say.** Press "Start now" and watch the sentence above
    it change and the capsule's line begin to move. Move "Indexing power" to 25 and to 100 and
    confirm `findra --searchindex` reports the rate changing with it - the setting has been
    honoured by the indexer since it was written and until now there was no way to change it
    without hand-editing `config.json`.

## From the mark

60. **The icon is on every surface the shell draws it on.** After a real install, look at each:
    the taskbar button while Findra is running, Alt-Tab, the Start-menu shortcut, a desktop
    shortcut, the Explorer listing at Large icons and at Details, and the Add and Remove Programs
    entry. `IconTests` decodes every size in `assets/icon/findra.ico` and checks named pixels, and
    the icon has been extracted back out of a compiled `findra.exe` - but WHICH size Windows picks
    on a real desktop, at a real scaling factor, is a thing no test here can see. The 16 px and
    20 px sizes are drawn differently from the large ones on purpose; whether that reads as the
    same mark at a glance in a real taskbar is a judgement nobody has made yet.
61. **The installer and the uninstaller.** `setup.exe`'s own icon is DONE and was checked here
    rather than assumed: the script compiles with `SetupIconFile` and `WizardSmallImageFile`, and
    the mark was extracted back out of the built `findra-0.1.0-x64.exe`. What remains by eye is
    the wizard itself - the mark should sit in the top corner of every page, on Inno's light
    background, at whatever scaling the machine is set to.
    What `unins000.exe` shows, and what the "Remove Findra" dialog puts in its title bar, is NOT
    known: Inno decides it, nothing in `findra.iss` sets it, and no uninstaller built from this
    script has been looked at. If they show Inno's default rather than the mark, that is the
    finding, and the fix is somewhere in the installer rather than in the application.
62. **The tray icon is the mark now, not the old pill.** It is drawn at 32 px in the running
    palette, with the lens's slot left as a genuine hole rather than filled. `IconTests` asserts
    the hole on three palettes. What it cannot show is the icon sitting on a real taskbar: check
    it on a **light** taskbar in particular, which is where a filled slot would have looked like a
    smudge and where the hole has to read as one.

## What could not be verified in this project at all

Written down so they are known gaps rather than assumed passes. Every one of them is a step above.

- ~~**Inno Setup is not installed here.**~~ It was, at `%LOCALAPPDATA%\Programs\Inno Setup 6`,
  which is not where any earlier check looked. The script now compiles for both architectures and
  the first compile found a defect no text assertion could reach (step 32). What is still unknown
  is the compile on a runner (step 36).
- ~~**GitHub Actions has never run.**~~ `ci.yml` has run green three times since the first push
  (step 35). `release.yml` and `winget.yml` are still asserted as text and nothing more, including
  the two assertions that exist because a deterministic PowerShell parse bug was once filed as
  something only a real machine could find (steps 36 and 38).
- **A real uninstall needs an elevated terminal and a registered scheduled task, and it is
  destructive.** `--dry-run` is the only form that has run here (steps 33 and 49).
- **A winget submission needs a pull request against somebody else's repository**, and the App
  Installer CLI may not even be on the runner (step 38).
- **Whether the shipped face reads as the product is a judgement** and has never been made: no
  Findra surface has been looked at by a person in Quicksand (steps 43 and 47).
- **Whether every control does something when a person clicks it** is the other judgement, and it
  is the one an earlier draft of the settings window failed with five dead controls (step 27a).
- **No launch that a stranger makes has ever happened here.** Every run in this project came from a
  terminal, which attaches to a console that already exists and therefore shows nothing new, or
  from `--searchshot`, which has no window at all. That is exactly why the console window went
  unnoticed for the whole project, and it is why step 50 is by eye: the csproj's `OutputType` is
  asserted and the diagnostics are proven to reach a terminal, but nothing reads the subsystem out
  of the built PE header, and the absence of the window on a double-click, a sign-in and an
  elevated logon task is not something this machine can show.

## Notes

Steps 1 to 4, 9 to 13 and 29 to 59 are the ones that have never executed in any form, except 32,
which is done, and 35, which has run and needs reading. Steps 5 to 8 have been verified by log
line and by inspection, but not by eye.

`docs/e2e-run-sheet.md` is the order to work through them in.
