# Changelog

All notable changes to Findra are documented here.

The format follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and Findra follows [Semantic Versioning](https://semver.org/spec/v2.0.0/).

## [Unreleased]

Findra has not been released yet. Everything below is the work so far, and it moves
into a numbered section on the first release.

### Added

- **Search by name across NTFS volumes.** An elevated helper reads the Master File
  Table and the change journal and holds the name index in memory; the interface runs
  unelevated and asks over a local named pipe. Exactly one call needs administrator
  rights, and it is the one that opens the volume.
- **A query grammar** covering words, phrases, globs, negation, `ext:`, `type:`,
  `in:`, `size:`, `modified:`, `created:`, `accessed:`, `case:`, whole-word, regular
  expressions, and alternatives.
- **The results card and the desktop capsule**, drawn directly with Skia. The card
  unfolds from the capsule, takes the keyboard, opens a result, and drags a row into
  Explorer.
- **Six palettes**, three dark and three light, derived from four colours each. The
  pair you choose follows the Windows light and dark setting, or pins to either.
  `%APPDATA%\Findra\palettes.json` accepts your own.
- **A global hotkey with a fallback chain**, because the first combination is already
  taken on some machines. Findra says which one it registered, and says so plainly if
  none could be.
- **A tray icon** carrying the version, the hotkey, and the update state.
- **A version check**: one anonymous request to the GitHub releases API, at most once
  a day, in the background. It never installs anything, and switching it off means no
  request is made.
- **Settings** in `%APPDATA%\Findra\config.json`, which cannot stop the application
  starting however badly they are edited.
- **Six diagnostic modes** so the product can be verified without a screen:
  `--searchprobe`, `--searchtest`, `--searchshot`, `--searchindex`, `--searchmodels`,
  `--searchbench`, plus `--version`. The first three run today; the rest arrive with
  the work they report on.
- **The content store**: a SQLite full-text index with a recorded schema version, a
  resumable work queue, and a consumed journal position per volume, so an interrupted
  index continues rather than starting over.

### Security

- The elevated helper parses no file content. It reads names, paths, sizes and
  modification times - metadata the filesystem hands over without opening anything -
  and never a byte of a file. Every decoder runs in the unelevated indexer, because
  decoders read arbitrary files found on disk and are the most likely thing a
  malformed file could exploit.
- The named pipe is restricted to the current user, and the interface verifies the
  owner before trusting a connection.

[Unreleased]: https://github.com/blakazulu/findra/commits/main
