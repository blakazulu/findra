# Findra - design

**Date:** 2026-09-01
**Status:** approved design, ready for an implementation plan
**Repo:** https://github.com/blakazulu/findra

---

## 1. What Findra is

Findra is a standalone Windows desktop search widget. A capsule sits on the desktop;
clicking it unfolds a results card in place. A global hotkey opens the same card centred
on whichever monitor the cursor is on. A tray icon holds quit, settings and download
status.

It searches four kinds of thing:

| Kind | How | Needs a model |
|---|---|---|
| **Names** | NTFS Master File Table, read directly, kept live by the USN journal | no |
| **Documents** | SQLite FTS5 over text pulled from PDF, Office, code, plain text | no |
| | e5-base embeddings over the same text, for meaning rather than exact words | yes |
| **Photos and video frames** | SigLIP-2 image and text towers, cosine similarity over stored vectors | yes |
| **Speech** | Whisper large-v3-turbo, with a Hebrew fine-tune as a second pass; transcripts are then searched as documents | yes |

Names answer in single-digit milliseconds with no model, no network and no wait. Everything
else is optional and consented to explicitly.

**Non-goals.** No cloud, no telemetry, no account. Nothing leaves the machine. No plugin
system, no scripting host, no multi-widget suite - Findra is one surface, and the design
leans on that everywhere.

---

## 2. Distribution

Two supported routes, both onto a stock Windows machine:

- `winget install <Publisher>.Findra`
- `git clone && dotnet publish -c Release`

**Self-contained publish.** Findra carries its own .NET runtime. A stranger installing from
winget must never meet a "install .NET first" prompt, and the `dotnet publish` route must
work on a machine with only the SDK. This costs roughly 60-70 MB in the artefact and is
worth it.

**No install-time UI exists on either route.** winget shows a package size, not a first-run
download; `dotnet publish` has no install step at all. So the consent for model downloads
lives in a first-run screen inside the app (§6). The winget manifest and README carry the
size as text; the app carries the actual consent.

**Models are never in the publish folder.** They download on first run into
`%LOCALAPPDATA%\Findra\models`.

---

## 3. Process architecture

Three processes. This split is the single most consequential decision here and is built in
from the first commit, because retrofitting it means re-plumbing the whole query path.

```
  ┌─────────────────────────────┐
  │  findra.exe --names         │   elevated, headless
  │  NtfsVolume + NameIndex     │   started by a logon scheduled task
  └──────────────┬──────────────┘
                 │  named pipe: queries in, matches + journal events out
  ┌──────────────┴──────────────┐
  │  findra.exe                 │   normal integrity, the UI
  │  grammar · ranking · card   │   settings · content search · tray · hotkey
  └──────────────┬──────────────┘
                 │  parent/child, SQLite as the handoff
  ┌──────────────┴──────────────┐
  │  findra.exe --index         │   normal integrity, child of the UI
  │  PdfPig · ONNX · Whisper    │   dies with the UI
  └─────────────────────────────┘
```

### Why exactly one elevated process, and why it is thin

Exactly one call needs administrator rights: `CreateFile(\\.\C:)`, which serves both
`FSCTL_ENUM_USN_DATA` (the initial enumeration) and `FSCTL_READ_USN_JOURNAL` (the live
tail). Nothing else in the engine does.

Running the whole app elevated is not an option for a public release. On a stock machine
that means a UAC dialog on every launch, and UIPI would block dragging a result row into
Explorer or any other normal-integrity app - which is one of the card's better features.

The helper is deliberately **thin**, and the reason is security, not effort. The content
indexer's job is running native decoders - PDF parsers, ONNX runtimes, Whisper, image
codecs - over arbitrary files found on the user's disk. That is precisely the code most
likely to be exploitable by a malformed input, and it must never run at high integrity. The
elevated helper opens a volume handle and scans bytes; it never parses untrusted file
content.

### Registration

The helper registers itself on first run via a `HighestAvailable` logon scheduled task
(`schtasks` with an XML definition). One UAC prompt, once, ever.

> **Trap.** `schtasks` CSV column headings are localized but the XML is not. Query and parse
> the XML form, never the CSV columns.

### The wire protocol

A local named pipe, message-framed, one connection.

- **`query`** - a parsed name query goes out, matching records come back. The reply carries
  the **generation counter** the query was stamped with. The UI discards any reply whose
  generation is not the current one. Without this, a slow answer to an abandoned query
  arrives late and overwrites a newer result. This counter exists on both ends from the
  first commit.
- **`journal`** - the helper streams USN change events. The UI decides what to enqueue for
  content indexing.

**Consequence to accept:** name search stops being an in-RAM `IndexOf` on every keystroke
and becomes an async round trip. It stays comfortably inside one frame over a local pipe,
but the code must be written async from the start, not adapted later.

**Consequence to accept:** the rule *"the parent decides what is indexed"* survives, but the
parent is now a different process from the one watching the journal.

### Lifetime

Content indexing runs only while the UI runs - the indexer is its child, so quitting stops
it by construction, with no lifetime code to write. This is the intended behaviour. The UI
must say so plainly ("indexing is paused because Findra is closed") rather than looking
idle. Findra lives in the tray, so in practice it is always up.

---

## 4. Data locations

The source engine put everything under Roaming, models included. Findra splits it, because
2.9 GB of model files must never sit in a roaming profile.

| Path | Holds |
|---|---|
| `%APPDATA%\Findra\config.json` | settings - small, roams correctly |
| `%APPDATA%\Findra\palettes.json` | user-authored palettes (§7) |
| `%LOCALAPPDATA%\Findra\models\` | the seven model files |
| `%LOCALAPPDATA%\Findra\index\` | SQLite name, FTS5 and vector stores |
| `%LOCALAPPDATA%\Findra\logs\` | `findra-YYYYMMDD.log`, rotated |

---

## 5. Search subsystems

All four ship. Names and document full-text need no model and are always available. The
three model-backed capabilities are independently installable (§6).

- **Names.** Full MFT enumeration on first run, then the USN journal keeps it live. Held in
  RAM in the elevated helper.
- **Query grammar.** The full existing grammar ports unchanged: `OR`, regex, size and date
  ranges, `dc:`/`da:`, case sensitivity, whole-word, path scoping, kind filters.
- **Document full-text.** SQLite FTS5 over text extracted by the document decoders.
- **Photos and video frames.** SigLIP-2 vision tower embeds images and sampled video frames;
  the text tower embeds the typed query; cosine similarity over the vector store.
- **Speech.** Whisper turbo transcribes with language detection. A file detected as Hebrew is
  re-run through the ivrit fine-tune. Transcripts are then embedded by e5 and searched like
  documents.
- **Semantic documents.** e5-base embeddings, so "the bill" finds the invoice.

---

## 6. Models and capabilities

### The seven files, measured on disk

| File | Serves | Size |
|---|---|---|
| `siglip2-vision.onnx` | indexing photos and video frames | 354.8 MB |
| `siglip2-text-q.onnx` | the typed query, for photos | 270.3 MB |
| `siglip2.spm` | its vocabulary | 4.0 MB |
| `e5-base-q.onnx` | documents **and transcripts** | 265.7 MB |
| `e5-small.spm` | XLM-R vocabulary | 4.8 MB |
| `whisper-turbo-q5.bin` | speech, every language | 547.4 MB |
| `whisper-ivrit.bin` | speech, Hebrew | 1,549.3 MB |
| | **everything** | **2.93 GB** |

These are real file sizes, not the declared minimum-byte floors, which are conservative by a
wide margin. **2.9 GB is the number for the README and the winget manifest.**

### The capabilities are not peers

Two dependencies fall out of the engine and shape the UI:

- **Speech needs e5.** A transcript is embedded and searched exactly like a document, so
  enabling Speech pulls in the same e5 pair that "meaning in documents" uses.
- **Hebrew needs the general model.** Whisper turbo runs first for language detection; only
  files it calls Hebrew are re-run through the fine-tune. Hebrew is a *second pass*, not an
  alternative, and cannot be selected on its own.

```
  words in documents  ──────────────────────────────  free, always on
  photos & video      ──  siglip2 vision + text + spm     629 MB
  meaning in docs     ──  e5-base + e5-spm                270 MB
  speech              ──  whisper-turbo + [e5 pair]       550 MB (+270 if e5 not taken)
    └ hebrew          ──  whisper-ivrit  (requires speech)  1.5 GB
```

Every size shown in the UI is therefore the **marginal** cost given what is already
selected. A fixed per-row number would make the total visibly fail to add up.

### Independently installable

The engine currently gates on all-or-nothing: the indexer requires the full set before it
starts, and content search requires the four query-side models before it answers anything.
Findra replaces both with per-capability gates:

- in the indexer's per-kind dispatch - a kind whose capability is absent is skipped, not
  failed;
- in content search - an absent capability contributes no candidates and is not an error;
- in the card - a query that would have matched an uninstalled capability offers it
  ("searching inside photos needs 630 MB - get it?").

**Every capability degrades silently when its model is absent.** This is a hard rule, not a
nicety: a missing model is a normal state, not an error state.

**Enabling a capability later re-queues only the files it covers.** Nothing already indexed
is redone. The existing vector-schema migration is the pattern to follow.

### First run

A single screen, presets over a checklist:

- Three presets across the top - **Just names** (0 MB) · **Recommended** (900 MB: photos +
  document meaning) · **Everything** (2.9 GB). One click decides it.
- The full list below, so nothing is hidden. Touching any row moves the preset to Custom.
- Hebrew appears nested under Speech, and only when the system locale or installed
  languages include Hebrew.
- **"free" is printed on the documents row.** Someone who skips everything still gets names
  and full-text search, which makes "Not now" a safe choice rather than a broken one.

Second act, same screen: per-capability progress, resumable, closable to the tray. It must
survive a reboot and a dropped connection and pick up where it left off. Downloads are
already per-file and resumable; that is preserved.

---

## 7. Look

### Palettes

A palette is four values - **accent**, **ink**, **ground**, and an `isLight` flag - with
every fill, row, tile, edge, shadow and hover derived from them.

Six ship:

| | Name | Accent | Ink | Ground |
|---|---|---|---|---|
| dark | **Mond** | `#FA7E00` | `#EBDBC0` | `#14141A` |
| dark | **Brass** | `#D8A657` | `#EDE4D3` | `#0F1219` |
| dark | **Verdigris** | `#4FBFA0` | `#E3E4DA` | `#0D1311` |
| light | **Paper** | `#C2410C` | `#221F1A` | `#F4F0E6` |
| light | **Blueprint** | `#2F5FD0` | `#182432` | `#EDF2F8` |
| light | **Porcelain** | `#D93A3A` | `#101012` | `#FBFBF9` |

The card painter currently derives everything assuming a dark ground. **Making that
derivation ground-aware is a real piece of work, and it is paid exactly once** - after it,
every palette in either mode is four constants.

**Selection model.** The user picks one dark palette and one light palette, plus a mode:
*Follow Windows* (default) · *Always dark* · *Always light*. Auto-follow needs a pair, which
is why it is two picks rather than one.

**Extensible, not authorable.** `%APPDATA%\Findra\palettes.json` holds the palette list; the
six built-ins are its first six entries, and a user adds a seventh by appending one object:
`name`, `accent`, `ink`, `ground`, `light`. That five-field object is the entire public
contract, and it is stable. There is deliberately **no element/page manifest system** - that generality exists to let many different widgets share
one renderer, and Findra has one surface.

**Typeface:** Quicksand throughout, shipped with the app.

### Surfaces

1. **The search card.** Ported near-verbatim: the capsule, the unfold, bidi caret handling,
   drag-out to any app, pure-layout hit testing, result kinds, the preview pane, why-it-matched.
   This is the hardest UI in the product and it arrives already working.
2. **First-run download screen** (§6). New.
3. **Settings window.** New, replacing a panel that was shaped around a multi-widget host. A
   Skia-drawn card matching the search card, with a **section rail**: *Look · Opening it ·
   What it searches · Content · About*. Fixed-height pane; the only scrolling list is
   exclusions. A tall single-column card was rejected - the content is roughly 1,400px, and a
   scrolling list inside a scrolling card is hand-drawn hit testing not worth owning.
4. **Capsule right-click.** Palette and pause live here too, so most people never open
   settings.
5. **Tray icon.** Quit, settings, download status, and recovery when the capsule is lost
   off-screen.

**Accepted cost of a hand-drawn settings window:** no free keyboard navigation and no free
screen-reader support. Mitigated by keeping the frequently-touched controls on the capsule,
and by calling the OS dialog for folder picking, which is the one place a native control is
genuinely required.

### Opening it

- The desktop capsule, clicked.
- A global hotkey, `Alt+Space` by default, opening the same card centred on the monitor
  under the cursor.
- **Registration can fail** - `Alt+Space` is the system menu chord in some configurations.
  On failure Findra walks a fallback chain, takes the first combination that registers, and
  **tells the user which one it landed on**. It must never fail silently. Settings has a
  rebind control that reports "that combination is taken" when it is.
- **Two positioning modes means two dim behaviours.** Opening from the capsule dims the
  monitor the capsule is on; opening from the hotkey dims the monitor the cursor is on. The
  dim already takes a screen rectangle, so this is a caller decision.

---

## 8. What is ported, what is rewritten, what is new

Findra starts from an existing search engine held locally on this machine:

```
C:\Code\Personal\Prism\src\Search\                   engine, card, diagnostics - 25 files, 7,617 lines
C:\Code\Personal\Prism\src\Widgets\CardText.cs                131
C:\Code\Personal\Prism\src\App\BidiText.cs                    154
C:\Code\Personal\Prism\src\App\Log.cs                         234
C:\Code\Personal\Prism\src\App\StartupManager.cs              208
C:\Code\Personal\Prism\src\Rendering\ThemeRenderer.Search.cs  121   capsule painter
```

Findra is a separate project, not a fork or a component of anything else. Everything it
takes is copied in and owned outright: namespaces become `Findra`, and log tags, probe
markers, config paths and file names follow. No lineage is described or implied anywhere in
the shipped product - README, UI, commit messages or code comments.

| Tier | Content | Lines |
|---|---|---|
| **Copy, engine** | volume, name index, SQLite store, vector store, media, file kinds, document text, query grammar, content queue, indexer, content search, image text, preview decoder, encoders | ~4,040 |
| **Copy, UI** | card, advanced popup, caret, plus the two text helpers | ~1,390 |
| **Copy, near** | card window - its colour source changes from a theme manifest to a `Palette` | 847 |
| **Copy, support** | logging, startup task registration, capsule painter | ~560 |
| **Copy, diagnostics** | the five probe/shot modes | ~450 |
| **Split** | search service - the name path moves behind the pipe | 702 |
| **Rewrite** | settings panel and its window → sectioned rail | ~474 → new |
| **New** | entry point, tray, hotkey + fallback, pipe protocol both ends, palette layer and light derivation, per-capability gating, first-run screen, config | ~1,200 est. |

Roughly **7,300 lines copied or near-copied**, **~1,700 lines written new** (the rewritten
settings plus the new entry point, tray, hotkey, pipe, palette layer and first-run screen),
and **one 702-line service reshaped** around the pipe boundary.

---

## 9. Verification

Five diagnostic modes, built from day one, non-negotiable:

| Mode | Answers |
|---|---|
| `--searchprobe [query]` | does the whole path work, end to end, on this machine |
| `--searchmodels` | are the models present, do they load, do they agree |
| `--searchindex` | what is in the index, and what is queued |
| `--searchshot out.png <state>` | render the card headlessly, in any state |
| `--searchtest` | the engine's own self-check |

`--searchshot` is how the UI gets built without a screen - it renders the card offscreen to
a PNG in any state, and it is how the palettes and both new surfaces will be iterated. It
must learn the new palettes and the two new surfaces as they are written.

`--searchprobe` must also report **which process answered** and **the current generation
counter**, so a pipe problem is visible without a debugger.

## 10. Testing

**TDD for all new code** - the pipe protocol and its generation counter, the palette layer
and light derivation, per-capability gating and the dependency graph, the hotkey fallback
chain, config load/save and migration.

The ported engine arrives working and is not rewritten test-first; it gets characterization
tests only where Findra changes its behaviour - principally the all-or-nothing model gates
becoming per-capability.

The generation counter deserves an explicit adversarial test: a slow reply to an abandoned
query must never win.

---

## 11. License and attribution

**Apache License 2.0, with a `NOTICE` file.**

The requirement is: free to use, free to clone, free to change - but users must credit the
author and the original work. Apache-2.0 is the mainstream OSI licence that actually carries
a **propagating attribution requirement**: anyone redistributing must pass the `NOTICE`
contents along. MIT only requires the copyright line be retained inside copies, which does
not reliably produce a visible credit. Apache-2.0 also adds an explicit patent grant and
requires modified files to be marked.

`NOTICE`:

```
Findra
Copyright (c) 2026 blakazulu (Liraz)

Original work and project home: https://github.com/blakazulu/findra

This product includes software developed by blakazulu.
If you use, modify or redistribute this software, you must retain this
notice and credit the original author and project page.
```

The README states the same in plain language above the fold.

---

## 12. Decisions taken, and what they cost

| Decision | Chosen | Rejected, and why |
|---|---|---|
| Scope | all four subsystems | names-only, names+documents - the full product was wanted |
| Model install | per capability | all-or-nothing asks for a Hebrew speech model to get document search |
| Elevation | thin helper, names only | fat daemon runs untrusted decoders at high integrity; a service buys pre-logon indexing that is not wanted |
| Indexing lifetime | stops when Findra quits | explicitly chosen |
| Entry | capsule + hotkey | capsule-only is unusable with a maximised window; hotkey-first throws away the identity |
| Palettes | six, dark+light pair, follow Windows | one look is less than wanted; the light repaint is paid once either way |
| Theming depth | `palettes.json`, four fields | a full element manifest is generality for widgets that do not exist |
| First run | presets over checklist | a bare checklist makes every user do the arithmetic; tiles explain least |
| Settings | Skia card, section rail | one tall scroller breaks on volume; a native window breaks the identity |
| Publish | self-contained | a runtime prompt on a stranger's machine is unacceptable |
| Licence | Apache-2.0 + NOTICE | MIT does not reliably produce a visible credit |

**Known risks.**

1. *The light-mode derivation* is the largest single piece of new visual work, and it is
   inside inherited code rather than beside it.
2. *The pipe boundary* changes name search from synchronous to asynchronous everywhere. It
   must be async from the first commit.
3. *Hand-drawn settings* forfeits keyboard and screen-reader support. Accepted, mitigated,
   and worth revisiting if Findra gets real adoption.
4. *Scheduled-task registration* is the one thing that can fail on a stranger's machine in a
   way Findra cannot fix; it needs a clear, non-fatal failure path that still leaves
   names working on whatever it can read unelevated.

---

## 13. Out of scope

Multi-widget hosting · themes beyond colour · plugins or scripting · cloud or sync ·
telemetry · a pre-logon Windows service · macOS or Linux · indexing network shares.
