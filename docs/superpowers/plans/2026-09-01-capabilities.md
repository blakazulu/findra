# Findra Plan 5 - Capabilities

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The three model-backed capabilities arrive, independently installable and independently absent. Photos and video frames, meaning in documents, and speech with a Hebrew second pass - each one downloadable on its own from a first-run screen, each one skipped silently when its files are not there, and each one re-queueing exactly the files it covers when it turns up later.

**Architecture:** A capability is a named set of model files plus the files it depends on, and every number the interface shows is the **marginal** cost of adding it to what is already chosen. The interface downloads (resumable, per file); the indexer child only ever asks whether a file is on disk. The indexer's kind switch moves behind a decoder seam that takes the installed set as an input, so "no model for this kind" is a route and not a special case. Content search gains a vector branch beside the FTS branch, and an absent encoder contributes no candidates rather than raising an error. Execution providers are chosen at runtime by trying a chain and taking the first that initialises, and every rejection is recorded with its reason.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), Microsoft.ML.OnnxRuntime.DirectML 1.24.4, Microsoft.ML.Tokenizers 2.0.0, Whisper.net / Whisper.net.Runtime / Whisper.net.Runtime.Vulkan 1.9.1, NAudio.Core / NAudio.Wasapi 3.0.0, System.Numerics.Tensors 10.0.11, Microsoft.Data.Sqlite 10.0.11, PdfPig 0.1.16, SkiaSharp 3.119.4, Avalonia 12.1.1, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-01-findra-design.md` - §2 distribution, §2a install/resume, §5 subsystems, §6 models and capabilities, §6 content indexing is off until asked for, §6 how much of a recording is worth transcribing, §6 running on whatever hardware is there, §9 verification, §9a the README, §10 testing.

**Two spec changes landed after this plan's first draft** (`7af5fd0`) and are carried here: **content indexing is off until asked for**, including the free document text the graph used to mark "always on" (Tasks 11 and 12), and **one user-set number decides how long a recording is worth transcribing**, covering audio and video together, with an over-length recording skipped for a reason of its own (Tasks 9, 11 and 12).

**Reconnaissance:** `.superpowers/sdd/plan-4-recon.md` - the per-file inventory of the port source, the model layer, the vector store, the media and OCR layers, and every all-or-nothing gate with file:line. Treat it as accurate.

**Inherited state:** `.superpowers/sdd/2026-09-01-content/final-review.md` §8 - the seams Plan 4 left ready, every place the current code assumes no models exist, and eleven named traps. **The trap map is the first table in Execution notes**: every trap, the task that addresses it, and the test that proves it.

**Review history:** the first draft of this plan was rejected (`.superpowers/sdd/plan-5-review.md`). Four findings shaped this one and each is called out at the task that carries it: the re-queue reason must be one the indexer's freshness check honours (C-1, Task 11), the capability gate must live where a test fake can inherit it (C-2, Task 9), the re-queue stamp must be per capability rather than per model family (C-3, Task 11), and no diagnostic may acquire a writer on the real vector store (C-4, Task 9).

## Global Constraints

- **Target framework moves to `net10.0-windows10.0.19041.0`, and every project in the tree moves together.** The WinRT projections this plan needs - `Windows.Media.Ocr`, `Windows.Media.Editing`, `Windows.Storage` thumbnails - do not exist below it. A test project left on the old flavour compiles until the first test touches a type that carries `[SupportedOSPlatform("windows10.0.19041.0")]`, and then fails in a way that reads as a missing reference. Task 1 moves them all at once and pins that with a test.
- **No RID in any project file, and arm64 stays reachable.** No `<RuntimeIdentifier>`, no `<RuntimeIdentifiers>`, no x64-only intrinsics, no hand-written vendor intrinsics. Self-contained publish is a property of the publish command, never of the csproj. ONNX Runtime's DirectML package and the Whisper runtimes carry their native assets per RID; restore resolves them at publish time and that is where the RID belongs.
- **No vendor-locked execution provider.** ONNX is **DirectML → CPU**; Whisper is **Vulkan → CPU**. CUDA, TensorRT, ROCm, OpenVINO and CoreML must not appear in any chain, any package reference, or any comment as something Findra might add. A portable path that works everywhere beats a fast path that works for a third of users (spec §6).
- **CPU is a supported configuration, not a failure state.** Nothing may throw, log an error, or show a warning because no accelerator was found. The only honest difference is how long the first content index takes, and the interface says so.
- **Downloads happen in the interface. The indexer child never downloads anything.** The source engine downloaded inside the child and blocked the whole queue until all seven files existed; the spec puts consent and progress on the first-run screen. `src/Findra/Content/` must contain no `HttpClient`, no `ModelDownloader` and no network call of any kind, and Task 4 has a test that says so.
- **Content indexing is off until asked for, and names are not.** Reading inside files walks every drive and runs for hours on a large disk; a name index costs seconds. So `Config.IndexContent` defaults to **false**, including for the free document text, and it stays off across restarts until somebody turns it on. There is exactly one switch - a second "enabled" bit beside the existing pause would be two settings that can disagree, and the interface would have no honest sentence for the disagreement. What the interface says is derived: never asked for, turned off after reading something, or paused because Findra is closed.
- **One number decides how long a recording is worth transcribing, and it covers audio and video together.** `Config.TranscribeMinutes`: zero is off, negative is no limit, positive is the limit in minutes, default 5. The named choices are presets over that one number, so a typed value and a preset are the same setting and cannot disagree. **The source's `MaxAudioSeconds = 3600` and `MaxVideoSpeechSeconds = 180` are the ancestors of the default, not the rule** - do not port them as behaviour.
- **A recording longer than the limit is skipped for a reason of its own.** `Decoders.TooLong`, distinct from `TooLarge`, `NoText`, `NoFormatReader` and `NoModel`. `StateSkipped` was already overloaded four ways; this is a fifth meaning and it is the only one a user can change from a settings control, so raising the limit must re-queue exactly those files and nothing else. A reason that is merely "too large" would either miss them or sweep up every unrelated skip.
- **A missing model is a normal state, not an error state.** Every capability degrades silently: the indexer records **Skipped** with a reason and never Failed, content search contributes no candidates and never throws, `--searchmodels` exits 0 and says which capabilities are off and what they would cost, and the card offers the download. This is a hard rule (spec §6), not a nicety.
- **Every size shown anywhere is the MARGINAL size given what is already selected.** A fixed per-row number makes the total visibly fail to add up, because Speech and Meaning share the e5 pair. Marginal arithmetic lives in exactly one place and every surface reads it from there.
- **Enabling a capability later re-queues only the files it covers, and the re-queue reason is `Indexer.Recheck`.** Nothing already indexed is redone. Re-downloading 2.9 GB or re-indexing a finished disk because an upgrade did not look first is the worst thing this product can do to someone (spec §2a) - and both get a test.
- **A re-queue that a capability makes MUST carry `Indexer.Recheck` as its reason.** `Indexer.cs:298-300` dequeues a row untouched when the reason is not `Recheck`, the row is not `StateSkipped`, and the file's bytes have not moved - which describes every document already read by Plan 4. A free-text reason therefore queues twelve thousand files, drains them at full speed, and writes not one embedding. Only photos escape it, and only because photos happen to be skipped today. This is the single most expensive mistake available in this plan, and Task 11 carries the test that catches it.
- **The gate that decides whether a kind can be read is `IDecoders.CanRead`, and it is consulted by `Indexer.Handle`.** Not inside the decode arms: a gate buried in the implementation cannot be inherited by a test fake, and "the decoder was never asked" stops being an assertion anyone can make.
- **No diagnostic may acquire a writer on the real vector store.** `Indexer.DrainOnce` takes an `IDecoders` and has no overload that builds one for you; `--searchbench`, `--searchindex` and `--searchtest` each pass a set they own. A benchmark that appends vectors for a synthetic corpus into a user's `vectors.bin` and then deletes the database referencing them is trap 4 re-opened through a second store.
- **The vector store is flushed before the transaction that references it commits.** A database row pointing past the vector header's count is a segment that silently never matches again; the reverse - a flushed row nothing references - is a few wasted kilobytes.
- **Models never live in the publish folder and never under Roaming.** `%LOCALAPPDATA%\Findra\models\` is the only answer, and `Paths.Models` is the only way to say it.
- **The elevated helper is untouched by this plan.** Every decoder here reads arbitrary user files and every one of them runs in the indexer child at normal integrity. Nothing in `src/Findra/Names/` or `src/Findra/Pipe/` gains a decoder, a model, or a package.
- **`ContentDb` wraps one `SqliteConnection` and refuses a second flow.** One writer per process. The vector store is a separate file with its own writer - the indexer child owns it - and the download manager records its progress in the `.part` file on disk and in memory, never in the database.
- **`models:` is this plan's meta prefix, and it is the only one it writes.** `indexer:` belongs to the child, `index:` to the interface's content loop, and the bare keys (`schema`, `usn:`, `walk:`, `suffixes:`, `journal:dropped`) to the queue feeder. Do not reuse any of them.
- **Every published or displayed number is formatted with `CultureInfo.InvariantCulture`.** The project sets `<InvariantGlobalization>false</InvariantGlobalization>`, so a bare `{n:N0}` renders `2,93 GB` on a German machine. The first-run screen's sizes, `--searchmodels`, and the `--searchbench` fragment are all read by people and by tests.
- **This plan adds no painted surface, so `--searchshot` learns no new state.** The first-run screen moved to Plan 6, where it shares a section rail and a painter with the settings window; the shot states, the §9b first-run disclosure and the legibility check go with it. Task 14 still corrects `CLAUDE.md`'s state list, which names a `panel` state that has never existed.
- **Reading words inside pictures is inherited free behaviour, not a capability.** `ImageText` needs no model and is not in the spec's §5 subsystem list or its §6 capability table. It gets no download and no graph node; it runs whenever a photo is being opened anyway, and `--searchmodels` names it as free beside words-in-documents. If a later plan wants it in the capability model, the spec has to say so first.
- **"Free" now means free of charge, not free of consent.** The spec's graph reads *"words in documents - free, opt-in"*: it costs no download and no model, and it still does not run until content indexing is turned on. Every surface that prints "free" says which of the two it means.
- **Do not simplify the indexer's freshness check.** `Indexer.cs:298-300`'s `StateOf(...) != ContentDb.StateSkipped` clause is the only thing that reopens a re-queued Skipped file, and this plan's whole re-queue story rests on it. `ARequeuedSkippedFileIsOpenedAgainWhateverReasonTheRequeueGave` is the test that pins it and it must stay green.
- **Grep for a reader before adding a field to a record.** Plan 4 shipped two fields that are written and read by nobody. A message field, a snapshot field or a report column with no consumer is a defect, not a placeholder.
- **No lineage anywhere.** Nothing in code, comments, commit messages or this document may describe Findra as derived from, forked from, or a component of another project. Referring to a source *path* while porting is fine. The name-grep (`grep -ric prism src/ tests/`) is necessary and **not sufficient**: read every ported comment, and note that this plan ports files whose comments name another product's process, its installer, its widgets and a machine's GPU by model. There is also a **binary** lineage leak in this plan's port surface - the vector file's magic number - which no text grep would ever find.
- **Test files follow the tree's existing convention.** No `namespace` declaration - the whole test project sits in the global namespace - `using Findra;` at the top, and `using Xunit;` even though the csproj already declares it globally. Any class that assigns `CultureInfo.CurrentCulture` carries `[Collection("culture")]` (`tests/Findra.Tests/Content/CultureCollection.cs`): xUnit runs classes in parallel on shared pool threads, and without it a concurrent test formatting any number can observe de-DE and fail for a reason that has nothing to do with it, rarely and miserably. In this plan that is `ModelStoreTests`, `SearchModelsReportTests` and `IndexLineFormatterTests`.
- **Build output pristine** - zero warnings from `dotnet build -warnaserror` and `dotnet test`.
- **TDD for all new code.** The ported encoders, decoders and vector store arrive working and are not rewritten test-first; they get characterization tests over the pure parts, and the behaviour Findra *changes* - the all-or-nothing gates becoming per-capability - is written test-first (spec §10).
- Commit messages carry no AI/Claude attribution.

## Where this sits

| Plan | Delivers |
|---|---|
| 1 - Foundation and the name pipe | ✅ 53 tests |
| 2 - The look | ✅ 174 tests |
| 3 - The widget | ✅ the window, capsule, tray, hotkey, config, update check, the card on the real index |
| 4 - Content | ✅ FTS5 store, text extraction, the indexer child, journal-driven enqueue, `--searchindex`, `--searchbench` - 519 tests |
| **5 - Capabilities** ← this plan | The model store and its downloads, the vector store, SigLIP-2, e5 and Whisper behind per-capability gates, OCR, media, previews, `--models`, `--searchmodels`, the TFM bump |
| 6 - Settings and shipping | The first-run screen and the settings window as one design, `--uninstall`/`--purge`, publish, winget, the real README |

## Port source

Files copied in this plan live on this machine at:

```
C:\Code\Personal\Prism\src\Search\Encoders.cs         386   ModelStore + Onnx + the three encoders
C:\Code\Personal\Prism\src\Search\VectorStore.cs      213   float16 rows, memory-mapped, brute-force dot products
C:\Code\Personal\Prism\src\Search\Media.cs            137   audio decode, whisper transcription, video frames
C:\Code\Personal\Prism\src\Search\ImageText.cs        119   OCR through Windows.Media.Ocr
C:\Code\Personal\Prism\src\Search\PreviewDecoder.cs   143   Skia decode + shell thumbnail  (NOT ShellAssoc)
C:\Code\Personal\Prism\src\Search\Indexer.cs          474   ONLY Photo/Audio/Video/Speech/LoadBitmap, ~150 lines
C:\Code\Personal\Prism\src\Search\ContentSearch.cs    156   ONLY the vector half and its score bands, ~60 lines
C:\Code\Personal\Prism\src\Search\SearchModelsProbe.cs 93   the shape of the probe, not its text
```

**What must NOT be ported, in any task of this plan:** `ShellAssoc.DefaultHandlerExe` (`PreviewDecoder.cs:118-143` - nothing here opens files by association), `QuietState` in any form (`Indexer.cs:190-194` - a host "the user is in a game" gate Findra has no host for), `SearchService.Populate(Sensors)`, the `PRISM_SEARCH_DIR` environment override, `ModelStore.EnsureAsync`'s call site inside `Indexer.Loop` (`Indexer.cs:172-180` - the whole point of this plan is that it moves), and `Indexer.Migrate`'s single `vecschema` string (`Indexer.cs:98,105,137` - it re-queues photos and re-embeds text together, and Task 11 replaces it with one stamp per model family). `SearchDb.cs`, `DocText.cs`, `ContentQueue.cs`, `SearchQuery.cs`, `FileKinds.cs`, `NameIndex.cs`, `NtfsVolume.cs` and the `SearchResult` records are **already in the tree** - do not port them twice.

Copy verbatim, change the namespace to `Findra`, rewrite every comment that names another product, another product's installer, its widgets, its tools folder or a particular graphics card, and rename literals. Do not restructure ported code beyond the renames and extractions this plan's file structure calls for.

---

## File structure

| File | Responsibility |
|---|---|
| `src/Findra/Findra.csproj` | *modify*: TFM, the six new package references, still no RID |
| `tests/Findra.Tests/Findra.Tests.csproj` | *modify*: TFM |
| `src/Findra/Models/ModelStore.cs` | the seven files, their URLs, their measured sizes, presence on disk. No network |
| `src/Findra/Models/Capabilities.cs` | the capability graph, its closure, marginal sizes, the kinds each covers, the presets, the card's offer |
| `src/Findra/Models/Sizes.cs` | one byte-to-human formatter, invariant, shared by every surface |
| `src/Findra/Models/ModelDownloader.cs` | resumable per-file download behind an injectable fetch. Interface-side only |
| `src/Findra/Models/Providers.cs` | the execution-provider chains and the record of what was tried and rejected |
| `src/Findra/Models/VectorStore.cs` | ported: float16 rows, tombstones, kind filter, memory-mapped reads |
| `src/Findra/Models/Encoders.cs` | ported: `Onnx`, `ClipImageEncoder`, `ClipTextEncoder`, `E5Encoder` |
| `src/Findra/Models/CapabilityGate.cs` | what a newly-installed capability re-queues, and the `models:vec:*` stamps that stop it happening twice |
| `src/Findra/Content/Media.cs` | ported: audio decode, transcription, video frames, the sample times |
| `src/Findra/Content/Speech.cs` | extracted from the source's `Indexer.Speech`: transcript lines merged into windows |
| `src/Findra/Content/ImageText.cs` | ported: OCR through the recognisers Windows ships |
| `src/Findra/Content/PreviewDecoder.cs` | ported: Skia decode and the shell thumbnail |
| `src/Findra/Content/Decoders.cs` | the seam: what can be read, given what is installed. The indexer's kind switch lives here |
| `src/Findra/Content/TranscribeLimit.cs` | one number, its presets, and the rule that decides whether a recording is worth transcribing |
| `src/Findra/Content/Indexer.cs` | *modify*: takes an `IDecoders`, tombstones the vector rows an upsert or a delete hands back |
| `src/Findra/Content/ContentBranch.cs` | *modify*: the vector branch beside the FTS branch, and the note that offers a missing capability |
| `src/Findra/Card/CardWindow.cs` | *modify*: the content search carries a `Semantic`; `DecodePreview` calls the real decoder |
| `src/Findra/Diagnostics/Models.cs` | `--models list` and `--models install <preset>` - the headless way to take a capability |
| `src/Findra/Diagnostics/Content.cs` | `--content on/off/status` and `--content limit <preset\|minutes>` - the headless way to ask for content indexing at all |
| `src/Findra/App/Config.cs` | *modify*: `IndexContent` replaces `IndexPaused` and defaults to off; `TranscribeMinutes` |
| `src/Findra/Content/IndexStatus.cs` | *modify*: three sentences for three states, so an index nobody asked for does not look idle |
| `src/Findra/App/App.axaml.cs` | *modify*: run the capability gate once at startup, before the content loop |
| `src/Findra/Diagnostics/SearchModels.cs` | `--searchmodels`: the snapshot and its pure formatter |
| `src/Findra/Diagnostics/Machine.cs` | *modify*: the accelerator line names the providers actually chosen |
| `src/Findra/Diagnostics/SelfTest.cs` | *modify* (Task 9 and Task 14): a decoder set it owns; the graph is consistent; installed models load |
| `src/Findra/Diagnostics/SearchIndex.cs` | *modify* (Task 9 and Task 14): a decoder set it owns; a models section |
| `src/Findra/Diagnostics/SearchBench.cs` | *modify* (Task 9 and Task 14): a decoder set that reaches no model and no real store; the accelerator line |
| `src/Findra/Card/IndexLineFormatter.cs` | *modify*: the name count is invariant |
| `tests/Findra.Tests/Build/ProjectFileTests.cs` | the TFM, the absent RID, the pinned versions |
| `tests/Findra.Tests/Models/ModelStoreTests.cs` | the seven files, the floors, the measured sizes |
| `tests/Findra.Tests/Models/CapabilityTests.cs` | closure, marginal sizes, presets, kinds, the offer, the formatter |
| `tests/Findra.Tests/Models/ModelDownloadTests.cs` | resume, short files, 416, cancellation, progress, and the indexer's silence |
| `tests/Findra.Tests/Models/ProviderTests.cs` | the chain, the record of rejections, the vendor ban |
| `tests/Findra.Tests/Models/VectorStoreTests.cs` | round trip, tombstones, kind filter, width change, the magic |
| `tests/Findra.Tests/Models/EncoderTests.cs` | preprocessing layout and range, mean pooling, the id shift |
| `tests/Findra.Tests/Models/CapabilityGateTests.cs` | what a new capability re-queues, and what it leaves alone |
| `tests/Findra.Tests/Content/MediaTests.cs` | sample times, noise lines, transcript windows |
| `tests/Findra.Tests/Content/DecoderGateTests.cs` | the per-capability route, and the vector rows a replace or a delete releases |
| `tests/Findra.Tests/Content/SemanticBranchTests.cs` | meaning finds what words cannot, and absence finds nothing quietly |
| `tests/Findra.Tests/Diagnostics/ModelsCommandTests.cs` | preset parsing, what a listing says, and the gate running after an install |
| `tests/Findra.Tests/Content/TranscribeLimitTests.cs` | zero, negative and positive; the presets over the number |
| `tests/Findra.Tests/App/ConfigTests.cs` | *modify*: the new default, the renamed field, the limit |
| `tests/Findra.Tests/Diagnostics/SearchModelsReportTests.cs` | the report formatter |

---

## Task 1: The framework moves, and the packages arrive

Nothing else in this plan compiles until this lands. It is deliberately alone: it touches every project file in the tree and a reviewer can accept or reject it without reading a line of capability code.

**Files:**
- Modify: `src/Findra/Findra.csproj`, `tests/Findra.Tests/Findra.Tests.csproj`
- Test: `tests/Findra.Tests/Build/ProjectFileTests.cs`

**Interfaces:**
- Produces: nothing at the type level. It produces a tree in which `Windows.Media.Ocr`, `Windows.Media.Editing`, `Windows.Storage.FileProperties` and the ONNX, Whisper, NAudio and TensorPrimitives types resolve.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Build/ProjectFileTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Xml.Linq;

using Findra;

/// <summary>
/// The project files themselves, asserted. Three rules in this plan live nowhere else: every
/// project moves to the same target framework together, no project pins a runtime identifier,
/// and the native-bearing packages are pinned rather than floating. All three are invisible to
/// every other test in the suite and all three break a stranger's machine rather than this one.
/// </summary>
public class ProjectFileTests
{
    private const string Tfm = "net10.0-windows10.0.19041.0";

    /// <summary>The repo root, found by walking up from this source file to the solution. The
    /// test binary's own directory is several levels into bin/ and moves with the configuration.
    /// </summary>
    private static string Root([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Findra.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IReadOnlyList<(string Path, XDocument Xml)> Projects()
    {
        var found = new List<(string, XDocument)>();
        foreach (string p in Directory.EnumerateFiles(Root(), "*.csproj", SearchOption.AllDirectories))
        {
            if (p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            found.Add((p, XDocument.Load(p)));
        }
        Assert.NotEmpty(found);
        return found;
    }

    [Fact]
    public void EveryProjectTargetsTheWindowsSdkFlavourTheDecodersNeed()
    {
        // The OCR, media and thumbnail projections do not exist below 10.0.19041.0. A project
        // left behind compiles until the first test touches a type marked for that platform,
        // and then fails as if a package reference were missing.
        foreach ((string path, XDocument xml) in Projects())
        {
            string? tfm = xml.Descendants("TargetFramework").FirstOrDefault()?.Value;
            Assert.Equal(Tfm, tfm);
            Assert.Empty(xml.Descendants("TargetFrameworks"));   // one flavour, not a matrix
        }
    }

    [Fact]
    public void NoProjectPinsARuntimeIdentifier()
    {
        // Windows on ARM stays reachable, and the cost of keeping it possible is that nobody
        // writes win-x64 into a csproj to make a native package restore. Self-contained is a
        // property of the publish command (spec §2, §6).
        foreach ((string path, XDocument xml) in Projects())
        {
            Assert.Empty(xml.Descendants("RuntimeIdentifier"));
            Assert.Empty(xml.Descendants("RuntimeIdentifiers"));
        }
    }

    [Fact]
    public void TheNativeBearingPackagesArePinnedToTheVersionsThisPlanTested()
    {
        var want = new Dictionary<string, string>
        {
            ["Microsoft.ML.OnnxRuntime.DirectML"] = "1.24.4",
            ["Microsoft.ML.Tokenizers"] = "2.0.0",
            ["Whisper.net"] = "1.9.1",
            ["Whisper.net.Runtime"] = "1.9.1",
            ["Whisper.net.Runtime.Vulkan"] = "1.9.1",
            ["NAudio.Core"] = "3.0.0",
            ["NAudio.Wasapi"] = "3.0.0",
            ["System.Numerics.Tensors"] = "10.0.11",
            ["SkiaSharp"] = "3.119.4",
            ["SQLitePCLRaw.bundle_e_sqlite3"] = "2.1.12",
        };

        XDocument app = Projects().Single(p => p.Path.EndsWith("Findra.csproj", StringComparison.Ordinal)).Xml;
        var have = app.Descendants("PackageReference")
                      .ToDictionary(e => e.Attribute("Include")!.Value, e => e.Attribute("Version")!.Value);

        foreach ((string name, string version) in want)
        {
            Assert.True(have.ContainsKey(name), $"Findra.csproj has no reference to {name}");
            Assert.Equal(version, have[name]);
        }
    }

    [Fact]
    public void NoVendorLockedExecutionProviderIsReferencedAnywhere()
    {
        // CUDA means NVIDIA only plus a large separate runtime, and ROCm is not a Windows story.
        // The ban is on the package list because that is where it would arrive first, quietly,
        // as "just for my machine" (spec §6).
        foreach ((string path, XDocument xml) in Projects())
            foreach (XElement r in xml.Descendants("PackageReference"))
            {
                string name = r.Attribute("Include")!.Value;
                Assert.False(name.Contains("Cuda", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("TensorRT", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("ROCm", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("OpenVino", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("CoreML", StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(path)} references {name}, which ties Findra to one vendor's silicon");
            }
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter ProjectFileTests`
Expected: FAIL. `EveryProjectTargetsTheWindowsSdkFlavourTheDecodersNeed` reports `net10.0-windows` against `net10.0-windows10.0.19041.0`, and `TheNativeBearingPackagesArePinned...` reports the eight missing references. `NoProjectPinsARuntimeIdentifier` and `NoVendorLockedExecutionProvider...` pass already - they are guards over a rule that currently holds, and their job is to keep holding it.

- [ ] **Step 3: Move the framework and add the packages**

In both csproj files change `<TargetFramework>net10.0-windows</TargetFramework>` to `<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>`.

Add to `src/Findra/Findra.csproj`, inside the existing `ItemGroup`:

```xml
    <!-- ONNX Runtime with the DirectML provider: DirectX 12, so one package covers NVIDIA, AMD
         and Intel, discrete or integrated, and the same package carries the CPU provider that
         every machine falls back to. Its native assets are per-RID and are resolved by the
         publish command; nothing here names an architecture. -->
    <PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.24.4" />
    <PackageReference Include="Microsoft.ML.Tokenizers" Version="2.0.0" />
    <!-- Whisper, with the Vulkan runtime beside the CPU one for the same reason DirectML is
         used rather than CUDA: Vulkan is the portable path across all three vendors. -->
    <PackageReference Include="Whisper.net" Version="1.9.1" />
    <PackageReference Include="Whisper.net.Runtime" Version="1.9.1" />
    <PackageReference Include="Whisper.net.Runtime.Vulkan" Version="1.9.1" />
    <!-- Media Foundation, through NAudio, is how a sound track becomes 16 kHz mono samples.
         It decodes with the codecs Windows already has; nothing links a media framework. -->
    <PackageReference Include="NAudio.Core" Version="3.0.0" />
    <PackageReference Include="NAudio.Wasapi" Version="3.0.0" />
    <!-- TensorPrimitives: the dot product the vector search is made of, vectorised by the
         runtime for whatever instruction set it finds. No intrinsics are written by hand. -->
    <PackageReference Include="System.Numerics.Tensors" Version="10.0.11" />
```

- [ ] **Step 4: Run the whole suite**

Run: `dotnet build -warnaserror` then `dotnet test`.
Expected: build clean with zero warnings; all 519 existing tests plus the 4 new ones pass.

`CA1416` warnings should **decrease**, not appear. `TargetPlatformMinVersion` defaults to `TargetPlatformVersion`, so this move does raise the project's `SupportedOSPlatformVersion` to 10.0.19041.0 - and that is precisely what makes every `[SupportedOSPlatform("windows10.0.19041.0")]` attribute in Tasks 8 and 9 satisfied project-wide, which is what lets the unattributed `Indexer` construct the attributed `Decoders` under `-warnaserror`. Two consequences to record in your report rather than to suppress: **Findra now declares a minimum of Windows 10 version 2004**, and the WinRT projection assemblies join the self-contained publish, which is a size fact Plan 6's winget manifest needs. Report any warning that does appear; a suppression here would hide the one warning this plan wants to see.

**This task is reversible up to the end of Task 7 and not afterwards.** Tasks 2-7 use no WinRT type, so reverting is two `<TargetFramework>` lines and eight `PackageReference`s. From **Task 8** it is not: `Media`, `ImageText` and `PreviewDecoder` are WinRT through and through and `CardWindow.DecodePreview` is wired to them. "Do it alone, first" reads as reversible, and it stops being so four tasks later.

- [ ] **Step 5: Confirm nothing about the publish shape changed**

Publish into your scratchpad directory - **not `/tmp`, which is not a path on this machine** - with the RID on the *command line*, which is where it belongs:

```bash
dotnet publish src/Findra -c Release --self-contained -r win-x64 -o "$TEMP/findra-publish-check"
```

Confirm it succeeds and that `onnxruntime.dll`, `DirectML.dll` and the whisper runtimes are in the output. Then **record two numbers and one fact** before deleting the folder:

- the **total size of the publish folder**, beside what it was before this task. Plan 6's winget manifest quotes a size, and the SDK projections plus ONNX Runtime DirectML plus three whisper runtimes are a large addition to a self-contained artefact.
- whether `win-arm64` also restores - `dotnet publish src/Findra -c Release --self-contained -r win-arm64 -o ...` - and **if a package has no arm64 asset, say which one** rather than working around it.

Both are facts nobody else on this plan will discover.

- [ ] **Step 6: Commit**

```bash
git add src/Findra/Findra.csproj tests/Findra.Tests/Findra.Tests.csproj tests/Findra.Tests/Build/ProjectFileTests.cs
git commit -m "The whole tree moves to the Windows SDK flavour the decoders need"
```

---

## Task 2: The seven files, and whether they are there

**Files:**
- Create: `src/Findra/Models/ModelStore.cs`, `src/Findra/Models/Sizes.cs`
- Test: `tests/Findra.Tests/Models/ModelStoreTests.cs`

**Interfaces:**
- Consumes: `Paths.Models` (`src/Findra/Core/Paths.cs:17`).
- Produces:
  - `Findra.Model` - `record Model(string File, string Url, long MinBytes, long Bytes, string Purpose)`.
  - `Findra.ModelStore` - `static string Dir`, `static string PathOf(Model, string? dir = null)`, `static bool Present(Model, string? dir = null)`, `static IReadOnlyList<Model> Missing(IEnumerable<Model>, string? dir = null)`, `static long TotalBytes(IEnumerable<Model>)`, and the seven `static readonly Model` instances plus `All`.
  - `Findra.Sizes` - `static string Human(long bytes)`.

**The trap in this task:** the source's `Model` carries only `MinBytes`, a deliberately conservative floor used to decide whether a file on disk is complete. The spec's §6 table is a different number - the **measured** size, which is what the first-run screen, the README and the winget manifest must show. Using the floor as the display size understates the total by 145 MB and makes "2.9 GB is the number for the README" false. Both numbers exist, they mean different things, and a test below asserts they are never equal.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Models/ModelStoreTests.cs`:

```csharp
using System.Globalization;

using Findra;

[Collection("culture")]
public class ModelStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-models-" + Guid.NewGuid().ToString("N"));

    public ModelStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Write(Model m, long bytes)
    {
        string p = ModelStore.PathOf(m, _dir);
        using var fs = new FileStream(p, FileMode.Create, FileAccess.Write);
        fs.SetLength(bytes);
        return p;
    }

    [Fact]
    public void TheSevenFilesAreTheOnesTheSpecMeasured()
    {
        Assert.Equal(7, ModelStore.All.Count);
        Assert.Equal(
            new[] { "siglip2-vision.onnx", "siglip2-text-q.onnx", "siglip2.spm", "e5-base-q.onnx",
                    "e5-small.spm", "whisper-turbo-q5.bin", "whisper-ivrit.bin" },
            ModelStore.All.Select(m => m.File).ToArray());
    }

    [Fact]
    public void EveryDeclaredSizeIsTheMeasuredOneAndNotTheConservativeFloor()
    {
        // Two numbers with two jobs. MinBytes decides "is the file on disk complete"; Bytes is
        // what a person is asked to consent to downloading. Collapsing them - which is what
        // happens if the port keeps only the field it inherited - understates the whole set by
        // 145 MB and makes the README's 2.9 GB wrong.
        foreach (Model m in ModelStore.All)
            Assert.True(m.Bytes > m.MinBytes,
                $"{m.File}: the declared size ({m.Bytes}) is not above the completeness floor ({m.MinBytes})");
    }

    [Fact]
    public void TheWholeSetIsTwoPointNineThreeGigabytes()
    {
        Assert.Equal(3_141_848_265L, ModelStore.TotalBytes(ModelStore.All));
        Assert.Equal("2.93 GB", Sizes.Human(ModelStore.TotalBytes(ModelStore.All)));
    }

    [Fact]
    public void EveryModelCarriesAHttpsUrlAndAPurposeSomebodyCanRead()
    {
        foreach (Model m in ModelStore.All)
        {
            Assert.StartsWith("https://", m.Url, StringComparison.Ordinal);
            Assert.NotEqual("", m.Purpose);
        }
    }

    [Fact]
    public void AFileShorterThanItsFloorIsNotPresent()
    {
        // A half-written file under the final name would otherwise read as installed for ever:
        // the loader opens it, the load fails, the capability is dead, and nothing re-downloads
        // it because "it is there". The floor is the only thing between that and a user.
        Model m = ModelStore.E5Spm;
        Assert.False(ModelStore.Present(m, _dir));      // nothing on disk

        Write(m, 10);
        Assert.False(ModelStore.Present(m, _dir));      // there, and far too short

        Write(m, m.MinBytes);
        Assert.True(ModelStore.Present(m, _dir));
    }

    [Fact]
    public void PresenceIsCheckedAgainstTheFloorAndNotTheDeclaredSize()
    {
        // A publisher who re-exports a model a few kilobytes smaller must not cost every user a
        // 1.5 GB re-download. The floor is generous on purpose; the declared size is for display.
        //
        // Siglip2Spm, not WhisperTurbo: SetLength allocates the clusters, so a fixture built on
        // the whisper floor writes 500 MB of temporary disk for an assertion about an inequality,
        // and on a small or full disk `dotnet test` then fails for a reason nobody connects to
        // this test. Any model whose floor is below its declared size proves the same thing.
        Model m = ModelStore.Siglip2Spm;
        Write(m, m.MinBytes);
        Assert.True(ModelStore.Present(m, _dir));
        Assert.True(m.MinBytes < m.Bytes);
    }

    [Fact]
    public void AMissingDirectoryIsANormalStateAndNotAnException()
    {
        string never = Path.Combine(_dir, "not-created-yet");
        Assert.Equal(ModelStore.All.Count, ModelStore.Missing(ModelStore.All, never).Count);
    }

    [Fact]
    public void MissingNamesExactlyWhatIsNotThere()
    {
        Write(ModelStore.Siglip2Spm, ModelStore.Siglip2Spm.MinBytes);
        IReadOnlyList<Model> gone = ModelStore.Missing(
            [ModelStore.Siglip2Spm, ModelStore.E5Spm], _dir);
        Assert.Equal(["e5-small.spm"], gone.Select(m => m.File).ToArray());
    }

    [Fact]
    public void ModelsLiveUnderLocalAppDataAndNeverBesideTheExecutable()
    {
        // Spec §2 and §4: never in the publish folder, because an upgrade replaces it, and
        // never under Roaming, because 2.9 GB must not follow somebody between machines.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.StartsWith(local, ModelStore.Dir, StringComparison.OrdinalIgnoreCase);
        Assert.False(ModelStore.Dir.StartsWith(roaming, StringComparison.OrdinalIgnoreCase));
        Assert.False(ModelStore.Dir.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(659_659_160L, "629 MB")]          // photos
    [InlineData(283_639_807L, "270 MB")]          // meaning in documents
    [InlineData(943_298_967L, "900 MB")]          // the Recommended preset - rounds UP to 900
    [InlineData(1_624_558_796L, "1.51 GB")]       // the Hebrew fine-tune
    [InlineData(3_141_848_265L, "2.93 GB")]       // everything
    public void SizesReadTheWayThePersonPayingForThemWouldWriteThem(long bytes, string want)
        => Assert.Equal(want, Sizes.Human(bytes));

    [Fact]
    public void SizesReadTheSameOnEveryMachine()
    {
        // InvariantGlobalization is false on purpose, so a bare format string renders "2,93 GB"
        // on a German machine - and this text goes on the first-run screen, in --searchmodels
        // and into the README.
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("2.93 GB", Sizes.Human(3_141_848_265L));
            Assert.Equal("900 MB", Sizes.Human(943_298_967L));
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter ModelStoreTests`
Expected: FAIL - `ModelStore` and `Sizes` do not exist.

- [ ] **Step 3: Write `Sizes`**

Create `src/Findra/Models/Sizes.cs`:

```csharp
using System;
using System.Globalization;

namespace Findra;

/// <summary>
/// One byte-to-words formatter, because every surface that shows a model size must agree with
/// every other one and with the README. Invariant always: this text is compared in tests, pasted
/// into a product page, and read on machines set to any locale.
/// </summary>
public static class Sizes
{
    private const long Mb = 1024L * 1024L;
    private const long Gb = Mb * 1024L;

    /// <summary>Whole megabytes below a gigabyte, two decimals above it with trailing zeros
    /// trimmed. The two-decimal form is not decoration: spec §6 says "2.93 GB is the number for
    /// the README", and one decimal would print 2.9, which is the conservative floor's total
    /// rather than the measured one.</summary>
    public static string Human(long bytes)
    {
        if (bytes < Gb)
            return Math.Round(bytes / (double)Mb, MidpointRounding.AwayFromZero)
                       .ToString("0", CultureInfo.InvariantCulture) + " MB";
        return Math.Round(bytes / (double)Gb, 2, MidpointRounding.AwayFromZero)
                   .ToString("0.##", CultureInfo.InvariantCulture) + " GB";
    }
}
```

- [ ] **Step 4: Write `ModelStore`**

Create `src/Findra/Models/ModelStore.cs`. Port `ModelStore` from `Encoders.cs:19-100`, keeping the URLs and the floors and **dropping `EnsureAsync` entirely** - the download is Task 4's and it lives on the other side of a process boundary from where it used to.

```csharp
using System;
using System.Collections.Generic;
using System.IO;

namespace Findra;

/// <summary>
/// One model file: where it comes from, how big it really is, and the size below which a file on
/// disk is not it.
///
/// <para><see cref="Bytes"/> is the measured size of the real file (spec §6). It is what a person
/// is asked to consent to downloading, what the first-run screen adds up, and what the README
/// quotes. <see cref="MinBytes"/> is a deliberately generous completeness floor: a publisher who
/// re-exports a model a few kilobytes smaller must not cost every user a 1.5 GB re-download, and
/// a truncated file must never read as installed. They are different numbers with different jobs
/// and neither may be used for the other's.</para>
/// </summary>
public sealed record Model(string File, string Url, long MinBytes, long Bytes, string Purpose);

/// <summary>
/// Where the models live and which of them are actually there. Nothing here touches the network -
/// the download is <see cref="ModelDownloader"/>'s, it runs in the interface, and the indexer
/// child asks this type only whether a file exists.
/// </summary>
public static class ModelStore
{
    /// <summary>Measured sizes come from the spec's table in MB, and MB there means 1024 KB.
    /// Declaring them this way keeps the arithmetic in one place and makes a drift between the
    /// table and the code a one-line change rather than seven.</summary>
    private static long Mib(double mb) => (long)(mb * 1024 * 1024);

    public static string Dir => Paths.Models;

    public static readonly Model Siglip2Vision = new("siglip2-vision.onnx",
        "https://huggingface.co/onnx-community/siglip2-base-patch16-256-ONNX/resolve/main/onnx/vision_model.onnx",
        350_000_000, Mib(354.8), "photos and video frames");

    public static readonly Model Siglip2Text = new("siglip2-text-q.onnx",
        "https://huggingface.co/onnx-community/siglip2-base-patch16-256-ONNX/resolve/main/onnx/text_model_quantized.onnx",
        250_000_000, Mib(270.3), "what you type, when you are looking for a picture");

    public static readonly Model Siglip2Spm = new("siglip2.spm",
        "https://huggingface.co/onnx-community/siglip2-base-patch16-256-ONNX/resolve/main/tokenizer.model",
        3_000_000, Mib(4.0), "its vocabulary");

    public static readonly Model E5Base = new("e5-base-q.onnx",
        "https://huggingface.co/Xenova/multilingual-e5-base/resolve/main/onnx/model_quantized.onnx",
        250_000_000, Mib(265.7), "the meaning of documents, and of transcripts");

    public static readonly Model E5Spm = new("e5-small.spm",
        "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/sentencepiece.bpe.model",
        4_000_000, Mib(4.8), "their vocabulary");

    public static readonly Model WhisperTurbo = new("whisper-turbo-q5.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
        500_000_000, Mib(547.4), "speech, in every language");

    public static readonly Model WhisperHebrew = new("whisper-ivrit.bin",
        "https://huggingface.co/ivrit-ai/whisper-large-v3-turbo-ggml/resolve/main/ggml-model.bin",
        1_500_000_000, Mib(1549.3), "speech, in Hebrew");

    public static readonly IReadOnlyList<Model> All =
        [Siglip2Vision, Siglip2Text, Siglip2Spm, E5Base, E5Spm, WhisperTurbo, WhisperHebrew];

    public static string PathOf(Model m, string? dir = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        return System.IO.Path.Combine(dir ?? Dir, m.File);
    }

    /// <summary>Is this model on disk and long enough to be itself? Never throws: an absent
    /// directory, an unreadable one and a locked file are all "not present", which is a normal
    /// state on a machine that has taken nothing.</summary>
    public static bool Present(Model m, string? dir = null)
    {
        try
        {
            var fi = new FileInfo(PathOf(m, dir));
            return fi.Exists && fi.Length >= m.MinBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static IReadOnlyList<Model> Missing(IEnumerable<Model> set, string? dir = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        var gone = new List<Model>();
        foreach (Model m in set) if (!Present(m, dir)) gone.Add(m);
        return gone;
    }

    /// <summary>The declared total of a set, de-duplicated by file - a set built from two
    /// capabilities that share the e5 pair must not count it twice.</summary>
    public static long TotalBytes(IEnumerable<Model> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (Model m in set) if (seen.Add(m.File)) total += m.Bytes;
        return total;
    }

    /// <summary>The size on disk of a model that is present, or 0. Printed beside
    /// <see cref="Model.Bytes"/> by <c>--searchmodels</c>, because the README's sizes have to
    /// come from real files rather than from this table (spec §9a).</summary>
    public static long ActualBytes(Model m, string? dir = null)
    {
        try { var fi = new FileInfo(PathOf(m, dir)); return fi.Exists ? fi.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
```

- [ ] **Step 5: Run it**

Run: `dotnet test --filter "ModelStoreTests"`
Expected: PASS, 11 tests. If `TheWholeSetIsTwoPointNineThreeGigabytes` fails, do **not** adjust the expected number - re-derive the seven `Mib(...)` values from the spec's table, because that assertion is the drift detector for every size in the product.

- [ ] **Step 6: Commit**

```bash
git add src/Findra/Models/ModelStore.cs src/Findra/Models/Sizes.cs tests/Findra.Tests/Models/ModelStoreTests.cs
git commit -m "The seven model files, their measured sizes, and whether they are on disk"
```

---

## Task 3: The capability graph, and what each row costs

The heart of the plan. Everything visible about capabilities - the first-run rows, the totals, the offer on the card, the re-queue plan, `--searchmodels` - reads its arithmetic from here.

**Files:**
- Create: `src/Findra/Models/Capabilities.cs`
- Test: `tests/Findra.Tests/Models/CapabilityTests.cs`

**Interfaces:**
- Consumes: `Model`, `ModelStore`, `Sizes` (Task 2); `ResultKind` (`src/Findra/Names/FileKinds.cs:6`); `SearchQuery` (`src/Findra/Names/SearchQuery.cs:29`).
- Produces:
  - `Findra.Capability` - `enum { Photos, Meaning, Speech, Hebrew }`.
  - `Findra.CapabilitySet` - `readonly record struct CapabilitySet(IReadOnlySet<Capability> Have)` with `bool Has(Capability)`, `static CapabilitySet Installed(string? dir = null)`, `static readonly CapabilitySet None`.
  - `Findra.Capabilities` - `static IReadOnlyList<Capability> All`, `static IReadOnlyList<Capability> Requires(Capability)`, `static IReadOnlySet<Capability> Close(IEnumerable<Capability>)`, `static IReadOnlySet<Capability> Drop(IEnumerable<Capability> from, Capability)`, `static IReadOnlyList<Model> OwnModels(Capability)`, `static IReadOnlyList<Model> ModelsFor(IEnumerable<Capability>)`, `static long MarginalBytes(Capability add, IEnumerable<Capability> already)`, `static long TotalBytes(IEnumerable<Capability>)`, `static int[] KindsCovered(Capability)`, `static string Title(Capability)`, `static bool HebrewIsOffered(IEnumerable<string> languageTags)`, `static Offer? OfferFor(SearchQuery q, CapabilitySet installed)`.
  - `Findra.Offer` - `readonly record struct Offer(Capability Capability, long MarginalBytes, string Text)`.
  - `Findra.Presets` - `static IReadOnlySet<Capability> JustNames/Recommended/Everything`, `enum Preset { JustNames, Recommended, Everything, Custom }`, `static Preset Match(IReadOnlySet<Capability>)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Models/CapabilityTests.cs`:

```csharp
using Findra;

public class CapabilityTests
{
    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    // ---- the graph ----

    [Fact]
    public void PhotosNeedNothingButTheirOwnThreeFiles()
    {
        Assert.Equal([Capability.Photos], Capabilities.Close([Capability.Photos]).Order().ToArray());
        Assert.Equal(
            ["siglip2-vision.onnx", "siglip2-text-q.onnx", "siglip2.spm"],
            Capabilities.OwnModels(Capability.Photos).Select(m => m.File).ToArray());
    }

    [Fact]
    public void SpeechPullsInTheDocumentModelsBecauseATranscriptIsADocument()
    {
        // Spec §6: a transcript is embedded and searched exactly like a document, so taking
        // Speech takes the same e5 pair "meaning in documents" uses. A closure that hands back
        // what it was given passes nothing here.
        IReadOnlySet<Capability> closed = Capabilities.Close([Capability.Speech]);
        Assert.Contains(Capability.Speech, closed);
        Assert.Contains(Capability.Meaning, closed);
        Assert.Equal(2, closed.Count);
    }

    [Fact]
    public void HebrewCannotBeTakenWithoutTheGeneralModelItSecondPasses()
    {
        // Hebrew is a SECOND PASS, never an alternative: turbo runs first for language
        // detection and only the files it calls Hebrew are re-run. A closure that walks one
        // level - the obvious first implementation - returns {Hebrew, Speech} and misses the
        // e5 pair that Speech itself drags in, which is a download set that cannot work.
        IReadOnlySet<Capability> closed = Capabilities.Close([Capability.Hebrew]);

        Assert.Equal([Capability.Meaning, Capability.Speech, Capability.Hebrew],
                     closed.OrderBy(c => (int)c).ToArray());
        Assert.Equal(3, closed.Count);
    }

    [Fact]
    public void ClosingAnAlreadyClosedSetChangesNothing()
    {
        // Idempotence, because the closure runs at every UI click and at every startup, and a
        // closure that grows on each pass would eventually select everything.
        IReadOnlySet<Capability> once = Capabilities.Close([Capability.Hebrew, Capability.Photos]);
        IReadOnlySet<Capability> twice = Capabilities.Close(once);
        Assert.Equal(once.OrderBy(c => (int)c), twice.OrderBy(c => (int)c));
    }

    [Fact]
    public void DroppingSomethingDropsWhateverDependedOnIt()
    {
        // Untick Speech with Hebrew ticked and Hebrew must go too. A naive Remove leaves a
        // selection that asks for the 1.5 GB fine-tune with no general model to detect
        // language with - a download set that installs and then does nothing.
        IReadOnlySet<Capability> after = Capabilities.Drop(
            [Capability.Meaning, Capability.Speech, Capability.Hebrew], Capability.Speech);
        Assert.Equal([Capability.Meaning], after.ToArray());
    }

    [Fact]
    public void DroppingSomethingLeavesWhatMerelySharesFilesWithIt()
    {
        // Speech and Meaning share the e5 pair, but Meaning does not DEPEND on Speech. Untick
        // Speech and documents keep their meaning - and keep their models.
        IReadOnlySet<Capability> after = Capabilities.Drop(
            [Capability.Meaning, Capability.Speech], Capability.Speech);
        Assert.Equal([Capability.Meaning], after.ToArray());
        Assert.Equal(283_639_807L, Capabilities.TotalBytes(after));
    }

    // ---- the arithmetic ----

    [Fact]
    public void TheSizeBesideARowIsWhatItAddsToWhatIsAlreadyChosen()
    {
        // The whole reason the spec forbids a fixed per-row table: Speech costs 818 MB on its
        // own and 547 MB once documents have already brought the e5 pair in. A fixed table
        // shows one of those two numbers and makes the total visibly fail to add up.
        Assert.Equal(857_630_309L, Capabilities.MarginalBytes(Capability.Speech, []));
        Assert.Equal(573_990_502L, Capabilities.MarginalBytes(Capability.Speech, [Capability.Meaning]));
        Assert.Equal("818 MB", Sizes.Human(Capabilities.MarginalBytes(Capability.Speech, [])));
        Assert.Equal("547 MB", Sizes.Human(Capabilities.MarginalBytes(Capability.Speech, [Capability.Meaning])));
    }

    [Fact]
    public void TheMarginalCostOfSomethingAlreadyChosenIsNothing()
    {
        Assert.Equal(0L, Capabilities.MarginalBytes(Capability.Photos, [Capability.Photos]));
        Assert.Equal(0L, Capabilities.MarginalBytes(Capability.Meaning, [Capability.Speech]));
    }

    [Fact]
    public void HebrewsMarginalCostIsTheFineTuneAloneOnceSpeechIsThere()
    {
        Assert.Equal(1_624_558_796L, Capabilities.MarginalBytes(Capability.Hebrew, [Capability.Speech]));
        // and from nothing it is the fine-tune plus everything Speech drags in
        Assert.Equal(1_624_558_796L + 857_630_309L, Capabilities.MarginalBytes(Capability.Hebrew, []));
    }

    [Fact]
    public void ATotalCountsAModelSharedByTwoCapabilitiesOnce()
    {
        // Adding the numbers shown beside the rows is the arithmetic a person does in their
        // head and it is WRONG here, because Meaning and Speech share 270 MB. The total is the
        // union of the files, not the sum of the rows.
        Assert.Equal(857_630_309L, Capabilities.TotalBytes([Capability.Meaning, Capability.Speech]));
        Assert.NotEqual(283_639_807L + 857_630_309L,
                        Capabilities.TotalBytes([Capability.Meaning, Capability.Speech]));
    }

    [Fact]
    public void EverythingIsTheNumberOnTheReadme()
    {
        Assert.Equal(3_141_848_265L, Capabilities.TotalBytes(Capabilities.All));
        Assert.Equal("2.93 GB", Sizes.Human(Capabilities.TotalBytes(Capabilities.All)));
    }

    // ---- the presets ----

    [Fact]
    public void TheThreePresetsAreTheOnesOnTheFirstScreen()
    {
        Assert.Empty(Presets.JustNames);
        Assert.Equal(0L, Capabilities.TotalBytes(Presets.JustNames));

        Assert.Equal([Capability.Photos, Capability.Meaning],
                     Presets.Recommended.OrderBy(c => (int)c).ToArray());
        Assert.Equal("900 MB", Sizes.Human(Capabilities.TotalBytes(Presets.Recommended)));

        Assert.Equal(4, Presets.Everything.Count);
        Assert.Equal("2.93 GB", Sizes.Human(Capabilities.TotalBytes(Presets.Everything)));
    }

    [Fact]
    public void ASelectionThatIsNoPresetIsCustom()
    {
        Assert.Equal(Preset.Recommended, Presets.Match(Presets.Recommended));
        Assert.Equal(Preset.JustNames, Presets.Match(Presets.JustNames));
        Assert.Equal(Preset.Everything, Presets.Match(Presets.Everything));
        Assert.Equal(Preset.Custom, Presets.Match(new HashSet<Capability> { Capability.Photos }));
    }

    // ---- what a capability covers ----

    [Fact]
    public void EnablingACapabilityCoversExactlyTheKindsItCanRead()
    {
        // This is what a newly installed capability re-queues. A capability that claims every
        // kind re-indexes a finished disk, which spec §2a calls the worst thing this product
        // can do to someone.
        Assert.Equal([(int)ResultKind.Photo, (int)ResultKind.Video], Capabilities.KindsCovered(Capability.Photos));
        Assert.Equal([(int)ResultKind.Document], Capabilities.KindsCovered(Capability.Meaning));
        Assert.Equal([(int)ResultKind.Audio, (int)ResultKind.Video], Capabilities.KindsCovered(Capability.Speech));
        Assert.Equal([(int)ResultKind.Audio, (int)ResultKind.Video], Capabilities.KindsCovered(Capability.Hebrew));
    }

    [Fact]
    public void NoCapabilityClaimsAKindWithNoContentToRead()
    {
        foreach (Capability c in Capabilities.All)
            foreach (int k in Capabilities.KindsCovered(c))
                Assert.True(FileKinds.HasContent((ResultKind)k),
                    $"{c} claims to cover {(ResultKind)k}, which has nothing inside it to read");
    }

    // ---- the Hebrew row ----

    [Theory]
    [InlineData(new[] { "en-US" }, false)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "th-TH", "nb-NO" }, false)]   // a substring match on "he" says true here
    [InlineData(new[] { "en-US", "he-IL" }, true)]
    [InlineData(new[] { "he" }, true)]
    [InlineData(new[] { "HE-il" }, true)]
    public void HebrewIsOfferedOnlyWhereHebrewIsInstalled(string[] tags, bool want)
        => Assert.Equal(want, Capabilities.HebrewIsOffered(tags));

    // ---- the offer on the card ----

    [Fact]
    public void AQueryForPicturesOffersThePictureCapabilityAndItsPrice()
    {
        Offer? o = Capabilities.OfferFor(new SearchQuery("sunset type:photo"), CapabilitySet.None);
        Assert.NotNull(o);
        Assert.Equal(Capability.Photos, o!.Value.Capability);
        Assert.Equal(659_659_160L, o.Value.MarginalBytes);
        Assert.Contains("629 MB", o.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsOfferedForACapabilityThatIsAlreadyInstalled()
    {
        // The control that stops an unconditional offer passing the test above. A pill that
        // asks a paying customer to buy what they already own is worse than silence.
        Assert.Null(Capabilities.OfferFor(new SearchQuery("sunset type:photo"), Set(Capability.Photos)));
    }

    [Fact]
    public void AQueryForSoundOffersSpeechAtWhatItWouldActuallyCostThisMachine()
    {
        // Marginal again: on a machine that already has documents' meaning, Speech is 547 MB
        // and the offer must say so rather than quoting the standalone 818.
        Offer? bare = Capabilities.OfferFor(new SearchQuery("what she said type:audio"), CapabilitySet.None);
        Offer? withDocs = Capabilities.OfferFor(new SearchQuery("what she said type:audio"), Set(Capability.Meaning));
        Assert.Equal(Capability.Speech, bare!.Value.Capability);
        Assert.Contains("818 MB", bare.Value.Text, StringComparison.Ordinal);
        Assert.Contains("547 MB", withDocs!.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryWordQueryOffersMeaningAndNotThePictureModels()
    {
        Offer? o = Capabilities.OfferFor(new SearchQuery("the lease"), CapabilitySet.None);
        Assert.Equal(Capability.Meaning, o!.Value.Capability);
    }

    [Fact]
    public void WithEverythingInstalledThereIsNothingToOffer()
    {
        var everything = new CapabilitySet(Presets.Everything);
        Assert.Null(Capabilities.OfferFor(new SearchQuery("sunset type:photo"), everything));
        Assert.Null(Capabilities.OfferFor(new SearchQuery("the lease"), everything));
        Assert.Null(Capabilities.OfferFor(new SearchQuery("said type:audio"), everything));
    }

    [Fact]
    public void HebrewIsNeverOfferedFromTheCard()
    {
        // It is a refinement of a capability somebody already chose, on a machine whose
        // language list says it is worth having. Offering a 1.5 GB download off the back of a
        // query is not a decision to make in a search box.
        foreach (string q in new[] { "שלום", "type:audio שלום", "shalom" })
            Assert.NotEqual(Capability.Hebrew,
                Capabilities.OfferFor(new SearchQuery(q), CapabilitySet.None)?.Capability);
    }

    [Fact]
    public void AnInstalledSetIsReadFromTheFilesOnDiskAndNotFromASetting()
    {
        // What is installed is a fact about the disk. Reading it from config.json would let a
        // hand-edited file claim a capability whose 1.5 GB is not there, and every load would
        // then fail at the first file a query touched.
        //
        // Meaning rather than Photos: the rule is the same and the fixture is 254 MB instead of
        // 603 MB. See Fill below.
        string dir = Path.Combine(Path.GetTempPath(), "findra-caps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(CapabilitySet.Installed(dir).Has(Capability.Meaning));
            foreach (Model m in Capabilities.OwnModels(Capability.Meaning))
                Fill(m, dir);
            Assert.True(CapabilitySet.Installed(dir).Has(Capability.Meaning));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void ACapabilityWhoseFilesArePartlyThereIsNotInstalled()
    {
        // One of the two e5 files is not "meaning works". An Any() here rather than an All()
        // would light the capability up and then fail on the first query.
        string dir = Path.Combine(Path.GetTempPath(), "findra-caps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Fill(ModelStore.E5Spm, dir);          // the small half only
            Assert.False(CapabilitySet.Installed(dir).Has(Capability.Meaning));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void SpeechIsNotInstalledWhileTheDocumentModelsItNeedsAreMissing()
    {
        // Installed-ness follows the same graph the download does. A machine holding half the e5
        // pair and nothing else cannot answer a speech search, because a transcript is searched
        // as a document - and it must not report Speech as ready.
        string dir = Path.Combine(Path.GetTempPath(), "findra-caps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Fill(ModelStore.E5Spm, dir);
            Assert.False(CapabilitySet.Installed(dir).Has(Capability.Speech));
            Assert.False(CapabilitySet.Installed(dir).Has(Capability.Meaning));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>
    /// A file just long enough to count as one of its model's.
    ///
    /// <para><c>SetLength</c> allocates the clusters on NTFS, so a fixture built on a large
    /// model's floor writes hundreds of megabytes of temporary disk for an assertion about a
    /// boolean - and on a small or full disk <c>dotnet test</c> then fails for a reason nobody
    /// connects to this test. The three tests above prove their rules with the e5 pair (254 MB
    /// between them, and 4 MB for the two that need only one file); the SigLIP trio would cost
    /// 603 MB apiece for exactly the same assertions.</para>
    /// </summary>
    private static void Fill(Model m, string dir)
    {
        using var fs = new FileStream(ModelStore.PathOf(m, dir), FileMode.Create);
        fs.SetLength(m.MinBytes);
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter CapabilityTests`
Expected: FAIL - `Capability`, `Capabilities`, `CapabilitySet`, `Presets`, `Preset` and `Offer` do not exist.

- [ ] **Step 3: Write it**

Create `src/Findra/Models/Capabilities.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Findra;

/// <summary>The four things a model buys. Words in documents is not here: it needs no model, it
/// is always on, and it is printed "free" on the first-run screen precisely so that taking none
/// of these is a safe choice rather than a broken one (spec §6).</summary>
public enum Capability { Photos, Meaning, Speech, Hebrew }

/// <summary>Which capability a preset stands for. Custom is not a preset a user picks - it is
/// what the screen becomes the moment they touch a row.</summary>
public enum Preset { JustNames, Recommended, Everything, Custom }

/// <summary>A capability the card would like to offer, and what it would cost on THIS machine.
/// <see cref="MarginalBytes"/> is marginal given what is already installed, so the sentence a
/// person reads is the size they would actually download.</summary>
public readonly record struct Offer(Capability Capability, long MarginalBytes, string Text);

/// <summary>
/// What this machine can actually do right now, read from the files on disk.
///
/// <para>Deliberately not read from config.json: what is installed is a fact about the disk, and
/// a settings file that claims a capability whose 1.5 GB is not there produces a load failure on
/// the first query instead of a quiet skip. The selection a user made is a setting; what arrived
/// is not.</para>
/// </summary>
public readonly record struct CapabilitySet(IReadOnlySet<Capability> Have)
{
    public static readonly CapabilitySet None = new(new HashSet<Capability>());

    public bool Has(Capability c) => Have is not null && Have.Contains(c);

    /// <summary>Every capability whose whole closed model set is on disk. Closed, not own: a
    /// Whisper file with no e5 pair beside it cannot answer a search, because a transcript is
    /// searched as a document.</summary>
    public static CapabilitySet Installed(string? dir = null)
    {
        var have = new HashSet<Capability>();
        foreach (Capability c in Capabilities.All)
        {
            bool all = true;
            foreach (Model m in Capabilities.ModelsFor(Capabilities.Close([c])))
                if (!ModelStore.Present(m, dir)) { all = false; break; }
            if (all) have.Add(c);
        }
        return new CapabilitySet(have);
    }
}

public static class Presets
{
    public static readonly IReadOnlySet<Capability> JustNames = new HashSet<Capability>();
    public static readonly IReadOnlySet<Capability> Recommended =
        Capabilities.Close([Capability.Photos, Capability.Meaning]);
    public static readonly IReadOnlySet<Capability> Everything =
        Capabilities.Close([Capability.Photos, Capability.Meaning, Capability.Speech, Capability.Hebrew]);

    public static Preset Match(IReadOnlySet<Capability> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        if (chosen.SetEquals(JustNames)) return Preset.JustNames;
        if (chosen.SetEquals(Recommended)) return Preset.Recommended;
        if (chosen.SetEquals(Everything)) return Preset.Everything;
        return Preset.Custom;
    }
}

/// <summary>
/// The capability graph and every number derived from it.
///
/// <para>The capabilities are NOT peers, and the two edges fall out of the engine rather than out
/// of a preference: Speech needs Meaning because a transcript is embedded and searched exactly
/// like a document, and Hebrew needs Speech because the general model runs first for language
/// detection and only the files it calls Hebrew are re-run through the fine-tune. Hebrew is a
/// second pass, never an alternative (spec §6).</para>
///
/// <para>Every size this type produces is MARGINAL - what adding one costs given what is already
/// chosen. Nothing anywhere may hold a fixed per-capability number: Speech is 818 MB alone and
/// 547 MB beside documents, and a fixed table makes the first-run total visibly fail to add
/// up.</para>
/// </summary>
public static class Capabilities
{
    public static readonly IReadOnlyList<Capability> All =
        [Capability.Photos, Capability.Meaning, Capability.Speech, Capability.Hebrew];

    /// <summary>The DIRECT prerequisites. <see cref="Close"/> walks these to a fixed point.</summary>
    public static IReadOnlyList<Capability> Requires(Capability c) => c switch
    {
        Capability.Speech => [Capability.Meaning],
        Capability.Hebrew => [Capability.Speech],
        _ => [],
    };

    /// <summary>A selection with everything it depends on, transitively. Hebrew closes to
    /// {Hebrew, Speech, Meaning} - a single step would stop at Speech and produce a download set
    /// that installs and then cannot answer anything.</summary>
    public static IReadOnlySet<Capability> Close(IEnumerable<Capability> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var have = new HashSet<Capability>(chosen);
        var queue = new Queue<Capability>(have);
        while (queue.Count > 0)
            foreach (Capability need in Requires(queue.Dequeue()))
                if (have.Add(need)) queue.Enqueue(need);
        return have;
    }

    /// <summary>A selection with one capability removed, and with anything that depended on it
    /// removed too. Unticking Speech while Hebrew is ticked must take Hebrew with it; leaving it
    /// selects a 1.5 GB fine-tune with no model to detect language for it.</summary>
    public static IReadOnlySet<Capability> Drop(IEnumerable<Capability> from, Capability gone)
    {
        ArgumentNullException.ThrowIfNull(from);
        var keep = new HashSet<Capability>(from);
        keep.Remove(gone);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Capability c in keep.ToList())
                foreach (Capability need in Requires(c))
                    if (!keep.Contains(need)) { keep.Remove(c); changed = true; }
        }
        return keep;
    }

    /// <summary>The files this capability itself adds - not its prerequisites'.</summary>
    public static IReadOnlyList<Model> OwnModels(Capability c) => c switch
    {
        Capability.Photos => [ModelStore.Siglip2Vision, ModelStore.Siglip2Text, ModelStore.Siglip2Spm],
        Capability.Meaning => [ModelStore.E5Base, ModelStore.E5Spm],
        Capability.Speech => [ModelStore.WhisperTurbo],
        Capability.Hebrew => [ModelStore.WhisperHebrew],
        _ => [],
    };

    /// <summary>Every file a selection needs, closed and de-duplicated.</summary>
    public static IReadOnlyList<Model> ModelsFor(IEnumerable<Capability> chosen)
    {
        var files = new List<Model>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Capability c in Close(chosen))
            foreach (Model m in OwnModels(c))
                if (seen.Add(m.File)) files.Add(m);
        return files;
    }

    public static long TotalBytes(IEnumerable<Capability> chosen) => ModelStore.TotalBytes(ModelsFor(chosen));

    /// <summary>What adding one more capability would cost, given what is already chosen. This
    /// is the only place the arithmetic lives, and every surface reads it from here.</summary>
    public static long MarginalBytes(Capability add, IEnumerable<Capability> already)
    {
        ArgumentNullException.ThrowIfNull(already);
        var have = new HashSet<Capability>(already);
        long before = TotalBytes(have);
        have.Add(add);
        return TotalBytes(have) - before;
    }

    /// <summary>The result kinds this capability can newly read - and therefore exactly what
    /// enabling it re-queues. Nothing else is touched.</summary>
    public static int[] KindsCovered(Capability c) => c switch
    {
        Capability.Photos => [(int)ResultKind.Photo, (int)ResultKind.Video],
        Capability.Meaning => [(int)ResultKind.Document],
        // A transcript is speech, and speech lives in both sound files and the sound track of a
        // short video. The Hebrew pass covers the same two kinds: it re-runs files the general
        // model already heard, and there is no way to know which without re-opening them.
        Capability.Speech or Capability.Hebrew => [(int)ResultKind.Audio, (int)ResultKind.Video],
        _ => [],
    };

    public static string Title(Capability c) => c switch
    {
        Capability.Photos => "Photos and video",
        Capability.Meaning => "Meaning in documents",
        Capability.Speech => "Speech",
        Capability.Hebrew => "Speech in Hebrew",
        _ => c.ToString(),
    };

    /// <summary>Is a Hebrew row worth showing at all? Spec §6: only when the system locale or the
    /// installed languages include Hebrew. Compared on the language SUBTAG, because a substring
    /// test on "he" is true for "th-TH" and would put a 1.5 GB row in front of a Thai user.</summary>
    public static bool HebrewIsOffered(IEnumerable<string> languageTags)
    {
        ArgumentNullException.ThrowIfNull(languageTags);
        foreach (string tag in languageTags)
        {
            string primary = tag.Split('-')[0];
            if (primary.Equals("he", StringComparison.OrdinalIgnoreCase)
             || primary.Equals("iw", StringComparison.OrdinalIgnoreCase))   // the legacy tag, still emitted
                return true;
        }
        return false;
    }

    /// <summary>The language tags this machine actually has. Impure, so it is separate from the
    /// rule above and the rule stays testable. Its one caller in this plan is <c>--models list</c>
    /// (Task 12), which shows the Hebrew row only on a machine where it is worth 1.5 GB; Plan 6's
    /// first-run screen becomes the second.</summary>
    public static IReadOnlyList<string> SystemLanguages()
    {
        var tags = new List<string> { CultureInfo.CurrentUICulture.Name, CultureInfo.InstalledUICulture.Name };
        try { foreach (string t in Windows.System.UserProfile.GlobalizationPreferences.Languages) tags.Add(t); }
        catch (Exception ex) { Log.Once("models|langs", "WARN", "models", $"could not read the installed languages :: {ex.Message}"); }
        return tags;
    }

    /// <summary>
    /// What this query would have found with a capability this machine has not got - or null.
    ///
    /// <para>The rule is deliberately narrow and ordered, because an offer that fires on every
    /// query is an advertisement. A query that names a kind offers the capability that reads that
    /// kind; anything else offers meaning in documents, which is the one that changes an ordinary
    /// word search. Hebrew is never offered here: it refines a capability somebody already chose,
    /// and 1.5 GB is not a decision to put in a search box.</para>
    /// </summary>
    public static Offer? OfferFor(SearchQuery q, CapabilitySet installed)
    {
        ArgumentNullException.ThrowIfNull(q);
        Capability? want = null;
        if (q.Kinds.Contains(ResultKind.Photo) || q.Kinds.Contains(ResultKind.Video)) want = Capability.Photos;
        else if (q.Kinds.Contains(ResultKind.Audio)) want = Capability.Speech;
        else if (q.HasNameTerms) want = Capability.Meaning;
        if (want is null || installed.Has(want.Value)) return null;

        long marginal = MarginalBytes(want.Value, installed.Have ?? new HashSet<Capability>());
        string what = want.Value switch
        {
            Capability.Photos => "Searching inside photos and video",
            Capability.Speech => "Searching what was said out loud",
            _ => "Searching documents by meaning rather than exact words",
        };
        return new Offer(want.Value, marginal, $"{what} needs {Sizes.Human(marginal)} - get it?");
    }
}
```

- [ ] **Step 4: Run it**

Run: `dotnet test --filter CapabilityTests`
Expected: PASS, 25 test methods / 30 cases with the theory rows.

- [ ] **Step 5: Commit**

```bash
git add src/Findra/Models/Capabilities.cs tests/Findra.Tests/Models/CapabilityTests.cs
git commit -m "The capability graph: what depends on what, and what each row actually costs"
```

---

## Task 4: Downloads, in the interface, resumable

**Files:**
- Create: `src/Findra/Models/ModelDownloader.cs`
- Test: `tests/Findra.Tests/Models/ModelDownloadTests.cs`

**Interfaces:**
- Consumes: `Model`, `ModelStore`, `Sizes` (Task 2).
- Produces:
  - `Findra.Fetched` - `sealed record Fetched(Stream Body, long TotalBytes, bool IsResume) : IDisposable`.
  - `Findra.Fetch` - `delegate Task<Fetched> Fetch(string url, long from, CancellationToken ct)`.
  - `Findra.RangeRefusedException` - thrown by a `Fetch` whose server will not honour the range.
  - `Findra.DownloadProgress` - `readonly record struct DownloadProgress(string File, long Got, long Total)`.
  - `Findra.DownloadOutcome` - `readonly record struct DownloadOutcome(Model Model, bool Complete, long Got, string? Problem)`.
  - `Findra.ModelDownloader` - `static Task<DownloadOutcome> GetAsync(Model, string dir, Fetch, Action<DownloadProgress>?, CancellationToken)`, `static Task<IReadOnlyList<DownloadOutcome>> GetAllAsync(IEnumerable<Model>, string dir, Fetch, Action<DownloadProgress>?, CancellationToken)`, `static Fetch Http(HttpClient)`.

**The two traps in this task:**

1. **Resuming is invisible in the result.** A downloader that ignores the bytes already fetched and starts over produces exactly the right file. The only way to catch it is to assert on what was *asked for* and how much was *transferred*, and the test below does both.
2. **The source promotes whatever arrived.** `File.Move(part, final)` runs unconditionally after the read loop (`Encoders.cs:97`), so a connection that closes early leaves a short file under the final name, above its floor, and `Present` says yes for ever. That is how a capability becomes permanently broken with nothing anywhere to say why.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Models/ModelDownloadTests.cs`:

```csharp
using System.Text;

using Findra;

public class ModelDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-dl-" + Guid.NewGuid().ToString("N"));

    public ModelDownloadTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A model whose floor is small enough to satisfy with a handful of bytes, so the
    /// whole download path can be exercised without a network or a gigabyte.</summary>
    private static readonly Model Tiny = new("tiny.bin", "https://example.invalid/tiny.bin", 6, 9, "a test");

    private static readonly byte[] Content = Encoding.ASCII.GetBytes("ABCDEFGHI");   // 9 bytes

    /// <summary>A server that honours ranges, recording what it was asked for.</summary>
    private static Fetch Server(List<long> askedFrom, byte[]? body = null)
    {
        byte[] all = body ?? Content;
        return (url, from, ct) =>
        {
            askedFrom.Add(from);
            if (from > all.Length) throw new RangeRefusedException(url, from);
            var slice = new MemoryStream(all, (int)from, all.Length - (int)from, writable: false);
            return Task.FromResult(new Fetched(slice, all.Length, from > 0));
        };
    }

    private string Part => Path.Combine(_dir, Tiny.File + ".part");
    private string Final => Path.Combine(_dir, Tiny.File);

    [Fact]
    public async Task AFinishedFileIsNotFetchedAgain()
    {
        // Spec §2a. Re-downloading gigabytes because an upgrade did not look first is the
        // single most annoying thing this product could do to someone, and it gets a test.
        File.WriteAllBytes(Final, Content);
        var asked = new List<long>();

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, Server(asked), null, default);

        Assert.Empty(asked);               // nothing was requested at all
        Assert.True(r.Complete);
    }

    [Fact]
    public async Task APartialDownloadResumesFromTheByteAlreadyFetched()
    {
        // The assertion that matters is `asked` - a downloader that throws the part away and
        // starts over produces a byte-identical file, so the file alone proves nothing.
        File.WriteAllBytes(Part, Content[..3]);
        var asked = new List<long>();

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, Server(asked), null, default);

        Assert.Equal([3L], asked);                                   // it asked for the rest
        Assert.Equal(Content, File.ReadAllBytes(Final));
        Assert.False(File.Exists(Part));
        Assert.True(r.Complete);
    }

    [Fact]
    public async Task ProgressCountsTheWholeFileAndNotJustThisLeg()
    {
        // Resuming a 1.5 GB file at 60% and then showing 0% is a bar that says the download
        // restarted when it did not. The last report must be 9 of 9, not 6 of 9.
        File.WriteAllBytes(Part, Content[..3]);
        var seen = new List<DownloadProgress>();

        await ModelDownloader.GetAsync(Tiny, _dir, Server([]), seen.Add, default);

        Assert.NotEmpty(seen);
        Assert.Equal(9L, seen[^1].Got);
        Assert.Equal(9L, seen[^1].Total);
        Assert.All(seen, p => Assert.True(p.Got >= 3, $"progress went backwards to {p.Got}"));
    }

    [Fact]
    public async Task ADownloadThatEndsShortIsNotPromoted()
    {
        // The source moves whatever arrived into place. A short file above its floor then reads
        // as installed for ever: every load fails, the capability is dead, and nothing
        // re-downloads it because it is "there".
        Fetch truncating = (url, from, ct) =>
            Task.FromResult(new Fetched(new MemoryStream(Content[..5]), Content.Length, from > 0));

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, truncating, null, default);

        Assert.False(r.Complete);
        Assert.False(File.Exists(Final));                       // nothing was promoted
        Assert.Equal(5, new FileInfo(Part).Length);             // and what arrived is kept, to resume from
        Assert.Contains("5", r.Problem!, StringComparison.Ordinal);
        Assert.Contains("9", r.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStalePartAgainstARepublishedFileStartsOver()
    {
        // A .part longer than the whole file cannot be a prefix of it. Without the restart the
        // install can never complete again, on any run.
        File.WriteAllBytes(Part, Encoding.ASCII.GetBytes("ZZZZZZZZZZZZZZZ"));   // 15 > 9
        var asked = new List<long>();

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, Server(asked), null, default);

        Assert.Equal([15L, 0L], asked);                 // refused, then started over
        Assert.Equal(Content, File.ReadAllBytes(Final));
        Assert.True(r.Complete);
    }

    [Fact]
    public async Task APartThatIsAlreadyTheWholeFileIsPromotedRatherThanFetchedAgain()
    {
        // Cancelled or killed between the last write and the rename. The .part holds the whole
        // file, the next run asks for a range at the end, the server refuses - and treating that
        // the same way as a stale part costs a full re-download of something already on the disk.
        File.WriteAllBytes(Part, Content);              // exactly 9 bytes, well over the floor of 6
        var asked = new List<long>();

        DownloadOutcome r = await ModelDownloader.GetAsync(Tiny, _dir, Server(asked), null, default);

        Assert.Equal([9L], asked);                      // refused once, and NOT re-fetched from 0
        Assert.True(r.Complete);
        Assert.Equal(Content, File.ReadAllBytes(Final));
        Assert.False(File.Exists(Part));
    }

    [Fact]
    public async Task CancellingLeavesThePartSoTheNextRunResumes()
    {
        using var cts = new CancellationTokenSource();
        Fetch slow = (url, from, ct) =>
            Task.FromResult(new Fetched(new CancellingStream(Content, after: 4, cts), Content.Length, from > 0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ModelDownloader.GetAsync(Tiny, _dir, slow, null, cts.Token));

        Assert.False(File.Exists(Final));
        Assert.True(File.Exists(Part));
        Assert.Equal(4, new FileInfo(Part).Length);
    }

    [Fact]
    public async Task EachFileInASetIsFetchedOnceAndTheOnesAlreadyThereAreSkipped()
    {
        var second = new Model("second.bin", "https://example.invalid/second.bin", 6, 9, "a second test");
        File.WriteAllBytes(Final, Content);              // Tiny is already installed
        var asked = new List<string>();
        Fetch f = (url, from, ct) => { asked.Add(url); return Task.FromResult(new Fetched(new MemoryStream(Content), 9, false)); };

        IReadOnlyList<DownloadOutcome> all = await ModelDownloader.GetAllAsync([Tiny, second], _dir, f, null, default);

        Assert.Equal([second.Url], asked);
        Assert.All(all, o => Assert.True(o.Complete));
    }

    [Fact]
    public void TheIndexerChildNeverDownloadsAnything()
    {
        // Spec §6 moves consent and progress onto the first-run screen, and the source engine
        // did the opposite: its indexer blocked the entire queue until all seven files existed
        // and fetched them itself. This is the guard that stops that coming back with a port.
        string content = Path.Combine(RepoRoot(), "src", "Findra", "Content");
        foreach (string file in Directory.EnumerateFiles(content, "*.cs"))
        {
            string src = File.ReadAllText(file);
            foreach (string banned in new[] { "HttpClient", "ModelDownloader", "WebClient", "HttpRequestMessage" })
                Assert.False(src.Contains(banned, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} mentions {banned}; downloads belong to the interface, not to the indexer child");
        }
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Findra.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>A body that cancels the token partway through, so the writer is interrupted
    /// mid-file exactly as a dropped connection would interrupt it.</summary>
    private sealed class CancellingStream(byte[] all, int after, CancellationTokenSource cts) : Stream
    {
        private int _at;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= after) { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }
            int n = Math.Min(count, after - _at);
            Array.Copy(all, _at, buffer, offset, n);
            _at += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => all.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter ModelDownloadTests`
Expected: FAIL - `ModelDownloader`, `Fetch`, `Fetched`, `RangeRefusedException`, `DownloadProgress` and `DownloadOutcome` do not exist. `TheIndexerChildNeverDownloadsAnything` passes already; it is a guard over a rule that currently holds.

- [ ] **Step 3: Write it**

Create `src/Findra/Models/ModelDownloader.cs`. Port `ModelStore.EnsureAsync` (`Encoders.cs:64-100`), keeping the resume shape and adding the completeness check the source lacks.

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Findra;

/// <summary>One response body, and how many bytes the whole file is. <see cref="TotalBytes"/> is
/// the WHOLE file, not this leg of it, so a resumed download reports honest progress.</summary>
public sealed record Fetched(Stream Body, long TotalBytes, bool IsResume) : IDisposable
{
    public void Dispose() => Body.Dispose();
}

/// <summary>Fetch <paramref name="url"/> starting at byte <paramref name="from"/>. The one seam
/// between the downloader and the network, so every test in this file runs without one.</summary>
public delegate Task<Fetched> Fetch(string url, long from, CancellationToken ct);

/// <summary>The server would not serve from that offset - the file behind the URL changed, or
/// the partial file is longer than the whole. The downloader answers by starting over.</summary>
public sealed class RangeRefusedException(string url, long from)
    : Exception($"the server would not serve {url} from byte {from.ToString(CultureInfo.InvariantCulture)}");

public readonly record struct DownloadProgress(string File, long Got, long Total);

public readonly record struct DownloadOutcome(Model Model, bool Complete, long Got, string? Problem);

/// <summary>
/// Fetching model files, one at a time, resumably - and IN THE INTERFACE.
///
/// <para>The engine this comes from downloaded inside the indexer child, and blocked its whole
/// queue until all seven files existed. That is exactly what the spec forbids: consent lives on
/// the first-run screen, progress is shown there, and the child only ever asks whether a file is
/// on disk. Nothing under <c>src/Findra/Content/</c> may reference this type.</para>
///
/// <para>Progress is not written to the database. The <c>.part</c> file IS the durable progress -
/// it survives a reboot and a dropped connection by being on disk - and the index's single
/// writer connection belongs to the queue feeder.</para>
/// </summary>
public static class ModelDownloader
{
    /// <summary>Progress is reported at most this often, so a 1.5 GB file does not spend its
    /// time repainting a bar.</summary>
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(300);

    public static async Task<DownloadOutcome> GetAsync(Model m, string dir, Fetch fetch,
                                                       Action<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(fetch);
        Directory.CreateDirectory(dir);
        string final = ModelStore.PathOf(m, dir), part = final + ".part";

        // Already here and long enough: not one byte is requested. Spec §2a - models present and
        // the right size are kept, not re-downloaded.
        if (ModelStore.Present(m, dir)) return new DownloadOutcome(m, true, ModelStore.ActualBytes(m, dir), null);

        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        Fetched got;
        try
        {
            got = await fetch(m.Url, have, ct).ConfigureAwait(false);
        }
        catch (RangeRefusedException ex)
        {
            // Two very different situations arrive here as the same status code, and telling them
            // apart is worth 1.5 GB.
            //
            // The first is a .part that is ALREADY THE WHOLE FILE - the process was cancelled or
            // killed between the last write and the rename below. A range at or past the end is
            // refused, and discarding it throws away a complete file sitting on the disk, which
            // spec §2a calls the single most annoying thing this product could do to someone. So
            // try promoting it and asking whether it is the file.
            if (have >= m.MinBytes)
            {
                try
                {
                    File.Move(part, final, overwrite: true);
                    if (ModelStore.Present(m, dir))
                    {
                        Log.Info("models", $"{m.File} was already complete on disk - nothing was fetched");
                        return new DownloadOutcome(m, true, ModelStore.ActualBytes(m, dir), null);
                    }
                    File.Move(final, part, overwrite: true);   // not the file; put it back
                }
                catch (IOException) { }
            }

            // The second is a stale .part against a file that has been re-published. Keeping it
            // would make every future run ask for a range that is refused, so the install could
            // never finish again on any run.
            Log.Warn("models", $"{m.File}: {ex.Message} - starting again from the beginning");
            try { File.Delete(part); } catch (IOException) { }
            have = 0;
            got = await fetch(m.Url, 0, ct).ConfigureAwait(false);
        }

        long done = got.IsResume ? have : 0;
        long total = got.TotalBytes;
        using (got)
        using (var fs = new FileStream(part, got.IsResume ? FileMode.Append : FileMode.Create,
                                       FileAccess.Write, FileShare.None))
        {
            Log.Info("models", $"fetching {m.File} ({m.Purpose})" +
                               (done > 0 ? $", resuming at {Sizes.Human(done)}" : ""));
            var buf = new byte[1 << 16];
            DateTime last = DateTime.UtcNow;
            int n;
            while ((n = await got.Body.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (DateTime.UtcNow - last > ProgressEvery)
                {
                    last = DateTime.UtcNow;
                    progress?.Invoke(new DownloadProgress(m.File, done, total));
                }
            }
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        progress?.Invoke(new DownloadProgress(m.File, done, total));

        // The completeness check the source has not got. A connection that closes early leaves a
        // short file, and promoting it puts something above its floor under the final name for
        // ever: every load fails and nothing re-fetches it, because it is "there".
        if (total > 0 && done < total)
        {
            string problem = $"the download ended at {done.ToString(CultureInfo.InvariantCulture)} of " +
                             $"{total.ToString(CultureInfo.InvariantCulture)} bytes";
            Log.Warn("models", $"{m.File}: {problem} - keeping what arrived so the next run resumes");
            return new DownloadOutcome(m, false, done, problem);
        }

        try
        {
            File.Move(part, final, overwrite: true);
        }
        catch (IOException ex)
        {
            // The one place the model directory is genuinely contended: if the indexer child has
            // the previous copy of this file open in an ONNX or whisper session, the rename is
            // refused. Everything fetched is in the .part, so the next run - after the child has
            // been restarted - resumes at zero cost. Letting this out of GetAllAsync would take
            // the first-run download down with an unhandled exception at the last byte.
            Log.Warn("models", $"{m.File}: downloaded, but could not be moved into place :: {ex.Message}");
            return new DownloadOutcome(m, false, done, ex.Message);
        }
        Log.Info("models", $"{m.File} is ready ({Sizes.Human(ModelStore.ActualBytes(m, dir))})");
        return new DownloadOutcome(m, true, done, null);
    }

    /// <summary>Every file in the set, in order, skipping the ones already there. Stops at the
    /// first failure - a set half fetched is resumable, and pressing on after a network fault
    /// only turns one failed file into six.</summary>
    public static async Task<IReadOnlyList<DownloadOutcome>> GetAllAsync(
        IEnumerable<Model> set, string dir, Fetch fetch, Action<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(set);
        var outcomes = new List<DownloadOutcome>();
        foreach (Model m in set)
        {
            DownloadOutcome o = await GetAsync(m, dir, fetch, progress, ct).ConfigureAwait(false);
            outcomes.Add(o);
            if (!o.Complete) break;
        }
        return outcomes;
    }

    /// <summary>The real fetch. One GET, a Range header when there is something to resume, and
    /// no header, parameter or identifier beyond a user agent - the model host sees the same
    /// request any browser would make.</summary>
    public static Fetch Http(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return async (url, from, ct) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (from > 0) req.Headers.Range = new RangeHeaderValue(from, null);
            HttpResponseMessage resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                                 .ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                resp.Dispose();
                throw new RangeRefusedException(url, from);
            }
            resp.EnsureSuccessStatusCode();
            bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
            long total = (resp.Content.Headers.ContentLength ?? 0) + (resumed ? from : 0);
            Stream body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new Fetched(body, total, resumed);
        };
    }
}
```

- [ ] **Step 4: Run it**

Run: `dotnet test --filter ModelDownloadTests`
Expected: PASS, 9 tests.

- [ ] **Step 5: Prove the resume test has teeth**

Temporarily change `long done = got.IsResume ? have : 0;` to `long done = 0;` - the mutation that keeps the resume but forgets the bytes already on disk. Run the suite:

- `ProgressCountsTheWholeFileAndNotJustThisLeg` fails: the last report is `(6, 9)`.
- `APartialDownloadResumesFromTheByteAlreadyFetched` fails with it, because `done < total` now trips the completeness check and nothing is promoted.

Revert. **Record the result in your report** - this is the one subject in this plan that produces a correct-looking file when it is wrong.

Do **not** use the from-zero mutation (`FileMode.Create` plus `fetch(url, 0)`). Under it the server returns all nine bytes, `IsResume` is false, `done` runs 0 to 9 honestly, and the progress test **passes** - only the resume test fails. A mutation that half-works teaches the wrong lesson about which assertion is load-bearing, and an earlier draft of this plan prescribed exactly that.

- [ ] **Step 6: Commit**

```bash
git add src/Findra/Models/ModelDownloader.cs tests/Findra.Tests/Models/ModelDownloadTests.cs
git commit -m "Model downloads that resume, refuse a short file, and never run in the child"
```

---

## Task 5: Whatever silicon is there

**Files:**
- Create: `src/Findra/Models/Providers.cs`
- Test: `tests/Findra.Tests/Models/ProviderTests.cs`

**Interfaces:**
- Produces:
  - `Findra.ProviderTry` - `readonly record struct ProviderTry(string Name, bool Chosen, string Reason)`.
  - `Findra.Chosen<T>` - `sealed record Chosen<T>(T Value, string Provider, IReadOnlyList<ProviderTry> Tried)`.
  - `Findra.Providers` - `static Chosen<T> First<T>(IReadOnlyList<(string Name, Func<T> Init)> chain)`, `static readonly string[] OnnxOrder`, `static readonly string[] WhisperOrder`, `static readonly string[] Banned`.
  - `Findra.NoProviderException` - `IReadOnlyList<ProviderTry> Tried`.

**The trap in this task:** every assertion of the form "it fell back to the CPU" is satisfied by an implementation that only ever uses the CPU. The tests below are written the other way round - the *first* candidate wins when it works, later candidates are never even constructed, and every rejection is named with its reason - so that "always CPU" and "record nothing" both fail.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Models/ProviderTests.cs`:

```csharp
using Findra;

public class ProviderTests
{
    private sealed record Session(string From);

    [Fact]
    public void TheFirstProviderThatInitialisesIsTheOneUsed()
    {
        // The assertion that "always fall back to the CPU" fails. If the chain is walked to the
        // end regardless, or the last entry is simply returned, this reports "CPU".
        int cpuBuilt = 0;
        Chosen<Session> c = Providers.First<Session>(
        [
            ("DirectML", () => new Session("DirectML")),
            ("CPU", () => { cpuBuilt++; return new Session("CPU"); }),
        ]);

        Assert.Equal("DirectML", c.Provider);
        Assert.Equal("DirectML", c.Value.From);
        Assert.Equal(0, cpuBuilt);            // the CPU session was never even constructed
    }

    [Fact]
    public void AProviderThatCannotInitialiseHandsOverToTheNextOne()
    {
        Chosen<Session> c = Providers.First<Session>(
        [
            ("DirectML", () => throw new InvalidOperationException("no DirectX 12 device")),
            ("CPU", () => new Session("CPU")),
        ]);

        Assert.Equal("CPU", c.Provider);
    }

    [Fact]
    public void EveryProviderItRejectedIsNamedWithTheReasonItWasRejectedFor()
    {
        // Spec §6, in as many words: "it's slow on my laptop" is unanswerable, "DirectML failed
        // to initialise, fell back to CPU" is a bug report. Recording only the winner - which is
        // what the source does - loses exactly the half that answers the question.
        Chosen<Session> c = Providers.First<Session>(
        [
            ("DirectML", () => throw new InvalidOperationException("no DirectX 12 device")),
            ("CPU", () => new Session("CPU")),
        ]);

        Assert.Equal(2, c.Tried.Count);
        ProviderTry rejected = c.Tried.Single(t => !t.Chosen);
        Assert.Equal("DirectML", rejected.Name);
        Assert.Contains("no DirectX 12 device", rejected.Reason, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderThatWasNeverTriedIsNotClaimedAsRejected()
    {
        // The control that stops a report inventing rows for the whole declared chain.
        Chosen<Session> c = Providers.First<Session>(
        [
            ("DirectML", () => new Session("DirectML")),
            ("CPU", () => new Session("CPU")),
        ]);

        Assert.Single(c.Tried);
        Assert.True(c.Tried[0].Chosen);
        Assert.Equal("", c.Tried[0].Reason);
    }

    [Fact]
    public void AChainWhereNothingInitialisesSaysSoWithEveryReasonInIt()
    {
        NoProviderException ex = Assert.Throws<NoProviderException>(() => Providers.First<Session>(
        [
            ("DirectML", () => throw new InvalidOperationException("no device")),
            ("CPU", () => throw new DllNotFoundException("onnxruntime.dll")),
        ]));

        Assert.Contains("DirectML", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no device", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CPU", ex.Message, StringComparison.Ordinal);
        Assert.Contains("onnxruntime.dll", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.Tried.Count);
    }

    [Fact]
    public void TheDeclaredChainsPutTheAcceleratorFirstAndTheCpuLast()
    {
        // A chain with CPU first still "works" everywhere and silently costs every user their
        // GPU. It is exactly the change somebody makes to close a support ticket.
        Assert.Equal(["DirectML", "CPU"], Providers.OnnxOrder);
        Assert.Equal(["Vulkan", "CPU"], Providers.WhisperOrder);
    }

    [Fact]
    public void EveryChainEndsAtTheCpuBecauseTheCpuIsASupportedConfiguration()
    {
        Assert.Equal("CPU", Providers.OnnxOrder[^1]);
        Assert.Equal("CPU", Providers.WhisperOrder[^1]);
    }

    [Fact]
    public void NoChainNamesAVendorLockedProvider()
    {
        foreach (string name in Providers.OnnxOrder.Concat(Providers.WhisperOrder))
            Assert.DoesNotContain(name, Providers.Banned, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CUDA", Providers.Banned, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ROCm", Providers.Banned, StringComparer.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter ProviderTests`
Expected: FAIL - `Providers`, `Chosen<T>`, `ProviderTry` and `NoProviderException` do not exist.

- [ ] **Step 3: Write it**

Create `src/Findra/Models/Providers.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Findra;

/// <summary>One provider that was tried, and what came of it. <see cref="Reason"/> is empty for
/// the one that worked and carries the exception's type and message for one that did not.</summary>
public readonly record struct ProviderTry(string Name, bool Chosen, string Reason);

/// <summary>What was built, by which provider, and everything that was tried on the way.</summary>
public sealed record Chosen<T>(T Value, string Provider, IReadOnlyList<ProviderTry> Tried);

public sealed class NoProviderException(string message, IReadOnlyList<ProviderTry> tried) : Exception(message)
{
    public IReadOnlyList<ProviderTry> Tried { get; } = tried;
}

/// <summary>
/// Which execution provider to run on, decided by trying them rather than by asking what the
/// machine is.
///
/// <para>Findra ships on winget and lands on machines nobody chose for it - AMD and Intel CPUs,
/// NVIDIA / AMD / Intel GPUs, integrated or discrete, and machines with no usable accelerator at
/// all. No capability may require a particular vendor, so the chains are DirectML (DirectX 12,
/// which covers all three) and Vulkan (the same breadth for the ggml runtime), each falling back
/// to the CPU. CUDA would mean NVIDIA only plus a large separate runtime, and ROCm is not a
/// Windows story: a portable path everywhere beats a fast path for a third of users.</para>
///
/// <para>The CPU is a supported configuration and not a failure state. Nothing here logs an error
/// or warns because no accelerator was found - the only honest difference is how long the first
/// content index takes.</para>
///
/// <para>Everything that was tried is recorded, including what was rejected and why, because
/// <c>--searchmodels</c> prints it (spec §6). That record is the difference between a solvable
/// support question and an unsolvable one.</para>
/// </summary>
public static class Providers
{
    public static readonly string[] OnnxOrder = ["DirectML", "CPU"];
    public static readonly string[] WhisperOrder = ["Vulkan", "CPU"];

    /// <summary>Named so the ban is a value a test can read, rather than a paragraph somebody
    /// has to remember. Anything here ties Findra to one vendor's silicon.</summary>
    public static readonly string[] Banned = ["CUDA", "TensorRT", "ROCm", "OpenVINO", "CoreML"];

    /// <summary>Build with the first candidate that initialises. Later candidates are not
    /// constructed at all once one succeeds - a provider is an expensive thing to make and a
    /// discarded one holds a device.</summary>
    public static Chosen<T> First<T>(IReadOnlyList<(string Name, Func<T> Init)> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var tried = new List<ProviderTry>(chain.Count);
        foreach ((string name, Func<T> init) in chain)
        {
            try
            {
                T made = init();
                tried.Add(new ProviderTry(name, true, ""));
                return new Chosen<T>(made, name, tried);
            }
            catch (Exception ex)
            {
                // Not a warning. A machine with no DirectX 12 device is an ordinary machine, and
                // the next rung of the chain is the answer rather than a degradation to report.
                tried.Add(new ProviderTry(name, false, $"{ex.GetType().Name}: {ex.Message}"));
                Log.Once($"models|provider|{name}", "INFO", "models",
                         $"{name} did not initialise, trying the next one :: {ex.GetType().Name}: {ex.Message}");
            }
        }
        throw new NoProviderException(
            "no execution provider would initialise: " +
            string.Join("; ", tried.Select(t => $"{t.Name} - {t.Reason}")), tried);
    }
}
```

- [ ] **Step 4: Run it**

Run: `dotnet test --filter ProviderTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Findra/Models/Providers.cs tests/Findra.Tests/Models/ProviderTests.cs
git commit -m "Try the accelerator, take the first that works, and write down what refused"
```

---

## Task 6: The vector store

A port, plus one rename that no text grep would ever catch.

**Files:**
- Create: `src/Findra/Models/VectorStore.cs`
- Test: `tests/Findra.Tests/Models/VectorStoreTests.cs`

**Interfaces:**
- Consumes: `Paths.Index` (`src/Findra/Core/Paths.cs:18`), `ContentDb.SegImage/SegText` (`src/Findra/Content/ContentDb.cs:45`), `System.Numerics.Tensors`.
- Produces: `Findra.VectorStore : IDisposable` - `const int Dim = 768`, `static string DefaultPath`, `long Count`, `VectorStore(string? path = null, bool writer = false)`, `long Append(ReadOnlySpan<float>, byte kind)`, `void Tombstone(long row)`, `void Flush()`, `bool Reload()`, `readonly record struct Match(long Row, float Score)`, `List<Match> Search(ReadOnlySpan<float> query, int k, ReadOnlySpan<byte> kinds)`, `static void Normalise(Span<float>)`.

**The lineage trap, and the mistake inside it.** The source's file format carries a four-byte magic number spelling out another product's initials, written into the header of every vector file this build produces. `grep -ric prism src/` will never find it, because in the source it is the integer `0x50565331`.

The obvious replacement - `0x46565331`, which reads `F V S 1` when you write the hex out - is **wrong**, and it is wrong in exactly the way the source's own `// 'PVS1'` comment is wrong. `BitConverter.TryWriteBytes` writes **little-endian**, so the low byte lands first and `0x46565331` puts `1SVF` on the disk. (The existing files on this machine begin `1SVP`, not `PVS1`; `plan-4-recon.md` inherits the same confusion.)

**The constant is `0x31535646`.** Its low byte is `0x46` = `F`, so the file reads `F V S 1`. The test below reads the four bytes back off a real file and compares them as ASCII, which is the only form of this assertion that cannot be satisfied by getting the endianness wrong twice - and the cheap "fix" of reversing the expected string in the test would leave the header spelling something arbitrary and quietly retire the only assertion protecting a binary lineage leak that no grep can find.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Models/VectorStoreTests.cs`:

```csharp
using System.Text;

using Findra;

public class VectorStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-vec-" + Guid.NewGuid().ToString("N"));

    public VectorStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Store => Path.Combine(_dir, "vectors.bin");

    /// <summary>A unit vector with all its weight on one axis, so two of them are orthogonal and
    /// their dot product is exactly 0.</summary>
    private static float[] Axis(int i)
    {
        var v = new float[VectorStore.Dim];
        v[i] = 1f;
        return v;
    }

    [Fact]
    public void AVectorIsItsOwnBestMatch()
    {
        using (var w = new VectorStore(Store, writer: true))
        {
            w.Append(Axis(0), 0);
            w.Append(Axis(1), 0);
            w.Append(Axis(2), 0);
            w.Flush();
        }
        using var r = new VectorStore(Store);

        List<VectorStore.Match> top = r.Search(Axis(1), 3, []);

        Assert.Equal(1, top[0].Row);
        Assert.True(top[0].Score > 0.99f, $"a vector scored {top[0].Score} against itself");
        Assert.True(top[1].Score < 0.01f, "an orthogonal vector scored as a match");
    }

    [Fact]
    public void HalfPrecisionKeepsEnoughOfTheVectorToRankWithIt()
    {
        // Every row is stored as float16, which is the whole reason a million vectors fit in a
        // file worth memory-mapping. If the conversion is wrong the scores are not slightly off,
        // they are noise, and this catches that rather than a rounding change.
        var v = new float[VectorStore.Dim];
        for (int i = 0; i < v.Length; i++) v[i] = (i % 7) - 3;
        VectorStore.Normalise(v);

        using (var w = new VectorStore(Store, writer: true)) { w.Append(v, 1); w.Flush(); }
        using var r = new VectorStore(Store);

        Assert.True(r.Search(v, 1, [])[0].Score > 0.99f);
    }

    [Fact]
    public void ATombstonedRowCanNeverMatchAgain()
    {
        // How a deleted or replaced file stops being findable. A no-op here leaves a photo that
        // was deleted a year ago answering queries for ever.
        using (var w = new VectorStore(Store, writer: true))
        {
            w.Append(Axis(0), 0);
            w.Append(Axis(1), 0);
            w.Tombstone(1);
            w.Flush();
        }
        using var r = new VectorStore(Store);

        List<VectorStore.Match> top = r.Search(Axis(1), 5, []);
        Assert.DoesNotContain(top, m => m.Row == 1);
    }

    [Fact]
    public void AKindFilterAnswersOnlyWithTheKindsItWasAskedFor()
    {
        // Named for what it measures. The search reads every row and filters on the kind byte, so
        // this is a correctness claim about the ANSWER and not a claim about work avoided.
        using (var w = new VectorStore(Store, writer: true))
        {
            w.Append(Axis(0), ContentDb.SegImage);
            w.Append(Axis(0), ContentDb.SegText);      // the same vector, a different kind
            w.Flush();
        }
        using var r = new VectorStore(Store);

        List<VectorStore.Match> images = r.Search(Axis(0), 5, [ContentDb.SegImage]);
        Assert.Single(images);
        Assert.Equal(0, images[0].Row);
        Assert.Equal(2, r.Search(Axis(0), 5, []).Count);   // and no filter means both
    }

    [Fact]
    public void AStoreWrittenAtAnotherWidthIsStartedOverRatherThanRead()
    {
        // A vector file from a build whose model had a different hidden size is not this build's
        // file. Reading it produces scores that are not wrong-looking, only wrong.
        using (var fs = new FileStream(Store, FileMode.Create))
        {
            // The REAL magic, written as the bytes it has to be, so this fixture fails the WIDTH
            // check and not the magic check - otherwise the test passes for a reason its name
            // does not give.
            fs.Write("FVS1"u8.ToArray());
            fs.Write(BitConverter.GetBytes(512));           // not Dim
            fs.Write(BitConverter.GetBytes(99L));
        }
        using (var w = new VectorStore(Store, writer: true)) { w.Append(Axis(0), 0); w.Flush(); }
        using var r = new VectorStore(Store);

        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void AReaderSeesOnlyWhatTheWriterFlushed()
    {
        // The count lives in the header, so a reader that trusted the file LENGTH would read a
        // row the writer is halfway through appending.
        using var w = new VectorStore(Store, writer: true);
        w.Append(Axis(0), 0);
        w.Append(Axis(1), 0);

        using (var early = new VectorStore(Store)) Assert.Equal(0, early.Count);

        w.Flush();
        using var after = new VectorStore(Store);
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public void NormaliseLeavesAZeroVectorAloneRatherThanProducingNaN()
    {
        // One NaN in a stored row makes every comparison against it false and every top-k list
        // that touches it wrong, silently, for the life of the file.
        var v = new float[VectorStore.Dim];
        VectorStore.Normalise(v);
        Assert.All(v, f => Assert.Equal(0f, f));
    }

    [Fact]
    public void NormaliseMakesAVectorUnitLength()
    {
        var v = new float[VectorStore.Dim];
        for (int i = 0; i < 8; i++) v[i] = 3f;
        VectorStore.Normalise(v);
        float sum = 0;
        foreach (float f in v) sum += f * f;
        Assert.True(Math.Abs(sum - 1f) < 1e-4f, $"the squared length is {sum}");
    }

    [Fact]
    public void TheFileFormatCarriesFindrasOwnMagicAndNobodyElses()
    {
        // A lineage leak that no grep of the source text can find: four bytes in the header of
        // every vector file this build writes, spelling out another product. It is also a
        // compatibility statement - a file with this magic is Findra's.
        using (var w = new VectorStore(Store, writer: true)) { w.Append(Axis(0), 0); w.Flush(); }

        byte[] head = File.ReadAllBytes(Store)[..4];
        Assert.Equal("FVS1", Encoding.ASCII.GetString(head));
    }

    [Fact]
    public void TheStoreLivesBesideTheIndexAndNotBesideTheModels()
    {
        Assert.Equal(Path.Combine(Paths.Index, "vectors.bin"), VectorStore.DefaultPath);
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter VectorStoreTests`
Expected: FAIL - `VectorStore` does not exist.

- [ ] **Step 3: Port it**

```bash
cp /c/Code/Personal/Prism/src/Search/VectorStore.cs src/Findra/Models/VectorStore.cs
```

Then, in order:

1. Namespace to `Findra`.
2. `private const int Magic = 0x50565331;` becomes **`0x31535646`**, with a comment saying what the four bytes spell *on disk* and why the constant looks back to front:

```csharp
    /// <summary>The four bytes every vector file starts with: 'F' 'V' 'S' '1'.
    ///
    /// <para>The constant looks reversed because it is not a string, it is an int32 written
    /// little-endian - the low byte lands first. Writing the "obvious" 0x46565331 puts `1SVF` on
    /// disk, which is the mistake the format this was ported from made and then documented as
    /// though it had not: those files begin `1SVP`. Assert on the BYTES, never on the literal.
    /// </para>
    /// </summary>
    private const int Magic = 0x31535646;
```

A file carrying anything else is correctly rejected by the magic-and-width check and the store is started over.
3. `DefaultPath` becomes `System.IO.Path.Combine(Paths.Index, "vectors.bin")` - the source reads it off a service type Findra does not have.
4. Rewrite the type comment: it names another product twice and describes its widgets. Keep the reasoning about brute force being the right choice below a million rows - that is a real engineering argument and it is Findra's now.
5. The `Log.Warn` in the width check keeps its message; the category becomes `"models"`.

The byte layout, the float16 rows, the parallel `.kinds` file, the tombstone-is-zeros rule and the block-of-256 search all stay exactly as they are. This code arrives working.

- [ ] **Step 4: Run it**

Run: `dotnet test --filter VectorStoreTests`
Expected: PASS, 10 tests. `AReaderSeesOnlyWhatTheWriterFlushed` is the one most likely to need a look: the reader must take its count from the header value clamped by the file length, never from the length alone.

- [ ] **Step 5: Read every comment**

`VectorStore.cs` is 213 lines of which about 25 are comments, and three of them name another product. Read all of them and ask whether each reads as written for Findra by somebody who has never seen another codebase. Report the pass separately from the name-grep.

- [ ] **Step 6: Commit**

```bash
git add src/Findra/Models/VectorStore.cs tests/Findra.Tests/Models/VectorStoreTests.cs
git commit -m "The vector store: half-precision rows, tombstones, and a magic number of our own"
```

---

## Task 7: The encoders

**Files:**
- Create: `src/Findra/Models/Encoders.cs`
- Test: `tests/Findra.Tests/Models/EncoderTests.cs`

**Interfaces:**
- Consumes: `ModelStore` (Task 2), `Providers` (Task 5), `VectorStore.Dim` / `VectorStore.Normalise` (Task 6), `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`, `SkiaSharp`.
- Produces:
  - `Findra.Onnx` - `static Chosen<InferenceSession> Open(string path, bool wantAccelerator)`, `static string Describe(InferenceSession)`, `static float[] MeanPool(Tensor<float> hidden, long[] mask, int hiddenSize)`, `static Tensor<float> Hidden(...)`.
  - `Findra.ClipImageEncoder : IDisposable` - `const int Size = 256`, `string Provider`, `IReadOnlyList<ProviderTry> Tried`, `ClipImageEncoder(bool wantAccelerator, string? dir = null)`, `static float[] Preprocess(SKBitmap)`, `float[][] Encode(IReadOnlyList<float[]>)`.
  - `Findra.ClipTextEncoder : IDisposable` - `string Provider`, `IReadOnlyList<ProviderTry> Tried`, `ClipTextEncoder(bool wantAccelerator = false, string? dir = null)`, `float[] Encode(string)`.
  - `Findra.E5Encoder : IDisposable` - `const int Hidden = 768`, `string Provider`, `IReadOnlyList<ProviderTry> Tried`, `E5Encoder(bool wantAccelerator = false, string? dir = null)`, `float[] EncodeQuery(string)`, `float[] EncodePassage(string)`, `float[][] EncodePassages(IReadOnlyList<string>)`, `static long[] ShiftIds(IReadOnlyList<int> sentencePieceIds, int max)`, `static string Passage(string path, string text)`.

**What this task changes about the port:** the source's `Onnx.Open` takes a `bool gpu` and does its own inline try/catch around DirectML, recording nothing. It becomes a call to `Providers.First` so that the chain, the choice and every rejection are one shared mechanism that `--searchmodels` can print. Everything else - the preprocessing, the token handling, the pooling - is copied unchanged.

**What can and cannot be tested here.** These types need a 350 MB file to construct, so nothing below constructs one. What *is* tested is every pure function inside them, and those are where the silent wrongness lives: a preprocessing layout that is subtly wrong produces embeddings that are plausible and useless, and no integration test would catch it either.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Models/EncoderTests.cs`:

```csharp
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

using Findra;

public class EncoderTests
{
    private static SKBitmap Solid(SKColor c, int w = 64, int h = 64)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(c);
        return bmp;
    }

    [Fact]
    public void APictureBecomesThreePlanesInTheOrderTheModelWasTrainedOn()
    {
        // One assertion that catches three separate wrong implementations at once:
        //   - an interleaved (H,W,C) layout, which the model reads as noise;
        //   - blue and red swapped, which is what happens when the pixel span is read as BGRA;
        //   - a 0..1 scaling instead of the -1..1 the model expects.
        // None of the three throws, and all three produce embeddings that look like embeddings.
        using SKBitmap red = Solid(new SKColor(255, 0, 0));
        float[] px = ClipImageEncoder.Preprocess(red);

        int plane = ClipImageEncoder.Size * ClipImageEncoder.Size;
        Assert.Equal(3 * plane, px.Length);
        Assert.All(px[..plane], v => Assert.True(Math.Abs(v - 1f) < 1e-3f, $"the red plane holds {v}"));
        Assert.All(px[plane..(2 * plane)], v => Assert.True(Math.Abs(v + 1f) < 1e-3f, $"the green plane holds {v}"));
        Assert.All(px[(2 * plane)..], v => Assert.True(Math.Abs(v + 1f) < 1e-3f, $"the blue plane holds {v}"));
    }

    [Fact]
    public void AMidGreyLandsInTheMiddleOfTheModelsRangeAndNotAtAHalf()
    {
        // The /127.5 - 1 scaling, asserted where the two candidate scalings differ most.
        using SKBitmap grey = Solid(new SKColor(128, 128, 128));
        float[] px = ClipImageEncoder.Preprocess(grey);
        Assert.All(px, v => Assert.True(Math.Abs(v) < 0.02f, $"mid grey mapped to {v}, not to about 0"));
    }

    [Fact]
    public void AWidePictureIsSquashedRatherThanCroppedSoItsEdgesSurvive()
    {
        // SigLIP was trained on squashed images, not centre-cropped ones, and a crop throws away
        // the edges of every wide photo silently - the output is the same size either way, so an
        // assertion on the LENGTH cannot tell the two apart and an earlier draft of this test
        // asserted nothing else.
        //
        // So: a 1024x256 picture whose left eighth is green and whose remainder is black. A plain
        // resize maps source column c to destination column c/4, so the green band survives at
        // destination columns 0..31. A 256x256 centre crop takes source columns 384..639, which
        // are all black, and the green is gone.
        var bmp = new SKBitmap(new SKImageInfo(1024, 256, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Black);
            using var green = new SKPaint { Color = new SKColor(0, 255, 0) };
            canvas.DrawRect(new SKRect(0, 0, 128, 256), green);
        }
        using (bmp)
        {
            float[] px = ClipImageEncoder.Preprocess(bmp);
            int plane = ClipImageEncoder.Size * ClipImageEncoder.Size;
            int greenPlane = plane;                       // planes are R, G, B
            int row = 128 * ClipImageEncoder.Size;        // halfway down, away from any edge

            Assert.Equal(3 * plane, px.Length);
            Assert.True(px[greenPlane + row + 5] > 0.5f,
                "the left of the picture is not in the output - it was cropped, not squashed");
            Assert.True(px[greenPlane + row + 200] < -0.5f,
                "the right of the picture is not in the output");
        }
    }

    [Fact]
    public void MeanPoolingIgnoresThePaddingItIsMaskedAgainst()
    {
        // Padding is attended over as zeros in the mask, and a pool that averages it anyway
        // drags every short passage towards whatever the padding embedding happens to be.
        var hidden = new DenseTensor<float>(new[] { 1, 3, 2 });
        hidden[0, 0, 0] = 2; hidden[0, 0, 1] = 4;
        hidden[0, 1, 0] = 4; hidden[0, 1, 1] = 8;
        hidden[0, 2, 0] = 1000; hidden[0, 2, 1] = 1000;     // padding, masked off

        float[] pooled = Onnx.MeanPool(hidden, [1, 1, 0], 2);

        Assert.Equal(3f, pooled[0], 3);
        Assert.Equal(6f, pooled[1], 3);
    }

    [Fact]
    public void MeanPoolingNothingIsZeroRatherThanADivideByZero()
    {
        var hidden = new DenseTensor<float>(new[] { 1, 2, 2 });
        float[] pooled = Onnx.MeanPool(hidden, [0, 0], 2);
        Assert.All(pooled, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void TheVocabularyShiftPutsEveryTokenWhereTheModelExpectsIt()
    {
        // XLM-R's ids are SentencePiece's shifted by one, to make room for <s>=0, <pad>=1 and
        // </s>=2, with SentencePiece's own <unk> (0) landing on 3. The tokenizer knows nothing
        // about that. Get it wrong and nothing throws: every embedding is off by one token id,
        // which is a model quietly reading a different sentence from the one that was typed.
        long[] ids = E5Encoder.ShiftIds([0, 5, 7], max: 512);

        Assert.Equal([0L, 3L, 6L, 8L, 2L], ids);
    }

    [Fact]
    public void APassageLongerThanTheModelIsCutButStillClosedProperly()
    {
        // The last token must be </s> whatever happens, or the model reads a truncated sentence
        // as an unfinished one.
        long[] ids = E5Encoder.ShiftIds(Enumerable.Repeat(9, 4000).ToList(), max: 16);

        Assert.Equal(16, ids.Length);
        Assert.Equal(0L, ids[0]);
        Assert.Equal(2L, ids[^1]);
    }

    [Fact]
    public void AChunkIsEmbeddedWithItsFileNameInFrontOfIt()
    {
        // "the lease agreement" has to find a Hebrew-named contract from a chunk that never
        // says the word lease. Separators become spaces so the name reads as words.
        string p = E5Encoder.Passage(@"C:\docs\rental-agreement_2026.pdf", "the tenant shall pay");

        Assert.StartsWith("rental agreement 2026", p, StringComparison.Ordinal);
        Assert.Contains("the tenant shall pay", p, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf", p, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter EncoderTests`
Expected: FAIL - `ClipImageEncoder`, `Onnx` and `E5Encoder` do not exist.

- [ ] **Step 3: Port it**

```bash
cp /c/Code/Personal/Prism/src/Search/Encoders.cs src/Findra/Models/Encoders.cs
```

Then:

1. **Delete the whole `ModelStore` class from the copy** - lines 19-100 of the source. It is Task 2's, and it is already in the tree with a second size field the source has not got.
2. Namespace to `Findra`. `Log` categories from `"search"` to `"models"`.
3. **`Onnx.Open` changes shape.** The source signature is `Open(string path, bool gpu, out string provider)` with an inline try/catch that logs a warning and falls through. Replace it with:

```csharp
    /// <summary>Open a session on the first provider that will have it. The chain, the choice and
    /// every rejection come back on the result, because --searchmodels prints all three: "it is
    /// slow on my laptop" is unanswerable, and "DirectML did not initialise, so this is the CPU"
    /// is a bug report (spec §6).</summary>
    public static Chosen<InferenceSession> Open(string path, bool wantAccelerator)
    {
        var chain = new List<(string, Func<InferenceSession>)>();
        if (wantAccelerator)
            chain.Add(("DirectML", () =>
            {
                var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                o.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
                // DirectML needs sequential execution and no memory-pattern planning.
                o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                o.EnableMemoryPattern = false;
                o.AppendExecutionProvider_DML(0);
                return new InferenceSession(path, o);
            }));
        chain.Add(("CPU", () =>
        {
            var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            o.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
            // Half the cores: this runs beside a queue that is already using the machine, and
            // it must not be the reason somebody's laptop is warm.
            o.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            return new InferenceSession(path, o);
        }));
        return Providers.First(chain);
    }
```

Each encoder then holds `Provider` and `Tried` off the `Chosen<InferenceSession>` it opened with, and takes an optional `string? dir = null` that it passes to `ModelStore.PathOf` so a test or a diagnostic can point at another folder.

4. **Extract `ShiftIds` and `Passage`.** `E5Encoder.Ids` (`Encoders.cs:313-325`) becomes:

```csharp
    /// <summary>SentencePiece's ids as the model numbers them: shifted by one to leave room for
    /// &lt;s&gt;=0, &lt;pad&gt;=1 and &lt;/s&gt;=2, with SentencePiece's own &lt;unk&gt; (0) landing
    /// on 3. Opened with &lt;s&gt; and always closed with &lt;/s&gt;, even when the text was cut.
    /// The tokenizer knows nothing about any of this; this does.</summary>
    public static long[] ShiftIds(IReadOnlyList<int> sentencePieceIds, int max)
    {
        ArgumentNullException.ThrowIfNull(sentencePieceIds);
        var ids = new List<long>(Math.Min(sentencePieceIds.Count + 2, max)) { 0 };
        foreach (int id in sentencePieceIds)
        {
            if (ids.Count >= max - 1) break;
            ids.Add(id == 0 ? 3 : id + 1);
        }
        ids.Add(2);
        return [.. ids];
    }
```

and the instance method becomes `ShiftIds(_tok.EncodeToIds(text, false, false), max)`. `Passage` moves from the source's `Indexer` (`Indexer.cs:140-141`) onto `E5Encoder` as a public static, because both the indexer and the migration embed with it and it must be the same string in both places.

5. **Say where each encoder runs, because the source's argument lies.** `E5Encoder(bool gpu, bool quantised)` in the source ignores both: it calls `Onnx.Open(..., gpu: false, ...)` unconditionally (`Encoders.cs:296`) and `quantised` only reaches a log line. The new signature is `E5Encoder(bool wantAccelerator = false, string? dir = null)` and the argument now means something, so the decision has to be made rather than inherited:

- **The query-side encoders stay on the CPU.** `Semantic.Open` passes `wantAccelerator: false` for both e5 and the SigLIP text tower. A query embeds one short string in about ten milliseconds either way, and initialising DirectML inside the interface process costs more memory and more startup than it saves on every keystroke for the rest of the session.
- **The indexer's vision tower asks for the accelerator.** `Decoders` passes `wantAccelerator: true` to `ClipImageEncoder`, which is where the batches are.
- **The indexer's e5 also asks for it**, because a first index embeds hundreds of thousands of chunks and that is the one place the difference is hours rather than milliseconds. This is a change from the source, which kept e5 on the CPU in both processes; the chain falls back to the CPU on a machine with no DirectX 12 device, so the change costs nothing where it does not help.

Drop the `quantised` parameter entirely. The model file is quantised or it is not, and a flag whose only effect is a log line is a field with no reader.

6. Rewrite every comment that names another product, another product's installer, its tools folder, or a particular graphics card. There are at least five, including one that names a specific GPU and one that describes which process is "the UI" in a product Findra is not.

- [ ] **Step 4: Run it**

Run: `dotnet test --filter EncoderTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Findra/Models/Encoders.cs tests/Findra.Tests/Models/EncoderTests.cs
git commit -m "SigLIP-2 and e5, on whichever provider will have them"
```

---

## Task 8: Sound, frames, words in pictures, and the preview the card has been missing

**Files:**
- Create: `src/Findra/Content/Media.cs`, `src/Findra/Content/Speech.cs`, `src/Findra/Content/ImageText.cs`, `src/Findra/Content/PreviewDecoder.cs`
- Modify: `src/Findra/Card/CardWindow.cs:952`
- Test: `tests/Findra.Tests/Content/MediaTests.cs`

**Interfaces:**
- Consumes: `Providers` (Task 5), `NAudio`, `Whisper.net`, `SkiaSharp`, the WinRT projections Task 1 unlocked.
- Produces:
  - `Findra.Media` - `const int SampleRate = 16_000`, `static (float[] Samples, double DurationSeconds) Decode(string, double maxSeconds)`, `static double Duration(string)`, `readonly record struct Line(double T0, double T1, string Text, string Language)`, `static Task<(List<Line> Lines, string Language)> Transcribe(float[], WhisperFactory general, WhisperFactory? hebrew, string? forceLanguage = null)`, `static bool IsNoise(string)`, `static Task<double> VideoDuration(string)`, `static Task<List<SKBitmap?>> Frames(string, IReadOnlyList<double>, int maxDim = 320)`, `static List<double> SampleTimes(double duration, double every = 10, int max = 90)`, `static Chosen<WhisperFactory> OpenWhisper(string path)`.
  - `Findra.Speech` - `static List<ContentDb.Segment> Merge(IReadOnlyList<Media.Line> lines, Func<string, long> embed, double maxSeconds = 20, int maxChars = 600)`.
  - `Findra.ImageText` - `static string Read(string path)`, `static bool MostlyScript(string, bool latin)`.
  - `Findra.PreviewDecoder` - `static SKImage? Decode(string path, ResultKind kind, int maxDim, double moment = -1)`, `static SKImage? DecodeWithSkia(string, int maxDim)`, `static SKImage? ShellThumbnail(string, int maxDim)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Content/MediaTests.cs`:

```csharp
using SkiaSharp;

using Findra;

public class MediaTests
{
    // ---- where a video is sampled ----

    [Fact]
    public void AClipShorterThanOneStepIsStillSampledOnce()
    {
        // A stepping loop that starts at `every` returns nothing for an eight-second clip, and
        // every short video on the disk is then indexed as having no pictures in it at all.
        List<double> times = Media.SampleTimes(8);

        Assert.Single(times);
        Assert.InRange(times[0], 0, 8);
    }

    [Fact]
    public void ALongFilmIsSpreadOverItsWholeLengthAndNeverExceedsTheFrameBudget()
    {
        // Ten hours. A fixed ten-second step is 3,600 frames - an afternoon of GPU per file -
        // and the budget is what stops one film starving the whole queue.
        List<double> times = Media.SampleTimes(36_000);

        Assert.InRange(times.Count, 2, 90);
        Assert.True(times[^1] > 30_000, $"the last sample is at {times[^1]}s of 36,000");
        for (int i = 1; i < times.Count; i++)
            Assert.True(times[i] > times[i - 1], "the sample times are not increasing");
    }

    [Fact]
    public void EverySampleIsInsideTheVideo()
    {
        foreach (double duration in new[] { 3.0, 11.0, 95.0, 3600.0 })
            foreach (double t in Media.SampleTimes(duration))
                Assert.InRange(t, 0, duration);
    }

    [Fact]
    public void AVideoOfNoLengthIsSampledNowhereRatherThanAtZero()
    {
        Assert.Empty(Media.SampleTimes(0));
        Assert.Empty(Media.SampleTimes(-1));
    }

    // ---- what a transcript is allowed to contain ----

    [Theory]
    [InlineData("[Music]", true)]
    [InlineData("(applause)", true)]
    [InlineData("\u266a la la la", true)]
    [InlineData("The lease agreement is signed", false)]
    [InlineData("She said [inaudible] and left", false)]   // a bracket INSIDE a real line
    public void SilenceHallucinationsAreDroppedAndRealSpeechIsKept(string line, bool noise)
        => Assert.Equal(noise, Media.IsNoise(line));

    // ---- how transcript lines become segments ----

    [Fact]
    public void TranscriptLinesAreMergedIntoWindowsASentenceFitsIn()
    {
        // Whisper emits lines of two or three seconds. One segment per line means a search for a
        // phrase spanning two of them finds neither.
        var lines = new List<Media.Line>();
        for (int i = 0; i < 10; i++) lines.Add(new Media.Line(i, i + 1, $"word{i}", "en"));

        List<ContentDb.Segment> segs = Speech.Merge(lines, _ => 0, maxSeconds: 20, maxChars: 600);

        Assert.Single(segs);
        Assert.Equal(0, segs[0].T0);
        Assert.Equal(10, segs[0].T1);
        Assert.Contains("word0", segs[0].Text, StringComparison.Ordinal);
        Assert.Contains("word9", segs[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastWindowIsFlushedEvenWhenItNeverFilledUp()
    {
        // The classic shape of this bug: a loop that only writes a window when it overflows,
        // and drops whatever was in the buffer when the input ran out. The tail of every
        // transcript on the machine is then missing, and nothing anywhere says so.
        var lines = new List<Media.Line>();
        for (int i = 0; i < 30; i++) lines.Add(new Media.Line(i, i + 1, $"word{i}", "en"));

        List<ContentDb.Segment> segs = Speech.Merge(lines, _ => 0, maxSeconds: 20, maxChars: 600);

        Assert.True(segs.Count >= 2, $"30 seconds at a 20-second window gave {segs.Count} segment(s)");
        Assert.Contains("word29", segs[^1].Text, StringComparison.Ordinal);
        Assert.Equal(30, segs[^1].T1);
    }

    [Fact]
    public void NoWordIsLostBetweenTwoWindows()
    {
        var lines = new List<Media.Line>();
        for (int i = 0; i < 30; i++) lines.Add(new Media.Line(i, i + 1, $"word{i}", "en"));

        string all = string.Join(" ", Speech.Merge(lines, _ => 0, 20, 600).Select(s => s.Text));

        for (int i = 0; i < 30; i++) Assert.Contains($"word{i}", all, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySpeechSegmentCarriesTheVectorRowItWasGiven()
    {
        // The embed callback hands back the row the vector went into, and the segment has to
        // carry it or the transcript is in the store with nothing pointing at it.
        long next = 40;
        var lines = new List<Media.Line> { new(0, 2, "hello there", "en") };

        List<ContentDb.Segment> segs = Speech.Merge(lines, _ => next++, 20, 600);

        Assert.Equal(40, segs[0].Vec);
        Assert.Equal(ContentDb.SegSpeech, segs[0].Kind);
    }

    [Fact]
    public void AnEmptyTranscriptIsNoSegmentsRatherThanOneEmptyOne()
    {
        Assert.Empty(Speech.Merge([], _ => 0, 20, 600));
    }

    // ---- words inside pictures ----

    [Theory]
    [InlineData("the quarterly revenue report", true, true)]
    [InlineData("\u05d4\u05e1\u05db\u05dd \u05e9\u05db\u05d9\u05e8\u05d5\u05ea \u05d7\u05ea\u05d5\u05dd", false, true)]
    [InlineData("the quarterly revenue report", false, false)]   // latin text from the Hebrew engine
    [InlineData("ab", true, false)]                              // too short to judge
    [InlineData("", true, false)]
    public void ARecogniserReadingTheWrongScriptIsThrownAway(string text, bool latin, bool keep)
    {
        // Two recognisers each read the whole image, and the one reading a script that is not
        // there hallucinates. Without this every screenshot carries a line of nonsense into the
        // full-text index, and nonsense in FTS is matches nobody asked for.
        Assert.Equal(keep, ImageText.MostlyScript(text, latin));
    }

    // ---- the card's preview pane ----

    [Fact]
    public void APictureOnDiskDecodesToAnImageAtTheSizeAsked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "findra-prev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string png = Path.Combine(dir, "wide.png");
            using (var bmp = new SKBitmap(800, 400))
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.CornflowerBlue);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 90);
                using var fs = File.Create(png);
                data.SaveTo(fs);
            }

            using SKImage? preview = PreviewDecoder.DecodeWithSkia(png, 200);

            Assert.NotNull(preview);
            Assert.True(preview!.Width <= 300, $"a 200 px preview came back {preview.Width} px wide");
            Assert.True(preview.Width > preview.Height, "the aspect ratio was not kept");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void SomethingThatIsNotAPictureDecodesToNothingRatherThanThrowing()
    {
        // The card's stage runs this over whatever row is selected. An exception here is an
        // exception on the UI thread for every text file somebody arrows onto.
        string dir = Path.Combine(Path.GetTempPath(), "findra-prev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string txt = Path.Combine(dir, "notes.txt");
            File.WriteAllText(txt, "this is not a picture");
            Assert.Null(PreviewDecoder.DecodeWithSkia(txt, 200));
            Assert.Null(PreviewDecoder.Decode(Path.Combine(dir, "gone.jpg"), ResultKind.Photo, 200));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter MediaTests`
Expected: FAIL - `Media`, `Speech`, `ImageText` and `PreviewDecoder` do not exist.

- [ ] **Step 3: Port the three files**

```bash
cp /c/Code/Personal/Prism/src/Search/Media.cs         src/Findra/Content/Media.cs
cp /c/Code/Personal/Prism/src/Search/ImageText.cs     src/Findra/Content/ImageText.cs
cp /c/Code/Personal/Prism/src/Search/PreviewDecoder.cs src/Findra/Content/PreviewDecoder.cs
```

Then:

1. Namespaces to `Findra`; log categories from `"search"` to `"index"` in the indexer-side files and `"card"` in `PreviewDecoder`'s thumbnail path, which the card also calls.
2. **`PreviewDecoder.cs`: delete `ShellAssoc` entirely** (source lines 118-143). Nothing in Findra opens a file by association, and it is a dead field waiting to happen.
3. `Media.IsNoise` becomes `public` - it is a rule about text, and the test above is the only thing standing between a transcript index and a page of `[Music]`.
4. `ImageText.MostlyScript` becomes `public` for the same reason.
5. **`Media.OpenWhisper` is new**, and it is where the Vulkan chain lands:

```csharp
    /// <summary>A whisper factory on the first runtime that will have it. The runtime order is a
    /// process-wide setting in the ggml loader rather than a per-call argument, so this sets it
    /// once and then reports what actually answered - which is the fact --searchmodels prints.
    /// </summary>
    public static Chosen<WhisperFactory> OpenWhisper(string path)
        => Providers.First<WhisperFactory>(
        [
            ("Vulkan", () =>
            {
                LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan];
                return WhisperFactory.FromPath(path);
            }),
            ("CPU", () =>
            {
                LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
                return WhisperFactory.FromPath(path);
            }),
        ]);
```

6. Rewrite the comments. `Media.cs`'s type comment names another product's rule and `ImageText.cs`'s names a machine's installed language pack as an anecdote; `PreviewDecoder.cs`'s names "the stage" of a product Findra does not have (Findra does have a stage, so that one only needs the product name removing).

- [ ] **Step 4: Write `Speech.Merge`**

Create `src/Findra/Content/Speech.cs`, extracted from the source's `Indexer.Speech` (`Indexer.cs:399-434`) with the encoder replaced by a callback so it is testable without a 550 MB file:

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace Findra;

/// <summary>
/// Transcript lines into searchable segments.
///
/// <para>Whisper emits two- or three-second lines, and one segment per line makes a phrase that
/// spans two of them findable in neither. They are merged into windows a sentence comfortably
/// fits inside - about twenty seconds, or six hundred characters, whichever comes first - and
/// each window keeps the start of its first line and the end of its last, so a result can say
/// when it was said and the card can seek there.</para>
///
/// <para><paramref name="embed"/> hands back the vector row the window's text was appended at.
/// Passing it in rather than holding an encoder is what lets the windowing rule - the part with
/// an off-by-one in it - be tested without a model on disk.</para>
/// </summary>
public static class Speech
{
    public static List<ContentDb.Segment> Merge(IReadOnlyList<Media.Line> lines, Func<string, long> embed,
                                                double maxSeconds = 20, int maxChars = 600)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(embed);
        var segs = new List<ContentDb.Segment>();
        var buf = new StringBuilder();
        double t0 = -1, t1 = 0;

        void Flush()
        {
            if (buf.Length == 0) return;
            string text = buf.ToString().Trim();
            segs.Add(new ContentDb.Segment(ContentDb.SegSpeech, t0, t1, embed(text), text));
            buf.Clear();
            t0 = -1;
        }

        foreach (Media.Line l in lines)
        {
            if (t0 < 0) t0 = l.T0;
            buf.Append(l.Text).Append(' ');
            t1 = l.T1;
            if (t1 - t0 >= maxSeconds || buf.Length > maxChars) Flush();
        }
        // The tail. A loop that only writes on overflow loses whatever was in the buffer when
        // the input ran out, which is the end of every transcript on the machine.
        Flush();
        return segs;
    }
}
```

- [ ] **Step 5: Give the card its preview back**

`src/Findra/Card/CardWindow.cs:952` reads:

```csharp
        private static SKImage? DecodePreview(string path, ResultKind kind, int maxDim, double moment) => null;
```

It has been a stub since Plan 3 because the decoder needed the framework this plan just moved to. Replace the body with `PreviewDecoder.Decode(path, kind, maxDim, moment)` and delete the `static` if the compiler asks. Leave the surrounding cache and threading exactly as they are - the caller already runs it off the UI thread and already holds the result in `PreviewCache`.

- [ ] **Step 6: Run it**

Run: `dotnet test --filter MediaTests` - PASS, 13 test methods / 21 cases (five theory rows for the noise check and five for the script check).
Then `dotnet test` - the whole suite, still green.

- [ ] **Step 7: See it**

Run `findra --searchshot preview-check.png results` and look at the stage: it has been drawing a placeholder since Plan 3 and should now draw a real thumbnail for a photo row. `--searchshot` builds a fixed fake result set whose paths may not exist on this machine, in which case the stage correctly stays empty - **say which you saw** rather than assuming.

- [ ] **Step 8: Commit**

```bash
git add src/Findra/Content/Media.cs src/Findra/Content/Speech.cs src/Findra/Content/ImageText.cs \
        src/Findra/Content/PreviewDecoder.cs src/Findra/Card/CardWindow.cs \
        tests/Findra.Tests/Content/MediaTests.cs
git commit -m "Sound, frames, the words inside pictures, and a real preview on the stage"
```

---

## Task 9: The indexer grows kinds, behind a per-capability gate

This is the all-or-nothing gate becoming per-capability, which spec §10 names as the one place the ported engine is written test-first.

**Files:**
- Create: `src/Findra/Content/Decoders.cs`, `src/Findra/Content/TranscribeLimit.cs`
- Modify: `src/Findra/Content/Indexer.cs`, `src/Findra/Content/ContentDb.cs`, `src/Findra/Diagnostics/SelfTest.cs`, `src/Findra/Diagnostics/SearchIndex.cs`, `src/Findra/Diagnostics/SearchBench.cs`
- Test: `tests/Findra.Tests/Content/DecoderGateTests.cs`, `tests/Findra.Tests/Content/TranscribeLimitTests.cs`

**Interfaces:**
- Consumes: `CapabilitySet`, `Capabilities` (Task 3); `VectorStore` (Task 6); `ClipImageEncoder`, `E5Encoder` (Task 7); `Media`, `Speech`, `ImageText`, `PreviewDecoder` (Task 8); `ContentDb`, `DocText` (Plan 4).
- Produces:
  - `Findra.KindResult` - `readonly record struct KindResult(List<ContentDb.Segment> Segments, string? Skip, string? Note = null)`.
  - `ContentDb.CountRecorded(string reason) : long`, beside `RecentSkips`.
  - `Findra.IDecoders : IDisposable` - `CapabilitySet Installed { get; }`, **`bool CanRead(ResultKind kind)`**, `KindResult Decode(ResultKind kind, string path, long bytes)`, `void Flush()`, `void Release(IReadOnlyList<long> vectorRows)`.
  - `Findra.Decoders : IDecoders` - `Decoders(CapabilitySet installed, VectorStore vectors, Func<int>? transcribeMinutes = null, string? modelDir = null, bool ownsVectors = false)`, `static Decoders ForThisMachine(Func<int> transcribeMinutes, string? modelDir = null)`, `static bool Covers(ResultKind kind, CapabilitySet installed)`, `static string? SizeGate(ResultKind kind, long bytes)`, `static (Model General, Model? Hebrew) SpeechModels(CapabilitySet installed)`, the recorded-reason constants `NoModel`, `TooLarge`, `TooLong`, `NoText`, `NoFormatReader`, `AnIcon`, and the size constants `MaxImageBytes`, `MinImageBytes`, `MaxVideoBytes`, `MaxDocBytes`, `MaxDecodeSeconds`.
  - `Findra.TranscribeLimit` - `const int Default = 5`, `const int Off = 0`, `const int NoLimit = -1`, `static IReadOnlyList<int> Presets`, `static bool Covers(int minutes, double durationSeconds)`, `static string? Named(int minutes)`, `static string Describe(int minutes)`, `static int? Parse(string)`.
  - `ContentDb.RecentSkips(int limit)`, beside `RecentFailures`.
  - `Findra.Indexer` - `static void Loop(ContentDb, int parentPid, Func<bool> running, IDecoders decoders)`, `static void DrainOnce(ContentDb, Action<string> report, IDecoders decoders)`. **The two- and three-argument overloads are deleted**, and `Indexer.Recheck` stays exactly where it is.

### The three decisions this task makes, and why each is forced

**1. The gate is `CanRead`, on the interface, consulted by `Indexer.Handle` - not a branch inside `Decode`.**

A gate written inside the decode arms cannot be inherited by a test fake, and then "the decoder was never asked" stops being an assertion anybody can make: a fake that does not gate reports a call for a capability that is absent, and the paired test that is the whole point of this task fails on its first line. An earlier draft of this plan put the gate inside `Decode`, wrote one fake that gated and one that did not, and four of its nine tests could not pass.

Nor can the gate be a reverse lookup of "which capability covers this kind". **`Video` is covered by Photos OR by Speech** - frames need the vision tower, the sound track needs whisper, and a video with only one of them installed is still worth opening. A `Capabilities.All.First(c => KindsCovered(c).Contains(kind))` returns Photos and silently drops every video on a speech-only machine.

So the rule is a static function, `Decoders.Covers(kind, installed)`, with the OR written out; `Decoders.CanRead` is `Covers(kind, Installed)`; the test's fake implements `CanRead` the same way. A mutation of `Covers` then breaks the tests in both directions, which is the only thing separating a real gate from today's skip-everything build.

**2. Nothing in this plan hands a diagnostic a writer on the real vector store.**

`Indexer.DrainOnce` and `Indexer.Loop` take an `IDecoders`, and **there is no overload that builds one for the caller**. That is deliberate: an overload that quietly calls `ForThisMachine()` is how `--searchbench` ends up appending vectors for a synthetic corpus into a user's `vectors.bin` and then deleting the database that referenced them - orphan rows nothing will ever tombstone, in a file that grows for ever. It is also how `--searchindex` gets an `IOException` out of a sharing violation while the real child holds the store. Trap 4 - *"a benchmark must not be able to rebuild a user's index"* - is what Plan 4 was sent back to fix, and this is the second door into it. Deleting the overload makes the mistake a compile error rather than a test.

**3. How long a recording is worth transcribing is one number the user sets, and the source's two constants are not it.**

The source caps audio at an hour and a video's sound track at three minutes (`Indexer.cs:40-41`), which is two constants making a decision that costs the user hours of their own machine's time. Spec §6 replaces both with **one setting covering audio and video together**: zero is off, negative is no limit, positive is the limit in minutes, default five. An asymmetry between audio and video would be invisible in the interface and surprising in use.

Two things follow that are easy to get wrong:

- **A recording over the limit is SKIPPED, with a reason of its own.** `Decoders.TooLong`, not `TooLarge`. `StateSkipped` already meant four different things and this is a fifth - and it is the only one a user can change from a settings control, so raising the limit later has to re-queue exactly these files. A reason shared with "too large" would sweep up every enormous document as well; no reason at all would miss them entirely.
- **A video over the limit is not necessarily skipped.** With Photos installed its frames are still worth reading, so the item is `StateIndexed` **and carries `TooLong` in its recorded-reason column** as a note about what was left undone. That is what `KindResult`'s third field, `Note`, is for: `Skip` decides the state and `Note` does not. Without the distinction the indexer derives Skipped from any reason at all, and every film whose frames were read is counted as unread. Raising the limit therefore re-queues on the reason rather than on the state, which is why Task 11's `RequeueKinds` filter reads the `error` column and not `state` alone.

The in-memory decode ceiling that remains, `MaxDecodeSeconds`, is a **memory** bound and not a policy one, and its comment says so: samples are 16 kHz float32, which is 3.66 MB per minute, so an unbounded decode of a long archive is gigabytes of `List<float>`. A recording longer than it is transcribed up to that point with a note, which is what the source's own `note` parameter already does.

**4. The vector store is flushed BEFORE the transaction that references it commits, and rows are released AFTER.**

Two orderings, opposite directions, both load-bearing:

- **Flush before commit.** `Decode` appends vectors; `Upsert` writes segments carrying those row numbers. If the child dies between the commit and the flush, the database references rows past the vector header's count and those segments silently never match again - a permanent, invisible miss. The other way round costs a few kilobytes of flushed rows nothing points at.
- **Release after commit.** A tombstone is destructive. Zeroing the old rows inside the transaction means a rollback leaves the surviving segments pointing at zeroed vectors, and those files stop being findable by meaning with nothing anywhere to say why.

### The trap in this task, stated plainly

"A photo is skipped when the models are missing" is what the code does *today*, with no capability logic at all. A test that only checks the skipped case passes against an implementation that has learned nothing. Every test below therefore has both halves - the decoder is asked when the capability is there, and is **not asked** when it is not - and both halves are asserted through a fake that implements `CanRead` with the production rule.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Content/DecoderGateTests.cs`:

```csharp
using Findra;

public class DecoderGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-gate-" + Guid.NewGuid().ToString("N"));

    public DecoderGateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ContentDb Open() => new(Path.Combine(_dir, "search.db"));

    private string File_(string name, string text = "the quarterly lease agreement and its deposit")
    {
        string p = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    /// <summary>
    /// One fake, and it answers <c>CanRead</c> with the SAME static rule the real Decoders uses.
    ///
    /// <para>That is what makes "the decoder was never asked" an assertion rather than a
    /// restatement: a mutation of <see cref="Decoders.Covers"/> changes what this fake says, so
    /// a gate that is unconditional in either direction shows up as a call count that is wrong
    /// in that direction. A fake with its own opinion about gating would test the fake.</para>
    ///
    /// <para>It answers <c>Decode</c> with one segment carrying a real-looking vector row, so the
    /// rows an upsert or a delete hands back are visible in <see cref="Released"/>.</para>
    /// </summary>
    private sealed class Fake(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public List<(ResultKind Kind, string Path)> Asked { get; } = [];
        public List<long> Released { get; } = [];
        public int Flushes;
        public long NextRow = 100;

        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);

        public KindResult Decode(ResultKind kind, string path, long bytes)
        {
            Asked.Add((kind, path));
            return new KindResult([new ContentDb.Segment(ContentDb.SegImage, -1, -1, NextRow++, "")], null);
        }

        public void Flush() => Flushes++;
        public void Release(IReadOnlyList<long> vectorRows) => Released.AddRange(vectorRows);
        public void Dispose() { }
    }

    // ---- the gate ----

    [Fact]
    public void APhotoIsOfferedToTheDecoderOnlyWhenTheModelsForItAreThere()
    {
        // BOTH halves. Without the second, an implementation that skips every photo
        // unconditionally - which is exactly what this build does today - passes.
        string photo = File_("holiday.jpg");

        using (ContentDb db = Open())
        {
            db.Enqueue("C", 1, photo, ResultKind.Photo, "test");
            var without = new Fake(CapabilitySet.None);
            Indexer.DrainOnce(db, _ => { }, without);

            Assert.Empty(without.Asked);
            Assert.Equal(ContentDb.StateSkipped, db.StateOf("C", 1));
        }

        using (ContentDb db = Open())
        {
            db.Enqueue("C", 2, photo, ResultKind.Photo, "test");
            var with = new Fake(Set(Capability.Photos));
            Indexer.DrainOnce(db, _ => { }, with);

            Assert.Single(with.Asked);
            Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 2));
        }
    }

    [Fact]
    public void SpeechAndPicturesAreGatedSeparatelyAndNotTogether()
    {
        // The all-or-nothing gate this replaces failed both together. With photos installed and
        // speech not, a picture must index and a sound file must skip, in one drain.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("holiday.jpg"), ResultKind.Photo, "test");
        db.Enqueue("C", 2, File_("voice.m4a"), ResultKind.Audio, "test");

        var d = new Fake(Set(Capability.Photos));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal([ResultKind.Photo], d.Asked.Select(a => a.Kind).ToArray());
        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));
        Assert.Equal(ContentDb.StateSkipped, db.StateOf("C", 2));
    }

    [Fact]
    public void AVideoIsWorthOpeningForItsFramesOrForItsSoundAndNotOnlyForBoth()
    {
        // Video is the one kind two capabilities cover. A reverse lookup of "which capability
        // covers this kind" returns Photos and silently drops every video on a speech-only
        // machine; an AND drops them on both single-capability machines.
        Assert.True(Decoders.Covers(ResultKind.Video, Set(Capability.Photos)));
        Assert.True(Decoders.Covers(ResultKind.Video, Set(Capability.Speech, Capability.Meaning)));
        Assert.False(Decoders.Covers(ResultKind.Video, CapabilitySet.None));

        // and the gate the indexer consults says the same thing
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("clip.mp4"), ResultKind.Video, "test");
        var d = new Fake(Set(Capability.Speech, Capability.Meaning));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Single(d.Asked);
        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));
    }

    [Fact]
    public void AMissingCapabilityIsNeverAFailure()
    {
        // Spec §6: a missing model is a normal state, not an error state. A Failed row would put
        // the file in the failure sample of --searchindex, where nobody can act on it, and
        // RequeueKinds deliberately leaves Failed rows alone - so it would never be picked up
        // when the capability finally arrived.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("a.jpg"), ResultKind.Photo, "test");
        db.Enqueue("C", 2, File_("b.m4a"), ResultKind.Audio, "test");
        db.Enqueue("C", 3, File_("c.mp4"), ResultKind.Video, "test");

        Indexer.DrainOnce(db, _ => { }, new Fake(CapabilitySet.None));

        (long _, long _, long failed, long skipped) = db.Counts();
        Assert.Equal(0, failed);
        Assert.Equal(3, skipped);
        Assert.Empty(db.RecentFailures(10));
    }

    [Fact]
    public void AFileSkippedForWantOfAModelSaysThatIsWhy()
    {
        // The reason string is what CapabilityGate's exclusion list and --searchindex's models
        // section both key on. An empty reason, or one borrowed from the size gates, makes a
        // photo waiting for a download indistinguishable from a photo that is too big to read.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("a.jpg"), ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, new Fake(CapabilitySet.None));

        Assert.Equal(Decoders.NoModel, db.RecentSkips(10).Single().Error);
    }

    [Fact]
    public void WordsInDocumentsStillWorkWithNoModelAtAll()
    {
        // Free of charge, which is what makes declining every download a complete answer rather
        // than a broken one. (Free of CONSENT is a different question: nothing is read at all
        // until content indexing is turned on, and that is the queue's pause, not this gate.)
        // A gate that accidentally covers Document takes full-text search away from everybody
        // who declined the download.
        Assert.True(Decoders.Covers(ResultKind.Document, CapabilitySet.None));

        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("notes.txt"), ResultKind.Document, "test");

        // The real Decoders, with an empty model folder and a throwaway vector store - never
        // Decoders.ForThisMachine(), which opens a writer on the REAL index directory.
        using var vectors = new VectorStore(Path.Combine(_dir, "vectors.bin"), writer: true);
        using var real = new Decoders(CapabilitySet.Installed(_dir), vectors, modelDir: _dir);
        Indexer.DrainOnce(db, _ => { }, real);

        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));
        Assert.Single(db.Fts("deposit", 5));
    }

    // ---- the vector rows a replace or a delete hands back ----

    [Fact]
    public void AReplacedFilesOldVectorRowsAreReleased()
    {
        // Upsert hands back the vector rows the segments it replaced were pointing at. This
        // build discards that return (`_ = _db.Upsert(...)`, Indexer.cs:321), which was correct
        // while every segment carried -1 and is a leak the moment they carry a row: the old
        // embedding of an edited document keeps matching queries for ever, beside the new one.
        using ContentDb db = Open();
        string doc = File_("contract.txt");
        var d = new Fake(Set(Capability.Photos, Capability.Meaning));

        db.Enqueue("C", 1, doc, ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, d);
        Assert.Empty(d.Released);                        // nothing to release the first time

        db.Enqueue("C", 1, doc, ResultKind.Photo, Indexer.Recheck);
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal([100L], d.Released);                // the first pass's row, handed back
    }

    [Fact]
    public void ADeletedFilesVectorRowsAreReleasedToo()
    {
        // Delete hands back the same list, and it is discarded in the same place
        // (Indexer.cs:274). A photo deleted a year ago answering a query is the visible form.
        using ContentDb db = Open();
        var d = new Fake(Set(Capability.Photos));

        db.Enqueue("C", 1, File_("gone.jpg"), ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, d);
        d.Released.Clear();

        db.Enqueue("C", 1, Path.Combine(_dir, "gone.jpg"), ResultKind.Photo, ContentDb.ReasonDelete);
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal([100L], d.Released);
    }

    [Fact]
    public void AFileThatFailsWhileBeingReadAlsoReleasesTheRowsItHeld()
    {
        // The third discarded return (Indexer.cs:343), on the failure path. A file that indexed
        // once and later throws - a PDF replaced by a broken one - keeps its old vector rows for
        // ever, and nothing will ever tombstone them because the item now says Failed.
        using ContentDb db = Open();
        string doc = File_("contract.txt");
        var d = new Fake(Set(Capability.Photos));

        db.Enqueue("C", 1, doc, ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, d);
        d.Released.Clear();

        // A decoder that throws on the second pass, standing in for a malformed file.
        var boom = new ThrowingDecoders(Set(Capability.Photos), d.Released);
        db.Enqueue("C", 1, doc, ResultKind.Photo, Indexer.Recheck);
        Indexer.DrainOnce(db, _ => { }, boom);

        Assert.Equal(ContentDb.StateFailed, db.StateOf("C", 1));
        Assert.Equal([100L], d.Released);
    }

    private sealed class ThrowingDecoders(CapabilitySet installed, List<long> released) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes)
            => throw new InvalidDataException("the file is malformed");
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) => released.AddRange(rows);
        public void Dispose() { }
    }

    // ---- the two orderings ----

    [Fact]
    public void TheVectorStoreIsFlushedBeforeTheDatabaseCommitsAndReleasedAfter()
    {
        // Flush before commit: a database row pointing past the vector header's count is a
        // segment that silently never matches again, for ever. Release after commit: a rollback
        // that has already zeroed the old rows leaves the surviving segments pointing at
        // nothing. The two orderings run in opposite directions and both are load-bearing.
        //
        // The commit is OBSERVED rather than announced, and that needs no seam in the shipping
        // code: a second, read-only connection sees the last committed snapshot, so while the
        // indexer's transaction is open it still reports the row as pending, and the moment the
        // transaction commits it reports the queue as empty. Asking that question at each event
        // is what turns "before" and "after" into two assertions rather than a list of strings
        // whose order nothing enforces.
        //
        // The SECOND drain is the one measured. The first indexes the file so there is a vector
        // row to release; on a re-check the transaction is the only thing standing between a
        // pending row and an empty queue.
        using ContentDb db = Open();
        using var reader = new ContentDb(db.Path, readOnly: true);
        var d = new OrderRecordingDecoders(Set(Capability.Photos), () => reader.PendingCount() == 0);

        db.Enqueue("C", 1, File_("a.jpg"), ResultKind.Photo, "test");
        Indexer.DrainOnce(db, _ => { }, d);

        db.Enqueue("C", 1, Path.Combine(_dir, "a.jpg"), ResultKind.Photo, Indexer.Recheck);
        d.Reset();
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.True(d.Flushed, "the vector store was never flushed");
        Assert.True(d.ReleasedRows, "no vector row was handed back to be released");
        Assert.False(d.CommitHadHappenedAtFlush,
            "the vector store was flushed AFTER the commit that referenced its rows - a child that " +
            "dies in between leaves segments pointing past the header's count, for ever");
        Assert.True(d.CommitHadHappenedAtRelease,
            "vector rows were released INSIDE the transaction - a rollback then leaves the " +
            "surviving segments pointing at zeroed vectors");
    }

    /// <summary>
    /// Answers, at each of the two events, whether the indexer's transaction had already
    /// committed - through a <paramref name="committed"/> probe the test builds from a second
    /// read-only connection. Nothing in the shipping code is instrumented for this.
    /// </summary>
    private sealed class OrderRecordingDecoders(CapabilitySet installed, Func<bool> committed) : IDecoders
    {
        private long _next = 100;
        public CapabilitySet Installed { get; } = installed;
        public bool Flushed { get; private set; }
        public bool ReleasedRows { get; private set; }
        public bool CommitHadHappenedAtFlush { get; private set; }
        public bool CommitHadHappenedAtRelease { get; private set; }

        public void Reset()
        {
            Flushed = ReleasedRows = CommitHadHappenedAtFlush = CommitHadHappenedAtRelease = false;
        }

        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);

        public KindResult Decode(ResultKind kind, string path, long bytes)
            => new([new ContentDb.Segment(ContentDb.SegImage, -1, -1, _next++, "")], null);

        public void Flush()
        {
            Flushed = true;
            CommitHadHappenedAtFlush = committed();
        }

        public void Release(IReadOnlyList<long> rows)
        {
            if (rows.Count == 0) return;       // the first drain has nothing to hand back
            ReleasedRows = true;
            CommitHadHappenedAtRelease = committed();
        }

        public void Dispose() { }
    }

    // ---- speech, and what makes Hebrew a second pass ----

    [Fact]
    public void TheGeneralModelIsAlwaysTheFirstPassAndTheFineTuneIsOnlyEverTheSecond()
    {
        // Spec §6: turbo runs first for language detection and only the files it calls Hebrew
        // are re-run through the fine-tune. An implementation that loads the fine-tune INSTEAD
        // when Hebrew is installed transcribes every English file with a Hebrew model, and
        // nothing about the output would look wrong enough to notice.
        Assert.Equal(ModelStore.WhisperTurbo, Decoders.SpeechModels(Set(Capability.Speech, Capability.Meaning)).General);
        Assert.Null(Decoders.SpeechModels(Set(Capability.Speech, Capability.Meaning)).Hebrew);

        var both = Decoders.SpeechModels(new CapabilitySet(Presets.Everything));
        Assert.Equal(ModelStore.WhisperTurbo, both.General);
        Assert.Equal(ModelStore.WhisperHebrew, both.Hebrew);

        // there is no arrangement of capabilities in which the fine-tune is the first pass
        foreach (Capability[] set in new[]
        {
            new[] { Capability.Hebrew }, [Capability.Hebrew, Capability.Speech],
            [Capability.Speech], [Capability.Photos, Capability.Hebrew],
        })
            Assert.Equal(ModelStore.WhisperTurbo, Decoders.SpeechModels(new CapabilitySet(Capabilities.Close(set))).General);
    }

    // ---- indexed, with a note ----

    [Fact]
    public void ALongVideoWhoseFramesWereReadIsIndexedAndSaysWhatItDidNotHear()
    {
        // The row Task 11's limit re-queue is built on, produced by the real path rather than
        // written by hand. Skip decides the state and Note does not: deriving the state from
        // "is there a reason at all" - which is what the indexer did before KindResult grew a
        // third field - marks every film whose frames were read as SKIPPED, and --searchindex
        // then reports a whole video library as unread.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("film.mp4"), ResultKind.Video, "test");

        var d = new NotingFake(Set(Capability.Photos, Capability.Speech, Capability.Meaning));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));   // its frames were read
        Assert.Equal(1, db.CountRecorded(Decoders.TooLong));         // and the note says what was not
        Assert.Empty(db.RecentSkips(10));
    }

    [Fact]
    public void AFileWithNothingReadAtAllIsStillSkipped()
    {
        // The control. Note must not become a way for everything to report itself indexed: a
        // decoder that read nothing sets Skip, and Skip still decides the state.
        using ContentDb db = Open();
        db.Enqueue("C", 1, File_("film.mp4"), ResultKind.Video, "test");

        var d = new SkippingFake(Set(Capability.Photos));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal(ContentDb.StateSkipped, db.StateOf("C", 1));
        Assert.Equal(Decoders.TooLong, db.RecentSkips(10).Single().Error);
    }

    /// <summary>Reads something and leaves something else undone - the long-video shape.</summary>
    private sealed class NotingFake(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes)
            => new([new ContentDb.Segment(ContentDb.SegFrame, 0, 0, 100, "")],
                   Skip: null, Note: Decoders.TooLong);
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Dispose() { }
    }

    /// <summary>Reads nothing, for the same reason.</summary>
    private sealed class SkippingFake(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes)
            => new([], Decoders.TooLong);
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Dispose() { }
    }

    // ---- how long a recording is worth transcribing ----

    [Fact]
    public void ARecordingLongerThanTheLimitIsSkippedForAReasonOfItsOwn()
    {
        // Not TooLarge, and not silence. This is the fifth meaning of StateSkipped and the only
        // one a user can change from a settings control, so raising the limit later has to
        // re-queue exactly these files - which it can only do if the reason is exact.
        Assert.NotEqual(Decoders.TooLarge, Decoders.TooLong);
        Assert.NotEqual(Decoders.NoModel, Decoders.TooLong);
        Assert.NotEqual(Decoders.NoText, Decoders.TooLong);
        Assert.NotEqual(Decoders.NoFormatReader, Decoders.TooLong);
        Assert.NotEqual(Decoders.AnIcon, Decoders.TooLong);
        Assert.Contains("long", Decoders.TooLong, StringComparison.OrdinalIgnoreCase);
    }

    // needs `using System.Reflection;` at the top of the file
    [Fact]
    public void NoPerKindRecordingConstantSurvivesBesideTheSetting()
    {
        // An hour for audio and three minutes for video are the ancestors of the default, not
        // the rule (spec §6). Porting them as behaviour re-introduces an asymmetry the setting
        // exists to remove, and it would be invisible: a video and a sound file of the same
        // length would behave differently for no reason anybody could see in the interface.
        string[] fields = [.. typeof(Decoders)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Select(f => f.Name)];

        Assert.DoesNotContain("MaxAudioSeconds", fields);
        Assert.DoesNotContain("MaxVideoSpeechSeconds", fields);
        // and the one number that replaced them is where the rule lives
        Assert.Contains("MaxDecodeSeconds", fields);          // a memory bound, not a policy one
        Assert.Equal(5, TranscribeLimit.Default);
    }

    // ---- the size gates, applied ----

    [Theory]
    [InlineData(ResultKind.Photo, 1_000, "an icon, not a picture")]
    [InlineData(ResultKind.Photo, 200L << 20, "too large")]
    [InlineData(ResultKind.Photo, 2L << 20, null)]
    [InlineData(ResultKind.Document, 300L << 20, "too large")]
    [InlineData(ResultKind.Document, 4_000, null)]
    [InlineData(ResultKind.Video, 10L << 30, "too large")]
    [InlineData(ResultKind.Video, 100L << 20, null)]
    public void ASizeGateIsAppliedAndNotMerelyDeclared(ResultKind kind, long bytes, string? skip)
    {
        // Asserting the constants against each other only proves arithmetic between four
        // literals in one file, and a port that keeps the constants and stops USING them passes
        // it. This asks the function the decode arms actually call. Below the icon floor every
        // favicon on the disk matches every query a little, which is worse than not being there.
        Assert.Equal(skip, Decoders.SizeGate(kind, bytes));
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter DecoderGateTests`
Expected: FAIL - `IDecoders`, `Decoders` and `KindResult` do not exist, `Indexer.DrainOnce` has no three-argument overload with an `IDecoders`, and `ContentDb.RecentSkips` does not exist.

- [ ] **Step 3: Write `TranscribeLimit`, and its tests**

One number, and the rule that reads it. It is separate from `Decoders` because it is pure, because
`Config`, the `--content` command and `CapabilityGate` all read it, and because the arithmetic is
where the mistakes are.

Create `tests/Findra.Tests/Content/TranscribeLimitTests.cs`:

```csharp
using Findra;

public class TranscribeLimitTests
{
    [Theory]
    [InlineData(0, 1, false)]           // off means off, even for a one-second clip
    [InlineData(0, 0.5, false)]
    [InlineData(-1, 36_000, true)]      // negative means no limit
    [InlineData(-99, 36_000, true)]     // ANY negative, not just -1
    [InlineData(5, 299, true)]
    [InlineData(5, 300, true)]          // exactly at the limit is inside it
    [InlineData(5, 301, false)]
    [InlineData(120, 7_200, true)]
    public void TheRuleIsZeroIsOffNegativeIsNoLimitAndPositiveIsMinutes(int minutes, double seconds, bool covered)
    {
        // Three meanings in one int, and each has a wrong implementation that looks right:
        // treating 0 as "no limit" transcribes everything on a machine that asked for nothing;
        // treating negative as 0 transcribes nothing for somebody who asked for everything;
        // `<` instead of `<=` drops a recording that is exactly five minutes long, which is what
        // a voice memo app produces.
        Assert.Equal(covered, TranscribeLimit.Covers(minutes, seconds));
    }

    [Fact]
    public void ThePresetsAreTheOnesTheSpecNames()
    {
        Assert.Equal([TranscribeLimit.Off, 5, 30, 120, TranscribeLimit.NoLimit], TranscribeLimit.Presets);
        Assert.Equal(0, TranscribeLimit.Off);
        Assert.True(TranscribeLimit.NoLimit < 0);
        Assert.Equal(5, TranscribeLimit.Default);
    }

    [Fact]
    public void APresetAndATypedValueAreTheSameSetting()
    {
        // Spec §6: "the named choices are presets over that one number, so a typed value and a
        // preset cannot disagree". A second field for the preset name is what makes them able
        // to - this asserts the name is DERIVED from the number and nothing else.
        Assert.Equal("2 hours", TranscribeLimit.Named(120));
        Assert.Equal("2 hours", TranscribeLimit.Describe(120));
        Assert.Null(TranscribeLimit.Named(17));                  // not a preset
        Assert.Equal("17 minutes", TranscribeLimit.Describe(17)); // still readable
    }

    [Fact]
    public void EveryPresetHasAName()
    {
        foreach (int m in TranscribeLimit.Presets)
            Assert.False(string.IsNullOrEmpty(TranscribeLimit.Named(m)), $"{m} has no name");
    }

    [Theory]
    [InlineData("off", 0)]
    [InlineData("5", 5)]
    [InlineData("30 minutes", 30)]
    [InlineData("2 hours", 120)]
    [InlineData("no limit", -1)]
    [InlineData("nolimit", -1)]
    [InlineData("17", 17)]
    public void APresetNameAndABareNumberBothParse(string word, int minutes)
        => Assert.Equal(minutes, TranscribeLimit.Parse(word));

    [Theory]
    [InlineData("soon")]
    [InlineData("")]
    [InlineData("5 fortnights")]
    public void AWordThatIsNeitherIsRefusedRatherThanTreatedAsZero(string word)
    {
        // Zero is a real setting - "transcribe nothing" - so a parse that falls back to it
        // silently turns speech search off for somebody who mistyped a number.
        Assert.Null(TranscribeLimit.Parse(word));
    }

    [Fact]
    public void EverySettingReadsTheSameOnEveryMachine()
    {
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("2 hours", TranscribeLimit.Describe(120));
            Assert.Equal(1_500, TranscribeLimit.Parse("1500"));
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }
}
```

Then `src/Findra/Content/TranscribeLimit.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Findra;

/// <summary>
/// How long a recording is worth transcribing, as one number of minutes.
///
/// <para>Transcription cost scales with the LENGTH of a recording rather than the size of its
/// file, and it is the most expensive thing Findra does - on a machine with no usable
/// accelerator an hour of audio is a long stretch of real time. So the length that is worth it
/// is the user's decision and not a constant in the code (spec §6).</para>
///
/// <para><b>One number, covering audio and video together.</b> An asymmetry between them would be
/// invisible in the interface and surprising in use. Zero is off, a negative value is no limit,
/// and any positive number is the limit itself; the named choices are PRESETS OVER THAT NUMBER,
/// so a typed value and a preset are the same setting and cannot disagree. A second field
/// holding the preset name is exactly how they would.</para>
/// </summary>
public static class TranscribeLimit
{
    public const int Off = 0;
    public const int NoLimit = -1;

    /// <summary>Voice memos, messages, clips and screen recordings - cheap on any machine, which
    /// is what a default has to be.</summary>
    public const int Default = 5;

    public static readonly IReadOnlyList<int> Presets = [Off, 5, 30, 120, NoLimit];

    /// <summary>Is this recording worth transcribing at the current setting?</summary>
    public static bool Covers(int minutes, double durationSeconds)
    {
        if (minutes == Off) return false;      // off means off, whatever the length
        if (minutes < 0) return true;          // ANY negative is no limit, not just -1
        return durationSeconds <= minutes * 60.0;   // exactly at the limit is inside it
    }

    /// <summary>The preset name for this number, or null when the user typed something of their
    /// own. Derived from the number - there is nowhere else for a name to live.</summary>
    public static string? Named(int minutes) => minutes switch
    {
        Off => "Off",
        5 => "5 minutes",
        30 => "30 minutes",
        120 => "2 hours",
        < 0 => "No limit",
        _ => null,
    };

    /// <summary>Always readable: the preset's name, or the number in minutes.</summary>
    public static string Describe(int minutes)
        => Named(minutes) ?? $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes";

    /// <summary>A preset name or a bare number of minutes. Null for anything else - zero is a
    /// real setting, so falling back to it would turn speech off for somebody who mistyped.</summary>
    public static int? Parse(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        string w = word.Trim();
        foreach (int m in Presets)
            if (string.Equals(Named(m)!.Replace(" ", ""), w.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                return m;
        return int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
    }
}
```

Run: `dotnet test --filter TranscribeLimitTests` - PASS, 8 test methods / 22 cases.

- [ ] **Step 4: Write `Decoders`**

Create `src/Findra/Content/Decoders.cs`. The Photo, Document, Audio, Video and Speech bodies come from the source's `Indexer` (`Indexer.cs:329-474`) essentially unchanged; what is new is the gate, the size function they share, and the ownership rule.

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using SkiaSharp;
using Whisper.net;

namespace Findra;

/// <summary>
/// What came of asking to read inside one file: its segments, and either the reason nothing was
/// read or a note about what was left undone. A skip is a normal outcome and never an error.
///
/// <para><see cref="Skip"/> and <see cref="Note"/> are different facts, and the state follows
/// Skip alone. Skip means nothing usable came back, and the item is
/// <see cref="ContentDb.StateSkipped"/>. Note means something did come back - and something else
/// did not: a long video whose frames were read while its sound track was passed over for length
/// is a genuinely INDEXED file that is also incomplete, and calling it skipped would tell
/// <c>--searchindex</c> and the card that a whole film library had never been read.</para>
///
/// <para>Both land in the same <c>items.error</c> column, because both are "the recorded reason
/// this row is the way it is" and <see cref="CapabilityGate"/> re-queues on that column without
/// caring which produced the string. Skip wins if a decoder ever sets both; no arm does.</para>
/// </summary>
public readonly record struct KindResult(List<ContentDb.Segment> Segments, string? Skip, string? Note = null);

/// <summary>
/// What this machine can read inside a file, given what is installed.
///
/// <para><see cref="CanRead"/> is the GATE and it lives here, on the interface, so that
/// <see cref="Indexer.Handle"/> can ask before it opens anything and a test's fake can answer
/// with the same rule the real one uses. A gate buried inside <see cref="Decode"/> is not
/// testable: "the decoder was never asked" stops being an assertion anybody can make, because
/// the fake that would prove it has to reimplement the rule and then the test tests the fake.
/// </para>
///
/// <para>This is an interface for one reason: the gate is the behaviour this plan CHANGES, and
/// it has to be provable without a 2.9 GB download.</para>
/// </summary>
public interface IDecoders : IDisposable
{
    /// <summary>What is on disk right now. Read once when the child starts: a model that arrives
    /// mid-session is picked up by the next child, and the interface starts one.</summary>
    CapabilitySet Installed { get; }

    /// <summary>Is there any point opening this kind of file at all? Asked before
    /// <see cref="Decode"/>, and a false answer is a Skipped row with
    /// <see cref="Decoders.NoModel"/> - never a Failed one.</summary>
    bool CanRead(ResultKind kind);

    KindResult Decode(ResultKind kind, string path, long bytes);

    /// <summary>Make every vector written so far durable. Called BEFORE the transaction that
    /// references those rows commits, because a database row pointing past the vector header's
    /// count is a segment that silently never matches again.</summary>
    void Flush();

    /// <summary>Release the vector rows a replaced or deleted item was pointing at. Called AFTER
    /// that transaction commits: a tombstone is destructive, and a rollback that has already
    /// zeroed the old rows leaves the surviving segments pointing at nothing.</summary>
    void Release(IReadOnlyList<long> vectorRows);
}

/// <summary>
/// The real decoders. Every one of them reads a file somebody else put on the disk, which is
/// exactly why this whole type runs in the indexer child at normal integrity and never in the
/// elevated helper (spec §3).
///
/// <para>Nothing here downloads anything. A capability whose files are not present is not an
/// error, a warning, or a reason to wait: the kind is skipped with a reason, the row stays
/// exactly where <see cref="ContentDb.RequeueKinds"/> can find it later, and the interface
/// offers the download.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class Decoders : IDecoders
{
    /// <summary>
    /// How much audio can be pulled into memory at once. This is a MEMORY bound and not a policy
    /// one - how long a recording is worth transcribing is
    /// <see cref="TranscribeLimit"/>'s, and it is the user's. Samples are 16 kHz float32, which
    /// is 3.66 MB a minute, so "no limit" over a long archive would be gigabytes of List&lt;float&gt;.
    /// A recording longer than this is transcribed up to here and the note says so.
    ///
    /// <para>The source's <c>MaxAudioSeconds = 3600</c> and <c>MaxVideoSpeechSeconds = 180</c> are
    /// deliberately NOT ported. They are two constants making a decision that costs hours of
    /// somebody else's machine, and one of them applied to audio while the other applied to
    /// video - an asymmetry that would be invisible in the interface.</para>
    /// </summary>
    public const double MaxDecodeSeconds = 4 * 3600;

    public const long MaxVideoBytes = 8L << 30;
    public const long MaxDocBytes = 200L << 20;
    public const long MaxImageBytes = 120L << 20;

    /// <summary>Below this an "image" is a UI icon - a checkbox, a favicon, a spinner - and every
    /// one of them matches every query a little, which is worse than not being there.</summary>
    public const long MinImageBytes = 10 << 10;

    /// <summary>Recorded against a file whose KIND needs a model this machine has not got. It is
    /// a normal, re-queueable outcome and never a failure: the capability that arrives later
    /// picks up exactly the rows carrying it. Its exact text crosses into
    /// <see cref="CapabilityGate"/> and into <c>--searchindex</c>'s models section, so it is one
    /// constant rather than a literal per reader.</summary>
    public const string NoModel = "no decoder for this kind yet";

    /// <summary>A file too big to be worth reading whole. A capability arriving later cannot make
    /// it smaller, which is why <see cref="CapabilityGate"/> excludes it from a re-queue.</summary>
    public const string TooLarge = "too large";

    /// <summary>A document with nothing in it. Same reasoning as <see cref="TooLarge"/>.</summary>
    public const string NoText = "no text";

    /// <summary>A format this build has no reader for - the doc/xls/ppt/rtf/odt set. Distinct
    /// from <see cref="NoModel"/>, which means "no MODEL for this KIND", and a later reader
    /// re-queues exactly these rows.</summary>
    public const string NoFormatReader = "no decoder for this format yet";

    public const string AnIcon = "an icon, not a picture";

    /// <summary>Recorded against a recording longer than the transcription limit. It is its OWN
    /// reason, distinct from <see cref="TooLarge"/>, because it is the only one on this list a
    /// user can change from a settings control: raising the limit re-queues exactly the rows
    /// carrying this and nothing else (spec §6). It is also written as a NOTE on an indexed
    /// video whose frames were read and whose sound track was not.</summary>
    public const string TooLong = "longer than the transcription limit";

    private readonly VectorStore _vectors;
    private readonly string? _dir;
    private readonly bool _ownsVectors;
    private readonly Func<int> _transcribeMinutes;
    private ClipImageEncoder? _vision;
    private E5Encoder? _e5;
    private WhisperFactory? _whisper, _whisperHe;
    private bool _dirty;

    public CapabilitySet Installed { get; }

    /// <summary><paramref name="ownsVectors"/> is false by default and that is the safe
    /// direction: a store the caller opened stays the caller's, and a type that guesses closes a
    /// test's store under it. Only <see cref="ForThisMachine"/> passes true, because only it
    /// opened one.</summary>
    /// <summary><paramref name="transcribeMinutes"/> is a delegate rather than a value because it
    /// is a SETTING and it can change while the child runs - the interface writes it to
    /// <c>index:transcribeminutes</c> and the child reads it the same way it reads
    /// <c>index:power</c>. A captured value would mean a change to the limit did nothing until
    /// the child was restarted, with no message anywhere saying so.</summary>
    public Decoders(CapabilitySet installed, VectorStore vectors, Func<int>? transcribeMinutes = null,
                    string? modelDir = null, bool ownsVectors = false)
    {
        Installed = installed;
        _vectors = vectors;
        _transcribeMinutes = transcribeMinutes ?? (() => TranscribeLimit.Default);
        _dir = modelDir;
        _ownsVectors = ownsVectors;
    }

    /// <summary>The set this machine actually has, with a writer on the real vector store. Only
    /// the <c>--index</c> child calls this. A diagnostic that calls it takes a writer on a file
    /// the running child already holds, and appends rows to a store its throwaway database will
    /// never reference.</summary>
    public static Decoders ForThisMachine(Func<int> transcribeMinutes, string? modelDir = null)
        => new(CapabilitySet.Installed(modelDir), new VectorStore(writer: true), transcribeMinutes,
               modelDir, ownsVectors: true);

    /// <summary>
    /// Whether a kind is worth opening at all, given what is installed. Static and pure, so the
    /// indexer, the real decoders and a test's fake all answer the same question the same way.
    ///
    /// <para>Video is the reason this cannot be a reverse lookup from kind to capability: its
    /// frames need the vision tower and its sound track needs whisper, and a video is worth
    /// opening for EITHER. A lookup that returns the first capability covering the kind drops
    /// every video on a speech-only machine, silently.</para>
    /// </summary>
    public static bool Covers(ResultKind kind, CapabilitySet installed) => kind switch
    {
        // Words in documents costs no download and no model, so nothing here gates it. Whether
        // it runs at all is a different question with a different answer - content indexing is
        // off until asked for (spec §6) - and that is the queue's pause, not this. Covers says
        // "can this be read"; the pause says "should anything be read at all".
        ResultKind.Document => true,
        ResultKind.Photo => installed.Has(Capability.Photos),
        ResultKind.Video => installed.Has(Capability.Photos) || installed.Has(Capability.Speech),
        ResultKind.Audio => installed.Has(Capability.Speech),
        _ => false,
    };

    public bool CanRead(ResultKind kind) => Covers(kind, Installed);

    /// <summary>The size rules, in one place, asked by the arms rather than repeated inside them.
    /// Null means "go ahead"; anything else is the skip reason.</summary>
    public static string? SizeGate(ResultKind kind, long bytes) => kind switch
    {
        ResultKind.Photo when bytes > MaxImageBytes => TooLarge,
        ResultKind.Photo when bytes < MinImageBytes => AnIcon,
        ResultKind.Document when bytes > MaxDocBytes => TooLarge,
        ResultKind.Video when bytes > MaxVideoBytes => TooLarge,
        _ => null,
    };

    /// <summary>Which whisper models a transcription uses. The general model is ALWAYS the first
    /// pass - it is what detects the language - and the fine-tune is only ever the second, over
    /// the files the first one calls Hebrew. There is deliberately no arrangement of capabilities
    /// that returns the fine-tune as <c>General</c>.</summary>
    public static (Model General, Model? Hebrew) SpeechModels(CapabilitySet installed)
        => (ModelStore.WhisperTurbo, installed.Has(Capability.Hebrew) ? ModelStore.WhisperHebrew : null);

    public void Flush()
    {
        // Only when something was actually appended. A model-free build writes no vectors at all,
        // and three FlushFileBuffers per file across a hundred thousand files is three hundred
        // thousand fsyncs on the queue's critical path for nothing.
        if (!_dirty) return;
        _vectors.Flush();
        _dirty = false;
    }

    public void Release(IReadOnlyList<long> vectorRows)
    {
        ArgumentNullException.ThrowIfNull(vectorRows);
        if (vectorRows.Count == 0) return;
        foreach (long row in vectorRows) _vectors.Tombstone(row);
        _dirty = true;
    }

    public void Dispose()
    {
        _vision?.Dispose(); _e5?.Dispose();
        _whisper?.Dispose(); _whisperHe?.Dispose();
        if (_ownsVectors) _vectors.Dispose();
    }

    /// <summary>Read inside one file. Only ever called for a kind <see cref="CanRead"/> said yes
    /// to - the gate is not repeated here, because two gates is two places to change.</summary>
    public KindResult Decode(ResultKind kind, string path, long bytes)
    {
        if (SizeGate(kind, bytes) is { } tooBig) return new KindResult([], tooBig);
        return kind switch
        {
            ResultKind.Document => Document(path),
            ResultKind.Photo => Photo(path),
            ResultKind.Video => Video(path),
            ResultKind.Audio => Audio(path),
            _ => new KindResult([], "not a content kind"),
        };
    }

    // ... Document, Photo, Audio, Video, Speech and the LoadBitmap helper, ported from
    // Indexer.cs:329-474.  Every _vectors.Append sets _dirty = true.
}
```

Write the five bodies out in full from the source. The four places the port changes:

- **`Document`** keeps its FTS segments whatever happens, and adds a vector only when `Installed.Has(Capability.Meaning)`. A document indexed without meaning is a complete, correct, findable document - so its segments carry `Vec = -1` and the row is `StateIndexed`, not skipped. It is free of charge and not free of consent: nothing reaches this arm until content indexing has been turned on. Meaning arriving later re-queues it with `Indexer.Recheck` and the second pass fills the column in. It keeps `DocText.CanExtract`'s `NoFormatReader` skip and the `NoText` skip, both of which are Plan 4's and both of which `CapabilityGate` now excludes from a re-queue.
- **`Photo`** runs the vision tower, then adds OCR text - `ImageText.Read` needs no model at all, so it runs whenever a photo is being opened - and embeds that text only when Meaning is installed.
- **`Audio`** reads the duration first - `Media.Duration` is a metadata call and does not decode anything - and asks `TranscribeLimit.Covers(_transcribeMinutes(), duration)`. A recording over the limit is `([], TooLong)`: skipped, with a reason of its own, and there is nothing else in a sound file to index. Under the limit it decodes at most `MaxDecodeSeconds` and transcribes.
- **`Video`** samples frames when Photos is installed, and transcribes the sound track when Speech is installed **and** `TranscribeLimit.Covers(...)` says the clip is short enough. The two are independent, and the return says which of the three outcomes happened:
  - frames read, sound track passed over for length: `new KindResult(frames, Skip: null, Note: TooLong)`. **Indexed, with a note.** The file really is searchable by what it looks like; the note records what was not heard, and raising the limit re-queues on exactly that.
  - nothing read at all, and the length was why: `new KindResult([], TooLong)`. Skipped.
  - nothing read at all for any other reason: `new KindResult([], "no frames")`.
- **`Speech`** takes its two models from `SpeechModels`, opens them through `Media.OpenWhisper`, passes them to `Media.Transcribe(samples, general, hebrew)` in that order, and windows the result with `Speech.Merge`. The general model is the first pass in every configuration; the fine-tune is loaded only when Hebrew is installed and is only ever handed in as the second argument.

`Decode` therefore returns a **recorded reason** rather than only a skip reason, and `KindResult` keeps the two apart: `Skip` sets the state to Skipped, `Note` leaves it Indexed, and both are written into `items.error`. That is the only reason the type has a third field - `--searchindex` reads the column for its counts and `CapabilityGate.ApplyLimit` re-queues on it, and neither cares which of the two produced the string.

- [ ] **Step 5: Change the indexer, and give it the gate**

In `src/Findra/Content/Indexer.cs`:

1. **`Loop` and `DrainOnce` take an `IDecoders`, and the overloads that do not are DELETED.** `Run` builds one with `Decoders.ForThisMachine(() => ...)`, reading the limit from `index:transcribeminutes` the same way the loop already reads `index:power` (`Indexer.cs:231`), holds it for the process, and disposes it last. The delegate, not a captured value: a change to the limit has to take effect on the next file rather than on the next restart. An overload that builds one for the caller is how a diagnostic silently acquires a writer on the real vector store; making that a compile error is worth more than a test.

2. **The gate goes in `Handle`, before the file is opened.** Replace the kind switch (`Indexer.cs:307-314`) with:

```csharp
            if (!_decoders.CanRead(item.Kind))
            {
                // Not an error and not a failure: a normal state this machine is in until the
                // capability that reads this kind arrives. The row stays exactly where
                // RequeueKinds can find it (spec §6).
                //
                // The return is captured here for the same reason as the other three, and the
                // case is real if rare: a machine that HAD a capability, indexed with it, and no
                // longer has the files - somebody cleared %LOCALAPPDATA%\Findra\models by hand.
                // The item drops back to Skipped, its segments go, and its vector rows have to go
                // with them or they answer queries for a file the index no longer describes.
                List<long> stale;
                using (var tx = _db.Begin())
                {
                    stale = _db.Upsert(item.Vol, item.Frn, item.Path, item.Kind, mtime, fi.Length,
                                       ContentDb.StateSkipped, Decoders.NoModel, [], tx);
                    _db.Dequeue(item.Id, tx);
                    tx.Commit();
                }
                _decoders.Release(stale);
                _done++;
                return "skipped";
            }

            KindResult decoded = _decoders.Decode(item.Kind, item.Path, fi.Length);
```

`NoDecoder` and `NoFormatDecoder` are deleted from `Indexer`; they live on `Decoders` as `NoModel` and `NoFormatReader`, **with the same strings**, so no row already on disk changes meaning. `Indexer.Recheck` stays exactly where it is - it is the constant Task 11 re-queues with.

3. **The two orderings.** The body after the gate becomes, in this order and no other:

```csharp
            _decoders.Flush();                     // before the commit that references the rows
            List<long> released;
            using (var tx = _db.Begin())
            {
                // The state follows Skip alone. Note goes into the same column and leaves the
                // row INDEXED: a long video whose frames were read is not a file Findra failed
                // to read, it is one it read incompletely, and the difference is visible in
                // every count --searchindex prints.
                int state = decoded.Skip is not null ? ContentDb.StateSkipped : ContentDb.StateIndexed;
                released = _db.Upsert(item.Vol, item.Frn, item.Path, item.Kind, mtime, fi.Length,
                                      state, decoded.Skip ?? decoded.Note, decoded.Segments, tx);
                _db.Dequeue(item.Id, tx);
                tx.Commit();
            }
            _decoders.Release(released);            // after it
```

4. **The third discarded return.** The failure path at `Indexer.cs:343` does `_ = _db.Upsert(..., StateFailed, ...)` inside its own transaction. It becomes:

```csharp
                List<long> dead;
                using (var tx = _db.Begin())
                {
                    dead = _db.Upsert(item.Vol, item.Frn, item.Path, item.Kind, mtime, 0, ContentDb.StateFailed,
                                      $"{ex.GetType().Name}: {ex.Message}", Array.Empty<ContentDb.Segment>(), tx);
                    _db.Dequeue(item.Id, tx);
                    tx.Commit();
                }
                _decoders.Release(dead);
```

A file that indexed once and later throws keeps its old vector rows for ever otherwise, and nothing will ever tombstone them because the item now says Failed. The comment beside each of the three saying this build has no vectors to release is deleted, because it is no longer true.

5. **`ContentDb.RecentSkips(int limit)`** is new, beside `RecentFailures` (`ContentDb.cs:733`) and identical to it except for `state=3`. `--searchindex`'s models section (Task 14) reads it, and so does `AFileSkippedForWantOfAModelSaysThatIsWhy`; without it the reason string has no reader at all, which is trap 11.

6. **`ContentDb.CountRecorded(string reason) : long`** is new beside it: `SELECT COUNT(*) FROM items WHERE error = $r`, with **no state clause**, because the reason it counts sits on an indexed row as readily as on a skipped one. Two readers - `--searchindex`'s `TooLongRecordings` line (Task 14 Step 2) and `ALongVideoWhoseFramesWereReadIsIndexedAndSaysWhatItDidNotHear`, which is the only way to see a note on a row that appears in neither recent-list.

- [ ] **Step 6: Hand every diagnostic a decoder set it owns**

Three call sites, and the two-argument overload is gone, so all three are compile errors until they are fixed. **All three files belong to this task's commit.**

- **`src/Findra/Diagnostics/SelfTest.cs:140`.** Its own comment says *"it never touches the real index - a self-test that left files in someone's search results is a self-test nobody runs twice"*, and a real `Decoders` would make that false. It already builds a temporary directory:

```csharp
                using var vectors = new VectorStore(Path.Combine(dir, "vectors.bin"), writer: true);
                using var decoders = new Decoders(CapabilitySet.Installed(), vectors);
                Indexer.DrainOnce(db, _ => { }, decoders);   // the default limit; the check reads a .txt
```

`CapabilitySet.Installed()` with no argument is deliberate: the check is worth more when it exercises whatever this machine actually has.

- **`src/Findra/Diagnostics/SearchBench.cs:536`.** The benchmark drains a synthetic corpus into a throwaway `bench.db` in a temp directory and then deletes it. With a real `Decoders` the segments go into `bench.db` while their **vectors go into the user's real `vectors.bin`** - orphan rows nothing will ever tombstone, in a file that grows for every synthetic document, for ever. It would also load ONNX and whisper into the benchmark process, which changes the number being measured.

```csharp
                // No capability, and a store in the benchmark's own directory. The number this
                // measures is extraction and full-text indexing; loading a model would change it,
                // and writing into the real vector store would leave rows behind that the
                // database referencing them is about to be deleted.
                using var benchVectors = new VectorStore(Path.Combine(dir, "vectors.bin"), writer: true);
                using var benchDecoders = new Decoders(CapabilitySet.None, benchVectors,
                                                       () => TranscribeLimit.Off);
                Indexer.DrainOnce(bench, _ => { }, benchDecoders);
```

The throughput row's corpus note (`SearchBench.cs:541`) gains "with no model loaded" so the fragment says what it measured.

- **`src/Findra/Diagnostics/SearchIndex.cs:200`.** This one drains the **real** index, so it wants the real decoders - but the running child already holds `vectors.bin` open, and a second writer is an `IOException` out of an unhandled path. Open it defensively:

```csharp
            IDecoders? decoders = null;
            // The limit comes off the same meta row the child reads, so a drain by hand obeys
            // the setting rather than a constant of its own.
            int Limit() => int.TryParse(db.Get("index:transcribeminutes"), NumberStyles.Integer,
                                        CultureInfo.InvariantCulture, out int m) ? m : TranscribeLimit.Default;
            try { decoders = Decoders.ForThisMachine(Limit); }
            catch (IOException ex)
            {
                Console.WriteLine($"  not draining: the indexer already has the vector store open ({ex.Message}).");
                Console.WriteLine("  the queued files stay queued; Findra's own indexer will take them.");
            }
            if (decoders is not null)
            {
                using (decoders) Indexer.DrainOnce(db, line => Console.WriteLine("  " + line), decoders);
            }
```

Queueing without draining is the honest outcome: the rows are in `pending` and the running child picks them up within a couple of seconds.

- [ ] **Step 7: Run it**

Run: `dotnet test --filter DecoderGateTests` - PASS, 16 test methods / 22 cases with the theory rows.
Then `dotnet test` - the whole suite. `ARequeuedSkippedFileIsOpenedAgainWhateverReasonTheRequeueGave` and `AKindWithNoDecoderIsSkippedWithAReasonRatherThanFailed` from Plan 4 must both still be green; if either went red, the gate changed a behaviour it was only supposed to make conditional.

Then the three diagnostics, because the point of Step 6 is that they still run:

```
findra --searchtest
findra --searchbench "$TEMP/bench.md"
findra --searchindex
```

and confirm afterwards that **`%LOCALAPPDATA%\Findra\index\vectors.bin` does not exist** (or, if it did before, that its length has not changed). That is the whole of C-4, checked directly.

- [ ] **Step 8: Prove the gate has teeth, in both directions**

Mutate `Decoders.Covers`, twice, and run the suite each time:

1. `ResultKind.Photo => true` - `APhotoIsOfferedToTheDecoderOnlyWhenTheModelsForItAreThere` fails on its first half (`Asked` is not empty), `SpeechAndPicturesAreGatedSeparatelyAndNotTogether` fails, `AMissingCapabilityIsNeverAFailure` fails.
2. `ResultKind.Photo => false` - the same first test fails on its **second** half (`Asked` is empty when Photos is installed).
3. `ResultKind.Video => installed.Has(Capability.Photos)` - `AVideoIsWorthOpeningForItsFramesOrForItsSound...` fails on the speech-only case, which is the one a reverse lookup gets wrong.

Revert each. **Report all three results.** A gate that only fails in one direction is half a gate, and the video case is the one an "obvious" refactor breaks.

- [ ] **Step 9: Commit**

```bash
git add src/Findra/Content/Decoders.cs src/Findra/Content/TranscribeLimit.cs \
        src/Findra/Content/Indexer.cs src/Findra/Content/ContentDb.cs \
        src/Findra/Diagnostics/SelfTest.cs src/Findra/Diagnostics/SearchIndex.cs \
        src/Findra/Diagnostics/SearchBench.cs \
        tests/Findra.Tests/Content/DecoderGateTests.cs tests/Findra.Tests/Content/TranscribeLimitTests.cs
git commit -m "Each kind is read only if its capability is here, and one number says how much of a recording is worth hearing"
```

---

## Task 10: Content search gains meaning

**Files:**
- Modify: `src/Findra/Content/ContentBranch.cs`, `src/Findra/Card/CardWindow.cs`
- Test: `tests/Findra.Tests/Content/SemanticBranchTests.cs`

**Interfaces:**
- Consumes: `VectorStore` (Task 6), `Capabilities.OfferFor` (Task 3), `ContentDb.SegmentsByVec` (`src/Findra/Content/ContentDb.cs:945`, written and unused since Plan 4).
- Produces:
  - `Findra.Semantic : IDisposable` - `sealed record Semantic(VectorStore Vectors, Func<string, float[]>? Text, Func<string, float[]>? Image)`, `static Semantic? Open(CapabilitySet, string? modelDir = null)`.
  - `Findra.ContentBranch` gains: `static SearchResults Search(ContentDb db, string raw, int max, SearchSort sort = SearchSort.Best, Func<string, bool, ResultMapper.Stat>? stat = null, Semantic? semantic = null, CapabilitySet installed = default)`, `const float PhotoFloor = 0.05f`, `const float PhotoSpan = 0.15f`, `const float PhotoCeiling = 0.92f`, `const float TextFloor = 0.78f`, `const float TextSpan = 0.12f`, `const float TextCeiling = 0.9f`, `const float BothBonus = 0.25f`, `static float PhotoScore(float cosine)`, `static float TextScore(float cosine)`.

**Why `Semantic` carries delegates rather than encoders:** the branch needs "encode this query as a picture" and "encode this query as text", and each may be absent. Delegates make the absence a `null` the branch already has to handle, and make every test in this file run against a vector store filled by hand, with no model on disk and no ONNX session. The two wrong implementations this catches - "an absent encoder throws" and "an absent encoder is indistinguishable from a present one that finds nothing" - are both invisible to a test that can only run with the models installed.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Content/SemanticBranchTests.cs`:

```csharp
using Findra;

public class SemanticBranchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-sem-" + Guid.NewGuid().ToString("N"));

    public SemanticBranchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ContentDb Open() => new(Path.Combine(_dir, "search.db"));
    private string VecPath => Path.Combine(_dir, "vectors.bin");

    private static float[] Axis(int i)
    {
        var v = new float[VectorStore.Dim];
        v[i] = 1f;
        return v;
    }

    /// <summary>An item with one segment pointing at one vector row.</summary>
    private static void Put(ContentDb db, ulong frn, string path, ResultKind kind, int segKind, long vec, string text)
    {
        using var tx = db.Begin();
        db.Upsert("C", frn, path, kind, 0, 100, ContentDb.StateIndexed, null,
                  [new ContentDb.Segment(segKind, -1, -1, vec, text)], tx);
        tx.Commit();
    }

    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    // ---- the score bands ----

    [Fact]
    public void ThePictureBandStretchesTheNarrowRangeTheModelActuallyUses()
    {
        // SigLIP-2 is a sigmoid model and its cosines sit LOW: unrelated is near 0 and
        // "obviously this" is around 0.10 to 0.12. Handing the raw cosine to the card - which
        // is what a straight port of the vector search does - scores every photo about 0.1, so
        // no photo ever ranks against anything and half of them tie.
        Assert.Equal(0f, ContentBranch.PhotoScore(0.05f), 3);
        Assert.Equal(0.92f, ContentBranch.PhotoScore(0.20f), 3);
        Assert.Equal(0.92f, ContentBranch.PhotoScore(0.90f), 3);   // clamped, never above the ceiling
        Assert.True(ContentBranch.PhotoScore(0.11f) > 0.3f);
    }

    [Fact]
    public void TheTextBandStartsWhereTheModelStopsSayingEverythingIsSimilar()
    {
        // e5 puts unrelated text near 0.75 and a paraphrase near 0.9. A floor at 0 would make
        // every document in the index a weak match for every query.
        Assert.Equal(0f, ContentBranch.TextScore(0.78f), 3);
        Assert.Equal(0.9f, ContentBranch.TextScore(0.90f), 3);
        Assert.Equal(0.9f, ContentBranch.TextScore(0.99f), 3);
    }

    // ---- meaning finds what words cannot ----

    [Fact]
    public void AFileFoundOnlyByMeaningIsInTheAnswer()
    {
        // The document never contains the word "lease", so the full-text branch cannot find it
        // and this test cannot pass by accident. A build with no vector branch returns nothing.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(3), ContentDb.SegText); w.Flush(); }
        Put(db, 1, Path.Combine(_dir, "tenancy.txt"), ResultKind.Document, ContentDb.SegText, 0,
            "the tenant shall pay the sum monthly in advance");

        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: _ => Axis(3), image: null);

        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: semantic,
                                               installed: Set(Capability.Meaning));

        Assert.Single(r.Rows);
        Assert.Equal("tenancy.txt", r.Rows[0].Name);
        Assert.Contains("like it", r.Rows[0].Why, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoMeaningModelTheSameQueryFindsNothingAndOffersTheDownload()
    {
        // Spec §6, both halves: an absent capability contributes no candidates and is not an
        // error, AND the card offers it. A branch that throws on a null encoder fails the
        // first; one that says nothing fails the second.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(3), ContentDb.SegText); w.Flush(); }
        Put(db, 1, Path.Combine(_dir, "tenancy.txt"), ResultKind.Document, ContentDb.SegText, 0,
            "the tenant shall pay the sum monthly in advance");

        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: null, image: null);

        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: semantic,
                                               installed: CapabilitySet.None);

        Assert.Empty(r.Rows);
        Assert.Contains("270 MB", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoSemanticStoreAtAllTheWordsStillAnswer()
    {
        // The free capability, through the same call. Passing null for the whole Semantic is
        // what the card does on a machine that took nothing, and it must be an ordinary answer.
        using ContentDb db = Open();
        Put(db, 1, Path.Combine(_dir, "notes.txt"), ResultKind.Document, ContentDb.SegText, -1,
            "the quarterly lease agreement and its deposit");

        SearchResults r = ContentBranch.Search(db, "deposit", 10, semantic: null, installed: CapabilitySet.None);

        Assert.Single(r.Rows);
    }

    [Fact]
    public void PicturesContributeNothingWhenTheirModelIsAbsentAndThatIsNotAnError()
    {
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(1), ContentDb.SegImage); w.Flush(); }
        Put(db, 1, Path.Combine(_dir, "holiday.jpg"), ResultKind.Photo, ContentDb.SegImage, 0, "");
        Put(db, 2, Path.Combine(_dir, "notes.txt"), ResultKind.Document, ContentDb.SegText, -1,
            "the quarterly lease agreement");

        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: _ => Axis(9), image: null);   // no picture encoder

        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: semantic,
                                               installed: Set(Capability.Meaning));

        Assert.Single(r.Rows);                       // the document, and no photo
        Assert.Equal("notes.txt", r.Rows[0].Name);
    }

    [Fact]
    public void APictureThatMerelyResemblesTheQueryALittleIsNotAMatch()
    {
        // Below the floor, and it must not appear at all. Without the floor every photo in the
        // library is a weak match for every query, which is the state the source's comment
        // describes measuring its way out of.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true))
        {
            var faint = new float[VectorStore.Dim];
            faint[0] = 0.03f; faint[1] = 0.9995f;          // ~0.03 against Axis(0)
            VectorStore.Normalise(faint);
            w.Append(faint, ContentDb.SegImage);
            w.Flush();
        }
        Put(db, 1, Path.Combine(_dir, "holiday.jpg"), ResultKind.Photo, ContentDb.SegImage, 0, "");

        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: null, image: _ => Axis(0));

        SearchResults r = ContentBranch.Search(db, "a sunset", 10, semantic: semantic,
                                               installed: Set(Capability.Photos));

        Assert.Empty(r.Rows);
    }

    [Fact]
    public void AFileThatMatchesBothWordsAndMeaningOutranksOneThatMatchesOnlyMeaning()
    {
        // Exact words are what the person typed. A file found both ways gets a bonus on top of
        // its vector score; dropping the bonus lets a paraphrase outrank the actual phrase.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true))
        {
            w.Append(Axis(3), ContentDb.SegText);      // row 0 - the one that says "lease"
            w.Append(Axis(3), ContentDb.SegText);      // row 1 - the paraphrase
            w.Flush();
        }
        Put(db, 1, Path.Combine(_dir, "both.txt"), ResultKind.Document, ContentDb.SegText, 0,
            "the lease agreement is signed");
        Put(db, 2, Path.Combine(_dir, "meaning-only.txt"), ResultKind.Document, ContentDb.SegText, 1,
            "the tenant shall pay monthly");

        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: _ => Axis(3), image: null);

        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: semantic,
                                               installed: Set(Capability.Meaning));

        Assert.Equal(2, r.Rows.Count);
        Assert.Equal("both.txt", r.Rows[0].Name);
        Assert.True(r.Rows[0].Score > r.Rows[1].Score);
    }

    [Fact]
    public void OneRowPerFileEvenWhenBothBranchesFindTheSameFile()
    {
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(3), ContentDb.SegText); w.Flush(); }
        Put(db, 1, Path.Combine(_dir, "both.txt"), ResultKind.Document, ContentDb.SegText, 0,
            "the lease agreement is signed");

        using var vectors = new VectorStore(VecPath);
        SearchResults r = ContentBranch.Search(db, "lease", 10,
                                               semantic: new Semantic(vectors, _ => Axis(3), null),
                                               installed: Set(Capability.Meaning));

        Assert.Single(r.Rows);
    }

    [Fact]
    public void AMomentInATranscriptCarriesTheTimeItWasSaid()
    {
        // A speech segment's answer has to be seekable: the row says when, and the card's stage
        // opens the file there. A transcript row with MomentSeconds of -1 is a search result
        // that makes somebody scrub through an hour of audio by hand.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(5), ContentDb.SegSpeech); w.Flush(); }
        using (var tx = db.Begin())
        {
            db.Upsert("C", 1, Path.Combine(_dir, "call.m4a"), ResultKind.Audio, 0, 100,
                      ContentDb.StateIndexed, null,
                      [new ContentDb.Segment(ContentDb.SegSpeech, 154.0, 172.0, 0, "we agreed on the deposit")], tx);
            tx.Commit();
        }

        using var vectors = new VectorStore(VecPath);
        SearchResults r = ContentBranch.Search(db, "deposit", 10,
                                               semantic: new Semantic(vectors, _ => Axis(5), null),
                                               installed: Set(Capability.Speech, Capability.Meaning));

        Assert.Single(r.Rows);
        Assert.Equal(154.0, r.Rows[0].MomentSeconds);
        Assert.Contains("2:34", r.Rows[0].Why, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGrammarStillAppliesToWhatMeaningFound()
    {
        // `lease ext:pdf` still means the pdf, on both branches. Skipping the filter on the
        // vector half makes the pill quietly ignore half the query language the card advertises.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true))
        {
            w.Append(Axis(3), ContentDb.SegText);
            w.Append(Axis(3), ContentDb.SegText);
            w.Flush();
        }
        Put(db, 1, Path.Combine(_dir, "a.txt"), ResultKind.Document, ContentDb.SegText, 0, "the tenant pays");
        Put(db, 2, Path.Combine(_dir, "b.pdf"), ResultKind.Document, ContentDb.SegText, 1, "the tenant pays");

        using var vectors = new VectorStore(VecPath);
        SearchResults r = ContentBranch.Search(db, "lease ext:pdf", 10,
                                               semantic: new Semantic(vectors, _ => Axis(3), null),
                                               installed: Set(Capability.Meaning));

        Assert.Single(r.Rows);
        Assert.Equal("b.pdf", r.Rows[0].Name);
    }

    [Fact]
    public void AnEmptyIndexStillSaysSoRatherThanOfferingADownload()
    {
        // Two different notes, and the wrong one is a lie. "Nothing indexed yet" is about the
        // machine; "this needs 270 MB" is about a capability. An index with nothing in it must
        // not be explained by a missing model.
        using ContentDb db = Open();
        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: null, installed: CapabilitySet.None);

        Assert.Empty(r.Rows);
        Assert.Contains("Nothing indexed yet", r.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("MB", r.Note, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter SemanticBranchTests`
Expected: FAIL - `Semantic` does not exist and `ContentBranch.Search` has no `semantic` or `installed` parameter.

- [ ] **Step 3: Write `Semantic` and the branch**

Add to `src/Findra/Content/ContentBranch.cs`:

```csharp
/// <summary>
/// The query side of the model-backed capabilities: a vector store, and the two ways of turning
/// what somebody typed into a vector. Either encoder may be null, and null means "that capability
/// is not installed" - which contributes no candidates and is not an error (spec §6).
///
/// <para>Delegates rather than encoder objects, so the branch's rules can be tested against a
/// vector store filled by hand with no model on disk. Absence being a null the branch already
/// handles is what makes "an absent capability is silent" a property of the code rather than a
/// thing to remember.</para>
/// </summary>
public sealed class Semantic(VectorStore vectors, Func<string, float[]>? text, Func<string, float[]>? image,
                            params IDisposable[] owned) : IDisposable
{
    public VectorStore Vectors { get; } = vectors;
    public Func<string, float[]>? Text { get; } = text;
    public Func<string, float[]>? Image { get; } = image;

    /// <summary>Disposes only what it was HANDED to own. A store the caller opened and a store
    /// <see cref="Open"/> opened are two different lifetimes, and a type that guesses gets one of
    /// them wrong - which is how a test's store gets closed under it, or how two encoders holding
    /// a GPU device are leaked for the life of the process.</summary>
    public void Dispose() { foreach (IDisposable d in owned) d.Dispose(); }

    /// <summary>What this machine can ask, given what is installed. Null when nothing is - the
    /// card then calls the branch with no semantic half at all, which is the ordinary state of a
    /// machine that took the "Just names" preset.</summary>
    public static Semantic? Open(CapabilitySet installed, string? modelDir = null)
    {
        if (!installed.Has(Capability.Photos) && !installed.Has(Capability.Meaning)) return null;
        var store = new VectorStore();
        var own = new List<IDisposable> { store };
        Func<string, float[]>? asText = null, asImage = null;
        try
        {
            if (installed.Has(Capability.Meaning))
            {
                var e5 = new E5Encoder(wantAccelerator: false, modelDir);
                own.Add(e5);
                asText = e5.EncodeQuery;
            }
            if (installed.Has(Capability.Photos))
            {
                var clip = new ClipTextEncoder(wantAccelerator: false, modelDir);
                own.Add(clip);
                asImage = clip.Encode;
            }
        }
        catch (Exception ex)
        {
            // A model that is on disk and will not load is the one case where an absent
            // capability IS worth a log line - it is not the normal state, it is a broken file.
            // It is still not an error the user has to acknowledge: whatever loaded stays.
            Log.Error("models", "a query encoder would not load - that capability is off for this session", ex);
        }
        if (asText is null && asImage is null)
        {
            foreach (IDisposable d in own) d.Dispose();
            return null;
        }
        return new Semantic(store, asText, asImage, [.. own]);
    }
}
```

Then, in `ContentBranch`:

```csharp
    /// <summary>Where a picture stops being unrelated. SigLIP-2 is a sigmoid model and its
    /// cosines sit LOW - unrelated is near 0 and "obviously this" is 0.10 to 0.12, measured on a
    /// real library. Do not compare these numbers to another model's.</summary>
    public const float PhotoFloor = 0.05f, PhotoSpan = 0.15f, PhotoCeiling = 0.92f;

    /// <summary>e5 puts unrelated text near 0.75 and a paraphrase near 0.9, so the interesting
    /// range is narrow and high. A floor of 0 would make every document a weak match for
    /// everything.</summary>
    public const float TextFloor = 0.78f, TextSpan = 0.12f, TextCeiling = 0.9f;

    /// <summary>What a file found by BOTH its words and its meaning gains. Exact words are what
    /// the person typed; without this a paraphrase can outrank the actual phrase.</summary>
    public const float BothBonus = 0.25f;

    public static float PhotoScore(float cosine)
        => Math.Clamp((cosine - PhotoFloor) / PhotoSpan, 0f, 1f) * PhotoCeiling;

    public static float TextScore(float cosine)
        => Math.Clamp((cosine - TextFloor) / TextSpan, 0f, 1f) * TextCeiling;
```

`Search` gains the two parameters and one extra pass, ported from `ContentSearch.Search` (`ContentSearch.cs:69-134`), *before* the existing FTS loop:

- If `semantic?.Image` is non-null, encode the query, `Vectors.Search(v, max * 2, [SegImage, SegFrame])`, drop anything below `PhotoFloor`, look the rows up with `db.SegmentsByVec` and offer each as a row scored by `PhotoScore`.
- If `semantic?.Text` is non-null, the same over `[SegText, SegSpeech]` with `TextFloor` and `TextScore`.
- **Each branch says how it found the file, and the four strings are fixed here** rather than left for an implementer to invent - `AFileFoundOnlyByMeaningIsInTheAnswer` asserts on one of them and `AMomentInATranscriptCarriesTheTimeItWasSaid` on another, so a guess makes a test fail for a reason that is not about the branch. `ContentBranch.ToResult` grows the two new cases beside the two it has:

  | Segment | Found by | `Why` |
  |---|---|---|
  | `SegImage` | the picture encoder | `"looks like it"` |
  | `SegFrame` | the picture encoder | `$"a moment at {Clock(t0)} looks like it"` |
  | `SegText` | the text encoder | `"says something like it"` |
  | `SegSpeech` | the text encoder | `$"said around {Clock(t0)}"` |
  | `SegText` | full text | `"contains the words"` (unchanged) |
  | `SegSpeech` | full text | `$"said at {Clock(t0)}"` (unchanged) |

  "Around" versus "at" is the honest distinction: a vector hit is a window that resembles the query, and a full-text hit is the word itself.
- The FTS loop then runs as it does today, except that a path already offered by a vector hit **has its existing offer's score raised** to `Math.Min(1f, existing + BothBonus)` rather than being replaced by a fresh `WordScore` row. Bumping rather than replacing is not a style choice: the vector offer for a transcript carries `MomentSeconds` and its own `Why`, and replacing it loses the timestamp the card seeks to. `AMomentInATranscriptCarriesTheTimeItWasSaid` is the test.
- `q.Allows` applies to every candidate from every branch, and the per-path dedupe keeps the best-scoring offer rather than the first.
- The note: `rows.Count == 0 && db.IndexedCount() == 0` still gives "Nothing indexed yet"; otherwise, when `rows.Count == 0` and `Capabilities.OfferFor(q, installed)` is non-null, the note is the offer's text. An index with nothing in it is explained by the index, never by a missing model.

**Do not rescale `WordScore`.** It sits at 0.86, just under a perfect name match, and the two semantic ceilings (0.92 and 0.9) were chosen against it. Task 9 of Plan 4 put the bm25 rank into the score as `RankStep`; that stays, and the semantic rows carry no rank step because their own score already orders them.

- [ ] **Step 4: Wire the card**

In `src/Findra/Card/CardWindow.cs`:

- The window takes a `Semantic?` beside the `ContentDb?` it already takes, created once by the shell in `App.axaml.cs` and lent to every card exactly as `_cardStore` is. It is **not** created per card: an encoder is a hundred milliseconds and a hundred megabytes.
- `ContentOnce` (`CardWindow.cs:712`) passes it plus `CapabilitySet.Installed()` into `ContentBranch.Search`. Read the installed set once when the shell starts, not per keystroke.
- The `NoContentIndex` note is unchanged - it is about the database, not about a model.

- [ ] **Step 5: Run it**

Run: `dotnet test --filter SemanticBranchTests` - PASS, 12 tests.
Then `dotnet test` - the whole suite; Plan 4's `ContentBranchTests` must all still be green, because every one of them calls the overload with no semantic half and that path is unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/Findra/Content/ContentBranch.cs src/Findra/Card/CardWindow.cs \
        tests/Findra.Tests/Content/SemanticBranchTests.cs
git commit -m "Content search answers by meaning too, and says nothing when it cannot"
```

---

## Task 11: What a new capability or a raised limit re-queues, and what it leaves alone

Three things the interface decides and records: which capabilities have had their backlog cleared,
whether content indexing has been asked for at all, and how long a recording is worth transcribing.
They are one task because they are one connection and one place - the interface's writer, before
the content loop exists.

**Files:**
- Create: `src/Findra/Models/CapabilityGate.cs`
- Modify: `src/Findra/Content/ContentDb.cs`, `src/Findra/Content/IndexStatus.cs`, `src/Findra/App/Config.cs`, `src/Findra/App/App.axaml.cs`
- Test: `tests/Findra.Tests/Models/CapabilityGateTests.cs`, `tests/Findra.Tests/App/ConfigTests.cs` (*modify*), `tests/Findra.Tests/Content/IndexStatusTests.cs` (*modify*)

**Interfaces:**
- Consumes: `Capabilities.KindsCovered`, `Capabilities.Title` (Task 3); `Decoders.TooLarge`, `Decoders.NoText` (Task 9); `Indexer.Recheck` (`src/Findra/Content/Indexer.cs:61`); `ContentDb.RequeueKinds` (`src/Findra/Content/ContentDb.cs:1054`).
- Produces:
  - `Findra.Requeue` - `readonly record struct Requeue(Capability Capability, int[] Kinds, string Stamp, string Why)`.
  - `Findra.CapabilityGate` - `const string StampPrefix = "models:cap:"`, `const string LimitKey = "models:limit:transcribe"`, `static string Family(Capability)`, `static string CurrentVersion(string family)`, `static string StampFor(Capability)`, `static IReadOnlyList<Requeue> Plan(CapabilitySet installed, IReadOnlyDictionary<Capability, string> stamps)`, `static IReadOnlyDictionary<Capability, string> StampsIn(ContentDb)`, `static Requeue? PlanForLimit(int wasMinutes, int nowMinutes)`, `static int Apply(ContentDb, IReadOnlyList<Requeue>)`, `static int ApplyLimit(ContentDb, int nowMinutes)`.
  - `ContentDb.RequeueKinds` gains an overload: `int RequeueKinds(int[] kinds, string reason, IReadOnlyList<string>? notBecause = null, IReadOnlyList<string>? onlyBecause = null)`, and the existing one is guarded against an empty `kinds`.
  - `Config.IndexContent : bool` (default **false**) replaces `Config.IndexPaused`; `Config.TranscribeMinutes : int` (default `TranscribeLimit.Default`).
  - `IndexStatus.Line` gains a `bool contentEnabled` parameter.

### The two mistakes this task exists to not make

**The re-queue reason must be `Indexer.Recheck`. Anything else is a no-op for three of the four capabilities.**

`src/Findra/Content/Indexer.cs:298-300` reads:

```csharp
if (item.Reason != Recheck
    && _db.StateOf(item.Vol, item.Frn) != ContentDb.StateSkipped
    && _db.IsCurrent(item.Vol, item.Frn, mtime))
{ ...Dequeue...; return "current"; }
```

A document already read by Plan 4 is `StateIndexed`, its modification time has not moved, and a free-text reason like `"meaning in documents is now installed"` is not `Recheck`. **Every one of those rows is dequeued untouched.** The log would say twelve thousand files queued, `--searchindex` would show the queue draining at full speed, and not one embedding would be written. The same holds for **Hebrew after Speech** (audio and video are already `StateIndexed` from the speech pass, so the second pass never runs on anything already transcribed) and for **any future `CurrentVersion` bump**, which is the entire purpose of the version stamp.

Only *Photos* works with a free-text reason, and only by accident: photos happen to be `StateSkipped` today, and the skipped clause reopens them whatever the reason says.

`Recheck`'s own doc comment already says exactly this - *"the bytes did not change, what Findra can do with them did"* - so the fix is to use the constant that was written for this, and to give `Requeue` a separate `Why` for the log and the report. The two tests that catch it are `EveryRequeueCarriesTheReasonTheIndexerReopensAnIndexedFileFor` and, end to end, `ADocumentAlreadyIndexedIsOpenedAgainWhenMeaningArrives`.

**The stamp must be per capability, not per model family.**

Speech and Hebrew and Meaning all embed with e5, so a stamp keyed on the *family* conflates two different facts: which embedding-space version is on disk, and which capabilities have had their backlog cleared. Walk the ordinary path with a family stamp: a user takes **Recommended** = {Photos, Meaning}, the gate queues photos and documents and stamps `siglip` and `e5`. Later the same user adds **Speech**. Both families are already at the current version, the plan comes back **empty**, and every audio file on the disk stays `StateSkipped` for ever - nothing short of the file being modified will ever pick it up.

So the key is `models:cap:<capability>` and the **value carries the family version**, `"<family>@<version>"`. A capability installed second has no stamp and clears its own backlog; a version bump changes the value for every capability in that family and clears all of theirs. `ACapabilityAddedAfterAnotherInTheSameFamilyStillClearsItsBacklog` is the test.

**Content indexing is off until asked for, and one switch says so.**

Spec §6: reading inside files walks every drive and runs for hours on a large disk, so Findra does not start it on its own - names are always on, content is not, **including the free document text**. `Config.IndexPaused` (default false, meaning "run") becomes `Config.IndexContent` (default **false**, meaning "do not run"): the same single bit, better polarity, better default, and it is written to the same `index:paused` meta row so the child, `IndexStatus` and `--searchindex` need no new mechanism.

**This silently stops an install that was already indexing, and that is accepted rather than overlooked.** An existing `config.json` carries `"indexPaused": false` and no `indexContent`, so on the next launch `IndexContent` takes its default of `false` and the queue stops moving. Nothing is lost - the index, the queue and every stamp survive, and `--content on` resumes exactly where it stopped - and there is no released version to upgrade from, so the only machines affected are this one and any other running a development build. **No `Config.Load` migration is written**, deliberately: a migration that read the old key would have to decide that somebody who never chose anything had chosen to index, which is the opposite of what the spec change is for. The status line is what makes it visible rather than mysterious, which is why Step 6 exists. If Findra ever ships before this lands, revisit it.

**One switch, not two.** A separate "enabled" bit beside a "paused" bit is two settings that can disagree, and the interface has no honest sentence for the disagreement. What the interface says is *derived* instead, from a fact the index already records:

| State | What the line says |
|---|---|
| off, and nothing has ever been read | "searching inside files is off - turn it on to read what is in them" |
| off, and something has | "searching inside files is off · N files already read" |
| on, backlog, no child running | the existing "N waiting - indexing is paused while Findra is closed" |
| on, and working | the existing progress line |

**Raising the transcription limit re-queues exactly the recordings it newly covers.**

The limit is a setting a user can change, so a change to it owes the index work in the same way a new capability does - but a *much narrower* piece of work. Re-queueing everything Speech covers would re-transcribe every recording already done; re-queueing nothing would leave the newly-covered ones invisible for ever. So the filter runs the other way round from the capability one: **only** rows whose recorded reason is `Decoders.TooLong`, which is exactly the set and nothing else. That is the fifth meaning of `StateSkipped` earning its own constant.

Lowering the limit is deliberately not symmetrical: it applies to files not yet read, and transcripts already written stay until the file changes. Deleting work somebody has already paid for, because they moved a slider down, is worse than keeping it.

**Two smaller things this task also fixes.** `RequeueKinds([], reason)` builds `IN ()` by string concatenation. MEASURED: the bundled SQLite accepts that as an empty list and matches nothing, so the danger is not the syntax - it is the transaction the method opens, which is a nested-transaction `InvalidOperationException` in a caller already holding a scope. It is guarded, and the guard returns before the database is touched at all. And `StateSkipped` means five different things, so `RequeueKinds([Document], ...)` would pick up every under-forty-character and every over-two-hundred-megabyte document alongside the formats a new reader can actually help; the two reason filters are how each caller says which set it means.

### What replaces the source's re-embed pass, and what it costs

The source had a `Migrate` that re-embedded stored text in place (`Indexer.cs:110-141`) rather than re-reading the files, specifically to avoid re-**transcribing** - whisper had spent hours. This plan's do-not-port list drops it, and the queue replaces it: a re-queued row is re-opened, re-extracted and re-embedded by the ordinary path.

That is a deliberate trade and it is worth writing down.

- **For documents it costs almost nothing.** Re-extracting a PDF is seconds and the embedding - the expensive half - happens either way. One mechanism instead of two, and no second code path over `TextSegments()`/`UpdateVec()` to keep correct.
- **For Hebrew it costs a re-transcription, and there is no way around it.** The fine-tune has to hear the audio; a stored English transcript cannot be converted into a Hebrew one. Every audio and video file is re-run through turbo for detection and the Hebrew ones through the fine-tune, which is what spec §6 describes the mechanism as doing.
- **For a future e5 version bump it would cost a re-transcription that the source avoided.** That is the one case where the queue is worse than a re-embed pass, and it does not arise in this plan: `CurrentVersion` is `"1"` for both families and nothing bumps it. `ContentDb.TextSegments()` and `UpdateVec()` stay as Plan 4 left them - written, tested and unused - and they are the seam the plan that first bumps a version should fill. Say so there rather than building it here for a caller that does not exist.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Models/CapabilityGateTests.cs`:

```csharp
using Findra;

public class CapabilityGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-gate5-" + Guid.NewGuid().ToString("N"));

    public CapabilityGateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ContentDb Open() => new(Path.Combine(_dir, "search.db"));
    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    /// <summary>Stamps that say "these capabilities have already had their backlog cleared, at
    /// the current version of their family".</summary>
    private static Dictionary<Capability, string> Done(params Capability[] caps)
    {
        var d = new Dictionary<Capability, string>();
        foreach (Capability c in caps) d[c] = CapabilityGate.StampFor(c);
        return d;
    }

    /// <summary>
    /// One item in the index. <paramref name="mtime"/> defaults to 0, which is fine for every test
    /// here that only inspects a plan or a queue - and is WRONG for any test that drains.
    ///
    /// <para><see cref="ContentDb.IsCurrent"/> compares the stored mtime against the file's real
    /// one, so a stored 0 makes the freshness check false and the indexer opens the row whatever
    /// its reason says. A draining test built on the default would pass with a free-text reason
    /// and prove nothing at all - which is exactly what this file's most important test did in an
    /// earlier draft. If you drain, pass <c>new FileInfo(path).LastWriteTimeUtc.Ticks</c>.</para>
    /// </summary>
    private static void Item(ContentDb db, ulong frn, ResultKind kind, int state, string? error, string path,
                             long mtime = 0)
    {
        using var tx = db.Begin();
        db.Upsert("C", frn, path, kind, mtime, 10, state, error, [], tx);
        tx.Commit();
    }

    private string File_(string name, string text = "the tenant shall pay monthly in advance")
    {
        string p = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    // ---- the reason, which is the whole of C-1 ----

    [Fact]
    public void EveryRequeueCarriesTheReasonTheIndexerReopensAnIndexedFileFor()
    {
        // Indexer.cs:298-300 dequeues a row untouched when the reason is not Recheck, the row is
        // not Skipped, and the bytes have not moved - which describes every document Plan 4 has
        // already read. A free-text reason therefore queues everything and does nothing.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Document, ContentDb.StateIndexed, null, File_("a.txt"));

        CapabilityGate.Apply(db, CapabilityGate.Plan(Set(Capability.Meaning), []));

        ContentDb.Pending row = Assert.Single(db.PendingRows());
        Assert.Equal(Indexer.Recheck, row.Reason);
    }

    [Fact]
    public void ADocumentAlreadyIndexedIsOpenedAgainWhenMeaningArrives()
    {
        // The end-to-end mirror of the skipped-file test, and the one the first draft of this
        // plan did not have. It drains a re-queued StateIndexed row whose bytes have not changed
        // and asserts the decoder was ASKED. With a free-text reason it is dequeued "current",
        // Asked is empty, and nothing anywhere reports a problem.
        //
        // THE REAL MODIFICATION TIME IS THE WHOLE FIXTURE. The freshness check is three clauses
        // AND-ed together, and this test is about the first of them; storing 0 falsifies the
        // third instead, so the row is opened whatever the reason says and the test passes
        // against the very bug it exists to catch. "The bytes have not changed" has to be true
        // on disk, not just in the sentence describing the test.
        using ContentDb db = Open();
        string doc = File_("contract.txt");
        Item(db, 1, ResultKind.Document, ContentDb.StateIndexed, null, doc,
             mtime: new FileInfo(doc).LastWriteTimeUtc.Ticks);

        Assert.Equal(1, CapabilityGate.Apply(db, CapabilityGate.Plan(Set(Capability.Meaning), [])));

        var d = new AskRecordingDecoders(Set(Capability.Meaning));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Equal([doc], d.Asked);
        Assert.Equal(0, db.PendingCount());
        Assert.Equal(ContentDb.StateIndexed, db.StateOf("C", 1));
    }

    [Fact]
    public void AnOrdinaryQueueEntryStillLeavesAnUnchangedFileAlone()
    {
        // The control, and it is not decoration: the cheap way to make the test above pass is to
        // delete the freshness check, and then every journal event re-reads a file whose bytes
        // did not change. Plan 4's AnIndexedFileWhoseBytesDidNotChangeIsStillDequeuedUntouched
        // covers the same rule from the other side and must also stay green.
        using ContentDb db = Open();
        string doc = File_("contract.txt");
        long mtime = new FileInfo(doc).LastWriteTimeUtc.Ticks;
        using (var tx = db.Begin())
        {
            db.Upsert("C", 1, doc, ResultKind.Document, mtime, 10, ContentDb.StateIndexed, null, [], tx);
            tx.Commit();
        }
        db.Enqueue("C", 1, doc, ResultKind.Document, "change");

        var d = new AskRecordingDecoders(Set(Capability.Meaning));
        Indexer.DrainOnce(db, _ => { }, d);

        Assert.Empty(d.Asked);
        Assert.Equal(0, db.PendingCount());
    }

    private sealed class AskRecordingDecoders(CapabilitySet installed) : IDecoders
    {
        public CapabilitySet Installed { get; } = installed;
        public List<string> Asked { get; } = [];
        public bool CanRead(ResultKind kind) => Decoders.Covers(kind, Installed);
        public KindResult Decode(ResultKind kind, string path, long bytes)
        {
            Asked.Add(path);
            return new KindResult([new ContentDb.Segment(ContentDb.SegText, -1, -1, -1, "words")], null);
        }
        public void Flush() { }
        public void Release(IReadOnlyList<long> rows) { }
        public void Dispose() { }
    }

    // ---- the plan ----

    [Fact]
    public void EnablingPicturesQueuesThePicturesAndNothingElse()
    {
        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(Set(Capability.Photos), []);

        Requeue r = Assert.Single(plan);
        Assert.Equal(Capability.Photos, r.Capability);
        Assert.Equal([(int)ResultKind.Photo, (int)ResultKind.Video], r.Kinds);
        Assert.DoesNotContain((int)ResultKind.Document, r.Kinds);
    }

    [Fact]
    public void ACapabilityWhoseBacklogIsAlreadyClearedQueuesNothing()
    {
        // The control that stops an unconditional plan. Without the stamp check, every launch
        // re-queues every photo on the disk, for ever - spec §2a's worst case, on a loop.
        Assert.Empty(CapabilityGate.Plan(Set(Capability.Photos), Done(Capability.Photos)));
    }

    [Fact]
    public void ACapabilityAddedAfterAnotherInTheSameFamilyStillClearsItsBacklog()
    {
        // C-3, and it is the ordinary path: somebody takes Recommended, and later adds Speech.
        // Speech, Meaning and Hebrew all embed with e5, so a stamp keyed on the model FAMILY is
        // already current and the plan comes back empty - every audio file on the disk stays
        // skipped for ever, and nothing short of the file being modified picks it up.
        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(
            Set(Capability.Photos, Capability.Meaning, Capability.Speech),
            Done(Capability.Photos, Capability.Meaning));

        Requeue r = Assert.Single(plan);
        Assert.Equal(Capability.Speech, r.Capability);
        Assert.Equal([(int)ResultKind.Audio, (int)ResultKind.Video], r.Kinds);
    }

    [Fact]
    public void AddingASecondCapabilityLeavesTheFirstAlone()
    {
        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(
            Set(Capability.Photos, Capability.Meaning), Done(Capability.Photos));

        Requeue r = Assert.Single(plan);
        Assert.Equal(Capability.Meaning, r.Capability);
        Assert.Equal([(int)ResultKind.Document], r.Kinds);
    }

    [Fact]
    public void AChangeToOneModelFamilysVersionDoesNotDisturbTheOther()
    {
        // The stamp's VALUE carries the family version, so bumping the picture space clears the
        // photo backlog and leaves documents alone. One version string for both families - which
        // is what the source keeps - re-reads every photo for a change to the document model.
        var stamps = Done(Capability.Photos, Capability.Meaning);
        stamps[Capability.Photos] = "siglip@0";              // an older picture space

        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(Set(Capability.Photos, Capability.Meaning), stamps);

        Requeue r = Assert.Single(plan);
        Assert.Equal(Capability.Photos, r.Capability);
    }

    [Fact]
    public void SpeechAndHebrewEachClearTheirOwnBacklogAndTheQueueIsNotDoubled()
    {
        // Both cover audio and video, so both appear in the plan - Hebrew's backlog is a real
        // and separate fact, and merging them would let one stamp discharge the other's debt.
        // The queue is keyed on (volume, frn), so two passes over the same rows is an upsert,
        // not a duplicate: three files, three pending rows.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.NoModel, File_("a.m4a"));
        Item(db, 2, ResultKind.Audio, ContentDb.StateSkipped, Decoders.NoModel, File_("b.m4a"));
        Item(db, 3, ResultKind.Video, ContentDb.StateSkipped, Decoders.NoModel, File_("c.mp4"));

        IReadOnlyList<Requeue> plan = CapabilityGate.Plan(
            Set(Capability.Meaning, Capability.Speech, Capability.Hebrew), Done(Capability.Meaning));

        Assert.Equal([Capability.Speech, Capability.Hebrew], plan.Select(r => r.Capability).ToArray());

        int queued = CapabilityGate.Apply(db, plan);

        Assert.Equal(3, db.PendingCount());
        // And the number it REPORTS is the number of rows that moved. Summing the two
        // RequeueKinds returns says six, because both entries cover the same three files and the
        // queue's UNIQUE(vol, frn) turns the second pass into an upsert. Six is what the log
        // line and --models' closing sentence would then tell somebody about a three-file queue.
        Assert.Equal(3, queued);
    }

    [Fact]
    public void NothingInstalledPlansNothing()
    {
        Assert.Empty(CapabilityGate.Plan(CapabilitySet.None, []));
    }

    // ---- applying it ----

    [Fact]
    public void ApplyingThePlanQueuesTheSkippedFilesAndRecordsThatItDid()
    {
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Photo, ContentDb.StateSkipped, Decoders.NoModel, File_("a.jpg"));
        Item(db, 2, ResultKind.Document, ContentDb.StateIndexed, null, File_("b.txt"));

        int n = CapabilityGate.Apply(db, CapabilityGate.Plan(Set(Capability.Photos), []));

        Assert.Equal(1, n);
        Assert.Equal(1, db.PendingCount());
        Assert.Equal(CapabilityGate.StampFor(Capability.Photos),
                     db.Get(CapabilityGate.StampPrefix + "photos"));
        Assert.Empty(CapabilityGate.Plan(Set(Capability.Photos), CapabilityGate.StampsIn(db)));
    }

    [Fact]
    public void ApplyingThePlanTouchesNoOtherProcessesMetaRows()
    {
        // The meta table has four writers with four prefixes, and reusing one is a collision
        // nothing would report. `models:` is this plan's, and nothing else may be written here.
        using ContentDb db = Open();
        db.Set("indexer:state", "idle");
        db.Set("index:paused", "0");
        db.Set("usn:C", "1 2");
        db.Set("schema", "1");

        CapabilityGate.Apply(db, CapabilityGate.Plan(Set(Capability.Photos), []));

        Assert.Equal("idle", db.Get("indexer:state"));
        Assert.Equal("0", db.Get("index:paused"));
        Assert.Equal("1 2", db.Get("usn:C"));
        Assert.Equal("1", db.Get("schema"));
        Assert.All(CapabilityGate.StampsIn(db).Keys,
                   c => Assert.NotNull(db.Get(CapabilityGate.StampPrefix + c.ToString().ToLowerInvariant())));
    }

    // ---- what RequeueKinds must and must not pick up ----

    [Fact]
    public void ARequeueForNoKindsIsNothingRatherThanACrash()
    {
        // The IN () clause is built by string concatenation, so an empty array emits `IN ()`.
        // MEASURED, not assumed: the bundled SQLite accepts that as an empty list and matches
        // nothing, so it is NOT the SqliteException it looks like. The danger is the transaction
        // below it - a nested-transaction InvalidOperationException in a caller that already
        // holds a scope - which is why the guard has to return before Begin.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Photo, ContentDb.StateSkipped, Decoders.NoModel, File_("a.jpg"));

        Assert.Equal(0, db.RequeueKinds([], Indexer.Recheck));
        Assert.Equal(0, db.PendingCount());
    }

    [Fact]
    public void TheDocumentRequeueLeavesAloneWhatNoModelCouldHelp()
    {
        // StateSkipped means four different things - no model for the kind, no reader for the
        // format, no text in it, too large. A new document model helps the first two and can do
        // nothing about the last two, and re-opening a 200 MB database dump on every install is
        // work with a guaranteed outcome.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Document, ContentDb.StateSkipped, Decoders.NoFormatReader, File_("a.rtf"));
        Item(db, 2, ResultKind.Document, ContentDb.StateSkipped, Decoders.TooLarge, File_("b.txt"));
        Item(db, 3, ResultKind.Document, ContentDb.StateSkipped, Decoders.NoText, File_("c.txt"));

        int n = db.RequeueKinds([(int)ResultKind.Document], Indexer.Recheck,
                                notBecause: [Decoders.TooLarge, Decoders.NoText]);

        Assert.Equal(1, n);
        Assert.EndsWith("a.rtf", db.PendingRows()[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExclusionOnlyAppliesToSkippedRowsAndNeverToIndexedOnes()
    {
        // An indexed row has no skip reason at all, and filtering on `error` would drop every
        // one of them - which would mean a new model never re-embeds anything already read, and
        // would hide C-1 behind a second bug with the same symptom.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Document, ContentDb.StateIndexed, null, File_("a.txt"));

        Assert.Equal(1, db.RequeueKinds([(int)ResultKind.Document], Indexer.Recheck,
                                        notBecause: [Decoders.TooLarge, Decoders.NoText]));
    }

    [Fact]
    public void AFileThatGenuinelyFailedIsStillNeverRetried()
    {
        // state IN (1, 3) - indexed and skipped, never failed. A file the decoder could not read
        // has not changed because a capability arrived, and retrying it on every install is a
        // loop with no exit.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Document, ContentDb.StateFailed, "PdfDocumentFormatException: broken xref", File_("a.pdf"));

        Assert.Equal(0, db.RequeueKinds([(int)ResultKind.Document], Indexer.Recheck, null));
    }

    // ---- the transcription limit ----

    [Fact]
    public void RaisingTheLimitQueuesOnlyTheRecordingsItNewlyCovers()
    {
        // The filter runs the OTHER way round from the capability one: only the rows recorded
        // TooLong, and nothing else. Re-queueing everything Speech covers re-transcribes every
        // recording already done - hours of somebody's machine for no new result.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, File_("long.m4a"));
        Item(db, 2, ResultKind.Audio, ContentDb.StateIndexed, null, File_("short.m4a"));
        Item(db, 3, ResultKind.Audio, ContentDb.StateSkipped, Decoders.NoModel, File_("nomodel.m4a"));
        Item(db, 4, ResultKind.Document, ContentDb.StateSkipped, Decoders.TooLarge, File_("huge.txt"));

        int n = CapabilityGate.ApplyLimit(db, 120);

        Assert.Equal(1, n);
        Assert.EndsWith("long.m4a", db.PendingRows()[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongVideoIndexedForItsFramesAloneIsQueuedAgainWhenTheLimitRises()
    {
        // A video over the limit with photos installed keeps its frames, so it is INDEXED
        // and carries TooLong as a note about what was left undone. A filter that looked at the
        // state rather than the recorded reason would miss every one of them.
        //
        // The row is built by hand here because this test is about the QUERY. That the product
        // really writes it is Task 9's
        // ALongVideoWhoseFramesWereReadIsIndexedAndSaysWhatItDidNotHear, which drives the same
        // state out of the real indexer - the two together are what stop this being a test of a
        // shape nothing produces.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Video, ContentDb.StateIndexed, Decoders.TooLong, File_("film.mp4"));

        Assert.Equal(1, CapabilityGate.ApplyLimit(db, TranscribeLimit.NoLimit));
        Assert.Equal(Indexer.Recheck, db.PendingRows()[0].Reason);
    }

    [Fact]
    public void LoweringTheLimitQueuesNothing()
    {
        // Deleting transcripts somebody already paid for, because they moved a slider down, is
        // worse than keeping them. The new limit applies to what has not been read yet.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, File_("long.m4a"));
        db.Set(CapabilityGate.LimitKey, "120");

        Assert.Equal(0, CapabilityGate.ApplyLimit(db, 5));
        Assert.Equal(0, db.PendingCount());
    }

    [Fact]
    public void TurningTranscriptionOffQueuesNothingEitherWay()
    {
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, File_("long.m4a"));
        db.Set(CapabilityGate.LimitKey, "5");

        Assert.Equal(0, CapabilityGate.ApplyLimit(db, TranscribeLimit.Off));
    }

    [Fact]
    public void AnUnchangedLimitQueuesNothingOnEveryLaunch()
    {
        // The control. Without the recorded value this runs on every start, and on a machine
        // with a large archive that is a re-transcription every time Findra opens.
        using ContentDb db = Open();
        Item(db, 1, ResultKind.Audio, ContentDb.StateSkipped, Decoders.TooLong, File_("long.m4a"));

        Assert.Equal(1, CapabilityGate.ApplyLimit(db, 120));
        Assert.Equal(0, CapabilityGate.ApplyLimit(db, 120));
    }

    [Fact]
    public void NoLimitIsHigherThanEveryNumberAndNotLowerThanAllOfThem()
    {
        // The one place the sign convention bites: -1 means "no limit", so it must compare as
        // MORE permissive than 120 and not less. A plain `now > was` gets this exactly backwards
        // and "no limit" then queues nothing at all.
        Assert.NotNull(CapabilityGate.PlanForLimit(120, TranscribeLimit.NoLimit));
        Assert.Null(CapabilityGate.PlanForLimit(TranscribeLimit.NoLimit, 120));
        Assert.NotNull(CapabilityGate.PlanForLimit(TranscribeLimit.Off, 5));
        Assert.Null(CapabilityGate.PlanForLimit(5, TranscribeLimit.Off));
    }

    // ---- the schema, which this plan does not move ----

    [Fact]
    public void AFreshInstallRunsNoSchemaMigrationOverAnEmptyIndex()
    {
        // Plan 4 left `Migrations` empty and this plan does not add to it - but the guard has to
        // hold before somebody does. A brand-new database has never been written by an older
        // build, so there is nothing to migrate it FROM, and `OpenedFromSchema` plus
        // `MigrationsRun` are what make "treated as current" and "treated as version zero"
        // distinguishable at all.
        var step = new ContentDb.Migration(ContentDb.SchemaVersion, [(int)ResultKind.Photo], "a test step");
        using var db = new ContentDb(Path.Combine(_dir, "fresh.db"), migrations: [step]);

        Assert.Equal(ContentDb.SchemaVersion, db.OpenedFromSchema);
        Assert.Empty(db.MigrationsRun);
    }

    [Fact]
    public void ThisPlanAddsNoSchemaMigration()
    {
        // Written down as an assertion rather than as a sentence in a document: nothing on disk
        // changes meaning in this plan. The vector column, the segment kinds and the queue are
        // all exactly what Plan 4 defined; what changes is only which of them get filled in.
        // A later plan that needs a step meets this test, and with it the three traps that go
        // live the moment `Migrations` is non-empty.
        Assert.Empty(ContentDb.Migrations);
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter CapabilityGateTests`
Expected: FAIL - `CapabilityGate` and `Requeue` do not exist; `RequeueKinds` has no three-argument overload; `ARequeueForNoKindsIsNothingRatherThanACrash` fails on its third assertion, which is the defect it names: the empty `IN ()` matches nothing, so the guard has to be proved by the transaction it does not open rather than by an exception it does not throw.

- [ ] **Step 3: Guard and extend `RequeueKinds`**

In `src/Findra/Content/ContentDb.cs:1054`, keep the existing method as a two-argument overload that forwards, and write:

```csharp
    /// <summary>Queue every item of the given kinds again, because something that can now read
    /// them arrived. Nothing is deleted here: the indexer replaces an item's segments when it
    /// gets to the row, so removing them up front would only blank the index for the length of
    /// the re-run. Returns how many rows were queued.
    ///
    /// <para><paramref name="reason"/> is not decoration. <see cref="Indexer"/> dequeues a row
    /// untouched unless the reason is <see cref="Indexer.Recheck"/>, the row is Skipped, or the
    /// file's bytes have moved - so a caller that invents a friendly sentence here queues
    /// thousands of files and re-reads none of them.</para>
    ///
    /// <para><paramref name="notBecause"/> filters the SKIPPED rows by the reason they were
    /// skipped for, and only those - an indexed row has no reason at all and must never be
    /// excluded by this. The recorded reason carries five different meanings ("no decoder for
    /// this kind", "no decoder for this format", "no text", "too large", "longer than the
    /// transcription limit") and a new model can do nothing about the middle three, so re-opening
    /// a 200 MB database dump on every install is work with a guaranteed outcome.</para>
    ///
    /// <para><paramref name="onlyBecause"/> is the mirror, and the narrow one: exactly the rows
    /// carrying one of these reasons, whatever their state. Raising the transcription limit uses
    /// it, and it has to reach an INDEXED video that was read for its frames and carries
    /// "longer than the transcription limit" as a note about the sound track nobody heard.</para>
    ///
    /// <para>An empty <paramref name="kinds"/> queues nothing and does not touch the database.
    /// The clause below is built by concatenation, so an empty array would emit <c>IN ()</c> -
    /// a syntax error thrown out of a background loop where nobody would catch it.</para>
    /// </summary>
    public int RequeueKinds(int[] kinds, string reason,
                            IReadOnlyList<string>? notBecause = null,
                            IReadOnlyList<string>? onlyBecause = null)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Length == 0) return 0;
        ...
        // state IN (1, 3): indexed AND skipped. [the existing comment stays verbatim]
        string filter = "";
        if (onlyBecause is { Count: > 0 })
        {
            // The narrow direction: exactly the rows carrying one of these reasons. Raising the
            // transcription limit uses it, because re-queueing everything Speech covers would
            // re-transcribe every recording already done.
            var named = onlyBecause.Select((_, i) => $"$o{i.ToString(CultureInfo.InvariantCulture)}");
            filter = $" AND error IN ({string.Join(",", named)})";
            for (int i = 0; i < onlyBecause.Count; i++)
                cmd.Parameters.AddWithValue($"$o{i.ToString(CultureInfo.InvariantCulture)}", onlyBecause[i]);
        }
        else if (notBecause is { Count: > 0 })
        {
            var named = notBecause.Select((_, i) => $"$e{i.ToString(CultureInfo.InvariantCulture)}");
            filter = $" AND (state <> {StateSkipped.ToString(CultureInfo.InvariantCulture)} " +
                     $"OR error IS NULL OR error NOT IN ({string.Join(",", named)}))";
            for (int i = 0; i < notBecause.Count; i++)
                cmd.Parameters.AddWithValue($"$e{i.ToString(CultureInfo.InvariantCulture)}", notBecause[i]);
        }
        cmd.CommandText = $"SELECT vol, frn, path, kind FROM items WHERE state IN (1, 3) " +
                          $"AND kind IN ({string.Join(",", kinds)}){filter}";
        ...
    }
```

The two filters are mutually exclusive and `onlyBecause` wins, because a caller that passes both
has not decided which set it means. Both read the `error` column rather than the state, which is
what lets `onlyBecause` reach an **indexed** video that carries `TooLong` as a note - and it is
why `notBecause` has to keep its `state <> StateSkipped OR error IS NULL` guard, or every indexed
row's NULL error would exclude it.

The kinds themselves stay concatenated - they are `int`s from an enum this code owns - but every reason string is a parameter, because a skip reason is free text that has come from an exception message.

- [ ] **Step 4: Write `CapabilityGate`**

Create `src/Findra/Models/CapabilityGate.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Findra;

/// <summary>One capability's worth of re-queueing: which kinds, the stamp to write once it is
/// done, and a sentence for the log. <c>Why</c> is NOT the queue's reason - the queue's reason is
/// always <see cref="Indexer.Recheck"/>, because that is the only string the indexer's freshness
/// check will reopen an already-indexed file for.</summary>
public readonly record struct Requeue(Capability Capability, int[] Kinds, string Stamp, string Why);

/// <summary>
/// What a newly-installed capability owes the index, and the record that stops it being owed
/// twice.
///
/// <para>Spec §6: enabling a capability later re-queues ONLY the files it covers, and nothing
/// already indexed is redone.</para>
///
/// <para>The record is keyed per CAPABILITY and its value carries the model family's version.
/// Keying it on the family alone conflates two facts - which embedding space is on disk, and
/// whose backlog has been cleared - and the ordinary path breaks on it: Speech, Meaning and
/// Hebrew all embed with e5, so somebody who takes Recommended and later adds Speech finds the
/// family already stamped, gets an empty plan, and every audio file stays skipped for ever.</para>
///
/// <para>This runs in the INTERFACE, on the writer connection the queue feeder owns, ONCE at
/// startup and before the content loop begins. It is deliberately not the child's: the child
/// would have to write a fourth namespace into the meta table and would race the feeder for the
/// writer.</para>
/// </summary>
public static class CapabilityGate
{
    /// <summary>The meta prefix this plan owns. `indexer:` is the child's, `index:` the content
    /// loop's, and the bare keys the queue feeder's; reusing any of them is a collision nothing
    /// would report. The key is `models:cap:photos`, `models:cap:meaning`, and so on.</summary>
    public const string StampPrefix = "models:cap:";

    /// <summary>The version of each embedding space. Bumped when the MODEL changes, so every
    /// vector already stored points somewhere that no longer exists - not when this code
    /// changes. Nothing in this plan bumps either.</summary>
    public static string CurrentVersion(string family) => family switch
    {
        "siglip" => "1",
        "e5" => "1",
        _ => "0",
    };

    /// <summary>Which family's space a capability's vectors live in. Speech and Hebrew embed
    /// their transcripts with the same text model documents use, which is why they share - and
    /// why the stamp cannot be keyed on this.</summary>
    public static string Family(Capability c) => c switch
    {
        Capability.Photos => "siglip",
        _ => "e5",
    };

    /// <summary>What a cleared backlog looks like for one capability: its family and that
    /// family's current version. A bump changes the value for every capability in the family,
    /// which clears all of their backlogs and none of the other family's.</summary>
    public static string StampFor(Capability c) => Family(c) + "@" + CurrentVersion(Family(c));

    private static string Key(Capability c) => StampPrefix + c.ToString().ToLowerInvariant();

    /// <summary>What is owed, given what is installed and what has already been done. One entry
    /// per capability, in <see cref="Capabilities.All"/> order.</summary>
    public static IReadOnlyList<Requeue> Plan(CapabilitySet installed, IReadOnlyDictionary<Capability, string> stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        var owed = new List<Requeue>();
        foreach (Capability c in Capabilities.All)
        {
            if (!installed.Has(c)) continue;
            string want = StampFor(c);
            if (stamps.TryGetValue(c, out string? at) && at == want) continue;
            int[] kinds = Capabilities.KindsCovered(c);
            if (kinds.Length == 0) continue;
            owed.Add(new Requeue(c, kinds, want,
                                 $"{Capabilities.Title(c).ToLowerInvariant()} is now installed"));
        }
        return owed;
    }

    /// <summary>Queue what is owed and record that it was, so the next launch owes nothing.
    /// Returns how many files were queued in total.</summary>
    public static int Apply(ContentDb db, IReadOnlyList<Requeue> plan)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(plan);
        // Counted as a DELTA rather than as a sum of the per-capability returns. Speech and
        // Hebrew cover the same two kinds, so both re-queue the same rows and the queue's
        // UNIQUE(vol, frn) makes the second pass an upsert - summing the returns reports six
        // files where three moved. Each capability's own line below is still its own true
        // count; this is the total, and it has to be the number of rows that actually changed.
        long before = db.PendingCount();
        foreach (Requeue r in plan)
        {
            // Indexer.Recheck, and nothing else. A friendlier sentence here is dequeued untouched
            // by Indexer.cs:298-300 for every row that is already indexed, which is every
            // document Plan 4 read and every transcript Speech wrote - the log would say twelve
            // thousand files queued and not one embedding would be written. r.Why is for the log,
            // where a sentence belongs.
            //
            // The exclusions: a new model can read a kind it had no decoder for, and a format the
            // old reader could not open. It cannot make a file smaller or put words into an empty
            // one.
            int n = db.RequeueKinds(r.Kinds, Indexer.Recheck, [Decoders.TooLarge, Decoders.NoText]);
            db.Set(Key(r.Capability), r.Stamp);
            Log.Info("models", $"{r.Why}: {n.ToString("N0", CultureInfo.InvariantCulture)} file(s) queued to be read again");
        }
        return (int)(db.PendingCount() - before);
    }

    /// <summary>The stamps as they stand, for <see cref="Plan"/>.</summary>
    public static IReadOnlyDictionary<Capability, string> StampsIn(ContentDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var at = new Dictionary<Capability, string>();
        foreach (Capability c in Capabilities.All) if (db.Get(Key(c)) is { } v) at[c] = v;
        return at;
    }

    // ---- the transcription limit ----

    /// <summary>The last limit this index was reconciled against. A recorded value, not a guess:
    /// without it the re-queue below runs on every launch, and on a machine with a large archive
    /// that is a re-transcription every time Findra opens.</summary>
    public const string LimitKey = "models:limit:transcribe";

    /// <summary>
    /// What a change to the transcription limit owes the index - which is either nothing, or one
    /// very narrow re-queue.
    ///
    /// <para>Only a MORE permissive limit owes anything, and "more permissive" is not
    /// <c>now &gt; was</c>: a negative value means no limit, so -1 is the most permissive setting
    /// there is and a plain numeric comparison reads it as the least. Rank the two on a scale
    /// where off is 0, no limit is infinity, and a positive number is itself.</para>
    ///
    /// <para>Lowering it owes nothing on purpose. Deleting transcripts somebody already paid for
    /// because they moved a slider down is worse than keeping them; the new limit applies to
    /// what has not been read yet.</para>
    /// </summary>
    public static Requeue? PlanForLimit(int wasMinutes, int nowMinutes)
    {
        static double Rank(int m) => m < 0 ? double.PositiveInfinity : m;
        if (Rank(nowMinutes) <= Rank(wasMinutes)) return null;
        return new Requeue(Capability.Speech,
                           [(int)ResultKind.Audio, (int)ResultKind.Video],
                           Stamp: nowMinutes.ToString(CultureInfo.InvariantCulture),
                           Why: $"the transcription limit is now {TranscribeLimit.Describe(nowMinutes)}");
    }

    /// <summary>Reconcile the index against the current limit, and record it. Returns how many
    /// recordings were queued.</summary>
    public static int ApplyLimit(ContentDb db, int nowMinutes)
    {
        ArgumentNullException.ThrowIfNull(db);
        int was = int.TryParse(db.Get(LimitKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                  ? w : TranscribeLimit.Default;
        Requeue? owed = PlanForLimit(was, nowMinutes);
        db.Set(LimitKey, nowMinutes.ToString(CultureInfo.InvariantCulture));
        if (owed is null) return 0;

        // onlyBecause, not notBecause: EXACTLY the recordings that were passed over for being
        // longer than the old limit, and nothing else. It reads the recorded reason rather than
        // the state, so it also reaches a long video that was indexed for its frames alone and
        // carries TooLong as a note about the sound track nobody heard.
        int n = db.RequeueKinds(owed.Value.Kinds, Indexer.Recheck, onlyBecause: [Decoders.TooLong]);
        Log.Info("models", $"{owed.Value.Why}: {n.ToString("N0", CultureInfo.InvariantCulture)} recording(s) queued to be heard");
        return n;
    }
}
```

- [ ] **Step 5: The two settings**

In `src/Findra/App/Config.cs`, **replace** `IndexPaused` and add the limit:

```csharp
    /// <summary>Whether Findra reads the CONTENTS of files at all. Off by default, and that is
    /// the whole point (spec §6): a name index costs seconds and no disk reading, while looking
    /// inside files walks every drive, opens every document, and on a large disk runs for hours.
    /// Findra does not start that on its own - not even for the free, model-free document text.
    ///
    /// <para>One bit, not two. An "enabled" flag beside a "paused" flag is two settings that can
    /// disagree, and there is no honest sentence for the disagreement. What the interface says is
    /// derived from this and from how much has already been read.</para></summary>
    public bool IndexContent { get; init; }

    /// <summary>How long a recording is worth transcribing, in minutes: zero is off, a negative
    /// value is no limit, and any positive number is the limit itself (spec §6). It covers audio
    /// and video together, deliberately - an asymmetry between them would be invisible in the
    /// interface and surprising in use. The named choices in the settings screen are PRESETS OVER
    /// THIS NUMBER, so there is nothing here for a preset name to disagree with.
    ///
    /// <para>Not clamped. A negative value is meaningful and a very large one is simply a limit
    /// nothing reaches, so there is nothing to protect the user from.</para></summary>
    public int TranscribeMinutes { get; init; } = TranscribeLimit.Default;
```

Both go into `Equals`, `GetHashCode` and the JSON round-trip. **`ConfigTests.EveryPropertyIsPartOfEquality` will fail and name them if they do not**, and `TheContentIndexDefaultsAreTheOnesTheSpecPromises` needs its `Assert.False(c.IndexPaused)` replaced - the assertion looks the same and means the opposite, which is worth a comment beside it. Add to `tests/Findra.Tests/App/ConfigTests.cs`:

```csharp
    [Fact]
    public void ReadingInsideFilesIsOffUntilSomebodyAsksForIt()
    {
        // Spec §6, and it is the one place the product deliberately does less until asked. The
        // assertion reads the same as the IndexPaused one it replaces and means the opposite:
        // false now means "do not read inside files", not "do not pause".
        Assert.False(Config.Default.IndexContent);
        Assert.False(Config.Load(null).IndexContent);
        Assert.False(Config.Load("{}").IndexContent);
    }

    [Fact]
    public void TheTranscriptionLimitDefaultsToTheCheapPreset()
    {
        Assert.Equal(5, Config.Default.TranscribeMinutes);
        Assert.Equal("5 minutes", TranscribeLimit.Describe(Config.Default.TranscribeMinutes));
    }

    /// <summary>`{ "transcribeMinutes": 0 }` - off, spelled out, because zero is a real setting.</summary>
    private const string JsonZero = "{ \"transcribeMinutes\": 0 }";

    [Fact]
    public void ANegativeTranscriptionLimitSurvivesTheRoundTrip()
    {
        // "No limit" is a negative number, and a clamp added "for safety" would silently turn
        // the most expensive setting in the product into the cheapest.
        Config c = Config.Default with { TranscribeMinutes = TranscribeLimit.NoLimit };
        Assert.Equal(TranscribeLimit.NoLimit, Config.Load(c.ToJson()).TranscribeMinutes);
        Assert.Equal(0, Config.Load(JsonZero).TranscribeMinutes);
    }
```

- [ ] **Step 6: Three sentences, so an index nobody asked for does not look idle**

`IndexStatus.Line` gains `bool contentEnabled` as its first parameter, and two branches ahead of everything else. Add to `tests/Findra.Tests/Content/IndexStatusTests.cs`:

```csharp
    [Fact]
    public void AnIndexNobodyHasAskedForSaysSoRatherThanLookingIdle()
    {
        // Spec §6: "the interface says which state it is in rather than looking idle". On a
        // fresh install the queue is empty and nothing is running, which is byte-for-byte what
        // a FINISHED index looks like - so without this the card says "up to date · 0 files"
        // about a machine that has never read anything.
        string line = IndexStatus.Line(contentEnabled: false, state: "", pending: 0, indexed: 0,
                                       alive: false, rebuilt: false);

        Assert.NotEqual("", line);
        Assert.Contains("off", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurningItOffAfterReadingSomethingSaysHowMuchItAlreadyHas()
    {
        // The other half: this is not a fresh install, it is somebody who turned it off, and
        // telling them their 9,000 indexed files are gone would be a lie.
        string line = IndexStatus.Line(contentEnabled: false, state: "", pending: 0, indexed: 9_000,
                                       alive: false, rebuilt: false);

        Assert.Contains("off", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9,000", line, StringComparison.Ordinal);
    }

    [Fact]
    public void OffIsNotTheSameSentenceAsPausedWhileFindraIsClosed()
    {
        // Two states that both mean "nothing is happening" and have opposite answers: one is
        // "turn it on", the other is "leave Findra open".
        string off = IndexStatus.Line(false, "", 1_200, 40, alive: false, rebuilt: false);
        string closed = IndexStatus.Line(true, "", 1_200, 40, alive: false, rebuilt: false);

        Assert.NotEqual(off, closed);
        Assert.Contains("Findra is closed", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("Findra is closed", off, StringComparison.Ordinal);
    }
```

Every existing `IndexStatusTests` case passes `contentEnabled: true`, which is what they were all
implicitly asserting.

- [ ] **Step 7: Run both gates once at startup, and only there**

In `src/Findra/App/App.axaml.cs`, immediately after the writer connection is opened (`:325`) and **before `_contentLoop` starts (`:354`)**:

```csharp
        // Before the content loop, and this is not a stylistic choice. QueueFeeder holds the
        // writer across a whole ContentDb.Scope, and ContentDb.Claim is a thread-id detector
        // rather than a lock: whichever flow arrives second gets an InvalidOperationException.
        // Running the gates here means there is no second flow yet.
        int requeued = CapabilityGate.Apply(writer, CapabilityGate.Plan(
            CapabilitySet.Installed(), CapabilityGate.StampsIn(writer)));
        requeued += CapabilityGate.ApplyLimit(writer, _config.TranscribeMinutes);
        if (requeued > 0)
            Log.Info("models", $"a change to what Findra can read queued {requeued.ToString("N0", CultureInfo.InvariantCulture)} file(s)");
```

and push both settings to the rows the child reads, beside the existing `index:power`:

```csharp
        // One bit, one row. IndexContent false means the queue does not move, which is the same
        // mechanism the pause switch already used - so the child, IndexStatus and --searchindex
        // need no new concept.
        writer.Set("index:paused", _config.IndexContent ? "0" : "1");
        writer.Set("index:transcribeminutes", _config.TranscribeMinutes.ToString(CultureInfo.InvariantCulture));
```

**There is no second call site in this plan.** A capability installed by `--models install` (Task 12) is applied by that process, on its own connection, in its own lifetime; a capability that arrives while Findra is open is picked up at the next start, because the child reads `CapabilitySet.Installed()` once when it starts and the gate runs once when the interface does. Say that in the `--models` output rather than pretending otherwise. Plan 6's first-run screen, which downloads inside a running interface, will need to marshal its gate call onto the content loop's flow - that is recorded in **What comes next** as an obligation it inherits, not a thing to leave for somebody to discover.

- [ ] **Step 8: Run it**

Run: `dotnet test --filter "CapabilityGateTests|ConfigTests|IndexStatusTests"` - PASS: 23 in `CapabilityGateTests`, plus the three added to each of the other two. Then `dotnet test` - the whole suite, including every existing `IndexStatusTests` case now passing `contentEnabled: true`.

- [ ] **Step 9: Prove the reason matters**

Change `Indexer.Recheck` in `Apply` to `$"{Capabilities.Title(r.Capability)} is now installed"` and run the suite. **Two tests must fail:**

- `EveryRequeueCarriesTheReasonTheIndexerReopensAnIndexedFileFor`, on the reason itself;
- `ADocumentAlreadyIndexedIsOpenedAgainWhenMeaningArrives`, with `Asked` empty - the row is dequeued "current" and never opened. That is the whole of C-1, visible.

**If only the first fails, the second test's fixture is wrong, not the code.** Check that it stores the file's real `LastWriteTimeUtc.Ticks`: with a stored mtime of 0 the third clause of the freshness check is false on its own, the row is opened whatever the reason says, and the test passes against the bug it exists to catch. An earlier draft of this plan shipped exactly that. Revert the mutation, and report both failures rather than one.

- [ ] **Step 10: Commit**

```bash
git add src/Findra/Models/CapabilityGate.cs src/Findra/Content/ContentDb.cs \
        src/Findra/Content/IndexStatus.cs src/Findra/App/Config.cs src/Findra/App/App.axaml.cs \
        tests/Findra.Tests/Models/CapabilityGateTests.cs tests/Findra.Tests/App/ConfigTests.cs \
        tests/Findra.Tests/Content/IndexStatusTests.cs
git commit -m "Content indexing waits to be asked, and a raised limit hears exactly what it newly covers"
```

---

## Task 12: `--models` and `--content`, without a screen

**The first-run screen is Plan 6's.** It is a whole painted surface - a layout, a painter, an Avalonia window, two `--searchshot` states and a legibility check - and Plan 6 already owns the settings window that this plan's own notes describe as "a second home for what the first-run screen offered". Designing the two together produces one section rail and one painter instead of two, and it moves ~700 lines and a `Config.cs` conflict out of a plan that is already at the top of what one plan should carry.

What it leaves behind is a real problem, and the spec's newest section makes it two. **Without a screen nothing in Plan 5 ever downloads a model**, so every capability is unreachable end to end - and now **content indexing is off until asked for**, so without a way to ask, even the free document text never runs and the whole content path is dead on a fresh install.

So this task is the headless answer to both, and it is worth keeping afterwards: it is how the capability path gets exercised on a machine with no screen, in CI, and by anybody reporting a bug.

**Files:**
- Create: `src/Findra/Diagnostics/Models.cs`, `src/Findra/Diagnostics/Content.cs`
- Modify: `src/Findra/Program.cs`
- Test: `tests/Findra.Tests/Diagnostics/ModelsCommandTests.cs`, `tests/Findra.Tests/Diagnostics/ContentCommandTests.cs`

**Interfaces:**
- Consumes: `Capabilities`, `Presets`, `Sizes`, `CapabilitySet` (Task 3); `ModelStore`, `ModelDownloader` (Tasks 2, 4); `CapabilityGate` (Task 11); `ContentDb` (Plan 4).
- Produces:
  - `Findra.Diagnostics.ModelsCommand` - `static Preset? ParsePreset(string)`, `static IReadOnlySet<Capability>? ParseCapabilities(string)`, `static string RenderList(CapabilitySet installed, bool hebrewOffered)`, `static Task<int> RunAsync(string[] args)`.
  - `Findra.Diagnostics.ContentCommand` - `static string RenderStatus(Config, IndexSnapshot)`, `static int Run(string[] args)`.

```
findra --models                          what is installed, what each capability would add
findra --models list                     the same
findra --models install <preset>         justnames | recommended | everything
findra --models install <cap>[,<cap>...] photos | meaning | speech | hebrew

findra --content                         is Findra reading inside files, and how much of a recording
findra --content status                  the same
findra --content on                      start reading inside files
findra --content off                     stop; nothing already read is thrown away
findra --content limit <preset|minutes>  off | 5 | 30 | "2 hours" | "no limit" | any number
```

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Diagnostics/ModelsCommandTests.cs`:

```csharp
using Findra;
using Findra.Diagnostics;

public class ModelsCommandTests
{
    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    [Theory]
    [InlineData("justnames", Preset.JustNames)]
    [InlineData("recommended", Preset.Recommended)]
    [InlineData("Everything", Preset.Everything)]
    [InlineData("EVERYTHING", Preset.Everything)]
    public void APresetIsNamedTheWayTheFirstScreenNamesIt(string word, Preset want)
        => Assert.Equal(want, ModelsCommand.ParsePreset(word));

    [Theory]
    [InlineData("custom")]     // not something anybody can ask for - it is what touching a row makes
    [InlineData("all")]
    [InlineData("")]
    public void AWordThatIsNotAPresetIsRefusedRatherThanGuessedAt(string word)
        => Assert.Null(ModelsCommand.ParsePreset(word));

    [Fact]
    public void TheListingShowsEveryCapabilityAndWhatItWouldAddToWhatIsThere()
    {
        // Marginal, on the command line as everywhere else: on a machine that already has
        // documents' meaning, Speech is 547 MB and not 818.
        string bare = ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: false);
        string withDocs = ModelsCommand.RenderList(Set(Capability.Meaning), hebrewOffered: false);

        Assert.Contains("629 MB", bare, StringComparison.Ordinal);      // photos
        Assert.Contains("818 MB", bare, StringComparison.Ordinal);      // speech, from nothing
        Assert.Contains("547 MB", withDocs, StringComparison.Ordinal);  // speech, beside meaning
    }

    [Fact]
    public void AnInstalledCapabilityIsShownAsInstalledAndCostsNothingMore()
    {
        string text = ModelsCommand.RenderList(Set(Capability.Photos), hebrewOffered: false);

        Assert.Contains("Photos and video", text, StringComparison.Ordinal);
        Assert.Contains("installed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFreeCapabilitiesAreNamedSoNobodyThinksSearchIsOff()
    {
        // Spec §6 prints "free" on the documents row for a reason: somebody who takes nothing
        // still gets names and full-text search, and a listing that shows only the paid rows
        // makes "just names" read as "no search".
        string text = ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: false);

        Assert.Contains("words in documents", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("free", text, StringComparison.OrdinalIgnoreCase);
        // Reading words inside pictures needs no model either, and it is not a capability.
        Assert.Contains("words inside pictures", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HebrewIsListedOnlyWhereItIsWorthAGigabyteAndAHalf()
    {
        Assert.DoesNotContain("Hebrew", ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: false),
                              StringComparison.Ordinal);
        Assert.Contains("Hebrew", ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: true),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingSaysWhatEverythingWouldCostAltogether()
    {
        Assert.Contains("2.93 GB", ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: true),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingReadsTheSameOnEveryMachine()
    {
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            string de = ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: true);
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(ModelsCommand.RenderList(CapabilitySet.None, hebrewOffered: true), de);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }

    [Fact]
    public void AskingForHebrewAsksForEverythingItNeeds()
    {
        // The closure, at the one edge a person types at. Asking for the fine-tune alone
        // downloads 1.5 GB that cannot detect a language and therefore cannot be used.
        IReadOnlySet<Capability> chosen = ModelsCommand.ParseCapabilities("hebrew")!;

        Assert.Contains(Capability.Speech, chosen);
        Assert.Contains(Capability.Meaning, chosen);
        Assert.Contains(Capability.Hebrew, chosen);
    }

    [Fact]
    public void AListOfCapabilitiesIsTakenTogetherAndClosedOnce()
    {
        IReadOnlySet<Capability> chosen = ModelsCommand.ParseCapabilities("photos,speech")!;

        Assert.Equal([Capability.Photos, Capability.Meaning, Capability.Speech],
                     chosen.OrderBy(c => (int)c).ToArray());
    }

    [Fact]
    public void AnUnknownCapabilityNameIsRefusedRatherThanIgnored()
    {
        // Silently dropping a name means `--models install photos,speach` installs photos and
        // reports success, and the person waits for speech search that is never coming.
        Assert.Null(ModelsCommand.ParseCapabilities("photos,speach"));
        Assert.Null(ModelsCommand.ParseCapabilities(""));
    }
}
```

- [ ] **Step 2: Write the failing test for `--content`**

Create `tests/Findra.Tests/Diagnostics/ContentCommandTests.cs`:

```csharp
using Findra;
using Findra.Diagnostics;

public class ContentCommandTests
{
    private static IndexSnapshot Empty(long indexed = 0, long queued = 0)
        => SearchIndexReportTests.Sample() with { Indexed = indexed, Queued = queued };

    [Fact]
    public void AFreshInstallSaysReadingInsideFilesIsOffAndHowToStart()
    {
        // Spec §6: off until asked for, and the interface says which state it is in rather than
        // looking idle. A fresh install's queue is empty and nothing is running, which is
        // byte-for-byte what a finished index looks like.
        string text = ContentCommand.RenderStatus(Config.Default, Empty());

        Assert.Contains("off", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--content on", text, StringComparison.Ordinal);
        Assert.DoesNotContain("up to date", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurnedOffAfterReadingSomethingSaysHowMuchItKept()
    {
        // Turning it off does not throw away what has been read, and saying so is the difference
        // between a switch somebody will use and one they will not touch again.
        string text = ContentCommand.RenderStatus(Config.Default, Empty(indexed: 9_000));

        Assert.Contains("9,000", text, StringComparison.Ordinal);
        Assert.Contains("off", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurnedOnItReportsTheQueueRatherThanTheSwitch()
    {
        string text = ContentCommand.RenderStatus(Config.Default with { IndexContent = true },
                                                  Empty(indexed: 40, queued: 1_200));

        Assert.Contains("1,200", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--content on", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTranscriptionLimitIsAlwaysNamedInWordsSomebodyCanRead()
    {
        Assert.Contains("5 minutes", ContentCommand.RenderStatus(Config.Default, Empty()), StringComparison.Ordinal);
        Assert.Contains("No limit", ContentCommand.RenderStatus(
            Config.Default with { TranscribeMinutes = TranscribeLimit.NoLimit }, Empty()), StringComparison.Ordinal);
        Assert.Contains("17 minutes", ContentCommand.RenderStatus(
            Config.Default with { TranscribeMinutes = 17 }, Empty()), StringComparison.Ordinal);
        Assert.Contains("Off", ContentCommand.RenderStatus(
            Config.Default with { TranscribeMinutes = TranscribeLimit.Off }, Empty()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusReadsTheSameOnEveryMachine()
    {
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            string de = ContentCommand.RenderStatus(Config.Default, Empty(indexed: 9_000));
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(ContentCommand.RenderStatus(Config.Default, Empty(indexed: 9_000)), de);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }
}
```

`SearchIndexReportTests.Sample()` is Plan 4's fixture and is **`private static`** today
(`tests/Findra.Tests/Diagnostics/SearchIndexReportTests.cs:19`). Widen it to `internal static`
rather than writing a second fixture that will drift from it - every parameter is already
optional, so `Sample()` works from this file the moment it is visible. Task 14 Step 2 adds three
fields to `IndexSnapshot` and both files build from the same helper, which is the point.

- [ ] **Step 3: Run both to watch them fail**

Run: `dotnet test --filter "ModelsCommandTests|ContentCommandTests"`
Expected: FAIL - `ModelsCommand` and `ContentCommand` do not exist.

- [ ] **Step 4: Write `--models`**

Create `src/Findra/Diagnostics/Models.cs`. `ParsePreset`, `ParseCapabilities` and `RenderList` are pure; `RunAsync` is the impure half.

`RenderList` prints, in fixed-width columns and every number through `CultureInfo.InvariantCulture`:

- the two things that need no model at all, first and marked free - **words in documents**, and **words inside pictures**, which is the OCR the indexer does whenever it opens a photo anyway and is not a capability, has no download and appears in no graph;
- one row per capability: its title, whether it is installed, and what it would add **given what is already installed** - `Capabilities.MarginalBytes(c, installed.Have)`, never a fixed number;
- Hebrew only when `hebrewOffered`, indented under Speech;
- the total for everything, and where the files live.

`RunAsync`:

1. With no argument or `list`, print `RenderList(CapabilitySet.Installed(), Capabilities.HebrewIsOffered(Capabilities.SystemLanguages()))` and exit 0.
2. With `install <what>`, resolve a preset name or a comma-separated capability list, **close it**, and print what will be fetched and what it comes to before fetching anything. Refuse an unrecognised word with exit 1 and the list of words that would have worked.
3. Fetch with `ModelDownloader.GetAllAsync(Capabilities.ModelsFor(chosen), ModelStore.Dir, ModelDownloader.Http(http), progress, ct)`, printing progress on one rewritten line. Anything already present is skipped without a byte, which is spec §2a and is `AFinishedFileIsNotFetchedAgain`'s promise made visible.
4. When every file is complete, open the index and run the gate:

```csharp
        using ContentDb db = ContentDb.OpenOrRebuild();
        int queued = CapabilityGate.Apply(db, CapabilityGate.Plan(CapabilitySet.Installed(), CapabilityGate.StampsIn(db)));
        Console.WriteLine($"{queued.ToString("N0", CultureInfo.InvariantCulture)} file(s) queued to be read again.");
```

   This process owns its own connection and has no content loop, so there is no second flow to race - which is exactly why the gate has no second call site inside the running interface (Task 11 Step 5).

5. **Say what happens next, plainly.** If Findra is running, its indexer read the installed set when it started and will not notice the new models until it restarts; the queued files are safe in `pending` either way. Print that rather than leaving somebody watching a queue that is not moving:

```
   Findra is running. Its indexer reads what is installed when it starts, so restart Findra
   to begin reading these files. Nothing is lost if you do not - the queue survives.
```

6. Exit 0 when everything asked for is present, 1 for a bad argument, 2 when a download did not complete (the `.part` is kept and the next run resumes).

In `src/Findra/Program.cs` add both arms beside the diagnostics, with a usage line each:

```csharp
            "--models"      => Diagnostics.ModelsCommand.RunAsync(args).GetAwaiter().GetResult(),
            "--content"     => Diagnostics.ContentCommand.Run(args),
```

`--models` is the plan's **second** `GetAwaiter().GetResult()`, and like the first it is at a
`Program.Main` switch arm where there is no async context to await into. There is no third:
`--content` downloads nothing and is synchronous throughout.

Note that `--content` is a different mode from `--index <parentPid>`, which is the child. Nothing
about the child's arm changes, and the usage block should make the difference obvious - one is a
setting a person changes, the other is a process nobody runs by hand.

- [ ] **Step 5: Write `--content`**

Create `src/Findra/Diagnostics/Content.cs`. `RenderStatus` is pure over a `Config` and an
`IndexSnapshot`; `Run` reads the config, writes it back, and reconciles.

`RenderStatus` prints four things, every number through `CultureInfo.InvariantCulture`:

- **whether Findra is reading inside files at all**, and when it is not, the command that starts it.
  The two off states are different sentences - "off, and nothing has been read" versus "off ·
  9,000 files already read" - because one of them is a fresh install and the other is somebody who
  turned it off, and telling the second that their index is empty is a lie.
- **what is in the index and what is queued**, when it is on.
- **how long a recording is worth transcribing**, through `TranscribeLimit.Describe`, so a preset
  and a typed number read the same way and there is no second field for them to disagree in.
- **which capabilities are installed**, as one line, with a pointer to `--models` for the rest.

`Run`:

1. With no argument or `status`, print the status and exit 0.
2. `on` / `off` set `Config.IndexContent`, save, and say what changed. Turning it **off** says
   plainly that nothing already read is thrown away; turning it **on** says the first pass reads
   every drive and can take hours, which is the honest warning the spec's "Findra does not start
   that on its own" is protecting people from.
3. `limit <what>` runs `TranscribeLimit.Parse`, refuses anything it cannot read with exit 1 and the
   list of words that would have worked - **never falling back to zero**, which is a real setting
   and would silently turn transcription off for somebody who mistyped a number - saves, and then
   reconciles:

```csharp
        using ContentDb db = ContentDb.OpenOrRebuild();
        int queued = CapabilityGate.ApplyLimit(db, minutes);
        Console.WriteLine(queued > 0
            ? $"{queued.ToString("N0", CultureInfo.InvariantCulture)} recording(s) queued to be heard."
            : "Nothing new to hear at that limit.");
```

4. Every arm that changes something ends with the same sentence `--models install` ends with: if
   Findra is running, it reads these settings when it starts, so restart it for the change to take
   effect on the queue. The settings are saved either way.

- [ ] **Step 6: Run them**

Run: `dotnet test --filter "ModelsCommandTests|ContentCommandTests"` - PASS, 18 test methods / 25 cases with the theory rows.

- [ ] **Step 7: Take a capability, and turn content indexing on, for real**

This is the first and only place in the plan where a model is actually fetched, and it is what makes Tasks 7, 9, 10 and 13 mean anything on this machine.

```
findra --content                       expect: reading inside files is off, and how to start
findra --models                        expect: nothing installed, 2.93 GB for everything
findra --models install recommended
findra --content on
findra --searchmodels
findra --searchindex
```

Expected, in order: a status saying content indexing is off on a machine that has never been asked
- **not** "up to date", which is what a finished index says and is the mistake this whole section
exists to prevent; a listing with nothing installed; roughly 900 MB fetched with visible progress
and a resumable `.part` if you interrupt it (**interrupt it once on purpose and run it again** -
the second run must continue rather than restart, which is
`APartialDownloadResumesFromTheByteAlreadyFetched` proven on a real network); a `--searchmodels`
report naming DirectML or the CPU for ONNX with every rejection; and a `--searchindex` models
section showing photos and meaning installed with a non-zero backlog queued.

Then start Findra and watch the queue move. **Record the provider that answered, the accelerator
line, and the throughput** - those are the first real numbers this product has produced for its
model path, and Plan 6's README needs them.

Then exercise the limit, which needs no download at all:

```
findra --content limit "2 hours"       expect: N recording(s) queued to be heard
findra --content limit 5               expect: nothing queued - lowering owes nothing
```

If you cannot download 900 MB, say so and run `findra --models install justnames` instead, which
asks for nothing and must still exit 0 with an empty plan - and run the `--content` arms anyway,
because they need no model and they are what makes the free document text run at all.

- [ ] **Step 8: Commit**

```bash
git add src/Findra/Diagnostics/Models.cs src/Findra/Diagnostics/Content.cs src/Findra/Program.cs \
        tests/Findra.Tests/Diagnostics/ModelsCommandTests.cs \
        tests/Findra.Tests/Diagnostics/ContentCommandTests.cs
git commit -m "--models and --content: see what it costs, take it, and ask Findra to start reading"
```

---

## Task 13: `--searchmodels`

The mode Plan 4 left as a real arm with a "not built yet" message. Replace the body; do not turn it back into an unknown-mode fall-through.

**Files:**
- Create: `src/Findra/Diagnostics/SearchModels.cs`
- Modify: `src/Findra/Program.cs:18` and its usage line
- Test: `tests/Findra.Tests/Diagnostics/SearchModelsReportTests.cs`

**Interfaces:**
- Consumes: `ModelStore`, `Capabilities`, `Sizes` (Tasks 2-3), `Providers`, `Onnx`, `Media.OpenWhisper` (Tasks 5, 7, 8).
- Produces:
  - `Findra.Diagnostics.ModelRow` - `readonly record struct ModelRow(string File, string Purpose, bool Present, long Declared, long Actual)`.
  - `Findra.Diagnostics.CapabilityRow` - `readonly record struct CapabilityRow(Capability Capability, bool Installed, int Have, int Needs, long MarginalBytes)`.
  - `Findra.Diagnostics.ModelsSnapshot` - `sealed record ModelsSnapshot(string Dir, IReadOnlyList<ModelRow> Models, IReadOnlyList<CapabilityRow> Capabilities, IReadOnlyList<ProviderTry> Onnx, IReadOnlyList<ProviderTry> Whisper, IReadOnlyList<string> Notes)`.
  - `Findra.Diagnostics.ModelsReport` - `static string Render(ModelsSnapshot)`, plus `const string Chosen = " : chosen"` and `const string Rejected = " : rejected - "`, which are the two markers a provider row is rendered with. They are constants rather than literals inside the formatter because a test counts them, and a test that re-types the marker tests its own copy of it.
  - `Findra.Diagnostics.SearchModels` - `static int Run(string[] args)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Diagnostics/SearchModelsReportTests.cs`:

```csharp
using System.Globalization;
using Findra.Diagnostics;

using Findra;

[Collection("culture")]
public class SearchModelsReportTests
{
    private static ModelsSnapshot Sample(bool anyPresent = true) => new(
        Dir: @"C:\Users\x\AppData\Local\Findra\models",
        Models:
        [
            new ModelRow("siglip2-vision.onnx", "photos and video frames", anyPresent, 372_034_764, anyPresent ? 372_034_764 : 0),
            new ModelRow("siglip2-text-q.onnx", "what you type", false, 283_430_092, 0),
            new ModelRow("e5-base-q.onnx", "meaning", false, 278_606_643, 0),
        ],
        Capabilities:
        [
            new CapabilityRow(Capability.Photos, false, anyPresent ? 1 : 0, 3, 659_659_160),
            new CapabilityRow(Capability.Meaning, false, 0, 2, 283_639_807),
        ],
        Onnx:
        [
            new ProviderTry("DirectML", false, "DllNotFoundException: DirectML.dll"),
            new ProviderTry("CPU", true, ""),
        ],
        Whisper: [new ProviderTry("Vulkan", true, "")],
        Notes: []);

    [Fact]
    public void EveryModelIsListedIncludingTheOnesThatAreNotThere()
    {
        // "Why are no photos being indexed" is unanswerable if the report only lists what it
        // found. The absent rows are the answer.
        string text = ModelsReport.Render(Sample());

        Assert.Contains("siglip2-vision.onnx", text, StringComparison.Ordinal);
        Assert.Contains("siglip2-text-q.onnx", text, StringComparison.Ordinal);
        Assert.Contains("e5-base-q.onnx", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APresentModelsSizeOnDiskIsPrintedBesideTheOneItShouldBe()
    {
        // Spec §9a: the README's model sizes come from the real files, not from the declared
        // table. Printing only one of the two numbers makes that impossible to check.
        string text = ModelsReport.Render(Sample());

        Assert.Contains("354.8 MB", text, StringComparison.Ordinal);   // declared, from the table
        Assert.Contains("on disk", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsThereButTheWrongSizeIsFlagged()
    {
        ModelsSnapshot s = Sample() with
        {
            Models = [new ModelRow("siglip2-vision.onnx", "photos", true, 372_034_764, 12_345)],
        };

        string text = ModelsReport.Render(s);

        Assert.Contains("12,345", text, StringComparison.Ordinal);
        Assert.Contains("expected", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACapabilityWithSomeOfItsFilesIsNotReportedAsReady()
    {
        // One of three is not "photos work". An Any() where an All() belongs lights the
        // capability up and then fails on the first query.
        string text = ModelsReport.Render(Sample());

        Assert.Contains("1 of 3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Photos and video : ready", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACapabilityThatIsOffSaysWhatItWouldCostToTurnOn()
    {
        string text = ModelsReport.Render(Sample());
        Assert.Contains("629 MB", text, StringComparison.Ordinal);
        Assert.Contains("270 MB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChosenProviderAndEveryRejectedOneAppearWithItsReason()
    {
        // Spec §6, the whole point of the mode: report the chosen provider AND every one it
        // rejected, with reasons. A report that prints only the winner loses the half that
        // answers "why is this slow".
        string text = ModelsReport.Render(Sample());

        Assert.Contains("DirectML", text, StringComparison.Ordinal);
        Assert.Contains("DirectML.dll", text, StringComparison.Ordinal);
        Assert.Contains("CPU", text, StringComparison.Ordinal);
        Assert.Contains("Vulkan", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderThatWasNeverTriedIsNotClaimedAsRejected()
    {
        // The sample tried three providers in total: DirectML (rejected) and CPU (chosen) for
        // ONNX, and Vulkan (chosen) for whisper. The whisper chain stopped at its first rung, so
        // the report must not invent a rejected CPU row to fill the declared chain out.
        //
        // Counted, not sliced. An earlier draft split the rendered text on the lowercase word
        // "whisper" and inspected the tail; every other surface in this plan capitalises it, so
        // the split found nothing, `[^1]` was the whole report, and the ONNX section's own "CPU"
        // failed the assertion for a reason that had nothing to do with the rule.
        string[] lines = ModelsReport.Render(Sample()).Split('\n');
        int rows = lines.Count(l => l.Contains(ModelsReport.Chosen, StringComparison.Ordinal)
                                 || l.Contains(ModelsReport.Rejected, StringComparison.Ordinal));

        Assert.Equal(3, rows);
    }

    [Fact]
    public void AMachineWithNoModelsAtAllProducesAWholeReportAndNotAnError()
    {
        // A missing model is a NORMAL state (spec §6). The report has to be complete and
        // readable on a machine that took the "Just names" preset, which is most of them.
        string text = ModelsReport.Render(Sample(anyPresent: false));

        Assert.Contains("siglip2-vision.onnx", text, StringComparison.Ordinal);
        Assert.Contains("2.93 GB", text, StringComparison.Ordinal);    // what everything would cost
        Assert.DoesNotContain("ERROR", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FAIL", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFreeCapabilityIsNamedSoNobodyThinksSearchIsOff()
    {
        string text = ModelsReport.Render(Sample(anyPresent: false));
        Assert.Contains("words in documents", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("free", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheReportReadsTheSameOnEveryMachine()
    {
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string de = ModelsReport.Render(Sample());
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(ModelsReport.Render(Sample()), de);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }

    [Fact]
    public void ANoteFromTheRunItselfIsCarriedThrough()
    {
        // Where "this model is on disk and would not load" ends up: a note, not a crash, and
        // not a silent omission either.
        ModelsSnapshot s = Sample() with { Notes = ["e5-base-q.onnx is present but would not load: InvalidProtobuf"] };
        Assert.Contains("would not load", ModelsReport.Render(s), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet test --filter SearchModelsReportTests`
Expected: FAIL - the four types do not exist.

- [ ] **Step 3: Write it**

Create `src/Findra/Diagnostics/SearchModels.cs`, in the same shape as `SearchIndexReport` (`src/Findra/Diagnostics/SearchIndex.cs:29`): a pure `Render(snapshot)` in fixed-width columns with every number through `CultureInfo.InvariantCulture`, and an impure `Run` that gathers the snapshot.

`Run` does, in order:

1. Build the model rows from `ModelStore.All`, `Present` and `ActualBytes`.
2. Build the capability rows: installed or not, how many of its closed model set are present out of how many, and `Capabilities.MarginalBytes(c, installed)`.
3. Choose the providers **without loading a model**, if none is present: `Providers.First` needs an initialiser, so with nothing on disk there is nothing to try and the two provider lists come back empty with a note saying so. With a model present, open it and keep `Chosen.Tried`.
4. If the query-side models are present, encode a few sentences and one picture and print the similarities, the way `SearchModelsProbe` does (`SearchModelsProbe.cs:37-72`) - that is what separates "the models are missing" from "the tokenizer is producing garbage" from "the scores are simply low". Any exception from a load becomes a `Note`, never a crash.

Provider rows render as `    DirectML : rejected - DllNotFoundException: DirectML.dll` and `    CPU : chosen`, using `ModelsReport.Chosen` and `ModelsReport.Rejected` so the markers exist once. **One row per `ProviderTry` that actually happened, never one per declared chain entry** - a chain that stopped at its first rung produces one row, and inventing the rest is the report describing an intention rather than a measurement.

Name the two free things at the top, beside the capabilities that cost something: **words in documents**, and **words inside pictures**, which is the OCR the indexer runs whenever it opens a photo, needs no model, and is not a capability. A report that lists only the paid rows makes a machine with nothing installed read as a machine with no search.
5. Print `Render(snapshot)`.

**The exit code.** `0` when the report is complete, **including on a machine with no models at all** - a missing model is a normal state and a non-zero exit would make every script treat the ordinary case as a failure. `2` only when a model that IS present would not load, which is a broken file and a real fault.

In `src/Findra/Program.cs`, `"--searchmodels" => NotBuiltYet()` becomes `"--searchmodels" => Diagnostics.SearchModels.Run(args)`, `NotBuiltYet` is deleted, and the usage line loses its "- not built yet".

- [ ] **Step 4: Run it, on this machine, twice**

```
findra --searchmodels
```

With no models installed: expect a complete report, exit 0, every one of the seven files listed as absent, four capability lines with their marginal costs, the free row named, and a note saying no provider was tried because nothing was loaded.

Then, if you have models on disk from a real first run, run it again and confirm the provider lines name what was actually chosen and what was rejected. **If you have no models, say so** - do not fabricate the second half.

- [ ] **Step 5: Commit**

```bash
git add src/Findra/Diagnostics/SearchModels.cs src/Findra/Program.cs \
        tests/Findra.Tests/Diagnostics/SearchModelsReportTests.cs
git commit -m "--searchmodels: what is there, what it would cost, and which provider answered"
```

---

## Task 14: Close-out

Not optional, and not delegable to a grep.

**Files:**
- Modify: `src/Findra/Diagnostics/Machine.cs`, `src/Findra/Diagnostics/SearchBench.cs`, `src/Findra/Diagnostics/SearchIndex.cs`, `src/Findra/Diagnostics/SelfTest.cs`, `src/Findra/Card/IndexLineFormatter.cs`, `CHANGELOG.md`, `CLAUDE.md`
- Test: `tests/Findra.Tests/Diagnostics/BenchTests.cs`, `tests/Findra.Tests/Diagnostics/SearchIndexReportTests.cs`, `tests/Findra.Tests/App/IndexLineFormatterTests.cs` - all *modify*

- [ ] **Step 1: The accelerator line stops being a placeholder**

`Machine.NoAccelerator` is `"CPU only - this build runs no models"` (`src/Findra/Diagnostics/Machine.cs:41`) and that sentence is now false. `MachineInfo.Accelerator` becomes the pair actually chosen, formatted `"ONNX: DirectML · Whisper: Vulkan"`, with `"CPU"` for either that fell back and `"not loaded"` for either whose models are absent.

Add to `tests/Findra.Tests/Diagnostics/BenchTests.cs`:

```csharp
    [Fact]
    public void TheAcceleratorLineNamesBothRuntimesAndWhatEachOneGot()
    {
        // A throughput number without the silicon beside it is meaningless (spec §6), and
        // "which silicon" is now two answers, because the two runtimes choose separately: a
        // machine can run DirectML for the vision tower and fall back to the CPU for whisper.
        string line = Machine.AcceleratorLine(onnx: "DirectML", whisper: "CPU");

        Assert.Contains("ONNX", line, StringComparison.Ordinal);
        Assert.Contains("DirectML", line, StringComparison.Ordinal);
        Assert.Contains("Whisper", line, StringComparison.Ordinal);
        Assert.Contains("CPU", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AMachineWithNoModelsSaysSoRatherThanClaimingACpuFallback()
    {
        // "CPU" would be a measurement of something that never ran. Not loaded is the truth.
        string line = Machine.AcceleratorLine(onnx: null, whisper: null);
        Assert.Contains("not loaded", line, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectML", line, StringComparison.Ordinal);
    }
```

`TheAcceleratorLineCarriesNoPlanNumbersOrOtherInternalLanguage` from Plan 4 stays exactly as it is and must stay green.

- [ ] **Step 2: `--searchindex` grows a models section, and says whether anybody asked**

Add to `IndexSnapshot`: `IReadOnlyList<(Capability Capability, bool Installed, long WaitingFiles)> Capabilities`, `bool ContentEnabled`, and `int TranscribeMinutes`. `WaitingFiles` is how many items sit at `StateSkipped` with the `Decoders.NoModel` reason among the kinds that capability covers - the number that answers "I turned photos on, what is it going to do".

`ContentEnabled` comes from the `index:paused` row rather than from `config.json`, because
`--searchindex` describes the index it is looking at and not the settings of an interface that may
not be running. It is the first line of the report, above the counts, for the same reason the
status line leads with it: an index nobody has asked for and a finished index have identical
counts.

```csharp
    [Fact]
    public void AnIndexNobodyHasAskedForSaysSoAboveTheCounts()
    {
        // Zero queued, zero indexed and no indexer is byte-for-byte what a FINISHED index looks
        // like. Without this line, the report a person runs to find out why nothing is happening
        // reads as though everything is done.
        string text = SearchIndexReport.Render(Sample() with
        {
            ContentEnabled = false, Queued = 0, Indexed = 0,
        });

        Assert.Contains("off", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("up to date", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheTranscriptionLimitIsReportedInWordsAndNotAsABareNumber()
    {
        // -1 printed raw reads as an error. It is the most permissive setting in the product.
        Assert.Contains("No limit", SearchIndexReport.Render(Sample() with { TranscribeMinutes = -1 }),
                        StringComparison.Ordinal);
        Assert.Contains("5 minutes", SearchIndexReport.Render(Sample() with { TranscribeMinutes = 5 }),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingsPassedOverForTheirLengthAreCountedSeparately()
    {
        // Distinct from "waiting for a model": one is cleared by a download and the other by a
        // setting, and a single "skipped" total tells nobody which lever to pull.
        string text = SearchIndexReport.Render(Sample() with { TooLongRecordings = 231 });

        Assert.Contains("231", text, StringComparison.Ordinal);
        Assert.Contains("limit", text, StringComparison.OrdinalIgnoreCase);
    }
```

`TooLongRecordings` is a third new field on `IndexSnapshot`, counted from
`items WHERE error = Decoders.TooLong`, and it is the number that makes `--content limit` a
decision somebody can make rather than a guess.

Add to `tests/Findra.Tests/Diagnostics/SearchIndexReportTests.cs`:

```csharp
    [Fact]
    public void EveryCapabilityIsListedWithWhatIsWaitingOnIt()
    {
        // Per capability, not one total: "8,000 files skipped" does not tell anybody which
        // download would clear them.
        IndexSnapshot s = Sample() with
        {
            Capabilities =
            [
                (Capability.Photos, false, 8_312),
                (Capability.Meaning, true, 0),
                (Capability.Speech, false, 44),
                (Capability.Hebrew, false, 0),
            ],
        };

        string text = SearchIndexReport.Render(s);

        Assert.Contains("8,312", text, StringComparison.Ordinal);
        Assert.Contains("44", text, StringComparison.Ordinal);
        Assert.Contains("Speech", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstalledCapabilityWithNothingWaitingStillAppears()
    {
        // The same rule the kind counts already follow: a zero row is an answer, and filtering
        // it out makes "why is nothing happening" unanswerable.
        IndexSnapshot s = Sample() with { Capabilities = [(Capability.Meaning, true, 0)] };
        Assert.Contains("Meaning in documents", SearchIndexReport.Render(s), StringComparison.Ordinal);
    }
```

- [ ] **Step 3: `--searchtest` learns about models**

Add three checks to `src/Findra/Diagnostics/SelfTest.cs`, in the style of the ones already there:

```csharp
        failed += Check("the capability graph is consistent", () =>
        {
            foreach (Capability c in Capabilities.All)
            {
                IReadOnlySet<Capability> once = Capabilities.Close([c]);
                if (!Capabilities.Close(once).SetEquals(once)) return $"closing {c} twice changes it";
                foreach (int k in Capabilities.KindsCovered(c))
                    if (!FileKinds.HasContent((ResultKind)k)) return $"{c} claims {(ResultKind)k}, which has no content";
            }
            // The measured total is what the README and the winget manifest quote.
            long all = Capabilities.TotalBytes(Capabilities.All);
            return Sizes.Human(all) == "2.93 GB" ? null : $"the whole model set measures {Sizes.Human(all)}";
        });

        failed += Check("every installed capability loads", () =>
        {
            CapabilitySet have = CapabilitySet.Installed();
            if (have.Have.Count == 0) { Console.WriteLine("        no capability is installed - nothing to load"); return null; }
            foreach (Capability c in have.Have)
                foreach (Model m in Capabilities.OwnModels(c))
                    if (!ModelStore.Present(m)) return $"{c} is installed but {m.File} is not there";
            return null;
        });

```

**There is no third check, and no legibility check.** This plan paints nothing: the first-run screen and its contrast pass are Plan 6's. A draft of this plan added one anyway, over surfaces nothing draws yet, and it was a duplicate as well as premature - `Derived.Contrast(d.Fade(150), c)` measures the same thing as `Derived.Contrast(d.Ink, c)`, because `Fade` changes only the alpha channel (`src/Findra/Look/Derived.cs:127-129`) and `Contrast` reads only RGB (`:204-211`). A check that cannot fail independently of the line above it gives false assurance about exactly the text it claims to cover. When Plan 6 measures faded note text it has to **composite the alpha against the surface first**, and that is written down in **What comes next**.

Both checks above run on a machine that has downloaded nothing, which is the point: the capability graph is the one thing in this plan that can be wrong everywhere.

- [ ] **Step 3b: `--searchshot` learns nothing, and `CLAUDE.md` still needs correcting**

This plan adds no painted surface, so there is no new shot state. Trap 9 - *"`--searchshot` must learn every new surface as it is written"* - is **deferred to Plan 6 with the screen**, and **What comes next** records it as an obligation rather than leaving it to be rediscovered.

`CLAUDE.md`'s command block is still wrong independently of that: it lists `--searchshot out.png <empty|typing|results|noresults|many|adv|panel>`, naming a `panel` state that has never existed and omitting `capsule`, `opening` and `openingempty`. Correct the whole list against `SearchShot.States`, and add the `--models` line beside the six diagnostics.

- [ ] **Step 4: The last culture leak**

`src/Findra/Card/IndexLineFormatter.cs:34` is `$"{n / 1_000_000.0:0.0}M"` with no culture, inside a line whose other half has a whole test devoted to reading the same on every machine. Fix it and add to `tests/Findra.Tests/App/IndexLineFormatterTests.cs`:

```csharp
    [Fact]
    public void TheNameCountReadsTheSameOnEveryMachine()
    {
        // 1.5M renders as "1,5M" under de-DE, in the same footer line as IndexStatus.Line,
        // which is careful to be invariant. One half of one sentence.
        var was = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var reply = new StatusReply(4321, [new VolumeStatus('C', 1_500_000, 0, Live: true)]);
            Assert.Contains("1.5M", IndexLineFormatter.IndexLineFor(reply), StringComparison.Ordinal);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
    }
```

`IndexLineFormatterTests` joins `[Collection("culture")]` for this - it assigns `CurrentCulture`, and `CultureCollection` exists to stop a parallel test observing de-DE and failing for a reason that has nothing to do with it.

- [ ] **Step 5: The dead-field sweep**

For every record field, snapshot property and report column this plan added, grep for a reader:

```bash
for f in MarginalBytes Actual Declared Tried WaitingFiles Nested Free Busy Got Installed; do
  echo "== $f"; grep -rn "\.$f" src/ --include=*.cs | grep -v "record\|=>" | head -5
done
```

Plan 4 shipped two fields written and plumbed and read by nobody, and neither review caught it. Anything with no reader is deleted, not documented.

- [ ] **Step 6: The lineage pass, read rather than grepped**

```bash
grep -ric prism src/ tests/                       # must be 0
grep -rn "ported from\|derived from\|forked\|upstream\|widget host\|another product\|copied from" src/ tests/
grep -rn "—\|–" src/ tests/                       # em and en dashes: must be none
```

Then **read** every comment in the six files this plan ported - `VectorStore.cs`, `Encoders.cs`, `Media.cs`, `ImageText.cs`, `PreviewDecoder.cs` and the arms lifted into `Decoders.cs`. Between them they carry comments naming another product's installer, its tools folder, its widgets, a specific graphics card, and a rule about a product's own UI process. The name-grep finds none of those. Ask of each: does this read as though it was written for Findra by somebody who has never seen another codebase?

Also check the one thing a text grep cannot reach: run

```bash
head -c 4 "$LOCALAPPDATA/Findra/index/vectors.bin" 2>/dev/null || echo "no vector file yet"
```

after any run that wrote one, and confirm it says `FVS1`.

- [ ] **Step 7: The changelog**

Add to `CHANGELOG.md` under `### Added`, in the voice the existing entries use - what it does for a person, never a plan number:

- **Photos and video, searched by what is in them.** Describe a picture and Findra finds it, including the frames of a video. The words inside a screenshot are read by the recognisers Windows already ships and go into the full-text index like any other document.
- **Meaning in documents**, so "the bill" finds the invoice.
- **Speech**, transcribed on your machine, with a second pass through a Hebrew model for the files the first pass hears as Hebrew.
- **Each of those is a separate download and each one is optional.** A capability whose files are not there is skipped silently - the words inside documents stay free and always on - and turning one on later reads exactly the files it covers and nothing else.
- **A first screen with three choices** and the real size beside each, where each size is what it would add to what you have already picked.
- **It runs on whatever is in the machine.** DirectX 12 for the vision and document models, Vulkan for speech, and the CPU wherever neither is there - which is a supported configuration and not a fallback anybody has to know about. `--searchmodels` says which one answered and what refused.
- **`--searchmodels`** now reports what is installed, what each capability would cost, and which execution provider was chosen.
- **Reading the contents of your files waits until you ask.** Names are searchable the moment Findra starts, because a name index costs seconds. Looking inside files walks every drive and can run for hours, so Findra does not begin on its own - `findra --content on` starts it, `--content off` stops it without throwing away anything already read, and it stays as you left it across restarts.
- **One setting decides how long a recording is worth transcribing**, covering sound files and video together: off, five minutes, thirty, two hours, no limit, or any number of minutes you type. Five minutes to begin with, which covers voice memos and clips and is cheap on any machine. A recording longer than the limit is passed over rather than failed, and raising the limit later goes back for exactly those and nothing else.

And under `### Security`, one line: every new decoder - the ONNX runtimes, whisper, the media pipeline, the picture codecs and the OCR - runs in the unelevated indexer child, because all five read files somebody else put on the disk.

- [ ] **Step 8: `CLAUDE.md`**

Three corrections, none of them about a surface this plan paints. Its "Commands" block still shows `--searchshot out.png <empty|typing|results|noresults|many|adv|panel>`, naming a `panel` state that has never existed and omitting `capsule`, `opening` and `openingempty` - correct the whole list against `SearchShot.States`, and add `--models` and `--content` beside the six diagnostics. Then update its Capabilities section: the dependency graph's `words in documents - free, always on` becomes **`free, opt-in`**, and the section gains a sentence saying content indexing is off until asked for and that one number, in minutes, decides how long a recording is worth transcribing.

- [ ] **Step 9: The whole suite, clean**

```bash
dotnet build -warnaserror        # zero warnings
dotnet test                      # everything green
findra --searchtest              # every check, including the three new ones
findra --searchmodels            # exit 0 with no models installed
findra --searchindex             # the models section appears
findra --searchbench bench.md    # the accelerator line names both runtimes
```

Report the test count before and after this plan, and say which of the six diagnostics you ran and what each answered.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "Close-out: the accelerator line, the models section, the graph self-check, the changelog"
```

---

## Done when

- **`findra --searchmodels` prints a complete report on a machine with no models at all, and exits 0.** Every one of the seven files is listed as absent, every capability says what it would cost, the free row says "free", and the exit code says the ordinary state is not a failure.
- **`findra --searchmodels` on a machine with models names the provider it chose and every provider it rejected, with the reason for each** - spec §6's explicit requirement, and the difference between a solvable support question and an unsolvable one.
- **`findra --models` lists every capability with what it would add given what is already installed**, names the two free things - words in documents and words inside pictures - shows Hebrew only where the machine's languages include it, and gives 2.93 GB as the total for everything. **`findra --models install recommended` fetches roughly 900 MB, resumes if it is interrupted, and queues exactly the files the two new capabilities cover.**
- **Reading inside files is off until somebody asks.** On a fresh install `findra --content` says so and gives the command that starts it - it does not say "up to date", which is what a finished index says and is what an empty one looks like. `--content on` starts it, `--content off` stops it and says how much is already read, and the setting survives a restart. `AnIndexNobodyHasAskedForSaysSoRatherThanLookingIdle`, `OffIsNotTheSameSentenceAsPausedWhileFindraIsClosed` and `AnIndexNobodyHasAskedForSaysSoAboveTheCounts` are the three tests that say so.
- **One number decides how long a recording is worth transcribing, covering audio and video together.** Zero is off, negative is no limit, positive is minutes, five by default; a preset and a typed value are the same setting. A recording over the limit is **skipped with a reason of its own**, and `findra --content limit "2 hours"` re-queues exactly those recordings - including a long video that was indexed for its frames alone - and nothing else. Lowering the limit queues nothing. `RaisingTheLimitQueuesOnlyTheRecordingsItNewlyCovers`, `ALongVideoIndexedForItsFramesAloneIsQueuedAgainWhenTheLimitRises` and `LoweringTheLimitQueuesNothing` are the three that say so.
- **No per-kind recording constant survives beside the setting** - the one-hour and three-minute pair are gone, and `NoPerKindRecordingConstantSurvivesBesideTheSetting` is the test.
- **A download that is interrupted resumes from the byte already fetched**, a file already present at the right size is not fetched again, and a download that ends short is not promoted - the `.part` is kept so the next run resumes. `APartialDownloadResumesFromTheByteAlreadyFetched`, `AFinishedFileIsNotFetchedAgain` and `ADownloadThatEndsShortIsNotPromoted` are the three tests that say so.
- **Every capability degrades silently when its model is absent.** The indexer records Skipped with a reason and never Failed, content search contributes no candidates and never throws, `--searchmodels` exits 0, and the card offers the download with its marginal size. `APhotoIsOfferedToTheDecoderOnlyWhenTheModelsForItAreThere`, `AMissingCapabilityIsNeverAFailure` and `WithNoMeaningModelTheSameQueryFindsNothingAndOffersTheDownload` are the three that say so.
- **Words in documents still work with nothing installed at all**, which is what makes "Not now" a safe answer on the first screen.
- **Photos and speech are gated separately**, in one drain: with the picture models present and whisper absent, a photo indexes and a sound file skips.
- **Enabling a capability re-queues exactly the kinds it covers and nothing else**, once - the second launch queues nothing - a capability installed *after* another that shares its model family still clears its own backlog, and a change to one family's version does not disturb the other. `EnablingPicturesQueuesThePicturesAndNothingElse`, `ACapabilityWhoseBacklogIsAlreadyClearedQueuesNothing`, `ACapabilityAddedAfterAnotherInTheSameFamilyStillClearsItsBacklog` and `AChangeToOneModelFamilysVersionDoesNotDisturbTheOther` are the four that say so.
- **And the re-queued files are actually read.** A document already indexed by Plan 4, whose bytes have not moved, is opened again when meaning arrives - because the queue's reason is `Indexer.Recheck` and nothing else. `ADocumentAlreadyIndexedIsOpenedAgainWhenMeaningArrives` is the test, and `AnOrdinaryQueueEntryStillLeavesAnUnchangedFileAlone` is the control that stops it being satisfied by deleting the freshness check.
- **No diagnostic acquires a writer on the real vector store.** After `--searchtest`, `--searchbench` and `--searchindex` have all run on a machine with no index, `%LOCALAPPDATA%\Findra\index\vectors.bin` still does not exist.
- **The vector store is flushed before the transaction that references it commits, and rows are released after it.** `TheVectorStoreIsFlushedBeforeTheDatabaseCommitsAndReleasedAfter` is the test.
- **A document skipped for being too large or having no text is not re-opened by a new document model**, because a new model cannot help it.
- **The vector rows a replaced or deleted file was pointing at are released.** A photo deleted a year ago does not answer a query.
- **Content search answers by meaning**: a document that never contains the typed word is found because it means it, one row per file, the grammar still applied, and a transcript row carries the second it was said at.
- **The execution provider is chosen by trying**, the first that initialises wins, later candidates are not even constructed, and nothing in the tree names CUDA, ROCm, TensorRT, OpenVINO or CoreML.
- **Every project in the tree targets `net10.0-windows10.0.19041.0` and no project pins a RID.** `dotnet publish --self-contained -r win-x64` works, and the report says whether `win-arm64` restores and, if not, which package stopped it.
- **The vector file's first four bytes are `FVS1`.**
- `dotnet test` green, `dotnet build -warnaserror` clean, `grep -ric prism src/ tests/` = 0, no em-dashes, no emoji, and a human-read comment pass over the six ported files done and reported.

---

## Why each test can fail

A test whose assertion is satisfied by the fallback path is a defect in the plan, not a spare
assertion: it reports green while the behaviour it names is broken, and it is worse than no test
because it stops anyone writing the real one. **This project has produced seven of them**, and the
first draft of this plan added eight more - four of which could not pass at all, because the fake
they asserted through did not implement the rule the code under test used.

So: every test below is listed with **the input or implementation that makes it fail**. If you
change or add a test while executing this plan, add its row here. If you cannot name an input that
breaks it, the test is not pulling its weight - say so rather than leaving it.

Three categories were watched for particularly, because each produces tests that cannot fail:

- **Anything asserting that a fallback happened.** "It skipped the photo because the model is
  missing" is what the code does today with no capability logic at all, and "it fell back to the
  CPU" is satisfied by a build that only ever uses the CPU. Every such test is paired with its
  opposite - the decoder IS asked when the capability is there, the FIRST provider wins when it
  works - and in `DecoderGateTests` the pair is asserted through a fake whose `CanRead` calls the
  production rule, so a mutation of that rule moves both halves.
- **Anything about a race or a lock.** Plan 4 produced a concurrency test that passed thirteen
  times out of thirteen with the lock deleted. **This plan adds no timing test.** Where a
  behaviour looked like it wanted one it is written as a determinism test instead:
  `AReaderSeesOnlyWhatTheWriterFlushed` is about the header count, and
  `TheVectorStoreIsFlushedBeforeTheDatabaseCommitsAndReleasedAfter` is about a recorded order.
  Both fail deterministically. The real concurrency answers are structural and are in
  **Execution notes**.
- **Anything asserting a relationship between constants.** Arithmetic between four literals in one
  file is not behaviour, and a port that keeps the constants and stops *using* them passes it.
  `ASizeGateIsAppliedAndNotMerelyDeclared` asks the function the decode arms call.

### Task 1 - `ProjectFileTests` (4)

| Test | Fails when |
|---|---|
| `EveryProjectTargetsTheWindowsSdkFlavourTheDecodersNeed` | any project keeps `net10.0-windows` - which is the state before this task, and the realistic partial failure is the *test* project being left behind |
| `NoProjectPinsARuntimeIdentifier` | `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` is added to make a native package restore |
| `TheNativeBearingPackagesArePinnedToTheVersionsThisPlanTested` | a package is missing, or a version floats to a different one |
| `NoVendorLockedExecutionProviderIsReferencedAnywhere` | `Microsoft.ML.OnnxRuntime.Gpu` or a CUDA package is added |

### Task 2 - `ModelStoreTests` (11)

| Test | Fails when |
|---|---|
| `TheSevenFilesAreTheOnesTheSpecMeasured` | a file is renamed, dropped, or the order changes |
| `EveryDeclaredSizeIsTheMeasuredOneAndNotTheConservativeFloor` | **the port keeps only `MinBytes` and reuses it for display** - the two become equal and every one of the seven rows fails |
| `TheWholeSetIsTwoPointNineThreeGigabytes` | any of the seven `Mib(...)` values drifts from the spec's table - the total moves and the README's number stops being true |
| `EveryModelCarriesAHttpsUrlAndAPurposeSomebodyCanRead` | a URL is pasted as `http://`, or a purpose is left empty and the `--models` row has nothing to say |
| `AFileShorterThanItsFloorIsNotPresent` | **`Present` is `File.Exists`** - the 10-byte file reports present, which is how a half-written file becomes permanently installed |
| `PresenceIsCheckedAgainstTheFloorAndNotTheDeclaredSize` | `Present` compares against `Bytes` - a re-published model a few kilobytes smaller costs everyone a re-download |
| `AMissingDirectoryIsANormalStateAndNotAnException` | `Present` lets a `DirectoryNotFoundException` out - the count assertion never runs |
| `MissingNamesExactlyWhatIsNotThere` | `Missing` returns everything, or nothing, rather than the difference |
| `ModelsLiveUnderLocalAppDataAndNeverBesideTheExecutable` | `Dir` is pointed at the publish folder (wiped on upgrade) or at Roaming (2.9 GB following somebody between machines) |
| `SizesReadTheWayThePersonPayingForThemWouldWriteThem` (6 rows) | truncation instead of rounding → `899 MB`; one decimal for GB → `2.9 GB`, which is the floor's total not the measured one; decimal MB → `990 MB` |
| `SizesReadTheSameOnEveryMachine` | a bare `{n:0.##}` without `InvariantCulture` → `2,93 GB` under de-DE |

### Task 3 - `CapabilityTests` (25)

| Test | Fails when |
|---|---|
| `PhotosNeedNothingButTheirOwnThreeFiles` | the closure adds a prerequisite Photos has not got, or a file is assigned to the wrong capability |
| `SpeechPullsInTheDocumentModelsBecauseATranscriptIsADocument` | **`Close` returns its input** - the obvious first implementation, and the one that makes Speech install and then answer nothing |
| `HebrewCannotBeTakenWithoutTheGeneralModelItSecondPasses` | **`Close` walks one level and stops** → `{Hebrew, Speech}`, 2 not 3, missing the e5 pair Speech itself needs |
| `ClosingAnAlreadyClosedSetChangesNothing` | the closure grows on each pass - it runs at every click, so it would end up selecting everything |
| `DroppingSomethingDropsWhateverDependedOnIt` | **a naive `Remove`** leaves Hebrew selected with no general model - a download set that installs and does nothing |
| `DroppingSomethingLeavesWhatMerelySharesFilesWithIt` | `Drop` confuses "shares files with" for "depends on" and takes Meaning away with Speech |
| `TheSizeBesideARowIsWhatItAddsToWhatIsAlreadyChosen` | **a fixed per-capability table** - both assertions return the same number, which the spec says makes the total visibly fail to add up |
| `TheMarginalCostOfSomethingAlreadyChosenIsNothing` | the marginal is the capability's own total regardless → a ticked row still shows 629 MB |
| `HebrewsMarginalCostIsTheFineTuneAloneOnceSpeechIsThere` | the marginal ignores the closure and reports only `OwnModels` → the second assertion is 1.5 GB instead of 2.3 |
| `ATotalCountsAModelSharedByTwoCapabilitiesOnce` | the total sums per-capability closures → 1.06 GB instead of 818 MB; the second assertion is what catches it |
| `EverythingIsTheNumberOnTheReadme` | any size drifts, or `All` gains or loses a capability |
| `TheThreePresetsAreTheOnesOnTheFirstScreen` | Recommended is not closed, or is `{Photos}` alone, or Everything omits Hebrew |
| `ASelectionThatIsNoPresetIsCustom` | `Match` returns the nearest preset rather than Custom |
| `EnablingACapabilityCoversExactlyTheKindsItCanRead` | a capability claims every kind → the whole disk is re-indexed, which spec §2a names as the worst outcome |
| `NoCapabilityClaimsAKindWithNoContentToRead` | `File` or `Folder` is added to a covered set → `RequeueKinds` queues rows the indexer will only skip again |
| `HebrewIsOfferedOnlyWhereHebrewIsInstalled` (6 rows) | **a substring test on "he"** → the `th-TH` row is true, and a Thai user is shown a 1.5 GB Hebrew row; always-true fails the first three rows |
| `AQueryForPicturesOffersThePictureCapabilityAndItsPrice` | the offer is silent, names the wrong capability, or quotes the whole 2.9 GB |
| `NothingIsOfferedForACapabilityThatIsAlreadyInstalled` | **the offer is unconditional** - the pair with the test above is what catches it |
| `AQueryForSoundOffersSpeechAtWhatItWouldActuallyCostThisMachine` | the offer text uses a fixed size → both assertions read the same number |
| `AnOrdinaryWordQueryOffersMeaningAndNotThePictureModels` | the offer rule tests kinds in the wrong order, or defaults to Photos because it is first in the enum |
| `WithEverythingInstalledThereIsNothingToOffer` | the installed check is missing on any branch - one branch per assertion |
| `HebrewIsNeverOfferedFromTheCard` | Hebrew is added to the offer chain - a 1.5 GB decision taken in a search box |
| `AnInstalledSetIsReadFromTheFilesOnDiskAndNotFromASetting` | `Installed` reads a setting → the first assertion (nothing on disk) reports the capability as present |
| `ACapabilityWhoseFilesArePartlyThereIsNotInstalled` | **`Any` where `All` belongs** → one of the e5 pair lights the capability up and every query then fails on the other |
| `SpeechIsNotInstalledWhileTheDocumentModelsItNeedsAreMissing` | `Installed` uses `OwnModels` rather than the closed set → a machine with no whisper model at all still reports Speech as ready once part of the e5 pair is there. The second assertion pins the same rule for Meaning, so a fixture that accidentally satisfied one would not satisfy both |

### Task 4 - `ModelDownloadTests` (9)

| Test | Fails when |
|---|---|
| `AFinishedFileIsNotFetchedAgain` | the presence check is missing → `asked` is non-empty, which on a real run is 2.9 GB fetched a second time |
| `APartialDownloadResumesFromTheByteAlreadyFetched` | **the `.part` is ignored and the download restarts** - the file assertion still passes, and `Assert.Equal([3L], asked)` is the only thing that catches it. Also fails under `long done = 0;`, which leaves the completeness check unsatisfied and promotes nothing |
| `ProgressCountsTheWholeFileAndNotJustThisLeg` | `long done = 0;` - progress counts only this leg → the last report is `(6, 9)`, and a resumed 1.5 GB download shows a bar that restarted. **Not** caught by the from-zero mutation, which is honest all the way through |
| `ADownloadThatEndsShortIsNotPromoted` | **the source's unconditional `File.Move`** → the final file exists at 5 bytes, above its floor, installed for ever and broken for ever |
| `AStalePartAgainstARepublishedFileStartsOver` | the 416 is not caught, or is caught and the stale `.part` is kept → the second `asked` entry never happens and the install can never complete on any future run |
| `APartThatIsAlreadyTheWholeFileIsPromotedRatherThanFetchedAgain` | the 416 handler deletes unconditionally → `asked` is `[9, 0]`, and on a real run that is a complete 1.5 GB file thrown away and fetched again |
| `CancellingLeavesThePartSoTheNextRunResumes` | the `.part` is deleted on cancel (gigabytes thrown away by a closed lid) or promoted anyway |
| `EachFileInASetIsFetchedOnceAndTheOnesAlreadyThereAreSkipped` | `GetAllAsync` re-fetches everything, or stops at the first already-present file |
| `TheIndexerChildNeverDownloadsAnything` | a port brings `EnsureAsync` back into `Indexer.Loop`, which is exactly where the source has it |

### Task 5 - `ProviderTests` (8)

| Test | Fails when |
|---|---|
| `TheFirstProviderThatInitialisesIsTheOneUsed` | **the implementation always returns the CPU**, or walks the whole chain and keeps the last → `Provider` is "CPU" and `cpuBuilt` is 1 |
| `AProviderThatCannotInitialiseHandsOverToTheNextOne` | an exception escapes instead of being caught → the test throws rather than asserting |
| `EveryProviderItRejectedIsNamedWithTheReasonItWasRejectedFor` | only the winner is recorded (what the source does) → `Tried.Count` is 1; or the reason is stored empty → the message assertions fail |
| `AProviderThatWasNeverTriedIsNotClaimedAsRejected` | the record is pre-filled from the declared chain → `Tried.Count` is 2 with a rejected row that never happened |
| `AChainWhereNothingInitialisesSaysSoWithEveryReasonInIt` | it returns `default(T)`/null instead of throwing, or the message names only the last failure |
| `TheDeclaredChainsPutTheAcceleratorFirstAndTheCpuLast` | somebody puts CPU first to close a support ticket - it still "works" everywhere and silently costs every user their GPU |
| `EveryChainEndsAtTheCpuBecauseTheCpuIsASupportedConfiguration` | a chain is trimmed to the accelerator alone → a machine with no GPU has no path at all |
| `NoChainNamesAVendorLockedProvider` | a CUDA rung is added to the ONNX chain |

### Task 6 - `VectorStoreTests` (10)

| Test | Fails when |
|---|---|
| `AVectorIsItsOwnBestMatch` | `Append` writes the wrong row, `Search` returns rows in the wrong order, or the dot product is wrong - the second assertion also catches a search that returns everything at the same score |
| `HalfPrecisionKeepsEnoughOfTheVectorToRankWithIt` | the float16 conversion is broken → the score is noise rather than ~1.0 |
| `ATombstonedRowCanNeverMatchAgain` | `Tombstone` is a no-op, or zeroes only the kind byte → row 1 is still in the answer |
| `AKindFilterAnswersOnlyWithTheKindsItWasAskedFor` | the `kinds` span is ignored → both rows come back for the restricted search, and "photos only" returns documents |
| `AStoreWrittenAtAnotherWidthIsStartedOverRatherThanRead` | the magic-and-width check is dropped → the count is 100, garbage rows and all. The fixture writes the REAL magic bytes, so it fails for the width and not for the magic |
| `AReaderSeesOnlyWhatTheWriterFlushed` | the reader derives its count from the file length → the early reader reports a non-zero count (1 in practice, because the writer's `FileStream` buffers behind the header) and can read a half-written row |
| `NormaliseLeavesAZeroVectorAloneRatherThanProducingNaN` | a bare divide → NaN, which poisons every comparison it touches, silently, for the life of the file |
| `NormaliseMakesAVectorUnitLength` | the division is by the squared length, or is skipped |
| `TheFileFormatCarriesFindrasOwnMagicAndNobodyElses` | **the port keeps `0x50565331`** (`1SVP` on disk), **or takes the obvious `0x46565331`** (`1SVF` on disk - little-endian writes the low byte first). Only `0x31535646` reads `FVS1`. A lineage leak in a binary header that `grep -ric prism` cannot see |
| `TheStoreLivesBesideTheIndexAndNotBesideTheModels` | `DefaultPath` is left pointing at the source's index directory, or moves under `models/` where an uninstall's "keep models" would strand it |

### Task 7 - `EncoderTests` (8)

| Test | Fails when |
|---|---|
| `APictureBecomesThreePlanesInTheOrderTheModelWasTrainedOn` | **an interleaved HWC layout** (same length, all three plane assertions fail); **BGR read as RGB** (planes 0 and 2 swap); **a 0..1 scaling** (the green and blue planes hold 0, not -1). Three separate wrong implementations, none of which throws |
| `AMidGreyLandsInTheMiddleOfTheModelsRangeAndNotAtAHalf` | `/255` instead of `/127.5 - 1` → 0.5 rather than ~0 |
| `AWidePictureIsSquashedRatherThanCroppedSoItsEdgesSurvive` | **a centre crop** → the green band at the left of the source is not in the output and the first assertion fails. A length-only assertion cannot catch this, because a crop produces an output of exactly the same size - which is what an earlier draft of this test asserted and nothing else |
| `MeanPoolingIgnoresThePaddingItIsMaskedAgainst` | the mask is ignored → the 1000s drag the mean to 335 |
| `MeanPoolingNothingIsZeroRatherThanADivideByZero` | the divide is unguarded → NaN |
| `TheVocabularyShiftPutsEveryTokenWhereTheModelExpectsIt` | the `+1` shift is dropped → `[0,0,5,7,2]`; the `0 → 3` case is dropped → `[0,1,6,8,2]`, which collides with `<pad>`. Nothing throws either way: the model quietly reads a different sentence |
| `APassageLongerThanTheModelIsCutButStillClosedProperly` | the truncation drops the closing `</s>` → the last id is 10, and every long passage reads as unfinished |
| `AChunkIsEmbeddedWithItsFileNameInFrontOfIt` | the name is left off (a Hebrew-named contract is then unfindable from a chunk that never says "lease"), the separators are not spaced, or the extension is left on |

### Task 8 - `MediaTests` (13 methods, 21 cases)

| Test | Fails when |
|---|---|
| `AClipShorterThanOneStepIsStillSampledOnce` | a loop starting at `every` → no frames at all for every short video on the disk |
| `ALongFilmIsSpreadOverItsWholeLengthAndNeverExceedsTheFrameBudget` | a fixed step with no budget → 3,600 frames, an afternoon of GPU for one file; a budget applied by truncation → the last sample is at 900 s of 36,000 and the film is indexed by its first fifteen minutes |
| `EverySampleIsInsideTheVideo` | an off-by-one past the end → the decoder is asked for a frame that does not exist, once per file |
| `AVideoOfNoLengthIsSampledNowhereRatherThanAtZero` | a duration of 0 yields one sample → every unreadable video is decoded at t=0 |
| `SilenceHallucinationsAreDroppedAndRealSpeechIsKept` (5 rows) | the bracket rule is dropped → every silent stretch adds `[Music]` to the index; an over-eager `Contains('[')` → the fifth row fails and real speech is thrown away |
| `TranscriptLinesAreMergedIntoWindowsASentenceFitsIn` | no merge at all → 10 segments, and a phrase spanning two whisper lines is findable in neither |
| `TheLastWindowIsFlushedEvenWhenItNeverFilledUp` | **the final `Flush()` is missing** → the tail of every transcript on the machine is absent, and `word29` is the assertion that names it |
| `NoWordIsLostBetweenTwoWindows` | the buffer is cleared without being written, or a line is dropped at a boundary |
| `EverySpeechSegmentCarriesTheVectorRowItWasGiven` | the callback's return is discarded → the transcript is in the store with nothing pointing at it and can never be found by meaning |
| `AnEmptyTranscriptIsNoSegmentsRatherThanOneEmptyOne` | the unconditional flush writes an empty segment → an empty row in FTS for every silent file |
| `ARecogniserReadingTheWrongScriptIsThrownAway` (5 rows) | the ratio test is dropped → row 3 fails and every screenshot carries a line of hallucinated Hebrew into FTS; the length floor is dropped → row 4 fails |
| `APictureOnDiskDecodesToAnImageAtTheSizeAsked` | the decode returns null for a valid PNG, ignores `maxDim`, or squashes the aspect ratio |
| `SomethingThatIsNotAPictureDecodesToNothingRatherThanThrowing` | `SKCodec.Create`'s null is not handled → an exception on the UI thread for every text file somebody arrows onto |

### Task 9 - `DecoderGateTests` (16 methods, 22 cases) and `TranscribeLimitTests` (8 methods, 22 cases)

Every one of these asserts through a fake whose `CanRead` calls `Decoders.Covers`, so a mutation of
the production rule moves the fake with it. That is what makes "the decoder was never asked" an
assertion rather than a restatement - and it is what an earlier draft got wrong, with one fake that
gated and one that did not, and four tests that could not pass.

| Test | Fails when |
|---|---|
| `APhotoIsOfferedToTheDecoderOnlyWhenTheModelsForItAreThere` | `Covers(Photo, ...) => true` → the first half fails (`Asked` is not empty with nothing installed); `=> false` → the second half fails (`Asked` is empty with Photos installed). **Both directions**, which is the point |
| `SpeechAndPicturesAreGatedSeparatelyAndNotTogether` | one gate for all model-backed kinds - the all-or-nothing gate this replaces → both are asked or neither is, and the `Asked` kinds are `[Photo, Audio]` or `[]` |
| `AVideoIsWorthOpeningForItsFramesOrForItsSoundAndNotOnlyForBoth` | the OR becomes a reverse lookup (`KindsCovered(c).Contains(kind)` with `First`) → Photos wins and the speech-only assertion fails; an AND → both single-capability assertions fail |
| `AMissingCapabilityIsNeverAFailure` | a skip is recorded as Failed → `failed` is 3, the files appear in `--searchindex`'s failure sample, and `RequeueKinds` (which deliberately skips Failed) never picks them up when the capability arrives |
| `AFileSkippedForWantOfAModelSaysThatIsWhy` | the reason is left empty or borrowed from the size gates → `CapabilityGate`'s exclusion list and `--searchindex`'s models section key on a string that is not there |
| `WordsInDocumentsStillWorkWithNoModelAtAll` | `Covers(Document, none)` returns false → full-text search is taken away from everybody who declined the download |
| `AReplacedFilesOldVectorRowsAreReleased` | **`_ = _db.Upsert(...)`, which is what the code does today** → `Released` is empty, and the old embedding of an edited document keeps matching beside the new one |
| `ADeletedFilesVectorRowsAreReleasedToo` | `_ = _db.Delete(...)` → a deleted photo answers queries for ever |
| `AFileThatFailsWhileBeingReadAlsoReleasesTheRowsItHeld` | the third discarded return on the failure path (`Indexer.cs:343`) → a file that indexed once and later throws keeps its rows for ever, and nothing will tombstone them because the item now says Failed |
| `TheVectorStoreIsFlushedBeforeTheDatabaseCommitsAndReleasedAfter` | the flush moves after the commit → the probe reports the queue already empty at flush time, `CommitHadHappenedAtFlush` is true, and a child killed in between leaves database rows pointing past the vector header's count, silently and permanently unmatched. The release moves inside the transaction → the read-only connection still sees the row pending, `CommitHadHappenedAtRelease` is false, and a rollback leaves the surviving segments pointing at zeroed vectors. **The commit is observed, not announced**: a second connection sees the last committed snapshot, so `PendingCount() == 0` is true only after the transaction lands. An earlier draft asserted on a `"commit"` marker that nothing appended, and the test could not run at all |
| `ALongVideoWhoseFramesWereReadIsIndexedAndSaysWhatItDidNotHear` | `Handle` derives the state from "is there a recorded reason at all" - which is what it did before `KindResult` grew a third field, and is the shape a port of the source lands on → the row is `StateSkipped`, the first and third assertions fail, and every film whose frames were read counts as unread in `--searchindex`. Or `Note` is dropped on the way to `items.error` → `CountRecorded` is 0, and raising the transcription limit later reaches nothing. It is also what makes Task 11's hand-built fixture a shape the product really writes rather than a test of SQL |
| `AFileWithNothingReadAtAllIsStillSkipped` | `Skip` stops deciding the state - the row reports Indexed and the first assertion fails; or `Skip` is dropped on the way to `items.error` (only `Note` is written) and the recorded reason comes back null. **Honest limit:** this pair does *not* discriminate `Skip`-decides-the-state from the `Segments.Count > 0` heuristic, because no arm in this plan returns segments and a `Skip` together, so the two rules agree on every input the product can produce. The explicit field was chosen anyway - a heuristic that is right only while nothing exercises the difference is a rule nobody can read off the type - and that choice is defended at `KindResult`'s doc comment rather than by a test pretending to prove it |
| `TheGeneralModelIsAlwaysTheFirstPassAndTheFineTuneIsOnlyEverTheSecond` | `SpeechModels` returns the fine-tune as `General` when Hebrew is installed → every English file is transcribed by a Hebrew model, and nothing about the output looks wrong enough to notice; or it returns the fine-tune when Hebrew is *not* installed → the second assertion |
| `ASizeGateIsAppliedAndNotMerelyDeclared` (7 rows) | any gate is dropped from `SizeGate` → that row returns null. One row per rule, and it asks the function the decode arms call rather than comparing four constants to each other |
| `ARecordingLongerThanTheLimitIsSkippedForAReasonOfItsOwn` | `TooLong` is made an alias of `TooLarge`, or of `NoModel` → the equality assertion for that constant fails. This is the fifth meaning of the recorded reason and the only one a user can change from a settings control: sharing a string with "too large" makes raising the limit sweep up every enormous document, and sharing one with "no model" makes a download re-queue every long recording |
| `NoPerKindRecordingConstantSurvivesBesideTheSetting` | `MaxAudioSeconds` or `MaxVideoSpeechSeconds` is ported as behaviour → the reflection assertion names it. Two constants deciding this re-introduce an asymmetry between audio and video that is invisible in the interface: a sound file and a video of the same length would behave differently for no reason anybody could see |
| `TranscribeLimitTests.TheRuleIsZeroIsOffNegativeIsNoLimitAndPositiveIsMinutes` (8 rows) | 0 read as "no limit" → row 1 transcribes everything on a machine that asked for nothing; negative read as 0 → row 3 transcribes nothing for somebody who asked for everything; `-1` special-cased instead of "any negative" → row 4; `<` instead of `<=` → row 6, which is a recording exactly five minutes long, which is what a voice-memo app produces |
| `TranscribeLimitTests.ThePresetsAreTheOnesTheSpecNames` | a preset is added, dropped or renumbered away from the spec's table |
| `TranscribeLimitTests.APresetAndATypedValueAreTheSameSetting` | the preset name is stored in a second field rather than derived → `Named` and `Describe` can disagree with the number, which is exactly what spec §6 forbids; or `Describe` returns null for a typed value → the settings screen shows a blank |
| `TranscribeLimitTests.EveryPresetHasAName` | a preset is added to the list and not to `Named` → a settings control with an unlabelled entry |
| `TranscribeLimitTests.APresetNameAndABareNumberBothParse` (7 rows) | the parse handles only one of the two forms → `--content limit 17` or `--content limit "2 hours"` is refused |
| `TranscribeLimitTests.AWordThatIsNeitherIsRefusedRatherThanTreatedAsZero` (3 rows) | the parse falls back to 0 → a mistyped number silently turns transcription off, which is a real setting and therefore an invisible failure |
| `TranscribeLimitTests.EverySettingReadsTheSameOnEveryMachine` | a bare `ToString()` on the number → the de-DE render differs, and `Parse` stops reading its own output |

### Task 10 - `SemanticBranchTests` (12)

| Test | Fails when |
|---|---|
| `ThePictureBandStretchesTheNarrowRangeTheModelActuallyUses` | **the raw cosine is used** → `PhotoScore(0.20)` is 0.20, every photo scores about 0.1, and half of them tie; the ceiling is dropped → the 0.90 row exceeds 0.92 |
| `TheTextBandStartsWhereTheModelStopsSayingEverythingIsSimilar` | the floor is 0 → `TextScore(0.78)` is 0.78 and every document is a weak match for every query |
| `AFileFoundOnlyByMeaningIsInTheAnswer` | there is no vector branch → 0 rows. The document never contains "lease", so FTS cannot rescue it and this cannot pass by accident. Also fails if the semantic row's `Why` is left as the FTS wording - Step 3 fixes all six strings in a table, so this is a check on the branch and not a guess about a string |
| `WithNoMeaningModelTheSameQueryFindsNothingAndOffersTheDownload` | a null encoder throws → the test errors rather than asserting; the offer is not wired into the note → `Note` has no size in it |
| `WithNoSemanticStoreAtAllTheWordsStillAnswer` | a null `Semantic` throws → the free capability is broken for everybody who took nothing |
| `PicturesContributeNothingWhenTheirModelIsAbsentAndThatIsNotAnError` | the image branch runs with a null encoder, or runs the text encoder's vector against the image kinds → 2 rows |
| `APictureThatMerelyResemblesTheQueryALittleIsNotAMatch` | the floor is dropped → every photo in the library is a weak match for every query |
| `AFileThatMatchesBothWordsAndMeaningOutranksOneThatMatchesOnlyMeaning` | the both-branches bonus is dropped → the two rows tie at 0.9 and the order falls to the tie-break, which is path length |
| `OneRowPerFileEvenWhenBothBranchesFindTheSameFile` | the per-path dedupe is applied per branch rather than across them → 2 rows for one file |
| `AMomentInATranscriptCarriesTheTimeItWasSaid` | `MomentSeconds` is left at -1 for a speech segment → a result that makes somebody scrub an hour of audio by hand; or the FTS pass **replaces** the vector row instead of raising its score, which throws away the timestamp and the `Why` the vector row carried. Step 3 says "bump, do not replace" for exactly this |
| `TheGrammarStillAppliesToWhatMeaningFound` | `q.Allows` is applied only on the FTS branch → both files come back and `ext:pdf` silently means nothing on half the answer |
| `AnEmptyIndexStillSaysSoRatherThanOfferingADownload` | the offer note is emitted whenever there are no rows → an index with nothing in it is explained by a missing model, which is a lie that sells something |

### Task 11 - `CapabilityGateTests` (23), plus 3 in `ConfigTests` and 3 in `IndexStatusTests`

| Test | Fails when |
|---|---|
| `EveryRequeueCarriesTheReasonTheIndexerReopensAnIndexedFileFor` | the reason is a friendly sentence rather than `Indexer.Recheck` - which is what an earlier draft of this plan prescribed |
| `ADocumentAlreadyIndexedIsOpenedAgainWhenMeaningArrives` | **the same mutation, end to end**: with a free-text reason the row is dequeued "current", `Asked` is empty, the queue drains at full speed and not one embedding is written. This is the test the first draft did not have, and without it "enabling meaning in documents" is a silent no-op for every document on the disk. **Its fixture has to store the file's real `LastWriteTimeUtc.Ticks`** - the freshness check is three clauses AND-ed together and this test is about the first, so a stored mtime of 0 falsifies the third instead, the row is opened whatever the reason says, and the test passes against the bug it exists to catch. A second draft of this plan shipped exactly that |
| `AnOrdinaryQueueEntryStillLeavesAnUnchangedFileAlone` | the freshness check is deleted to make the test above pass → every journal event re-reads a file whose bytes did not change |
| `EnablingPicturesQueuesThePicturesAndNothingElse` | the plan queues every kind → the whole disk is re-read |
| `ACapabilityWhoseBacklogIsAlreadyClearedQueuesNothing` | **the plan is unconditional** → every launch re-queues every photo, for ever |
| `ACapabilityAddedAfterAnotherInTheSameFamilyStillClearsItsBacklog` | **the stamp is keyed on the model family** → Speech finds `e5` already stamped by Meaning, the plan is empty, and every audio file stays skipped for ever. This is the ordinary path - Recommended, then Speech - and nothing else in the suite covers it |
| `AddingASecondCapabilityLeavesTheFirstAlone` | the plan is recomputed from the installed set with no stamp check → the photos are queued again alongside the documents |
| `AChangeToOneModelFamilysVersionDoesNotDisturbTheOther` | the stamp's value does not carry the family version → a bump is invisible and nothing is re-embedded; or one version covers both families → every photo is re-read for a change to the document model |
| `SpeechAndHebrewEachClearTheirOwnBacklogAndTheQueueIsNotDoubled` | the two are merged into one entry → one stamp discharges the other's debt; the queue is not keyed on (volume, frn) → six pending rows for three files; or `Apply` sums the per-capability returns instead of measuring the queue's delta → it reports six files queued when three moved, which is the number the log line and `--models`' closing sentence show somebody |
| `NothingInstalledPlansNothing` | the plan is derived from the stamps alone → work is planned for capabilities that are not there |
| `ApplyingThePlanQueuesTheSkippedFilesAndRecordsThatItDid` | the stamp is not written → the last assertion plans the same work again on the next launch |
| `ApplyingThePlanTouchesNoOtherProcessesMetaRows` | `index:` or `indexer:` is reused as the prefix, or the stamp write clobbers a bare key → one assertion per prefix |
| `ARequeueForNoKindsIsNothingRatherThanACrash` | **the unguarded call reaching `Begin`**, which is the state today. Not the SQL: the bundled SQLite accepts `IN ()` as an empty list and matches nothing. It is the transaction, which is a nested-transaction `InvalidOperationException` in a caller already inside a scope |
| `TheDocumentRequeueLeavesAloneWhatNoModelCouldHelp` | the reason filter is dropped → 3 rows, and every 200 MB dump and every empty file is re-opened on every install |
| `TheExclusionOnlyAppliesToSkippedRowsAndNeverToIndexedOnes` | the filter is written as a bare `error NOT IN (...)` → an indexed row's NULL error excludes it, a new model never re-embeds anything already read, and the symptom is indistinguishable from the C-1 bug |
| `AFileThatGenuinelyFailedIsStillNeverRetried` | the state clause becomes `IN (1,2,3)` → a broken PDF is retried on every install, for ever |
| `AFreshInstallRunsNoSchemaMigrationOverAnEmptyIndex` | `OpenSchema` treats an unstamped empty database as version 0 → `OpenedFromSchema` is 0 and `MigrationsRun` has the step in it. It asserts that no step RAN, not just that the stamp is right, which is what the retired version of this test could not do |
| `ThisPlanAddsNoSchemaMigration` | **weaker, and deliberately so.** It is a decision gate, not a behaviour test: it fails when somebody adds a migration, and it exists so that the three traps that go live at that moment are read at that moment. Do not treat it as a proof of anything |
| `RaisingTheLimitQueuesOnlyTheRecordingsItNewlyCovers` | the filter uses `notBecause` instead of `onlyBecause` → all four fixtures are queued, and on a real machine every recording already transcribed is transcribed again; no filter at all → the same; the wrong constant → the one row that should move does not |
| `ALongVideoIndexedForItsFramesAloneIsQueuedAgainWhenTheLimitRises` | the filter reads `state` rather than the recorded reason → an indexed video carrying `TooLong` as a note is missed, and raising the limit never reaches a single film |
| `LoweringTheLimitQueuesNothing` | `PlanForLimit` compares the two numbers without ranking → 5 versus 120 looks like a change and queues work that will only be skipped again |
| `TurningTranscriptionOffQueuesNothingEitherWay` | `Off` (0) is ranked as a number rather than as the least permissive setting → turning transcription off queues every recording on the disk |
| `AnUnchangedLimitQueuesNothingOnEveryLaunch` | the recorded value is not written, or is written before the comparison → the re-queue runs at every start, and on a machine with a large archive that is a re-transcription every time Findra opens |
| `NoLimitIsHigherThanEveryNumberAndNotLowerThanAllOfThem` | **a plain `now > was`** → `-1` reads as the least permissive setting, "no limit" queues nothing at all, and going the other way queues everything. The sign convention is the one place this arithmetic bites |
| `ConfigTests.ReadingInsideFilesIsOffUntilSomebodyAsksForIt` | the default is left at "on" - which is what the field it replaces meant - so a fresh install starts reading every drive without being asked. The three loads cover the default, a missing file and an empty object |
| `ConfigTests.TheTranscriptionLimitDefaultsToTheCheapPreset` | the default is 0 (nothing is ever transcribed and speech looks broken) or a large number (an hour of whisper on a laptop nobody asked for) |
| `ConfigTests.ANegativeTranscriptionLimitSurvivesTheRoundTrip` | a clamp is added "for safety" → the most expensive setting in the product silently becomes the cheapest; or 0 is treated as unset and replaced by the default → "off" is impossible to choose |
| `IndexStatusTests.AnIndexNobodyHasAskedForSaysSoRatherThanLookingIdle` | the new branch is missing → an empty index and a finished one produce the same line, and the card says "up to date · 0 files" about a machine that has never read anything |
| `IndexStatusTests.TurningItOffAfterReadingSomethingSaysHowMuchItAlreadyHas` | the two off states share one sentence → somebody who turned it off is told their 9,000 files are gone |
| `IndexStatusTests.OffIsNotTheSameSentenceAsPausedWhileFindraIsClosed` | the switch and the closed-app pause share a sentence → two states with opposite answers ("turn it on" and "leave Findra open") read identically |

### Task 12 - `ModelsCommandTests` (13 methods, 20 cases) and `ContentCommandTests` (5)

| Test | Fails when |
|---|---|
| `APresetIsNamedTheWayTheFirstScreenNamesIt` (4 rows) | a name drifts from the screen's wording, or the parse is case-sensitive |
| `AWordThatIsNotAPresetIsRefusedRatherThanGuessedAt` (3 rows) | the parse falls back to a default → `--models install all` quietly installs something nobody asked for; `custom` is accepted → the one preset that is not a choice becomes one |
| `TheListingShowsEveryCapabilityAndWhatItWouldAddToWhatIsThere` | a fixed per-row size → the bare and the with-documents listings show the same number for Speech |
| `AnInstalledCapabilityIsShownAsInstalledAndCostsNothingMore` | the listing is computed without the installed set → an installed capability is offered at full price |
| `TheFreeCapabilitiesAreNamedSoNobodyThinksSearchIsOff` | only the paid rows are listed → "just names" reads as "no search"; or reading words inside pictures is left out, which is the free behaviour nothing else in the product mentions |
| `HebrewIsListedOnlyWhereItIsWorthAGigabyteAndAHalf` | the row is unconditional → a 1.5 GB line in front of every user; or it never appears → the capability is unreachable on the machines it is for |
| `TheListingSaysWhatEverythingWouldCostAltogether` | the total is a sum of the displayed rows → the shared e5 pair is counted twice |
| `TheListingReadsTheSameOnEveryMachine` | a bare `{n:N0}` → the de-DE render differs |
| `AskingForHebrewAsksForEverythingItNeeds` | the closure is not applied at the command line → 1.5 GB is fetched and cannot be used, because there is no model to detect a language with |
| `AListOfCapabilitiesIsTakenTogetherAndClosedOnce` | each name is closed and installed separately → the download order is wrong and the intermediate states are unusable |
| `AnUnknownCapabilityNameIsRefusedRatherThanIgnored` | a bad name is dropped → `--models install photos,speach` installs photos, reports success, and somebody waits for speech search that is never coming |
| `ContentCommandTests.AFreshInstallSaysReadingInsideFilesIsOffAndHowToStart` | the status is rendered from the counts alone → a fresh install reads as "up to date", because zero queued and zero indexed is byte-for-byte what a finished index looks like. The `DoesNotContain` half is what catches it |
| `ContentCommandTests.TurnedOffAfterReadingSomethingSaysHowMuchItKept` | the two off states share one sentence → the switch reads as destructive and nobody touches it again |
| `ContentCommandTests.TurnedOnItReportsTheQueueRatherThanTheSwitch` | the "turn it on" advice is printed unconditionally → it is shown to somebody who already did |
| `ContentCommandTests.TheTranscriptionLimitIsAlwaysNamedInWordsSomebodyCanRead` | `Describe` is bypassed for a raw number → `-1` is printed, which reads as an error rather than as the most permissive setting in the product |
| `ContentCommandTests.TheStatusReadsTheSameOnEveryMachine` | a bare `{n:N0}` → the de-DE render differs |

### Task 13 - `SearchModelsReportTests` (11)

| Test | Fails when |
|---|---|
| `EveryModelIsListedIncludingTheOnesThatAreNotThere` | absent rows are filtered out → "why are no photos indexed" is unanswerable |
| `APresentModelsSizeOnDiskIsPrintedBesideTheOneItShouldBe` | only one of the two numbers is printed → the README's sizes cannot be checked against real files |
| `AFileThatIsThereButTheWrongSizeIsFlagged` | the two are printed but never compared → a truncated file reads as installed |
| `ACapabilityWithSomeOfItsFilesIsNotReportedAsReady` | `Any` where `All` belongs, or the have/needs counts are not printed |
| `ACapabilityThatIsOffSaysWhatItWouldCostToTurnOn` | the marginal size is omitted, or a fixed number is used |
| `TheChosenProviderAndEveryRejectedOneAppearWithItsReason` | only the chosen provider is printed - the source's behaviour, and the half that answers the support question |
| `AProviderThatWasNeverTriedIsNotClaimedAsRejected` | the report is rendered from the declared chain rather than from what was tried → four provider rows instead of three. It counts the two markers `ModelsReport.Chosen` and `Rejected` rather than slicing the text on a word whose capitalisation it cannot see |
| `AMachineWithNoModelsAtAllProducesAWholeReportAndNotAnError` | the report short-circuits when nothing is present, or renders the ordinary state in the language of a failure |
| `TheFreeCapabilityIsNamedSoNobodyThinksSearchIsOff` | the free rows are left out → a machine with no models reads as a machine with no search |
| `TheReportReadsTheSameOnEveryMachine` | a bare `{n:N0}` → the de-DE render differs |
| `ANoteFromTheRunItselfIsCarriedThrough` | `Notes` is a field with no reader - the exact defect Plan 4 shipped twice |

### Task 14 - the close-out tests (8 added)

| Test | Fails when |
|---|---|
| `TheAcceleratorLineNamesBothRuntimesAndWhatEachOneGot` | the line keeps `Machine.NoAccelerator`'s constant, or names only one runtime → a benchmark on a machine running DirectML for vision and the CPU for whisper reports one of the two |
| `AMachineWithNoModelsSaysSoRatherThanClaimingACpuFallback` | "CPU" is printed for a runtime that never loaded → the fragment publishes a measurement of something that did not run |
| `EveryCapabilityIsListedWithWhatIsWaitingOnIt` | one total instead of a per-capability count → "8,000 skipped" does not say which download clears them |
| `AnInstalledCapabilityWithNothingWaitingStillAppears` | zero rows are filtered → "why is nothing happening" is unanswerable |
| `TheNameCountReadsTheSameOnEveryMachine` | the existing `{n / 1_000_000.0:0.0}M` with no culture → `1,5M` under de-DE, in the same footer sentence whose other half has a test devoted to being invariant |
| `AnIndexNobodyHasAskedForSaysSoAboveTheCounts` | the report leads with the counts → the diagnostic somebody runs to find out why nothing is happening reads as though everything is done, which is the single most misleading thing it could say |
| `TheTranscriptionLimitIsReportedInWordsAndNotAsABareNumber` | `Describe` is bypassed → `-1` in a bug report reads as a fault rather than as a setting |
| `RecordingsPassedOverForTheirLengthAreCountedSeparately` | the count is folded into the skipped total → nobody can tell whether the lever is a download or a setting |

**Total: 192 test methods, 236 cases with the theory rows.**

---

## Execution notes

### The eleven inherited traps, and where each one lands

`final-review.md` §8's list, checked against the tree rather than against this plan's prose.

| # | Trap | Where it lands |
|---|---|---|
| 1 | `RequeueKinds([], reason)` runs a statement and opens a transaction for nothing | **Task 11.** Guard plus `ARequeueForNoKindsIsNothingRatherThanACrash`. **The trap as inherited was stated wrongly**: the empty `IN ()` is not a SQL error - the bundled SQLite accepts it as an empty list and matches nothing. The real danger is the transaction, which is a nested-transaction `InvalidOperationException` in a caller that already holds a scope, so the guard has to return before `Begin`. |
| 2 | A re-queued **Skipped** file is reopened only because of one clause | **Task 11, and it is the reason that task was rewritten.** The clause protects *skipped* rows only, and this plan re-queues *indexed* ones - so the answer is not "do not simplify the clause" but "use the reason the clause honours". `Indexer.Recheck`, `EveryRequeueCarriesTheReasonTheIndexerReopensAnIndexedFileFor`, and `ADocumentAlreadyIndexedIsOpenedAgainWhenMeaningArrives` end to end. |
| 3 | `StateSkipped` is overloaded four ways | **Task 11, and the spec has since added a fifth meaning.** `Decoders.TooLong` - a recording longer than the transcription limit - is the only one a user can change from a settings control, so it gets its own constant and the re-queue that reads it runs the *opposite* way round: `onlyBecause`, exactly those rows, rather than `notBecause`, everything except some. Both filters read the `error` column, which is what lets the narrow one reach an indexed video carrying `TooLong` as a note. Tests: `TheDocumentRequeueLeavesAloneWhatNoModelCouldHelp`, its control `TheExclusionOnlyAppliesToSkippedRowsAndNeverToIndexedOnes`, `RaisingTheLimitQueuesOnlyTheRecordingsItNewlyCovers`, and `ARecordingLongerThanTheLimitIsSkippedForAReasonOfItsOwn`. |
| 4 | `--searchbench` must not write to the real index | **Fixed in the tree** by Plan 4's fix wave (`SearchBench.cs:336` is `readOnly: true`) and **re-opened by this plan through a second store**: `DrainOnce` now needs decoders, and a convenience overload would hand the benchmark a writer on the real `vectors.bin`. **Task 9** deletes the overload, fixes all three call sites, and checks the file does not exist afterwards. |
| 5 | A brand-new database treated as version 0 | **Fixed in the tree** (`OpenedFromSchema`, `MigrationsRun`). **Task 11** keeps a test that asserts no step *ran*. |
| 6 | The full pass cannot see a modified file | **Fixed in the tree** (`EnumeratedFile.Mtime`, `QueueFeeder.FillFrom`). Not inherited; this plan is silent about it on purpose. |
| 7 | `ContentDb` is one writer per flow, enforced by a thread-id check rather than a lock | **Task 11 Step 5**, and the answer is placement rather than locking: the gate runs at startup, before `_contentLoop` exists, and `--models install` runs in its own process. **There is no second in-process call site in this plan**, which is why there is nothing to marshal. Plan 6's screen downloads inside a running interface and inherits the obligation - recorded in **What comes next**. |
| 8 | The meta table is a shared namespace; pick a fourth prefix | **Task 11.** `models:cap:<capability>`, with `ApplyingThePlanTouchesNoOtherProcessesMetaRows` asserting the other three prefixes are untouched. |
| 9 | `--searchshot` must learn every new surface | **Deferred to Plan 6 with the screen**, because this plan paints nothing. Task 14 Step 3b still corrects `CLAUDE.md`'s state list, which names a `panel` state that has never existed. |
| 10 | Every displayed number must be `InvariantCulture`; one leak (m-7) | **Task 14 Step 4** fixes `IndexLineFormatter.Count`, still unfixed in the tree, with `TheNameCountReadsTheSameOnEveryMachine`. Invariant tests also in Tasks 2, 12 and 13. |
| 11 | A dead field is easy to add - grep for a reader | **Task 14 Step 5** sweeps the new fields, and the rule is in Global Constraints. Two consequences worth naming: `ContentDb.RecentSkips` is added in Task 9 *because* the skip reason otherwise has no reader, and `ContentDb.TextSegments`/`UpdateVec` stay deliberately unused - they are Plan 4's, already tested, and they are the seam a future version bump fills (Task 11's trade-off note says why nothing here uses them). The two pre-existing dead members - `VolumeStatus.Dropped` and `PurgeOrphanDeletes` - are **left alone**: they belong to the pipe and the queue, not to this plan, and touching them here would put a `Names/`-adjacent change in a capability commit. |

### The dependency shape

```
1 (framework + packages)  -- the gate, alone, first; reversible only up to Task 7
 |
 +-- 2 (model store) --- 3 (capability graph) --+-- 4 (downloads) --------+
 |                                              |                        |
 +-- 5 (providers) ----- 7 (encoders) ---+      |                        |
 |                                       |      |                        |
 +-- 6 (vector store) ------------------ +-- 9 (indexer kinds) -- 11 (gate/requeue) -- 12 (--models)
 |                                       |      |                        |            |
 +-- 8 (media, ocr, previews) -----------+      +-- 10 (semantic search) +            |
                                         |                                            |
                                         +-- 13 (--searchmodels) --------------------- +-- 14 (close-out)
```

- **Task 1 is the gate.** Nothing else compiles - not the WinRT decoders, not the ONNX types. Alone, first, and do not start anything else until `dotnet build -warnaserror` is clean on the whole tree. It is reversible up to the end of Task 7 and not afterwards.
- **Tasks 2, 5 and 6 are independent** once Task 1 lands. Task 2 owns `Models/{ModelStore,Sizes}.cs`, Task 5 owns `Models/Providers.cs`, Task 6 owns `Models/VectorStore.cs`. Disjoint, so they run concurrently.
- **Task 3 needs 2** and owns `Models/Capabilities.cs` alone. It is the most consequential single file in the plan - every visible number comes out of it - so give it its own review even though it is pure.
- **Tasks 4, 7 and 8 fan out.** Task 4 needs 2; Task 7 needs 2, 5 and 6; Task 8 needs 5. Three disjoint file sets.
- **Task 9 must be serial after 3, 6, 7 and 8.** It is where all of them meet.
- **Task 10 needs 3, 6 and 7** but not 9 - the branch reads a store a test can fill by hand. It can run beside Task 9.
- **Task 11 needs 9**, not only 3: its tests use `Decoders.NoModel`, `Decoders.TooLarge`, `Decoders.NoText`, `Decoders.TooLong`, `Decoders.Covers` and `TranscribeLimit`, and its whole subject is the reason string the indexer honours.
- **Task 12 needs 3, 4 and 11.** It is the end-to-end task: it is where a model is fetched for the first time and where the gate is watched doing its work on a real index.
- **Task 13 needs 2, 3, 5, 7 and 8** - the last because `Media.OpenWhisper` is where the whisper chain's `ProviderTry` rows come from.
- **Task 14 is last, and is not optional.** The comment pass in particular cannot be delegated to a grep.

### Files two tasks touch

Everything else is disjoint. These four are not, and each is a real conflict rather than a note:

| File | Tasks | How to resolve |
|---|---|---|
| `Card/CardWindow.cs` | 8 (one line: `DecodePreview`, `:952`) and 10 (the content-search call and the constructor) | Different methods. Keep both. |
| `Diagnostics/SelfTest.cs` | 9 (a decoder set it owns, at `:140`) and 14 (two new checks at the end) | Different regions. Task 9 lands first; Task 14 appends. |
| `Diagnostics/SearchIndex.cs` | 9 (the drain's decoders, at `:200`) and 14 (the models section in `Snapshot` and `Render`) | Different regions. Task 9 lands first. |
| `Diagnostics/SearchBench.cs` | 9 (the drain's decoders, at `:536`) and 14 (the accelerator line) | Different regions. Task 9 lands first. |

`Content/Indexer.cs` is touched by Task 9 only - Task 11 reads `Indexer.Recheck` and does not edit the file. `App/App.axaml.cs`, `App/Config.cs` and `Content/IndexStatus.cs` are touched by Task 11 only. `Program.cs` is touched by Tasks 12 and 13, in three switch arms and three usage lines; expect a small conflict and keep them all.

`Content/ContentDb.cs` is touched by **both** Task 9 (`RecentSkips`) and Task 11 (`RequeueKinds`'s
two filters) - different methods, no overlap, Task 9 first.

### Who owns what

**The meta table now has four writers and must keep having exactly four prefixes:**

| Prefix | Writer | Written by |
|---|---|---|
| `indexer:` | the `--index` child | `Indexer.Status` |
| `index:` | the interface's content loop | `App.axaml.cs` |
| bare (`schema`, `usn:`, `walk:`, `suffixes:`, `journal:dropped`) | the queue feeder | `QueueFeeder` |
| **`models:cap:`** | **the interface at startup, and `--models install` in its own process** | **`CapabilityGate.Apply`** |

**The vector store:** one writer, in the indexer child, held by `Decoders` for the life of the
process and disposed only by the `Decoders` that opened it. The interface opens read-only,
memory-mapped connections and calls `Reload()`. Diagnostics open a throwaway store in their own
temp directory, or - in `--searchindex`'s case, which drains the real index - the real one, guarded
by a `catch (IOException)` that says the running child already has it. The downloader writes no
store at all: the `.part` file is its whole durable state.

### Concurrency, honestly

There are four things here that look like races. Three of them are, and none of them is answered by
a timing test - Plan 4's lesson stands, and this plan adds no timing test at all.

1. **The gate writing while the feeder writes - real, and answered by placement.** `QueueFeeder`
   holds the writer across a whole `ContentDb.Scope`, and `ContentDb.Claim` is a thread-id detector
   rather than a lock: whichever flow arrives second gets an `InvalidOperationException`. So the
   gate runs at startup, **before** `_contentLoop` is created (`App.axaml.cs:354`), and the only
   other place a capability is installed is `--models`, which is a separate process with its own
   connection. There is nothing to marshal because there is no second in-process caller. Plan 6's
   first-run screen downloads inside a running interface and will need to post the gate onto the
   content loop's flow; that is written into **What comes next** rather than left to be found.
2. **The vector store's writer against the interface's mapping - real, and benign.** `Append` grows
   the file past the reader's fixed mapping length and the header count is written last, so an
   appended row can never be half-seen; `AReaderSeesOnlyWhatTheWriterFlushed` pins that. But
   `Tombstone` overwrites 1,536 bytes **in place**, through a coherent file-backed section a reader
   may be scanning. The worst outcome is one row scored as noise for the duration of one write:
   one wrong result, never a crash, never corruption. Worth knowing; not worth a mechanism.
3. **Two writers on the vector store - real, and removed rather than handled.** `FileShare.Read`
   means the second writer gets an `IOException`, and the second writer could only ever be a
   diagnostic. Task 9 deletes the overload that would have created one and guards the one caller
   that legitimately wants the real store.
4. **The downloader against the model directory - not a race, with one guarded exception.**
   `Present` never looks at `.part`, `File.Move(part, final, overwrite: true)` is atomic on one
   volume, and the `.part` is opened `FileShare.None`. The exception is the rename itself: if the
   indexer child holds the previous copy of that file open in an ONNX or whisper session, the
   move is refused. **Both moves in `GetAsync` are inside a `try`/`catch (IOException)`** - the
   speculative one in the 416 handler and the final one - and the final one returns
   `new DownloadOutcome(m, false, done, ex.Message)` rather than letting an exception out of
   `GetAllAsync` at the last byte. Everything fetched is still in the `.part`, so the next run
   costs nothing.

And one ordering bug that is not a race at all but reads like one, because it needs a crash to
show: the vector store must be **flushed before** the transaction referencing its rows commits,
and its tombstones written **after**. `TheVectorStoreIsFlushedBeforeTheDatabaseCommitsAndReleasedAfter`
is the test - it observes the commit through a second read-only connection rather than asking the
shipping code to announce one - and Task 9 Step 5 carries the ordering.

### Three things an agent on this machine cannot verify alone

1. **A real download and a real model load.** Every test in Task 4 runs against an in-memory server
   and Tasks 7 and 9 construct no session. Task 12 Step 5 is where that changes, and it is written
   as an instruction with a fallback (`--models install justnames`) rather than as an assumption.
2. **A second capability installed after the first.** C-3's ordinary path - Recommended, then
   Speech - is covered off-disk by `ACapabilityAddedAfterAnotherInTheSameFamilyStillClearsItsBacklog`,
   but only a real second install on a real index shows the queue moving.
3. **The elevated helper running.** Nothing in this plan touches it, and `--searchindex`'s volume
   sections stay empty without it. That is the defined non-elevated outcome, not a failure.

Say which of the three you hit and what you did instead. Do not fake any of them.

## What comes next

**Plan 6 - Settings and shipping.** It inherits four things from this plan by name, so that none of
them has to be rediscovered:

- **The first-run screen**, moved here whole. Three presets across the top with the marginal size
  beside every row, the free rows printed free, Hebrew nested under Speech and shown only where
  `Capabilities.HebrewIsOffered` says so, and the totals from `Capabilities.TotalBytes` - all of
  which already exist and are tested. It shares a section rail and a painter with the settings
  window, which is why it waited. Three obligations travel with it:
  - **Spec §9b's disclosure.** The update check is the one thing that leaves the machine, and the
    spec says it is disclosed *on the first-run screen* beside the model downloads and can be
    switched off. Nothing in Plan 5 shows a screen, so nothing in Plan 5 discloses it, and the
    obligation is unmet until Plan 6 meets it. Test that it appears on the painted screen, not
    that a `with` expression works.
  - **`--searchshot` learns `firstrun` and `firstrundownloading`**, and the shot has to render on a
    machine that has downloaded nothing. Trap 9 is deferred, not dismissed.
  - **The gate's second call site.** A download that completes inside a running interface must post
    `CapabilityGate.Apply` onto the content loop's flow rather than taking the writer from another
    thread; `QueueFeeder` holds it across a whole transaction and `ContentDb.Claim` throws rather
    than waiting.
- **`Config` gains the chosen capabilities.** Plan 5 adds `IndexContent` and `TranscribeMinutes` and
  no capability field: what is *installed* is read from the disk by `CapabilitySet.Installed`,
  which is a fact rather than an intention. The screen needs to remember what was *asked for*, and
  when it adds that field it should be called something other than `Capabilities` - an instance
  member of that name shadows the static class inside `Config` - and it needs a string-name JSON
  converter, because a `Capability[]` round-trips as integers by default and reordering the enum
  would silently reinterpret a saved file. `ThemeMode` already has one for exactly this reason.
- **The two settings this plan put on the command line want controls.** `--content on/off` becomes
  the switch that spec §6 calls "the one place the product deliberately does less until asked",
  and `--content limit` becomes the five named choices over one number - **presets over the
  number, never a second field**, because a preset name stored beside the value is exactly how a
  typed value and a preset come to disagree. `TranscribeLimit.Named`, `Describe` and `Parse`
  already do all of it; the screen supplies rectangles.
- **"Not now" is now a complete answer.** With content indexing off by default, somebody who takes
  no model and does not turn reading on has a working product - names, instantly - rather than a
  deferral. The first-run screen should say that rather than implying something is unfinished.
- **A measured uninstall.** `--uninstall`/`--purge` must state what it would free, and
  `ModelStore.ActualBytes` plus `Sizes.Human` already answer that from real files rather than from
  the declared table.
- **The README's numbers.** `--searchbench`'s accelerator line now names both runtimes and the
  provider each one got, so the machine block is complete; `--searchmodels` prints declared-versus-
  on-disk sizes, which is what makes spec §9a's "model sizes come from the real files" checkable;
  and 2.93 GB is one constant that three surfaces read.

And one thing to do rather than inherit: **decide where OCR belongs.** Reading words inside
pictures ships in this plan as free behaviour with no capability row, no download and no graph
node, because it needs no model - but the spec's §5 subsystem list and §6 capability table do not
mention it at all. Either add it to the spec or record the omission deliberately; leaving a
user-visible behaviour undescribed in the document that governs the product is how the next plan
inherits an argument.

When Plan 6 or a later plan first bumps `CapabilityGate.CurrentVersion`, read Task 11's trade-off
note before writing anything: `ContentDb.TextSegments()` and `UpdateVec()` are the untouched seam
for re-embedding stored text in place, and they exist so that a version bump does not have to
re-transcribe every hour of audio on the disk.
