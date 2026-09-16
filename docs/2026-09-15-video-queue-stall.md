# 15 September 2026: one AVI stopped the search index for a day

Found because the search capsule said `indexing videos · 0 of 60,054` for hours and did not move.
Nothing was broken in a way that showed. No error, no crash, no warning in the log, a live indexer
process using CPU, and a status line that looked like work in progress. This is what was going on,
how it was found, and what changed in 2.33.0.

Measurements are from this machine (Ryzen 9 9900X3D, RTX 5070 Ti) and a real library on `D:`.

---

## 1. What it looked like

- The pill read `indexing videos · 0 of 60,054` for hours.
- The log's last `[search]` lines from the indexer were `indexer working`, then nothing.
- The indexer process (pid 7776) was alive and busy: 4,955 CPU-seconds, about three quarters of a
  core, still climbing.
- `search.db`'s `meta` table: `state = indexing`, `current = Super Mario.avi`, `done = 0`,
  `pending = 60054`, heartbeat fresh.

**The `0` was honest, and misleading.** `done` counts files finished *by this indexer process*, and
this one had not finished any. It reads as "nothing indexed", but 8,753 items were already in the
index. It really meant "stuck on the first file it took".

## 2. What was actually happening

The queue head was `D:\Personal\ילדים\Super Mario\Super Mario.avi`:

| | |
|---|---|
| Size | 697 MB, dated 2008 |
| Video | MPEG-4 Part 2, **XviD**, 560x304 |
| Audio | MP3 |
| Length | 6,277 s (1 h 44 min) |

The indexer takes one file at a time, oldest first (`SearchDb.TakeNext`). So while this file was in
hand, the 60,054 behind it waited, including 48,605 photos that would each have taken a fraction of
a second.

The timeline, from the log and the queue row:

| Time | What happened |
|---|---|
| 09:47:29 | Last item indexed (`The magic Bag.mkv`). The AVI is taken next and never finishes. |
| 12:10:58 | That Prism session stops. The indexer has spent 2 h 23 min on the file. |
| 12:11 | Prism restarts; the new indexer takes the same file straight back. |
| 12:13 | Prism restarts again; same file again. The row now shows `attempts = 3`. |
| 13:57 | Investigated: 1 h 44 min into this attempt, `done = 0`. |

### Why restarting did not help

`pending.attempts` exists for files that **crash** the indexer: it is spent before the file is
opened, and a row that runs out of attempts is written off. A **hang** is not a crash, so the
attempt never ended. Each restart spent one more and started the same hour-long job again. The
fourth take would have written the file off, and the next row in the queue was
`The Flash S1E1 Pilot.avi`, another XviD AVI that behaves exactly the same. There were 1,240 AVIs
queued.

## 3. Finding the slow call

The elevated indexer could not be sampled with `dotnet-stack` from an unelevated shell (access
denied), so the frame code was copied into a scratch console program and timed on the same files.
At the time, video frames came from Windows' own media stack:
`Windows.Media.Editing.MediaComposition.GetThumbnailAsync`, at the nearest key frame, width 512.

| File | Per frame | What came back |
|---|---|---|
| `Super Mario.avi` (XviD) | **49,008 / 48,906 / 49,769 ms** | 3,094 bytes each: a solid black image |
| `The Flash S1E1 Pilot.avi` (XviD) | **47,206 / 48,831 ms** | 3,699 bytes each: solid black |
| `The magic Bag.mkv` (already indexed) | 492 / 268 / 374 ms | real pictures |
| `botp_open.mov` (Cinepak) | threw `COMException` at once | nothing |

A long video asks for 90 frames (`Media.SampleTimes`: one every 10 s, at most 90). At 49 s each,
that is **74 minutes for one file, and every frame is black**. It is the arithmetic of a single
unloaded call; inside the indexer, at BelowNormal priority, the file did not finish in 2 h 23 min.
Why it was slower still in there was not measured.

**This failure reported success.** The call returned a valid image every time. Nothing threw,
nothing was logged, and had it finished, the index would have held 90 black frames for the film
and matched them against queries for "dark".

The same film through ffmpeg, which was already installed on this machine:

| ffmpeg on `Super Mario.avi` | |
|---|---|
| One frame at 71 s / 3,000 s / 6,000 s | 629 / 121 / 118 ms |
| All 90 frames in one key-frame pass | **6 s** |
| Frame from 50 minutes in | a real scene |

## 4. A question on the way: the 3-minute rule

It came up whether long videos were supposed to be content-indexed at all. The rule in the spec,
`docs/SEARCH-WIDGET.md` and `CLAUDE.md` is narrower than it sounds: a video over 3 minutes skips only
its **sound** (no transcription). Frames are taken from **every** video. So the code was doing what
was specified; the specification just had no answer for a decoder that never returns.

Three fixes were weighed: stop taking frames from videos over 3 minutes, give up quickly on a video
whose frames are bad, or replace the decoder. The decoder was the root cause, so it was replaced,
and the give-up guard went in with it so no single file can hold the queue again.

## 5. The fix (2.33.0)

### ffmpeg reads video now

`src/Search/Ffmpeg.cs` runs the bundled `ffmpeg.exe` as a short child process: once to read the
file's length and streams (`Probe`), and once per frame (`Frame`).

- **A process, not a linked library.** A decoder that hangs is killed at a timeout, and one that
  crashes takes nothing with it. Each child goes into a kill-on-close job (the one `OpenRgbHost`
  already had), so a dead indexer leaves no ffmpeg behind.
- **`-ss` before `-i`.** Seek to the key frame, then decode forward to the exact time. After `-i`,
  ffmpeg decodes from the start of the file, which would bring back the same slowness.
- **`-map 0:V:0`, capital V.** Real video streams only. A cover picture attached to an MP4 or MKV is
  also a "video" stream, and 90 seeks into it would store 90 copies of the cover.
- **`scale=iw*sar:ih` first**, so an anamorphic rip is squared before being fitted to 512 px.
- **BMP over a pipe.** Nothing to compress or decompress on either side; Skia decodes it.
- **A file with no stated length** still gets its first frame; a file with no video stream gets no
  frame requests at all (`no video stream`).

With the pinned build, per frame:

| File | Per frame |
|---|---|
| XviD AVI, 560x304 | 111-139 ms |
| WMV2, 208x160 | 62-77 ms |
| Cinepak MOV, 160x120 (threw before) | 61-82 ms |
| H.264 MP4, 1906x798, 3 h 55 min | 141-245 ms |
| H.264 MKV, 1280x536, 2 h 49 min | 122-337 ms |

### No single video can stall the queue

`Media.Frames` now has limits, and they are the point of the change rather than decoration:

- **Three failed frames in a row with nothing decoded** end the file (`FrameGiveUp`).
- **Five minutes** is the most any video may spend on frames (`FrameBudget`).
- **20 seconds** kills a single frame (`FrameTimeout`); a healthy one is under a quarter of a second.

The file is recorded `no frames: <ffmpeg's own words>` and the queue moves on. A `frames:` warning is
logged once per extension and reason.

### Cleaning up after the old decoder

On its first run, 2.33.0's indexer does two things once (`Indexer.VideoRule`), and only when ffmpeg
is present - without it, every video would be written off on the spot:

1. **Every video already indexed goes back into the queue** (as `reembed`, because the file did not
   change and the freshness check would otherwise skip it). Some of them hold black frames. A short
   video's sound is transcribed again with it.
2. **Queued videos have their spent attempts reset** (`SearchDb.ResetAttempts`).

**The second one was caught in review, not by a test.** The stuck AVI had already used three of its
four attempts under the old decoder. Without the reset, the new build would have taken it, counted
the fourth attempt, and written it off as a crashing file before ffmpeg ever opened it - the file
that started all this would have been the one file the fix never indexed.

### Shipping ffmpeg

- **Build:** gyan.dev's "essentials" 9.0.1, from its GitHub mirror's versioned release, so the URL
  always serves the same bytes. The zip is unsigned, so its SHA256 is pinned
  (`FEC81AE0...E65DA2E9`); a mismatch deletes the download and stops the build. Only `ffmpeg.exe`,
  `LICENSE` and `README.txt` are kept.
- **`installer/stage-ffmpeg.ps1`** stages it into `installer\redist\ffmpeg`, and
  `build-installer.ps1` runs it **before** publishing. `Prism.csproj` copies that folder into both a
  dev build's output and the publish, so it reaches `{app}\ffmpeg` with no extra line in `Prism.iss`,
  and a Debug build indexes with the same binary the installer ships. The build refuses to continue
  if the publish has no `ffmpeg\ffmpeg.exe`.
- **`Link`, not `LinkBase`.** The first version of the csproj item used `LinkBase="ffmpeg"`, which is
  ignored for a file inside the project folder: the build succeeded and put the exe under
  `installer\redist\ffmpeg\` in the output, where nothing looks.
- **Licence:** this build is GPLv3. Whoever receives `PrismSetup.exe` is owed ffmpeg's source,
  as they already are OpenRGB's.
- The 2.33.0 installer, ffmpeg included, is 142.1 MB.

## 6. How it was verified

- `--searchtest` passes, with new checks on reading ffmpeg's output: duration, a real video stream,
  a cover picture that is not one, a container with no stated length, and a file it cannot open.
- `--searchindex` against a scratch index (`PRISM_SEARCH_DIR`):

  | File | Before | After |
  |---|---|---|
  | `Super Mario.avi`, 1 h 44 min | 74+ min, black frames | **14.7 s**, real frames |
  | `The Hobbit - An Unexpected Journey.mkv`, 2 h 49 min | - | 14.9 s |
  | `botp_open.mov` (Cinepak) | threw | indexed |

  Searching that index for "a wizard with a staff" returned the Hobbit MKV at the right moment
  (1:21:03), and "a cartoon" returned the WMV of cartoon characters.
- **The cleanup, on a copy of the real index** trimmed to the stuck AVI (still at 3 attempts), the
  next AVI, and two indexed videos: the log read `video rule: 2 indexed video(s) re-queued ..., 1
  queued video(s) had attempts spent on the old decoder forgotten`, and the AVI indexed in 12.4 s,
  the Flash episode in 11.7 s.

## 7. What is still open

- **The limits have not fired yet.** No test file hit the 20 s timeout, the three-miss give-up or the
  five-minute budget. They are simple, but they are unexercised.
- **Short videos with sound can still be slow.** In the scratch run, a 2.5-minute WMV took 290 s and
  a 1.5-minute MOV 58 s, almost none of it frames. The rest is the soundtrack's transcription, which
  this change did not touch. The scratch run goes through `--searchindex`, which may not get the
  indexer's Vulkan device choice, so it may not reflect the real indexer. Unconfirmed either way.
- **The morning's `slow:` warnings are unexplained.** Before the stall, 1-3 MB MP4s took 137-462 s
  each under the old decoder. Whether that was the frames or the sound was never measured.

## 8. What to take from it

- **A failure that returns a valid result is the worst kind.** The black frames came back as good
  images, so nothing anywhere could notice. Only timing the call and looking at one of the pictures
  showed it.
- **A queue that handles one item at a time needs a limit on every item.** Without one, the slowest
  file sets the speed for everything behind it, and "slow" can mean forever.
- **Retry limits protect against crashes, not hangs.** `attempts` did its job exactly as designed
  and still let one file consume a day, because a hang never ends an attempt.
- **When the thing that reads a file changes, what was recorded under the old reader is stale** -
  the stored frames, and the attempts too.
- **A count needs to say what it counts.** `0 of 60,054` was accurate for the session and read as
  "nothing works".
