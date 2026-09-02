# End-to-end checklist

Everything here needs an elevated terminal, a screen, or a real disk, so no automated run
in this project has ever executed it. It accumulates as plans land, and is worked through
once on a real installed build.

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

## Notes

Steps 1 to 4 and 9 to 13 are the ones that have never executed in any form. Steps 5 to 8
have been verified by log line and by inspection, but not by eye.
