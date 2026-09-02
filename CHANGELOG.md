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
- **A memory-mapped vector store** for meaning-based search: every embedded photo,
  document chunk or transcript segment is a half-precision row tagged with its kind,
  appended by the indexer and ranked by a normalised dot product over the whole file -
  brute force on purpose, since a single pass beats an index below a million rows. A
  deleted or replaced file's row is zeroed rather than removed, so it can never answer
  a query again, and a reader only ever sees rows the writer has flushed.

- **The groundwork for searching inside photos, recordings and meaning**: the model store
  that knows which model files are present and what they cost, the accelerator selection that
  tries the graphics card first and falls back to the processor while recording every provider
  it rejected and why, and the vector store the embeddings live in.

- **A privacy policy** at `PRIVACY.md`, saying what Findra stores, where, and the single
  request that leaves the machine. It states plainly that the index holds the text of the
  documents it indexed and is not encrypted, which is one reason content indexing is off
  until you ask for it.

### Changed

- **Content indexing is off until you ask for it**, including the free full-text search of
  documents. Searching by name is always on and costs nothing. Reading inside files walks
  every drive and opens every document, so Findra no longer starts that uninvited.
- **One setting decides how long a recording is worth transcribing**, covering audio and
  video together. It defaults to short clips of about five minutes, offers longer presets
  and no limit at all, and accepts a number of minutes typed in. A recording over the limit
  is skipped rather than failed, so raising the limit later picks up exactly those files.
- **Findra now needs Windows 10 version 2004 (build 19041) or newer.** Reading words out of
  pictures, transcribing speech and pulling frames out of video all use decoders Windows only
  publishes from that build onwards, so the whole application is built against it.


### Security

- The elevated helper parses no file content. It reads names, paths, sizes and
  modification times - metadata the filesystem hands over without opening anything -
  and never a byte of a file. Every decoder runs in the unelevated indexer, because
  decoders read arbitrary files found on disk and are the most likely thing a
  malformed file could exploit.
- The named pipe is restricted to the current user, and the interface verifies the
  owner before trusting a connection.

[Unreleased]: https://github.com/blakazulu/findra/commits/main
