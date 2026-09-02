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

- **The encoders that turn a picture or a passage into a vector**: SigLIP-2's image and text
  towers, which put a photo and the words you would use to look for it in the same space, and
  multilingual e5 for documents and transcripts. Each one opens on whichever execution provider
  will have it, so there is a single answer to what this machine chose and why. Indexing asks
  for the graphics card, where the work is measured in hours; typing a query runs on the
  processor, where it is measured in milliseconds.

- **A privacy policy** at `PRIVACY.md`, saying what Findra stores, where, and the single
  request that leaves the machine. It states plainly that the index holds the text of the
  documents it indexed and is not encrypted, which is one reason content indexing is off
  until you ask for it.

- **Resumable model downloads, fetched by the interface and never by the indexer.** A model
  file already on disk at its full size is never re-requested; a partial one resumes from
  the byte already fetched rather than starting over; a connection that closes early leaves
  a short file on disk to resume from instead of promoting it under the final name; and a
  `.part` from a file that has since been republished smaller is discarded and re-fetched
  from the start rather than mistaken for a finished download.

- **Capabilities you install one at a time, priced by what they actually add.** Searching
  photos and video, reading meaning out of documents, and transcribing speech are separate
  downloads, and speech in Hebrew is a refinement of speech rather than a substitute for it.
  They are not independent of each other: a transcript is searched exactly like a document, so
  taking speech takes the document models with it, and Hebrew needs the general speech model
  that decides which recordings are Hebrew in the first place. Every size Findra shows you is
  what that row would add to what you have already chosen, so speech costs 818 MB on its own
  and 547 MB once documents are in, and the total is the files themselves rather than the sum
  of the rows. What is installed is read from the files on disk rather than from a setting, and
  a capability missing half its files counts as absent.

- **Sound, video frames, and the words inside pictures.** A recording's sound track is decoded
  with the codecs Windows already has and transcribed, and the lines are gathered into windows
  a whole sentence fits inside, so a phrase is still findable when the speaker paused in the
  middle of it; a recording in Hebrew is transcribed a second time by a model that reads it
  properly. A video is sampled across its whole length rather than by a fixed step, so a
  ten-hour film costs the same handful of frames as a ten-minute one. And the words inside a
  screenshot are read by the recognisers Windows already ships - no download, nothing to
  install, and nothing said when a language is not on the machine.

- **A picture on the card.** Selecting a result now shows the real thing: a photo decoded at
  preview size with its orientation honoured, the thumbnail Explorer would show for the
  formats Skia cannot read, and - for a video matched at a moment in it - that frame rather
  than the poster.

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
