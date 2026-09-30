# Settings

Rules for `src/Findra/Settings/`. Keyboard and screen readers are in `src/Findra/Look/CLAUDE.md`.

## Models toggle

The **Models toggle** section (`Section.AddOns`, `SettingsModel.AddOnRows`) draws the rules in
`src/Findra/Models/CLAUDE.md` ("Add-ons: off, remove, catch up"): read them before changing a row.

## Other rows

- **`IndexPowerLevels` is the one list of duty-cycle levels**, like `TranscribeLimit.ShortName`: one
  table, every level inside `Config.Load`'s clamp, and the row writes the NUMBER, never the index.
- **"Start now" sits beside the toggle**: it writes the configuration AND asks the shell.
- **Settings > About**: "Details for a bug report" copies `MachineReport` (opens no model, sends
  nothing); the log button opens the log FOLDER (the path is otherwise only in `--version`).
