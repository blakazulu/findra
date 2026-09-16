# Video frames, and a limit on every file

**Status:** designed, 2026-09-16.

## The problem, as measured

Findra takes video frames through `Windows.Media.Editing` - `MediaComposition.GetThumbnailAsync`
at the nearest key frame (`Media.cs:217`). For MPEG-4 Part 2 video, which is what most of an old
library is, that call **takes about 33 seconds and returns a black picture**.

Measured on this machine (AMD Ryzen 9 9900X3D, NVIDIA RTX 5070 Ti, Windows 11 26200), at the
320px the indexer asks for, over 23 files covering every codec on one real library of 4,712
videos:

| codec | files in that library | today, per frame | Source Reader, per frame |
|---|---|---|---|
| XviD / DivX 4 / DivX 5 | 1,159 | **33,000 ms, black** | 17-24 ms, a real picture |
| DivX 3 / MP43 | 72 | 1,200 ms | 3-17 ms |
| WMV1 / WMV2 / WMV3 | 300 | 850 ms | 2-13 ms |
| H.264 (MP4, MOV, MKV) | 3,130 | 340-660 ms | 20-62 ms |
| HEVC | 24 | not measured | 206 ms (3840x2176) |
| MPEG-1 | 9 | not measured | 29 ms (see *empty samples*) |
| MPEG-4 in MP4 | 4 | not measured | 4 ms |
| SVQ1, Cinepak | 6 | throws | no decoder on this machine |
| no video stream | 8 | not measured | reports no video stream |

A video asks for up to 90 frames (`Media.SampleTimes`). So one XviD film costs **about 50 minutes
and stores 90 black frames**, where Source Reader reads the whole file in 1.5 to 2.5 seconds and
stores real ones. The same Windows decoders are underneath both paths; the editing pipeline
wrapped around them is what stalls.

**The failure reports success.** Nothing throws. A black frame is a valid picture, so it is
embedded by the vision tower and stored, and nothing re-reads a file that succeeded.

**And one such file stops the whole queue.** `TakeNext` hands out one file at a time, oldest
first, so everything behind it waits. `ContentDb.MaxAttempts` does not help: an attempt is spent
before the file is opened precisely so that a decoder which takes the process down is written off,
but **a hang is not a crash**. The attempt never ends, the child never exits, `IndexerHost` never
restarts it, and the only thing that spends an attempt is the person restarting Findra.

## What this changes

1. Frames come from Media Foundation's Source Reader.
2. No file may hold the queue: the child reports progress, and the interface kills a child that
   stops making it.
3. A video Windows cannot decode is a recorded, re-queueable skip with the codec named, and
   Settings says so.
4. A schema step re-reads the frames already stored, resets attempts, and brings back videos that
   were written off.

## 1. Reading frames

**`VideoFrames` is the only place a video is opened for pictures**, used by the indexer and by the
card's preview. `Media.Frames` and `Media.VideoDuration` go; `PreviewDecoder` calls this instead.

The Media Foundation calls are written by hand through vtable slots, in the shape `GpuAdapter`
already uses for DXGI. That is a deliberate choice over a package: the one typed wrapper available
drags in a beta COM runtime, and what is needed here is ten interfaces.

### Opening

Every stream is deselected and only the first video stream is selected, with video processing
enabled and RGB32 asked for. Two facts come off the open:

- **The duration**, which replaces `Media.VideoDuration`.
- **The codec**, as the native format identifier. It reads even when no decoder exists, which is
  what makes section 3 possible.

### One frame at a time

Seek, then take the first sample at or before that time. This is the key-frame precision Findra
asks for today, and it lands up to about 12 seconds before the requested time. Decoding forward to
the exact time was measured at 3 to 8 times the cost and is not worth it for a picture that only
has to describe the film.

**Four corrections, each from a file that got them wrong:**

- **Crop.** H.264 is decoded padded to a multiple of 16 rows: a 1280x536 film decodes 1280x544 and
  the extra rows are green. The visible area arrives with the media-type-changed flag on the first
  sample.
- **Rotation.** `MF_MT_VIDEO_ROTATION` says the picture is stored rotated counter-clockwise, so it
  is rotated back clockwise by that amount. Every phone video carries it. On the library measured,
  2,361 of the 4,712 videos are `.mov` H.264 files, and every one sampled was a phone recording
  carrying this flag - so today they are all indexed on their side.
- **Pixel aspect.** Applied where it is not square. No file in the measured library is anamorphic,
  so this one is reasoned from the format rather than from a measurement.
- **An empty first sample.** The MPEG-1 decoder returns an all-zero buffer for the first sample
  after a seek: 70 of 90 frames came back pure black with real pictures in between. Skipping an
  all-zero sample and taking the next one fixes it (0 flat frames, 2.8 s for the file) and costs
  nothing on the other ten files measured.

The result is an upright, square-pixel bitmap fitted to the size asked for.

### What can come back

A frame is a **picture**, **empty** (every pixel zero), or **unreadable**.

- **An empty frame is never stored.** It is not merely a weak match: a black frame is close to
  every dark-ish query at once, which is the same reasoning `DocText.WorthEmbedding` is written
  under one level along.
- **Empty means all zero, and nothing looser.** A dark frame is not empty: the first sample of
  most films measured here is a real fade-in from black, and a threshold on how dark a picture may
  be is a rule about what somebody is allowed to find. All-zero is the decoder saying it produced
  nothing, which is a different statement.
- **Three empty or unreadable frames in a row, with no picture yet, end the file.**
- **Five minutes is the most one video may spend on frames.**
- A video that yields no pictures is recorded `no frames`, which is a skip and not a failure.

### Failures at the open

| what Media Foundation says | what Findra records |
|---|---|
| no decoder for the codec | section 3's skip reason, with the codec named |
| no video stream in the file | `no video stream`; the sound track is still transcribed |
| anything else | the existing failure path |

### Tests

- The crop, rotation and pixel-aspect arithmetic, the flat-frame rule, the give-up rule and the
  mapping from error code to reason are pure and tested without a file.
- One integration test writes a three-second H.264 clip with Media Foundation's own encoder, a
  different colour each second, and asserts that seeking returns the right colour and that the
  frame is the right way up. Nothing binary is checked into the repository.

## 2. No file may hold the queue

**The child reports progress while it works, not only between files.**

`Decoders` takes a throttled `beat` delegate, the way it already takes the transcription limit,
and writes the existing `indexer:beat` row at most every few seconds. Beats go at every step of
real work and **never on a timer**, because a timer keeps a hung decoder looking alive: each video
frame, each block of audio decoded, each Whisper segment, each e5 batch, each PDF page, and either
side of a model load.

**The interface decides when progress has stopped.** `IndexerWatch.Decide(beat, now, hostRunning,
pending)` is pure and answers leave-it or kill-it: kill only when the child is running, work is
queued, and the last beat is more than three minutes old. A paused or idle child writes its status
every two seconds, so neither trips it. `PumpIndexer` already runs every 400 ms and already calls
`EnsureRunning`; it calls this beside it and asks `IndexerHost.Kill(reason)`.

**A watchdog kill restarts immediately**, resetting the crash backoff rather than pushing it
towards five minutes. The attempt was already counted before the file was opened, so a file that
hangs every time is written off on its fourth sight - about ten minutes rather than a day. The
write-off reason becomes "this file stopped or ended every attempt to read it", which covers both
causes.

**A child younger than the stall window is never judged**, and that is the half that makes the
immediate restart safe rather than a process storm. `indexer:beat` is written by the CHILD and
outlives the child that wrote it, so a replacement read against the row its predecessor left behind
is killed on the very turn it is started - and so is the next, every 400 ms for as long as Findra
runs. The file is never written off either, because an attempt is committed only once a child has
taken a row off the queue and a child killed that fast never gets there. `IndexerHost.SinceStart`
is the age the rule reads, and it is null when there is no child.

A warning names the file, how long it went without progress, and which attempt it was.
`--searchprobe` gains the current file and how long ago its last progress was.

`IndexStatus.Alive` keeps its meaning exactly: a stale beat from a live process is still a busy
child. It is simply no longer stale during a long file.

**Tests:** `Decide` as a table (fresh beat, stale beat, paused, idle, no child, empty queue);
`IndexerHost.Kill` through a seam taking its effects as delegates, asserting the kill and a restart
that does not escalate; a fake decoder that beats and then stops, driving the real loop; and a row
killed three times, written off with the new reason. And the SEQUENCE through the real host - kill,
restart, and what the turn after that decides - because the grace period above is invisible to a
test of the rule on its own: the predicate is correct in every one of those table rows while the
composition kills a child every 400 ms.

## 3. A video Windows cannot decode

A machine without the HEVC extension cannot read HEVC, which is what recent phones record. That is
true of the decoder Findra uses today as well, so this is not a regression - but today it is
invisible.

- **The recorded reason names the codec**: `no decoder for this video format yet (HEVC)`. It is a
  skip, never a failure, and it sits where a re-queue can find it.
- **`RequeueKinds` gains prefix matching**, so every codec-blocked video is reachable without
  listing codecs one by one.
- **`VideoDecoders.Fingerprint()`** hashes the video decoders Windows currently has. The interface
  keeps it in the index and re-checks it every few minutes; when it changes, exactly the
  codec-blocked videos are re-queued. That is what makes installing a codec take effect without a
  reinstall.
- **Settings > Content says so, on the Photos and video row.** The pane is a fixed rectangle and is
  already full - with the Hebrew row offered, the last row ends about 15px above the bottom - so a
  new row does not fit, and this is the row the fact belongs to anyway. With the capability
  installed: "installed" as now when nothing is blocked; a button reading "212 need HEVC" that
  opens the Microsoft Store page when a codec can be bought; and plain "installed, 6 unreadable"
  when there is no fix to offer. The button is the only new action, and it opens a page - Findra
  installs nothing itself, exactly as with updates (spec §9b).
- **The Store listing is the paid one** (`9NMZLZ57R3T7`, $0.99). The free "from Device
  Manufacturer" listing now installs only on machines that shipped with it, so nothing in the
  product may call this free.
- **`--searchindex` groups blocked videos by codec**, and `why:<path>` names the codec and what
  would fix it.
- **`settingscontent` gains an installed Photos capability with blocked videos**, so the new branch
  is rendered by a shot rather than shipped unlooked at. The README and website shots regenerate
  through `build/Make-Shots.ps1`.

## 4. The migration: schema 6

Every video frame already in an index was taken by the old decoder, and for the MPEG-4 Part 2
family they are black. Three groups of rows need different things:

- **Indexed videos re-read their frames, and only their frames.** A new queue reason, `reframe`,
  makes `Indexer.Handle` decode frames alone and call `ContentDb.ReplaceSegments(item, [SegFrame],
  segments, tx)`, which deletes only the frame segments - returning their vector rows to be
  tombstoned after the commit, as `Upsert` already does - and leaves transcripts, their text and
  their vectors untouched. A plain re-queue would re-transcribe every video under the transcription
  limit, which is hours of Whisper to arrive at the transcript already held.
- **Queued videos have their attempts reset.** A video that spent attempts under the old decoder
  would otherwise be written off before the new one opened it, which would leave the file that
  motivated this change as the one file the change never reads.
- **Written-off videos come back**, with a full read: nothing usable is stored for them.
  `RequeueKinds` deliberately skips failed rows, so the step passes `includeFailed`.

`Migration` gains three fields - the queue reason, whether to reset attempts, and whether to
include failed rows - and the runner passes them. `ReWalk` stays **false**: which files are
eligible has not changed, only what is stored about the ones already known (spec §2a).

**Cost:** frames only. On the library measured, 4,712 videos at 1.5 to 6 seconds each is roughly
three to six hours of background reading at full duty cycle, and no re-transcription.

**Tests:** an indexed video keeps its speech segments, their text and their vector rows while its
frame vectors are replaced; a queued video's attempts are reset; a failed video is re-queued; and
a photo is untouched by the whole step.

## What this does not do

- **It does not ship ffmpeg.** On the library measured, 6 files of 4,712 cannot be read at all
  (SVQ1 and Cinepak), and those fail immediately with a reason a person can read rather than
  hanging. Bundling a decoder is a licence, a size and an architecture decision that this evidence
  does not justify. **Revisit it if reports show many unreadable videos on other machines** - the
  route would be an optional download beside the models, from the publisher's own release with a
  pinned hash, not a binary in the installer.
- **It does not decode to the exact requested time.** Key-frame precision is what Findra asks for
  today and it costs 3 to 8 times less.
- **It does not change what is sampled.** `SampleTimes` is untouched.

## What has not been shown

Everything above was measured on one machine: x64, AMD processor, NVIDIA card, software decoding,
with the HEVC, VP9, AV1 and MPEG-2 extensions installed. In particular:

- **No arm64 machine has run any of it**, and Microsoft has an acknowledged, unfixed bug where
  `IMFSourceReader::ReadSample` hangs at random on arm64. Findra ships arm64. That is an argument
  for section 2 rather than against section 1: a hang there is now bounded at ten minutes for the
  file rather than unbounded for the queue.
- **No Source Reader call hung during these measurements** - 23 files, read four times over in
  different modes, several thousand frames in all. That is not evidence that none will.
- **A machine without the codec extensions** reads fewer formats. Section 3 is what makes that
  visible instead of silent.
- **Windows N editions have no Media Foundation at all** until the Media Feature Pack is installed.
  Findra's audio path already depends on it; video now depends on it in the same way.
