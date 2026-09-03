# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

**`docs/superpowers/specs/2026-09-01-findra-design.md` is the contract** - read it before
writing anything. Everything below is a summary of the parts that are easy to get wrong, not
a replacement for it.

Findra is a standalone Windows desktop search widget: a capsule on the desktop that unfolds
into a results card, plus a global hotkey. .NET 10, Avalonia, SkiaSharp, SQLite.

All six plans have landed. The tree holds the three processes and the name pipe, the palette
layer and the card, the capsule, tray and hotkey, the FTS5 content store and the indexer child,
the model store and the per-capability gates, the settings window and the first-run screen,
`--uninstall`, an Inno Setup installer, three GitHub Actions workflows, the winget manifests and
a README written out of real renders and real measurements. What is left is not a plan but
`docs/end-to-end-checklist.md`: everything that needs a UAC prompt, a sign-out, a real installer
or a public tag, none of which has ever run here.

## Commands

```bash
dotnet build -warnaserror -t:Rebuild           # zero warnings, always - and -t:Rebuild, because
                                               # an incremental build reports a false clean
dotnet test                                    # TDD applies to new code (see below)
dotnet publish -c Release --self-contained     # self-contained is required, not optional
pwsh -File build/Publish.ps1 -Rid win-x64      # the publish the installer and CI both use
pwsh -File build/Check-Diagnostics.ps1 -Exe publish/win-x64/findra.exe   # every headless mode
pwsh -File build/Check-Release.ps1 -Tag v0.1.0 # may this tag be released, and what are its notes
```

Six diagnostic modes are non-negotiable and are built from day one. They are how the app is
verified without a screen, and `--searchshot` in particular is how UI gets iterated headlessly:

```bash
findra.exe --searchprobe [query]      # whole path end to end; must report which process
                                      # answered, the current generation counter, and what
                                      # the content indexer is doing
findra.exe --searchmodels             # are models present, do they load, do they agree, and
                                      # which execution provider answered for each runtime
findra.exe --searchindex [file|folder|q:query]...   # what is indexed, what is queued; given
                                      # paths it queues and drains them, given q: it queries
findra.exe --searchshot out.png <state> [palette]   # sixteen states, listed below
findra.exe --searchtest               # engine self-check
findra.exe --searchbench [out.md] [corpus]   # measured numbers, as a pasteable Markdown
                                      # fragment; `corpus` is how many files it generates
findra.exe --version                  # print the version and log location, then exit
```

The `--searchshot` states are `SearchShot.States`, and that list is the only definition of them.
Nine draw the card, five the settings window and two the first-run screen:

```
capsule  empty  typing  results  noresults  many  adv  opening  openingempty
settings  settingsopening  settingssearches  settingscontent  settingsabout
firstrun  firstrundownloading
```

Two more modes are settings a person changes rather than diagnostics. **They survive the
first-run screen and the settings window rather than being replaced by them** - they are how the
capability path is exercised on a machine with no screen, in CI, and by anybody reporting a bug:

```bash
findra.exe --models                   # what is installed, and what each capability would add
                                      # given what is already there
findra.exe --models install <preset|cap[,cap]>   # justnames | recommended | everything, or
                                      # photos, meaning, speech, hebrew
findra.exe --content [on|off]         # is Findra reading inside files at all
findra.exe --content limit <length>   # off | 5 | 30 | 2 hours | no limit | any number of minutes
```

Four modes are neither a diagnostic nor a setting, and the first two are not run by hand:

```bash
findra.exe --names                    # the elevated name-index helper, started by the task
findra.exe --index <parentPid>        # the content indexer, started by the UI
findra.exe --uninstall [--purge] [--dry-run]   # stop everything, remove the scheduled task, the
                                      # autostart entry and the program files; --purge also
                                      # deletes the models, the index and the settings;
                                      # --dry-run prints the whole plan and changes nothing
findra.exe --stop                     # stop the interface, the indexer and the name helper
```

Anything beginning with `--` that is not one of these exits 1 and prints the list. A mistyped
mode must never fall through to the greeting, because a script checks the exit code.

The `--searchbench` fragment opens at heading level two and every section below it at level
three, so it pastes under the README's own `#` with nothing to edit. It refuses to print a
throughput rate for a run shorter than a second and says how to get a longer one.

`--searchshot` must learn every new palette and every new surface as it is written.

## The README is a product page

It has to sell Findra to someone who has never heard of it, so it carries screenshots and
numbers - and **both must be real**.

- **Screenshots come from `--searchshot`**, which draws the actual card with the actual
  painter. Every image is the product, not a mockup. Regenerate by running the command;
  never hand-edit. Record the command next to each image so anyone can reproduce it.
- **Every number comes from `--searchbench`** pasted verbatim and whole, with the machine named
  (CPU, RAM, disk class, accelerator, Windows build). A number without its machine is marketing,
  not measurement. Model sizes come from real files on disk, never the declared floors. The
  fragment is pasted with every section it printed, never the flattering half of it.
- **Never quote a throughput rate from a default-sized `--searchbench` run.** A run of a second
  or two disagrees with itself by more than a published `files/min` or `MB/s` deserves. Quote the
  latency tables, the enumeration numbers and the store sizes, which are stable at any size; if
  an extraction rate is quoted at all, regenerate it with a corpus of at least 10,000 and say in
  the sentence above it that that is what produced it.
- **No claim appears that a reader cannot reproduce** with a command from the README itself.
- **No comparative claims against named competitors** - Findra cannot benchmark them fairly.
  `tests/Findra.Tests/Build/Repo.cs` holds the one name list the README test and the winget
  listing test share. It is deliberately incomplete: one well-known tool's name is an ordinary
  English word and also one of Findra's own presets, so it cannot be grepped for. That one is a
  reading, not a test.

The README was written last, out of the surfaces and the benchmark. Every image in it is a
`--searchshot` render with the command that produced it printed underneath; regenerate by
running the command, never by hand-editing an image.

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
words in documents  ─  free, opt-in (FTS5, no model)
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
- **Reading inside files is off until somebody asks**, models or no models. Names are
  searchable the second Findra starts, because a name index costs seconds; looking inside
  files walks every drive and can run for hours, so it never begins on its own. `--content on`
  starts it, `--content off` stops it without discarding anything already read, and the setting
  survives a restart. An index nobody has asked for and a finished index have identical counts,
  so every surface says which one it is looking at rather than printing "up to date".
- **One number, in minutes, decides how long a recording is worth transcribing**, covering
  sound files and video together. Zero is off, negative is no limit, positive is minutes, five
  by default. A recording over the limit is passed over with a reason of its own, and raising
  the limit goes back for exactly those and nothing else.
- **A file's size on disk never equals the declared size in the table.** That table is the
  spec's figure in megabytes to one decimal place; real files miss it by tens of kilobytes,
  mostly upward. `ModelStore.SizeSlack` is the only place that width is decided, and nothing
  may compare a file's length to `Model.Bytes` for equality.

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

`Derived.Tile` and `Derived.Chip` are painted by the settings surfaces and are in the legibility
check in `--searchtest` and in `DerivedTests`. Nothing in either is "reserved for later" any more.

## The typeface

Quicksand ships inside the application, one weight, embedded from `assets/fonts/Quicksand-Regular.ttf`,
under the SIL Open Font License 1.1. `assets/fonts/OFL.txt` reaches the publish folder and
therefore the installer, because OFL condition 2 makes the licence travel with every copy.

**`Parts.Face` is the only place in `src/Findra` that resolves a typeface.** The card, the
capsule, both settings surfaces and `--searchshot` all draw through it, and a missing resource
falls back to the system default with a log line rather than stopping the application - a type
initialiser that throws is unreportable.

That single resolver is why a label's fit is **measured** rather than eyeballed: the tests that
check a label into its pill and the test that checks the column is not wider than it needs to be
are each other's opposites. **If a label is ever tight, shorten the label.** Do not widen the
tolerance and do not move the column - satisfying one of that pair by moving the geometry breaks
the other. Bold is `SKFont.Embolden` on the same face; there is no second file.

## Shipping

- **The version lives in `Directory.Build.props` and nowhere else.** No `<Version>`,
  `<AssemblyVersion>`, `<FileVersion>` or `<InformationalVersion>` in any `.csproj`.
  `IncludeSourceRevisionInInformationalVersion` is off and `BuildInfo.Normalise` strips anything
  from `+` onward anyway, because `Version.TryParse` rejects `1.2.0+sha`, `UpdateCheck.Compare`
  answers 0 for what it cannot parse, and 0 is routed to "up to date" - a permanent lie.
- **A tag with no matching `CHANGELOG.md` section fails the release, and the notes are that
  section.** GitHub's generated release notes are never turned on. `build/Check-Release.ps1` is
  the gate, each refusal has its own exit code, and the workflow calls it rather than knowing it.
- **No pre-release tags.** The comparison cannot order `1.2.0-rc.1`, and `/releases/latest`
  excludes prereleases, so such a tag ships a release nobody's update check will ever see. The
  refusal in `Check-Release.ps1` is the line to delete when the comparison learns semver
  prereleases.
- **Nothing but a person on the Actions tab publishes to the winget catalogue.** No `push`,
  `tag`, `release` or `schedule` trigger may reach `winget.yml`. A mis-tagged GitHub release is
  recoverable; one that reaches the catalogue is somebody else's `winget upgrade`.
- **Both architectures ship from the first release, in one manifest.** `win-x64` and `win-arm64`
  from one matrix, two `Installers:` entries under one `PackageVersion`.
- **The signing step is a placeholder that does nothing, and nothing anywhere claims otherwise** -
  not the README, not the installer, not the release body, not the winget listing, and not
  `docs/code-signing-policy.md`, whose status note is coupled to the empty step by a test.
- **The installer is a third distribution route and `installer` a fourth install source**, beside
  `winget`, `source` and `unknown`. Spec §2 and §9b still say two and three; the plan document
  argues the amendment and this line is the record until the spec is amended.
- **No version number in the install directory**, and `AppId` is a fixed GUID: the scheduled task
  stores an absolute path to `findra.exe`, so a versioned directory points an elevated logon task
  at a binary that no longer exists after every upgrade.

## Data locations

Config roams, bulk does not - 2.9 GB of models must never sit in a roaming profile, and models
must never live in the publish folder.

| Path | Holds |
|---|---|
| `%APPDATA%\Findra\config.json`, `palettes.json` | settings |
| `%LOCALAPPDATA%\Findra\models\` | the seven model files |
| `%LOCALAPPDATA%\Findra\index\` | SQLite name, FTS5 and vector stores |
| `%LOCALAPPDATA%\Findra\logs\` | `findra-YYYYMMDD.log` |

## Versions and updates

Findra knows its version, learns whether a newer one exists, and tells you. **It never
installs anything by itself** - no self-updater, no background installer, no elevation for
updates. Replacing a running executable and re-registering an elevated scheduled task are the
two operations most likely to leave a machine broken; winget already solves that correctly.

The update check is the **one exception** to "nothing leaves the machine", and it is written
down rather than buried (spec §9b): an anonymous HTTPS GET to the GitHub Releases API, at
most once per 24 hours, on startup, in the background. No query parameters, no machine or
install identifier, nothing about files or searches. It never blocks anything - a failure is
a log line, not a dialog. On by default, disclosed on the first-run screen, and switchable
off, where off means no request is made.

It reports the action matching how the user installed: `winget upgrade blakazulu.Findra`, or
a link to the release notes for a source build. The install source is recorded at first run,
not guessed each launch.

**Compare parsed version numbers, never strings** - `1.10.0` is newer than `1.9.0`, and a
check that gets that wrong is worse than none, because it tells people they are current when
they are not.

## Install, resume and uninstall

**Installing never discards work that is still good.** On startup, inspect what is on disk
and continue from it: models present and correctly sized are kept, partial downloads resume
from the byte already fetched, an index with a current schema is used as-is and a non-empty
queue is **resumed**, an older schema is migrated with only the invalidated files re-queued.
"Done" is a fact the index records - schema version, consumed USN position per volume,
pending queue - not a guess. Re-downloading 2.9 GB or re-indexing a finished disk because an
upgrade did not look first is the worst thing this product could do to someone; it gets a test.

The name index is exempt - it lives in RAM in the helper and is rebuilt by MFT enumeration
in seconds at every logon. Only content survives restarts.

**Uninstall always removes** the app files, **the `HighestAvailable` scheduled task**, any
autostart entry, and stops the helper and indexer first. Missing the scheduled task orphans
an elevated logon task pointing at a deleted binary on a stranger's machine - that is a
defect, not an inconvenience.

**Uninstall keeps by default** `%LOCALAPPDATA%\Findra\models\`, `index\`, and
`%APPDATA%\Findra\` config. Deleting them is opt-in via a checkbox and a flag, and the prompt
states the **measured** size it would free, not a vague warning.

**`findra.exe --uninstall` is a first-class mode** (with `--purge` to also delete data),
because the `dotnet publish` route has no installer - without it, everyone who built from
source is left with an elevated scheduled task and no supported way to remove it.

## No lineage

**Findra is a separate project, not a fork or a component.** Everything in it is owned outright:
namespaces are `Findra`, and log tags, probe markers, config paths and file names follow.

Nothing anywhere - the README, the UI, a commit message, a manifest, a build script or a code
comment - may describe Findra as derived from another project, name one, or describe behaviour
that is not Findra's. This is not a name grep: the leaks that survive one are doc comments
explaining another product's behaviour, magic constants justified by another product's history,
and vocabulary that belongs to a different object model. Read the comments; ask of each whether it
reads as written for Findra by somebody who has never seen another codebase.

The same rule covers this repository's own development vocabulary. A shipped comment may not cite
a plan, a task number or a review, because those documents are not in the tree a reader has. Name
the thing rather than the document that asked for it.

## The changelog

`CHANGELOG.md` is updated on **every commit**, in the same commit, in its pathspec. Never
leave it for a sweep at release time.

It is load-bearing rather than decorative. The release workflow reads the section matching a
tag and uses it as the release notes, and a tag with no matching section fails the release.
It is also where Findra's own update check sends someone who built from source.

The format is [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/): entries go under
`## [Unreleased]` in one of Added, Changed, Deprecated, Removed, Fixed or Security, and move
into a numbered section on release. Write each line for a person reading release notes, not
for a reviewer reading a diff.

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
