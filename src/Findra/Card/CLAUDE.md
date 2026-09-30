# The card, the capsule and the progress pill

Rules for `src/Findra/Card/`. Screen readers, cursors and fitting the screen are in
`src/Findra/Look/CLAUDE.md`; the root `CLAUDE.md` holds what applies everywhere.

## The card

Four pills stack beside the field: Content, Advanced, **Settings** and **reading** (`ReadingPill`:
"Start now" / "Starting..." / "Stop"; the shell does both halves through `ApplyConfig` and
`ISettingsHost.StartIndexing`).

- **Clicking elsewhere does NOT close the card.** Esc (clears, then closes), the close button, the
  hotkey, the capsule and opening a result do. Deactivation only closes the dim window.
- **The close button hangs off the card's top-right corner**, so the window is
  `SearchCardLayout.Overhang` wider and taller than the card: size windows and bitmaps with
  `WindowWidth`/`WindowHeight`; `Paint` draws the card that far down. `HitTest` takes CARD
  coordinates (callers subtract `Overhang` from y); the close button is its one negative answer.
- **The header's left says what the rows ARE** (count and `Results.Query`), **the right what is
  happening** (`searching…`, a note, or the timing): `SearchCardPainter.Header`. `contentloading`
  is a words-only answer while `Semantic.Loading` (`ContentBranch.StillLoading`); the card asks again
  once the models are in (`SearchCardState.AskAgain`, on the tick).
- **`contentwaiting` is the Content pill NOT offering** (reading on, nothing read yet), hovered on
  purpose: suppressing the hover fill is half of what makes a dead control read dead.
- **`SearchCardLayout.HeaderRight` is the field's right edge, not the card's**, or a timing is drawn
  across the Settings pill.
- **The field's editing is `FieldEdit`** (pure: selection anchor, replace, word jumps, double-click
  word); `FieldCaret.Spans` places the highlight through the same bidi cells as the caret. Ctrl+C
  copies a selection, else the highlighted result's path.
- **The empty card's height is the hint's OR the column's, whichever is larger.**
- **The pills do not ellipsise.** The painter's labels and empty hints are named constants so
  `CardPillTests` measures what is drawn; the stage buttons' labels likewise.
- **The stage's picture gives way to its text** (`SearchCardLayout.StagePicture`): beside six rows
  or fewer the stage is only `StageMinH` and the picture shrinks so every line sits above the
  buttons. `CardStageTests` reads the pixels just above the top button.
- **The empty field shows what was opened lately** (`OpenedHistory`, `opened.json`, 200 paths, never
  a query): `SearchCardState.Recent`. **Every layout call takes `ShowsBody`, never `HasQuery`**
  (which decides only the placeholder). The first keystroke drops the recent rows; the stage says
  "opened" and shows no score (`--searchshot recent`).
- **Opening lifts a file's ORDERING score, never its shown score**: `ResultMapper.Finish(lift:)`,
  Best sort only, at most `OpenedHistory.MaxLift` (0.1), so it reorders neighbours and never lifts a
  poor match over a good one. `Config.RememberOpened` off deletes the file.
- **`ContentPill.Decide` owns what pressing Content means**, not `CardWindow`. Release: search
  again. Files read and reading off: turn reading back on. Nothing read: open Settings at Content. A
  count nothing has read yet: search.

## More like this

- **It is a query, not a mode.** The solid-accent button above Open (and Ctrl+L) writes
  `like:"<full path>"` (`SearchQuery.LikeQuery`) into the field and searches, so chips, sort, Esc,
  the generation counter and filters beside it work unchanged. A `like:` query is CONTENT-ONLY and
  runs whatever the Content pill says (`CardWindow.RunSearch`); plain words beside it are ignored.
  The header reads `8 like "name"` (`SearchQuery.LikeOf`, never a full parse per paint).
- **Drawn for a content kind, pressable once `SimilarReady`**: `SearchCardState.OffersSimilar` is the
  row's kind; `SimilarReady` (a vector store AND `ContentDb.HasVectors`) is decided off the
  interface thread in `HighlightChanged`, never in the painter. Not ready: `Dim` ink, unavailable
  to UI Automation, the plain arrow. `SearchCardLayout.SimilarRect` keeps its room for EVERY row so
  the picture does not jump; `HitTest(similar:)` answers `Similar` only where it is drawn. The lens
  is the mark's 16 px form (`TrayIconFactory.DrawMark(small:)`). Both looks are in the legibility
  checks (`DerivedTests`, `--searchtest`).
- **`ContentBranch.Like` asks with the file's own vectors, no encoder**: a photo or video's picture
  rows (frames sampled evenly to `LikeSamples`) ask picture kinds, anything else its passage or
  speech rows ask word kinds; best score per file, the file itself left out, every read inside the
  `SearchLock`/`Follow`/`VectorsPath` retry. No store: `LikeNothingToCompare`; no vectors:
  `LikeNotReadYet`. `items_path_nocase` is what lets `ItemByPath` seek. Its scales are in
  `src/Findra/Content/CLAUDE.md`.
- `--searchshot similar` draws the result, `many` the greyed-out button.

## The progress pill

Under the card and under the capsule's bar: dial, what is being read, count, percentage. **The pill
IS the bar** - the fill runs underneath the words.

- **`ProgressPill` paints it for both surfaces**; each supplies only its rectangle, and the painter
  measures both ends and lays the track in what is left.
- **`IndexStatus` owns both shapes**: `Line` (card footer, tray tooltip) and `Pill`. One composer.
- **Shown only while there is work in hand; `IndexStatus.Pill` takes no argument that could say
  otherwise.** Reading off, no live indexer or an empty queue draws nothing - never a bar at 0 or
  100% (spec §3), and there is no `evenWhenSettled`. The **Content pill** answers whether Findra is reading. `--searchprobe` prints
  what the pill would draw, and names the settled state in `Pill`'s order.
- **It hangs BELOW the card.** `SearchCardLayout.Height` is the card; `WindowHeight` adds the pill's
  band. Size windows and bitmaps with the second, draw and hit-test with the first.
  `CardProgressTests` reads a pixel column: card, nothing, pill.
- **The label is a word, never the `ResultKind`.** `IndexStatus.Doing` switches on the ENUM; an
  unrecognised row falls back to the bare verb.
- **`CapsulePainter.Placeholder` is one constant**, so `--searchshot capsule` draws the real string.
- Two open paths, two dim behaviours: from the capsule, dim the capsule's monitor; from the hotkey,
  the monitor under the cursor.
