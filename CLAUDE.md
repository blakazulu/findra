# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

There is no source yet. The repo holds a design spec and nothing else.
**`docs/superpowers/specs/2026-09-01-findra-design.md` is the contract** - read it before
writing anything. Everything below is a summary of the parts that are easy to get wrong, not
a replacement for it.

Findra is a standalone Windows desktop search widget: a capsule on the desktop that unfolds
into a results card, plus a global hotkey. .NET 10, Avalonia, SkiaSharp, SQLite.

## Commands

No `.csproj` exists yet. The spec fixes these as the intended shape:

```bash
dotnet build
dotnet test                                    # TDD applies to new code (see below)
dotnet publish -c Release --self-contained     # self-contained is required, not optional
```

Six diagnostic modes are non-negotiable and are built from day one. They are how the app is
verified without a screen, and `--searchshot` in particular is how UI gets iterated headlessly:

```bash
findra.exe --searchprobe [query]      # whole path end to end; must report which process
                                      # answered and the current generation counter
findra.exe --searchmodels             # are models present, do they load, do they agree
findra.exe --searchindex              # what is indexed, what is queued
findra.exe --searchshot out.png <empty|typing|results|noresults|many|adv|panel>
findra.exe --searchtest               # engine self-check
findra.exe --searchbench [out.md]     # measured numbers, as a pasteable Markdown fragment
```

`--searchshot` must learn every new palette and every new surface as it is written.

## The README is a product page

It has to sell Findra to someone who has never heard of it, so it carries screenshots and
numbers - and **both must be real**.

- **Screenshots come from `--searchshot`**, which draws the actual card with the actual
  painter. Every image is the product, not a mockup. Regenerate by running the command;
  never hand-edit. Record the command next to each image so anyone can reproduce it.
- **Every number comes from `--searchbench`** pasted verbatim, with the machine named
  (CPU, RAM, disk class, Windows build). A number without its machine is marketing, not
  measurement. Model sizes come from real files on disk, never the declared floors.
- **No claim appears that a reader cannot reproduce** with a command from the README itself.
- **No comparative claims against named competitors** - Findra cannot benchmark them fairly.

The README is written last, once the surfaces and the benchmark exist. Until then the repo
carries a deliberately plain placeholder that promises nothing it cannot yet show.

## Architecture

**Three processes.** This split exists from the first commit; retrofitting it means
re-plumbing the entire query path.

- `findra.exe --names` - elevated, headless, started by a `HighestAvailable` logon scheduled
  task. Owns the NTFS volume handle and the in-RAM name index, and nothing else.
- `findra.exe` - the UI, at normal integrity. Owns grammar, ranking, content search, settings,
  the card, tray and hotkey.
- `findra.exe --index` - the content indexer, a child of the UI.

Exactly one call needs admin rights: `CreateFile(\\.\C:)`, serving `FSCTL_ENUM_USN_DATA` and
`FSCTL_READ_USN_JOURNAL`. Nothing else does.

**The elevated helper must never parse untrusted file content.** Decoders (PDF, ONNX,
Whisper, image codecs) run over arbitrary files found on disk and are the most likely thing
to be exploitable by a malformed input. They belong in the indexer at normal integrity, never
in the elevated process. Running the whole app elevated is also rejected because UIPI would
block dragging a result row into Explorer.

**The pipe.** Local named pipe between helper and UI. Two consequences that shape all the code:

- **Name search is async everywhere.** It is a round trip, not an in-RAM `IndexOf`. Write it
  async from the start.
- **Every query carries a generation counter, stamped on the reply, checked by the UI.**
  Without it a slow answer to an abandoned query arrives late and overwrites a newer result.
  This needs an explicit adversarial test.

The helper also streams USN journal events; the UI decides what to enqueue. The rule "the
parent decides what is indexed" still holds, but the parent is no longer the process watching
the journal.

**Indexing stops when the app quits.** The indexer is a child of the UI, so this is by
construction - no lifetime code. The UI must say so plainly rather than looking idle.

## Capabilities and models

Model-backed capabilities are **independently installable**, and they are **not peers** -
there is a dependency graph:

```
words in documents  ─  free, always on (FTS5, no model)
photos & video      ─  siglip2 vision + text + spm            629 MB
meaning in docs     ─  e5-base + e5-spm                       270 MB
speech              ─  whisper-turbo + [e5 pair]              550 MB (+270 if e5 not taken)
  └ hebrew          ─  whisper-ivrit, requires speech         1.5 GB
```

- Speech needs e5 because a transcript is embedded and searched exactly like a document.
- Hebrew needs the general Whisper model: turbo runs first for language detection, and only
  files it calls Hebrew are re-run through the fine-tune. Hebrew is a second pass, never an
  alternative.
- **Everything is 2.9 GB** - measured file sizes, not the conservative minimum-byte floors.
- **Sizes shown in the UI are marginal**, given what is already selected. Fixed per-row
  numbers make the total visibly fail to add up.
- **A missing model is a normal state, not an error state.** Every capability degrades
  silently when its model is absent: the indexer skips that kind, content search contributes
  no candidates, and the card offers the download.
- Enabling a capability later re-queues **only the files it covers**.

## Hardware portability

Findra ships on winget and lands on machines nobody chose for it - AMD or Intel CPUs,
NVIDIA / AMD / Intel GPUs, integrated or discrete, or no usable accelerator at all.
**No capability may require a particular vendor**, and nothing may fail because of the
silicon it found.

- **Detect at runtime, never assume.** Try providers in order, take the first that
  initialises: ONNX (SigLIP-2, e5) is **DirectML → CPU**; Whisper is **Vulkan → CPU**.
  Both cover NVIDIA, AMD and Intel in one path.
- **No vendor-locked providers.** CUDA means NVIDIA only plus a large separate runtime;
  ROCm is not a Windows story. A portable path everywhere beats a fast path for a third
  of users.
- **CPU is a supported configuration, not a failure state.** Only the initial content
  index is slower - queries embed one short string. The UI says so rather than looking stuck.
- **No CPU-feature assumptions** - no AVX-512 requirement, no vendor-specific intrinsics.
- **`--searchmodels` reports the chosen provider and every one it rejected, with reasons.**
  "It's slow on my laptop" is unanswerable; "DirectML failed to initialise, fell back to
  CPU" is a bug report.
- **`--searchbench` records the accelerator** beside the CPU, RAM, disk and Windows build.
- **Never assume x64** - no hardcoded RID in source, no x64-only intrinsics. x64 ships
  first; keeping arm64 reachable costs nothing now and a lot later.

## Palettes

A palette is `name`, `accent`, `ink`, `ground`, `light`. Everything else - fills, rows, tiles,
edges, shadows, hovers - is derived. That five-field object is the entire public contract.

Six ship (dark: Mond, Brass, Verdigris; light: Paper, Blueprint, Porcelain). The user picks
one dark and one light, plus a mode: Follow Windows / Always dark / Always light - auto-follow
needs a pair, which is why it is two picks.

The card painter's derivation assumes a dark ground. **Making it ground-aware is paid exactly
once**; after that every palette in either mode is four constants. This is the largest piece
of new visual work and it lives inside inherited code.

There is deliberately no element/page manifest system. Users extend
`%APPDATA%\Findra\palettes.json`; they do not author layouts.

## Data locations

Config roams, bulk does not - 2.9 GB of models must never sit in a roaming profile, and models
must never live in the publish folder.

| Path | Holds |
|---|---|
| `%APPDATA%\Findra\config.json`, `palettes.json` | settings |
| `%LOCALAPPDATA%\Findra\models\` | the seven model files |
| `%LOCALAPPDATA%\Findra\index\` | SQLite name, FTS5 and vector stores |
| `%LOCALAPPDATA%\Findra\logs\` | `findra-YYYYMMDD.log` |

## Provenance

Findra starts from an existing search engine held locally at `C:\Code\Personal\Prism\src\Search`
(plus `CardText.cs`, `BidiText.cs`, `Log.cs`, `StartupManager.cs`, `ThemeRenderer.Search.cs`).
Roughly 7,300 lines are copied or near-copied, ~1,700 written new, and one 702-line service
reshaped around the pipe.

**Findra is a separate project, not a fork or a component.** Everything it takes is copied in
and owned outright - namespaces become `Findra`, and log tags, probe markers, config paths and
file names follow. No lineage is described or implied anywhere in the shipped product: README,
UI, commit messages or code comments. Referring to the source path when porting is fine;
describing Findra as derived from another project is not.

## Testing

TDD for all new code: the pipe protocol and its generation counter, the palette layer and light
derivation, per-capability gating and the dependency graph, the hotkey fallback chain, config
load/save and migration.

The ported engine arrives working and is not rewritten test-first. It gets characterization
tests only where Findra changes its behaviour - principally the all-or-nothing model gates
becoming per-capability.

## Gotchas

- `schtasks` CSV column headings are localized; the XML is not. Parse the XML form.
- Hotkey registration can fail (`Alt+Space` is the system menu chord in some configurations).
  Walk a fallback chain, take the first that registers, and **tell the user which one it landed
  on**. Never fail silently.
- Two open paths mean two dim behaviours: from the capsule, dim the capsule's monitor; from the
  hotkey, dim the monitor under the cursor.
- Scheduled-task registration is the one thing that can fail on a stranger's machine in a way
  Findra cannot fix. It needs a non-fatal path that still leaves names working on whatever can
  be read unelevated.

## Licence

Apache-2.0 with a `NOTICE` file. The requirement is free use, cloning and modification, with
propagating attribution to blakazulu and https://github.com/blakazulu/findra. Apache's NOTICE
is the mechanism that carries that forward; MIT would not.
