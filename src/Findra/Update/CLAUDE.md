# The update window

Rules for `src/Findra/Update/`. The check itself (`UpdateCheck`: the 24 hour gate, the winget
catalogue, comparing versions) is in `src/Findra/App/CLAUDE.md`.

**Check for updates** in the tray and **Check now** in Settings open one small window; nothing else
does, and the daily background check never does.

- **`UpdateFlow` is the window as a list of states**, pure and tested; `UpdateSession` starts what
  each move names through delegates; the window only paints and reports presses.
- **The check keeps this machine's installer from the same response** (`InstallerOf`, by
  `ProcessArchitecture`); an entry with no `sha256:` digest is never offered.
- **Downloads are GitHub-only, over HTTPS, capped at 512 MB, and checked** (size and SHA-256) before
  anything runs. Redirects are followed one hop at a time, each held to the host rule.
- **Findra never replaces its own files.** The installer runs with
  `/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-`; winget with exactly `upgrade --id blakazulu.Findra
  --exact --accept-source-agreements --accept-package-agreements --disable-interactivity`, no
  window, both streams drained, killed at 20 minutes. A hand-off that returns at all installed
  nothing, because the installer stops Findra first.
- **`--stop` exits 2 when it stopped a running interface**, and the installer starts Findra again
  through `explorer.exe` (never elevated) only after a silent install that stopped one.
- **While the installer or winget runs, the window cannot be closed**: they cannot be called back.
- **Inside a Store package** the window opens on `UpdateStep.Store` (Open Store), and About drops
  the check-for-updates switch.
