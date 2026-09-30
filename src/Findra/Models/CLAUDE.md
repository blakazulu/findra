# Capabilities, models, pricing and which chip does the work

Rules for `src/Findra/Models/` (and the capability gate, first-run pricing and `--models`). The
cross-cutting hardware rules are in the root `CLAUDE.md`.

## Capabilities

Independently installable and **not peers**:

```
words in documents  ─  free, opt-in (FTS5, no model)
photos & video      ─  siglip2 vision + text + spm            629 MB   (also the words in pictures)
meaning in docs     ─  e5-base + e5-spm                      1.04 GB
speech              ─  whisper-turbo + [e5 pair]              550 MB (+1.04 GB if e5 not taken)
  └ hebrew          ─  whisper-ivrit, requires speech         1.5 GB
```

- Speech needs e5 (a transcript is embedded like a document). Hebrew needs turbo: turbo detects
  language and only Hebrew files re-run through the fine-tune - a second pass, never an alternative.
- **Everything is 3.7 GB** - measured sizes, not floors.
- **The meaning model is FULL PRECISION.** Quantised e5 (`model_quantized.onnx`) gives 0.970 cosine between DirectML and CPU
  even at `ORT_DISABLE_ALL`; fp32 agrees to 1.000000 and is faster than fp16 on CPU.
  `ProviderAgreementTests` holds the floor; `--searchmodels` prints the cosine.
- **Documents are embedded on the ACCELERATOR, queries on the PROCESSOR** (`Decoders.E5()` vs
  `ContentBranch`; ~3x faster first pass). Safe only because the providers agree.
- **Shapes are rounded, because DirectML compiles per SHAPE.** `E5Encoder.Bucket` rounds length to a
  multiple of 32, `BatchBucket` batch to a power of two. Padding is masked; a padding ROW repeats
  the first row, never empty.
- **The picture model is checked on the PROCESSOR too.** fp16 SigLIP-2 throws on CPU at
  `ORT_ENABLE_ALL` (`SimplifiedLayerNormFusion`); fp32 ships and one file serves both providers - do
  not swap to fp16. See `docs/superpowers/specs/2026-09-05-e5-full-precision-design.md`.

## Add-ons: off, remove, catch up

Settings has six sections; **Models toggle** (`Section.AddOns`, "add-on" in code) holds the four capability rows
(Content keeps the switch, "Reading now", power and the transcription limit). Not installed:
"Add · <marginal size>". Installed: a Choice of "Turn off"/"Turn on" and "Remove".
`SettingsModel.AddOnRows`.

- **Off is `Config.AddOnsOff`**, written to `AddOns.OffKey` by the pump and read by the child per
  file (`Decoders.ForThisMachine(off:)` -> `CapabilitySet.Without`). Off stops NEW reading only;
  search keeps working. Speech off takes Hebrew; Meaning off does not take Speech.
- **Every re-queue plans against what READS** (`installed.Without(off)`), or an off add-on's
  NoModel skips are re-queued every launch for a child that skips them again.
- **Coming back catches up exactly**: `AddOns.NoteAway` records when it stopped (the earlier of two
  wins), `AddOns.CatchUp` re-queues its kinds read since then (`RequeueKinds(readSince:)`, on
  `indexed_at`). Called at startup, after an install, and on Turn on.
- **Remove asks first** (`RemovePrompt`, over the pane; Escape cancels). `AddOns.FilesToDelete`
  keeps any file an add-on that stays still needs; Meaning under Speech deletes nothing, so its
  button says "Turn off". Speech takes Hebrew. The reader is told first (off row), then files are
  deleted with retries; a file still held goes on `AddOns.LeftoverKey`, swept at the next start
  before any model opens.
- **"Keep what was found" is ticked by default.** Unticked, `AddOns.Forget` re-queues the kinds
  (videos with `Reframe`, so the transcript stays). For Photos and Meaning, kept findings are NOT
  searchable without the model - the words say "so adding it back later is quick".

## Pricing

- **`Capabilities.MarginalBytes`** is what Settings and `--models` quote (what one more adds).
- **First-run rows are priced at their own files via `Capabilities.OwnModels`** - a number that
  never moves when a row is ticked, and the only pricing that adds up to 3.7 GB. The summary is
  `TotalBytes(Close(chosen))` (Speech alone: 547 MB row, 1.57 GB bottom line).
- **The first-run screen prices what is STILL TO FETCH**: `FirstRunState.OnDisk` (by file), read via
  `FirstRun.NotHereYet` by rows, tiles, summary and button; half-present is priced at the missing half.
- **`FirstRun.PresetChoice` drops Hebrew where its row is not drawn**; never select a capability
  with no row. **`FirstRun.AlreadyChosen` ticks what is present, closed over the dependency graph.**
  The selection decides only what is FETCHED.
- **`CapabilitySet` carries the FILES it was built from; every price reads those** (a partial folder
  is ordinary). A set built by hand derives files from its capabilities.

## Downloads and pick-up

- **`ModelDownloader` refuses a short download on a floor**, not only the response length: bytes vs
  `Model.MinBytes`, and with no `Content-Length`, `Model.Bytes` less `ModelStore.SizeSlack`. (A guard
  conditioned on data the other end may choose not to send is not a guard.)
- **Disk size never equals the declared size.** `ModelStore.SizeSlack` is the only place that width
  is decided; never compare a length to `Model.Bytes` for equality.
- **The indexer asks what is installed before every file** (`Decoders.CanRead` calls `Refresh()`).
  The transcription limit is likewise live: `CapabilityGate.ApplyLimit` writes
  `index:transcribeminutes` before its re-queue and the child reads it per recording.
- **No stamp while its backlog remains.** `CapabilityGate.StampsIn` withholds a stamp while its kinds
  are skipped for `Decoders.NoModel` and unqueued; `Apply` re-queues exactly those.
- **Query encoders open when the index first holds something they can match, never before** (e5 is
  ~1 GB on the processor). `Semantic.Wanted` decides (installed AND vectors of its kind in `vectors.bin.kinds`); the pump
  (`OpenTheQueryEncodersTheIndexNowNeeds`) opens the rest off the loop when the kinds file grows,
  reading what is installed from disk. **A slot is filled, never replaced** (`Semantic.Supply`), so
  an open card gains it and nothing is disposed under a query. Each encoder is tried once a session.
- **Each encoder opens in a try of its own** (`Semantic.Open`; the Hebrew fine-tune too), so one
  corrupt file cannot take the others down.

## Which chip does the work

A device index is not a choice: `AppendExecutionProvider_DML(0)` and the speech default take whatever
is listed first - often the integrated GPU, which reports success while being slower.

- **`GpuAdapter.Best()` picks by the most dedicated video memory**, and that is the whole rule (no
  vendor or device-name lists). Never a software rasteriser.
- **`VulkanAdapter` hides the other devices via `GGML_VK_VISIBLE_DEVICES` on the indexer child's
  `ProcessStartInfo`** (set in `IndexerHost`); the wrapper's device option is ignored and an
  in-process `SetEnvironmentVariable` changes nothing native code reads.
- **A diagnostic that opens whisper re-execs itself** (`--searchmodels`, `--searchindex` with paths,
  `--searchtest`, `--searchbench` call `VulkanAdapter.ReExecWithDiscrete`, once, only with a card). A
  new mode that opens a speech model must too.
- **Both fail soft**: any enumeration problem returns null and changes nothing.
- **A provider that LOADS has not been shown to WORK.** `Media.ProveItTranscribes` runs a second of
  tone before accepting the accelerated rung; `WhatIsWrongWith` judges the SHAPE (finite, ordered,
  in-range timestamps, no control characters), never the words. False acceptance writes nonsense
  for ever.
- **`--searchmodels` names the chips (even with no model on disk), the chosen provider and every
  rejected one with reasons.** This machine is not evidence for the rule (card at 0 in both APIs).
