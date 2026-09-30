# CLAUDE.md

Guidance for Claude Code in this repository. **`docs/superpowers/specs/2026-09-01-findra-design.md`
is the contract** - read it before writing anything; these files summarise what is easy to get wrong.

Findra is a standalone Windows desktop search widget: a capsule on the desktop that unfolds into a
results card, plus a global hotkey. .NET 10, Avalonia, SkiaSharp, SQLite. Shipped (0.9.0 and on):
releases page, Inno installer, winget, Microsoft Store. What is left is
`docs/end-to-end-checklist.md` (UAC, sign-out, real installer, public tag); `docs/e2e-run-sheet.md`
is the same items in phases, and `build/Check-E2E.ps1` answers what a script can.

## Where the rules live

This file holds what applies everywhere. Each folder below has its own `CLAUDE.md`, loaded when you
work there - **read the one for the folder you are about to change**:

| Folder | Holds |
|---|---|
| `src/Findra/Card/` | the card, More like this, the progress pill, header and stage rules |
| `src/Findra/Look/` | screen readers and the keyboard, cursors, fitting the screen, palettes, typeface |
| `src/Findra/First/` | the first-run screen and welcome page |
| `src/Findra/Startup/` | startup order and the reading hold, the helper task, one interface, uninstall, Store-package behaviour |
| `src/Findra/App/` | hotkey, tray icon, config, the update CHECK |
| `src/Findra/Update/` | the update WINDOW |
| `src/Findra/Settings/` | Settings rows |
| `src/Findra/Content/` | what is read, file readers, queue and migrations, GPU yielding, video frames, the vector file, score scales |
| `src/Findra/Models/` | capabilities, add-ons, pricing, downloads, which chip does the work |
| `src/Findra/Names/` | the pipe, ranking, walked drives, the journal log |
| `website/` | the site, generated pages, promises, Netlify |
| `packaging/store/` | the MSIX package and the Store |

## Commands

```bash
dotnet build -warnaserror -t:Rebuild           # zero warnings; -t:Rebuild, incremental lies clean
dotnet test                                    # TDD applies to new code
pwsh -File build/Publish.ps1 -Rid win-x64      # the self-contained publish the installer and CI use
pwsh -File build/Check-Diagnostics.ps1 -Exe publish/win-x64/findra.exe   # every headless mode
pwsh -File build/Check-Release.ps1 -Tag v0.1.0 # may this tag be released, and its notes
pwsh -File build/Check-E2E.ps1 -Exe publish/win-x64/findra.exe   # read-only run-sheet checks;
                                               # "not yet" is a third outcome, not a failure
pwsh -File build/Make-Shots.ps1 -Exe publish/win-x64/findra.exe   # redraw docs/shots + site copy
pwsh -File build/Make-Msix.ps1 -Rid win-x64    # Store MSIX (then -Rid win-arm64, then -Bundle)
node build/Make-Icon.mjs                       # regenerate every copy of the mark. By hand only
node build/Make-Pages.mjs                      # regenerate the site's written pages; CI fails on
                                               # any diff it leaves
node build/Ping-IndexNow.mjs [--send]          # tell Bing/Yandex; nothing sent without --send
node tests/edge/markdown.test.mjs              # the edge function's tests
```

**Diagnostic modes verify the app without a screen** and are non-negotiable:

```bash
findra.exe --searchprobe [query]      # which process answered, the generation counter, the indexer
findra.exe --searchmodels             # models present, loading, agreeing; provider and chip
findra.exe --searchindex [file|folder|q:query|why:path]...   # a bare path QUEUES it; q: and why:
                                      # only read (the index opens read-only without a path)
findra.exe --searchshot out.png <state> [palette]   # how UI is iterated headlessly
findra.exe --searchtest               # engine self-check, including palette legibility
findra.exe --searchbench [out.md] [corpus]   # measured numbers, pasteable Markdown
findra.exe --version                  # version and log location
findra.exe --models [install <preset|cap[,cap]> | remove <cap> [--forget] [--dry-run]]
findra.exe --content [on|off] | --content limit <length>
findra.exe --names | --index <parentPid>     # the helper and the indexer; not run by hand
findra.exe --uninstall [--purge] [--dry-run] # stop everything, remove task, autostart, files
findra.exe --stop                     # stop all three; exits 2 when it stopped an interface
```

- `--models` and `--content` are settings that **survive the first-run screen and Settings rather
  than being replaced by them** (CI, headless use, bug reports).
- Any other `--` argument exits 1 and prints the list; never fall through to the greeting.
- The `--searchbench` fragment opens at heading level two so it pastes under the README's `#`
  unedited; it refuses a throughput rate for a run under a second.
- **`SearchShot.States` is the only list of screenshot states** (forty-six: eighteen card, seven
  settings, eight first-run, thirteen update; `ReadmeTests` holds the count the README quotes).
  **Where a painter branches on data a state supplies, some state has to supply it**, or the branch
  ships unlooked at. `--searchshot` learns every new palette and surface as it is written.

## The console

**`OutputType` is `WinExe`, not `Exe`** (`Exe` drags a console window behind every terminal-less
launch; `ProjectFileTests` asserts it). `src/Findra/Core/ParentConsole.cs` buys stdout back:
**`AttachConsole(ATTACH_PARENT_PROCESS)`, never `AllocConsole`**; standard handles are read BEFORE
attaching and restored if already set (redirected output must not go to the window; only an unset
handle gets `CONOUT$`); **`Borrow()` runs before `UseUtf8OnTheConsole()`** or Hebrew prints as
replacement characters. `--names`, `--index` and the interface do not attach; every other mode does,
including a mistyped one. The shell does not wait for `findra.exe` (cosmetic); `dotnet run
--project src/Findra -- --version` waits.

## Architecture

**Three processes**, from the first commit:

- `findra.exe --names` - elevated, headless, started by a `HighestAvailable` logon scheduled task.
  Owns the NTFS volume handles and the in-RAM name index, and nothing else.
- `findra.exe` - the UI, normal integrity: grammar, ranking, content search, settings, card, tray,
  hotkey. **One interface per index** (`OnlyOne`).
- `findra.exe --index` - the content indexer, a child of the UI; indexing stops when the app quits.

Exactly one call needs admin: `CreateFile(\\.\C:)`, for `FSCTL_ENUM_USN_DATA` and
`FSCTL_READ_USN_JOURNAL`. **The
elevated helper must never parse untrusted file content** - decoders (PDF, ONNX, Whisper, image
codecs, OCR) run in the indexer. The whole app is not elevated because UIPI would block dragging a
result into Explorer. The helper streams USN events over a local named pipe; the UI decides what to
enqueue. **Every query carries a generation counter, stamped on the reply and checked by the UI.**

## Hardware portability

Findra lands on AMD or Intel CPUs, NVIDIA / AMD / Intel GPUs, integrated or discrete, or none.
**No capability may require a particular vendor**, and nothing may fail because of the silicon.

- **Everything has been measured on exactly one configuration: an x64 AMD CPU with a discrete
  NVIDIA card, plus the processor-only path on it.** No AMD or Intel GPU. On arm64 only the tests
  and headless diagnostics have run (`ci.yml`'s `arm64` job on `windows-11-arm`, no GPU). **Say so wherever a number or
  hardware claim is written** - README, site and here (`ReadmeTests`, `WebsiteTests` hold the
  wording). Vendor-neutral chains are a design, not evidence. The failures guarded against came
  from an AMD 780M, so the untested hardware is the likeliest to break.
- **Detect at runtime.** ONNX (SigLIP-2, e5) is **DirectML → CPU**; Whisper is **Vulkan → CPU**. No
  CUDA, no ROCm. **CPU is supported, not a failure state** (the UI says the first pass is slower).
- **No CPU-feature assumptions** (no AVX-512, no vendor intrinsics) and **never assume x64** (no
  hardcoded RID).
- `--searchmodels` reports the chosen provider and every rejected one with reasons; `--searchbench`
  records the accelerator beside CPU, RAM, disk and Windows build. Which chip does the work, and
  proving a provider works, are in `src/Findra/Models/CLAUDE.md`.

## The README is a product page

Screenshots and numbers, and **both must be real**.

- **Screenshots come from `--searchshot`**, never mockups or hand edits; the command is printed under
  each image. **Every number comes from `--searchbench`**, pasted verbatim and whole, with the
  machine named. Model sizes from real files, never floors.
- **Never quote a throughput rate from a default-sized run**; an extraction rate needs a corpus of at
  least 10,000, stated above it. **No claim a reader cannot reproduce** with a README command.
- **No comparative claims against named competitors** (`tests/Findra.Tests/Build/Repo.cs` holds the
  list for the README and winget listing tests).

## The mark

A lens with the capsule's search field cut out, Mond's accent on Mond's ground, solid. **`build/
Make-Icon.mjs` holds the only copy of the geometry** and emits the `.ico`, SVGs, wizard image,
favicon, share images and Store tiles; never hand-edit an output (`IconTests` holds every copy to the
others). It needs node; nothing in the build does. The binary `.ico` in the tree is a deliberate
exception (a PE resource). **Small sizes are drawn, not shrunk**: `HINTS` thickens the handle and
drops the slot at 16 px; 20 and 40 exist for 125%/150%. The tray icon is drawn at runtime.

## Shipping

- **The version lives in `Directory.Build.props` only** - no `<Version>`, `<AssemblyVersion>`,
  `<FileVersion>` or `<InformationalVersion>` in any `.csproj`.
  `IncludeSourceRevisionInInformationalVersion` is off and `BuildInfo.Normalise` strips from `+`
  (`Version.TryParse` rejects `1.2.0+sha`).
- **A release**: bump `Directory.Build.props`, turn `## [Unreleased]` into `## [x.y.z] - date` (plus
  its compare link), bump `packaging/winget/*.yaml` versions and URLs (the hashes stay placeholder
  zeros; `winget.yml` fills them), `website/content/about.md` and `home.md`, the bundle name in
  `docs/store.md`; run `node build/Make-Pages.mjs` and `Check-Release.ps1`; push `main`, then the
  `vX.Y.Z` tag. `release.yml` builds both installers from the tag.
- **A tag with no matching `CHANGELOG.md` section fails the release; that section is the notes.**
  GitHub's generated notes are never on. **No pre-release tags** (`/releases/latest` excludes them).
- **Only a person publishes to winget**: `winget.yml` has no `push`, `tag`, `release` or `schedule`
  trigger; when asked in words, `gh workflow run winget.yml -f version=X -f submit=true`. Same rule
  for `store.yml`. Both architectures in one manifest, two `Installers:` entries.
- **The signing step is a placeholder, and nothing claims otherwise** - README, installer, release
  body, winget listing, `docs/code-signing-policy.md` (coupled by a test).
- **Install sources**: `installer`, `winget`, `store`, `source`, `unknown` (spec §2 and §9b are
  older; this is the record).
- **Inno architecture identifiers are irregular** (`x86compatible`, `x86os`, `x64compatible`,
  `x64os`, `arm32compatible`, `arm64`, `win64`; no `arm64compatible`), so `installer/findra.iss`
  spells them per architecture, never by suffixing `{#Arch}`; `InstallerScriptTests` checks them,
  and that the uninstall report variable is `AnsiString` (for `LoadStringFromFile`). No version in the install directory; `AppId` is a
  fixed GUID (the task stores an absolute path to `findra.exe`).

## Install, resume and updates

- **Installing never discards work that is still good.** Correctly sized models are kept, partial
  downloads resume, a current-schema index is used and its queue **resumed**, an older schema is
  migrated re-queuing only invalidated files. Re-downloading 2.9 GB or re-indexing a finished disk
  is the worst thing Findra could do; it gets a test. (The name index is RAM, rebuilt at logon.)
- **A guard conditioned on data the other end may choose not to send is not a guard** - pair it with
  one on what is on disk.
- **Findra never replaces its own files.** Update now runs the installer or winget; nothing happens
  without the press.
- **The update check is the one exception to "nothing leaves the machine"** (spec §9b): anonymous,
  at most daily, disclosed on the first screen, switchable off. Details in `src/Findra/App/`.

## Data locations

Config roams, bulk does not - 2.9 GB of models must never sit in a roaming profile or the publish
folder.

| Path | Holds |
|---|---|
| `%APPDATA%\Findra\config.json`, `palettes.json` | settings |
| `%LOCALAPPDATA%\Findra\models\` | the seven model files |
| `%LOCALAPPDATA%\Findra\index\` | SQLite name, FTS5 and vector stores (schema 8) |
| `%LOCALAPPDATA%\Findra\logs\` | `findra-YYYYMMDD.log` |
| `%LOCALAPPDATA%\Findra\opened.json` | what was opened from the card (`OpenedHistory`) |

**Never run a dev build in a mode that WRITES to the real index** (a schema bump migrates the
installed copy's data); read-only diagnostics are fine.

## No lineage

**Findra is a separate project, not a fork or a component.** Namespaces are `Findra`, and log tags,
probe markers, config paths and file names follow. Nothing - README, UI, commit message, manifest,
build script or code comment - may describe Findra as derived from another project, name one, or
describe behaviour that is not Findra's. It is not a name grep: leaks survive as comments explaining
another product's behaviour, constants justified by another product's history, another object
model's vocabulary. A shipped comment may not cite a plan, a task number or a review either: name
the thing, not the document that asked for it.

## The changelog

`CHANGELOG.md` is updated on **every commit**, in the same commit - never in a sweep at release time.
The release uses the tag's section as notes and the update check sends source builds there.
[Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/): entries under `## [Unreleased]` in
Added, Changed, Deprecated, Removed, Fixed or Security. Write for a person reading release notes, not
a reviewer reading a diff. `- Documentation:` bullets are dropped from the site's changelog.

## Testing

- **TDD for all new code.** The ported engine gets characterization tests only where Findra changes
  its behaviour.
- **An effect that cannot be run in a test gets a seam, not a test that reads its own source**
  (`Uninstall.Run`, `HelperTask.Unregister` take effects as delegates; `Autostart` takes an
  `IStore`). Ask what edit keeps a grepped string and breaks the behaviour (wrapping task removal
  in `if (!quiet)`). Text assertions only where there is no code to run: `installer/findra.iss` and the
  workflow YAML.
- **The test assembly never logs into `Paths.Logs`** (`TestLogFolder` sends it to
  `%TEMP%\findra-test-logs`; `LogPlacementTests`).
- **A subprocess is drained asynchronously and killed on a timeout.**
- **Ask what a log line costs per day, not per occurrence.**

## Licence

Apache-2.0 with a `NOTICE` file (attribution to blakazulu and https://github.com/blakazulu/findra
propagates; MIT would not). **`LICENSE`, `NOTICE` and `assets/fonts/OFL.txt` reach installs only
because `src/Findra/Findra.csproj` copies them into the publish folder** (the installer's `[Files]`
copies only that folder); `LicenseFile=` in the Inno
script only DISPLAYS the licence. `TypefaceTests` holds all three.
