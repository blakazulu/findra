# The shell: tray, hotkey, config, update check

Rules for `src/Findra/App/`. The startup order in `App.axaml.cs` is in
`src/Findra/Startup/CLAUDE.md`; the update WINDOW is in `src/Findra/Update/CLAUDE.md`.

- **Hotkey registration can fail** (`Alt+Space` is the system menu chord in some configurations).
  Walk a fallback chain, take the first that registers, and **tell the user which one landed**
  (`FirstRunWindow.NoteHotkey` shows it on the welcome page).
- **The tray icon is drawn at runtime** (`TrayIconFactory`): no plate, its slot a real hole,
  asserted on three palettes through a `Render` seam. `DrawMark(small:)` is also the More like this
  button's lens.
- **Config roams, bulk does not** (`Config` in `%APPDATA%`, models and index in `%LOCALAPPDATA%`).
  `Config.Load` clamps every level; a new setting needs load/save/migration tests.

## The update check (`UpdateCheck`)

- **At most once per 24 hours, on startup, in the background**, an anonymous HTTPS GET to the GitHub
  Releases API - for a winget install, to `UpdateCheck.CatalogueUrl` (the catalogue's folder in
  `microsoft/winget-pkgs`), because `winget upgrade` can only install what the catalogue has. No
  query parameters, machine or install identifier. Never blocks; a failure is a log line. Off stops
  the daily check; **a manual check (`CheckAsync(manual: true)`) ignores the gate and the switch**.
- **It reports the action for how the user installed** (`winget upgrade blakazulu.Findra`, the
  installer, release notes for a source build); the install source is recorded at first run, never
  guessed. Inside a Store package it sends nothing.
- **Compare parsed versions, never strings** (`1.10.0` > `1.9.0`). **Both sides are checked**:
  `Compare` answers 0 if EITHER fails to parse, and that must not read as "up to date"
  (`BuildInfo.Normalise` strips `+sha` for this reason).
