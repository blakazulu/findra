# Reading inside files: the indexer, readers, the vector file, score scales

Rules for `src/Findra/Content/` (and `Diagnostics/ExplainFile.cs`). Models, pricing and downloads
are in `src/Findra/Models/CLAUDE.md`.

## What is read

- **Off until somebody asks**, models or not; names are searchable at once. `--content on|off`
  survives restarts (off keeps what was read). Surfaces say "not asked" vs "finished", never just
  "up to date".
- **What Findra skips is what somebody asked it to skip, and nothing else.** `QueueFeeder.Eligible`
  is a content kind and the exclusion list; no hidden second rule, **no "always read" list**. Noise
  folders live in `FileKinds.DefaultExclusions` (`node_modules`, `.git`, `bin`, `obj`, `packages`,
  `site-packages`) where the user can see and delete them.
- **A missing model is a normal state**: the kind is skipped, search contributes nothing, the card
  offers the download. Enabling a capability re-queues **only the files it covers**.
- **`DocText.WorthEmbedding` withholds the vector from a chunk under 400 characters, 60 words or 25
  distinct words** (short passages match every query). **Nothing is skipped**: the chunk is stored
  and full-text indexed; `Chunk` keeps anything over forty characters. A binding DENSITY check
  (never a mere brace) keeps `.html` safe as a document. **Transcripts are exempt.**
- **Scanned PDF pages are read by OCR, as part of Documents** (`ScannedPdf`): a page whose text
  layer has under `MinWords` (10) words is rendered by `Windows.Data.Pdf` and read by `ImageText`,
  in page order, at most `MaxPages` (100) a file, one beat each. Pages with text are never rendered.
- **The words inside PICTURES are not free**: `ImageText` runs in `Decoders.Photo`, after the vision
  encoder, so they come with Photos. Never list them beside words in documents.
- **Every zip-based format reads through `DocText.Archive`, one `ArchiveBudget` per file**: 256 MB
  decompressed (counted on the stream, never the sizes the zip claims) and 10,000 parts. Spent means
  stop and KEEP what was read, never fail the file; `SpentException` is an `IOException` so
  `XmlReader` passes it on. `Extract(archiveBytes:)` is the test seam.
- **RTF (`RtfText`) and OpenDocument (`content.xml`, matched by namespace) have their own readers**;
  `.doc`, `.xls` and `.ppt` stay in `DocText.NoReader`. Neither reader throws on garbage.
- **Extraction beats as it reads**: per PDF page and OCR'd page, per docx/pptx paragraph, per xlsx
  row and shared string, so the watchdog spares slow healthy files.
- **One number, in minutes, limits transcription** (sound and video): zero off, negative no limit,
  five by default. Over-limit recordings get their own reason; raising the limit re-queues exactly
  those. Asked on the first-run screen (under Speech) and in Settings. `TranscribeLimit.ShortName`
  holds the five labels both draw ("Off", "5 min", "30 min", "2 hr", "No limit").

## The queue and the index

- **A file that keeps ending the indexer is written off after `ContentDb.MaxAttempts` tries.**
  `pending.attempts` is incremented AND COMMITTED before the file is opened (a crashing decoder
  never reaches the handler). Added by `AddColumnIfMissing`, deliberately NOT a numbered migration.
- **A full pass removes as well as adds.** `QueueFeeder.FillFrom` queues a delete for every item on
  the volume the walk did not see and drops queued rows it did not see (never a queued delete; an
  empty walk sweeps nothing). A delete event for a file only QUEUED takes its row off
  (`ForgetQueued`). Removals run `Indexer.RemoveBatch` at a time.
- **Migrations.** A step that changes WHICH FILES are eligible sets `ReWalk` (`RequeueKinds` only
  moves existing rows; `JournalTail.ResumeFrom` owes the full pass). Opt-in per step (spec §2a). **A `ReWalk` OWES every volume a walk** (`OweEveryVolumeAWalk`); only a
  completed `FillFrom` clears the debt (an older build still running once wrote positions back and
  drives were never walked). Schema 7 is a `ReWalk`; schema 8 re-queues only Documents skipped for
  `NoText` or `NoFormatReader` (`Migration.OnlyBecause`).
- **Indexing stops when the app quits** (the indexer is its child); the UI must say so.

## Plain words

Status text is for somebody who has never heard of an index, a GPU or a codec, and says whether they
need to do anything. `IndexStatus` composes all of it from `IndexExtra`; `IndexStatus.Sentence` is
the Settings line under "Reading now". Its inputs include the kind waiting for the card
(`indexer:around`), the processor reason (`indexer:processor`) and `IndexStatus.RestartInKey`. **Counts are files DONE** (read + left out + failed).
`default(IndexExtra)` carries null strings - read them with `IsNullOrEmpty`. `IndexerHost.RestartIn`
replaces "Findra is closed" during a crash backoff; a child that ran `HealthyMinutes` resets it.

## Staying out of the way

The power setting shapes how hard Findra works; **`IndexGate` decides whether others are working.**

- **Asked before every file, in `Indexer.Loop`**, after the pause switch and before the attempt is
  counted. Fullscreen first (`SHQueryUserNotificationState`; a locked screen does not count), then
  the card. `IndexGate.Holds`: fullscreen app, game, presentation. **Quiet time (6) is NOT Focus
  Assist** (it is the first hour after a first sign-in); never hold it.
- **Two kinds of busy card (`GpuPressure`), engines first.** `Engine` (another process over 30% on
  an engine): everything that needs the card WAITS. `Memory` (other programs leave less than the
  models plus a margin, once past an ordinary desktop): pictures and meaning run ON THE PROCESSOR
  (`IndexGate.OnProcessor`; `IDecoders.OnProcessor` drops loaded models on a change). Speech still
  waits (its runtime is chosen once per process, `IndexGate.UsesSpeech`). Busy at once, free after a minute of free
  readings (`GpuHold.Pressure`). PDH English counter paths, keyed by the adapter LUID.
- **Only a file that would load a model waits for the card**; fullscreen holds everything but
  deletes. **The queue does not stop behind a held file**: `Indexer.NotHeldBack` takes the oldest row
  of a kind the gate lets through (`ContentDb.TakeNextOf`, on `pending_kind`); a held file that is gone or unchanged is settled
  without waiting.
- **Anything that cannot be measured is not busy. No "wait until idle" rule** - the gate yields to
  other WORK, not to a person being present.
- **Waiting means holding no models** (`IDecoders.Unload` on every wait and a minute after the queue
  empties) **and no video memory**: the speech runtime keeps a ~1.5 GB pool once it has transcribed,
  so `MachineGate.LeftOnCard` measures this process's own card memory and above
  `Indexer.RecycleAboveBytes` (256 MB) the child exits with `IndexerHost.RecycleExitCode` (3),
  restarted with no backoff. Measured, never "restart after speech".
- **The wait is said**: `indexer:state`, "waiting for the GPU", `--searchprobe`. Measured on one
  machine only.

## Video frames

`VideoFrames` is the one place a video is opened for pictures, through the source reader - **never
`Windows.Media.Editing`** (~33 s a frame and black frames reported as success).

- **Four corrections**: `VideoGeometry` crops to the visible area (H.264 padding is a green strip);
  frames are rotated by the rotation flag; `VideoFrames.Frame` reads past an all-zero sample (MPEG-1
  after a seek); non-square pixels are squared (reasoned, not measured).
- **Empty means every pixel exactly zero** (`IsEmpty`); a dark frame is real.
- **Limits**: `VideoRead.GiveUpAfter` (three empty frames, nothing decoded) and `VideoRead.Budget`
  (five minutes a video); nothing decoded records `Decoders.NoFrames`, a skip.
- **Attempts catch crashes; the watchdog catches hangs.** `IndexerWatch.ShouldRestart` kills a live,
  reading child that has gone `IndexerWatch.StallSeconds` (three minutes) without a beat; restarted immediately,
  backoff reset. **A child younger than that window is never judged** (`indexer:beat` outlives its
  writer; judging by it kill-loops; `IndexerHost.SinceStart` is the age). `IndexerHostTests` drives the sequence.
- **Skip reasons**: `NoVideoCodec` (names the codec), `NoVideoStream` (audio-only, still
  transcribed), `NoContainerReader`. The first and last re-queue when `VideoDecoders.Fingerprint`
  changes. **The HEVC extension is paid; nothing may call it free** (`VideoCodecStore`).
- **A `reframe` writes frames via `ReplaceSegments`** (transcript kept); outcomes go through
  `RecordFrameOutcome`. **A failed re-read keeps the transcript**, never the ordinary failure path.
  `ContentDb.BlockedVideoCodecs` counts `StateIndexed` and `StateSkipped` together.
- One machine only; `IMFSourceReader::ReadSample` has a random hang on arm64 that the watchdog bounds.

## The vector file

**Never open `vectors.bin` by name; ask `ContentDb.VectorsPath()`.** `VectorCompaction` (between
files, queue empty, past `MinDeadRows` and `MinDeadShare`) copies live rows into
`vectors-<utc>.bin`, and `ContentDb.Renumber` renumbers segments AND writes `index:vectors` in one
transaction. Readers call `Semantic.Follow` under `Semantic.SearchLock`, and a search whose file
name changed between scan and segment lookup is asked again. `DeleteStale` removes old files.

- **`VectorStore.Search` takes every query in ONE pass** (kind byte first, a row read only when some
  query wants its kind, half the cores, ties to the lower row). `Reload` re-reads the kinds each call.
- **Content results drop files the disk no longer has** (`ResultMapper.Finish(keepMissing: false)`);
  the name half keeps them. Content tests describe a disk with `ContentBranchTests.Here`.

## Score scales

- **SigLIP-2's score is `sigmoid(exp(logit_scale) * cos + logit_bias)`**; `ModelStore` records the
  scalars (112.90, -16.7718). **`PhotoFloor` 0.09, span 0.06** (real ~0.13, unrelated up to 0.066).
- **`TextFloor` 0.81, `TextSpan` 0.06** (unrelated averages 0.780, best answers 0.838-0.868). **Each
  scale ends just past its own best real match** so the two are comparable; the ceilings (0.90 vs
  0.92) were left alone deliberately.
- **A file against a file has its own scales** (the typed floors admit nearly everything):
  `PictureLikeFloor` 0.70 span 0.30, `PassageLikeFloor` 0.91 span 0.09, both ending at a copy. No
  empty band between noise and matches; measured on one machine (675 pictures, 72,046 passages).
- `ScoreScaleTests` carries every number and method.

## Explaining one file

**`--searchindex why:<path>` answers "I can see this file and searching does not find it".** It reads
and changes nothing (a bare path QUEUES). With `q:` it scores that file's own vectors (`ScoreOf`)
against `ContentBranch`'s own floors. `ExplainFile.Verdict` names THE reason, in decision order: not
on disk, not a content kind, excluded, queued, never offered, passed over, failed, read-but-empty,
read-but-edited-since, read.
