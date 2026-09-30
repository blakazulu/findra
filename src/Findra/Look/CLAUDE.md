# Screen readers, cursors, fitting the screen, palettes, typeface

Rules for `src/Findra/Look/`, and for every drawn surface that uses it (card, Settings, first-run,
update window, capsule).

## Screen readers and the keyboard

**Every drawn surface publishes its elements to UI Automation** (`Access.cs`): `CardAccess`,
`SettingsAccess`, `FirstRunAccess`, `UpdateAccess` and the capsule's one button are PURE lists of
`AccessNode`s (role, name, state, bounds in layout units) built from the same state and layout the
painter and hit test read. `AccessiblePeer` turns them into automation children.

- **Invoking an element presses its centre through the surface's own press path** (`PressAt`,
  shared with the pointer; the card's is `CardWindow.PressAt`, a `SearchHit` in, the action out, which
  `AccessInvoke` reaches by hit-testing the node's centre), so
  a screen reader can do nothing a click cannot. `AccessTests` holds every actionable node's centre
  to its own hit-test target (and index), per section and state. **A new control needs its node,
  or it is invisible to Narrator.**
- **Element peers derive from `ControlAutomationPeer` over the canvas** only because that is the one
  peer Windows can put on the screen (`ToScreenCore` is `private protected`); they are POOLED by
  kind and position, each subscribed to the canvas for its life.
- **Drawn elements cannot take Windows focus**: focus stays on the canvas, `FocusedKey` names the
  element, and a polite live region (`AccessiblePeer.Say`) speaks each move. Tab/Shift+Tab, Enter
  and Space in Settings, first-run and update (tunnelling `KeyDown`, or Avalonia's navigation takes
  Tab first); `AccessRing` shows only after the keyboard is used. The card keeps its own keys
  (typing, arrows, Enter, Tab through chips) and speaks counts and the highlight.
- **The Advanced popup is listed INSTEAD of what it covers** (reading pill, header, chips, rows,
  stage buttons): `adv:field:0..9` (Edit, `SearchAdvancedLayout.FieldName`, placeholder as help),
  `adv:check:0/1`, `adv:kind:0..5` (Option), `adv:button:0..2`, in `SearchAdvancedLayout.Stops`
  order. Its keyboard is `SearchCardState.AdvFocusOn` + `AdvFocus`: Tab walks every stop
  (`CardAccess.NextStop`), Space presses a check, chip or button (its character is swallowed), Enter
  presses a button or applies, Esc closes; typing lands only in a focused field. The search field
  answers `Field` while it is open (a press there puts the popup away).
- Verified with the UI Automation client (tree, bounds, Invoke, real Tab/Space) on the welcome
  screen; never with Narrator's speech. The popup's keyboard has only unit tests.

## Cursors

**`Pointers.cs` is the only place a cursor shape is decided**, mapped from each surface's own
hit-test answer.

- **The capsule body is the only Move cursor**; nothing that answers a click may offer to move.
- **The field and the advanced form's fields take the I-beam; everything else clickable the hand.**
- **Every arm is written out and the default throws** (a target added to `SearchTarget`,
  `PanelTarget` or `FirstRunTarget` and forgotten would show the plain arrow).
- `PointerCursor.Of` builds cursors ON DEMAND and keeps them, never in a static initialiser.

## Fitting the screen

**Every window fits the screen it opens on, at every scaling.** Layouts are fixed units Windows
multiplies by the scaling; the first-run page (928) is 1160 px at 125% on 1080p, and with no title
bar its buttons were unreachable (Store rejection 10.1.2.10). `ScreenFit.Factor` shrinks a surface
that does not fit (never grows, floor 0.5); the window scales the canvas by it and divides every
pointer position by it, so layouts, hit tests and `--searchshot` stay in layout units. First-run and
Settings resize through their own fit method (never set `Width`/`Height`), refit in `Opened`, and
`KeepInside` the working area; the card folds it into its zoom (`CardOverPlacement.FittedZoom`).
`ScreenFitTests` holds every surface to 1080p at 100-175%.

## Palettes

A palette is `name`, `accent`, `ink`, `ground`, `light`; everything else is derived, and that
five-field object is the entire public contract. Six ship (dark: Mond, Brass, Verdigris; light:
Paper, Blueprint, Porcelain); the user picks one dark, one light, and Follow Windows / Always dark /
Always light. The derivation is ground-aware and **paid exactly once**; every palette is then
constants. Users extend `%APPDATA%\Findra\palettes.json`, not layouts. Every derived pair drawn as
text on a fill (`Derived.Tile`, `Derived.Chip`, the More like this button) is in the legibility
check in `--searchtest` and `DerivedTests`.

## The typeface

Quicksand, one weight, embedded from `assets/fonts/Quicksand-Regular.ttf` (SIL OFL 1.1).

- **`Parts.Face` is the only place in `src/Findra` that resolves a typeface**; a missing resource
  falls back to the system default with a log line, never a throwing initialiser.
- Label fit is **measured** by opposing tests. **If a label is tight, shorten the label** - never
  widen the tolerance or move the column.
- Bold is `SKFont.Embolden` on the same face; no second file.
