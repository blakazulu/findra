# The first-run screen

Rules for `src/Findra/First/`. The startup order the screen sits in (who waits for it, the reading
hold) is in `src/Findra/Startup/CLAUDE.md`; pricing of the rows is in `src/Findra/Models/CLAUDE.md`.

## The screen

- **A button's work is shown, never hidden behind a frozen window.** `FirstRunState.Work` (a
  `FirstRunWork`) is a step card over the page; the hit test refuses everything while it is up.
  "Get these" shows it and POSTS `Answered`, so the card is drawn first; the shell ticks each step
  (`StepDone`), awaits `FirstRunWindow.Frame()` after each, and calls `EndWork()` in a finally -
  which is when the next page (and the resize) happens. Steps may finish out of order (name search
  waits for UAC); the spinner stays on the first unfinished one.
- **The hit test takes the STATE, never the five bounds.** `FirstRunLayout.HitTest(x, y, state)`
  derives everything from what the painter reads. `FirstRun.LimitRow` is not
  `FirstRunLayout.BandRow` (which `SurfaceHeight` and the painter read); mixing them made the last
  question unclickable wherever a Hebrew row sits below Speech.
- **Once answered it is the welcome page** (`Welcome.cs`): where Findra lives (capsule, the hotkey
  that actually LANDED via `FirstRunWindow.NoteHotkey`, tray), what happens now, About with two
  links, and "Open settings" (the shell opens Settings after the page closes).
- **Screenshot states, so both halves of each painter get reviewed**: `firstrunfinished` (took
  reading: Later / Start reading) and `firstrunready` (plain, Done) are the finished shapes;
  `firstrunnames` is "Just names" (no bars, no shortcut, update checks off); `firstrunspeech` shows
  the transcription-limit row that appears only when Speech is ticked.
- **Row titles and notes must fit** (`FirstRunPainter.RowText` is the space measured and drawn).
  The Photos note says pictures are found by what is in them or written on them: the words in
  pictures come with Photos, never free.
