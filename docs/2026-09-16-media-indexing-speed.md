# 16 September 2026: why audio and video indexing is slow, and what can be done about it

Asked because the content index had been working through audio and video for most of two days.
The question was whether that is a fault, and how much faster it can go without skipping music.

The short answer: **nothing was broken, whisper on this card is already at its limit, and most of
the obvious ways to make it faster lose lyrics.** One real bug turned up (a diagnostic running on
the wrong GPU), and one safe speed-up did (not transcribing home videos with nobody talking in
them). This is the evidence, including everything that was tried and rejected, and what changed in
2.34.0.

Measurements are from this machine (Ryzen 9 9900X3D, RTX 5070 Ti 16 GB) and the real library on
`D:`. Every whisper timing below was taken with the live indexer running on the same GPU, so
absolute numbers are somewhat high; comparisons within one run are fair.

---

## 1. What the index was actually doing

From `search.db` over the preceding 18 hours of indexing (`items.indexed_at` deltas):

| Kind | Files | Median | Hours spent |
|---|---|---|---|
| Audio | 4,615 | 12 s | 16.4 |
| Video | 495 | 10 s | 1.5 |
| Photo | 87 | 1 s | 0.04 |
| Document | 10 | 4 s | 0.01 |

**16.4 of 18 hours went on music.** 4,707 audio files, 30 GB, had been transcribed by then. The
queue is oldest-first and one file at a time (`SearchDb.TakeNext`), and `D:\Personal\Mp3` happened
to sit at the head of it.

Still queued at the time: 49,852 photos, 12,404 documents, 4,316 videos, 1,752 audio files. About
59 hours of indexing at the 75% power setting, 45 at 100%.

`indexed_at` is in whole seconds, so the photo and document medians above are rounded to the
second. Measured directly (section 5), a photo is about 0.2 s.

## 2. The bug: the diagnostic transcribed on the integrated GPU

`--searchindex` exists to answer "why did this file take so long". It runs `Indexer.DrainOnce` in
its own process, and nothing set `GGML_VK_VISIBLE_DEVICES` for that process. The real indexer gets
the variable from `ContentQueue` when it is launched as a child; `--whisperprobe` had its own
re-exec. `--searchindex` had neither, so whisper ran on ggml's default Vulkan device, which on this
machine is the Radeon iGPU.

Same song (`50 Cent - In Da Club.mp3`, 3:13), same code:

| Path | Time |
|---|---|
| `--searchindex`, before the fix | **177 s** |
| `--searchindex` with the variable set | 35 s (the first file includes loading the model) |
| The next three songs, warm | 9.3 s, 11.4 s, 12.3 s |
| Real indexer, median over 4,615 files | 12 s (about 9 s of work at 75% power) |

The probe was about fifteen times slower than the thing it was diagnosing.

**Fix:** `VulkanAdapter.ReExecWithDiscrete` restarts the process with the variable set when it is
missing. `--searchindex` and `--whisperprobe` both use it. It has to be a re-exec: ggml reads the
variable through the C runtime, and `Environment.SetEnvironmentVariable` never reaches that copy
(the same trap `VulkanAdapter` already documents).

## 3. Where a song's twelve seconds go

`--whisperprobe` on `2Face - African Queen.mp3` (4:21):

| Step | Time |
|---|---|
| Decode to 16 kHz mono | 441 ms |
| Transcribe on the RTX | 12,362 ms (103 lines) |

Transcription is all of it. The log's figure of "461 ms per minute of audio" comes from a reference
minute that is mostly not dense speech. On a song it is about 3 s per minute, seven times higher,
because whisper's decoder cost scales with the tokens it emits, and lyrics never stop.

## 4. Everything tried on whisper itself, and why it was rejected

`--whisperprobe <file> <seconds> sweep` was added to measure this. It loads one model and runs the
same audio through each setting, printing the time, the line count and the first line of text. A
setting that changes the words is a different transcript, not a speed-up.

The first surprise: whisper's cost depends on what the file is, not how long it is. A song and a
home video respond in opposite directions.

### The temperature ladder

When a 30 s window fails whisper's entropy or log-probability check, whisper decodes it again at a
higher temperature, up to six times. Dense speech passes on the first try. A home video of wind and
traffic fails every step of every window, and pays for all of them to arrive at nothing.

On a 99 s phone video (`2014-03-31 13.44.04.mov`) the shipped settings took **17.2 s to produce
twenty lines of `*חקרק*`**, with the GPU no faster than the processor, because the work is the retry
loop rather than the matrices.

| Setting | Song (261 s) | Home video (99 s) |
|---|---|---|
| As shipped | 12.5 s, 103 lines | 17.2 s, 20 lines, `*חקרק*` |
| No fallback (`TemperatureInc 0`) | **17.0 s** (slower), 158 lines | **4.2 s**, 34 lines, `אוווווווווו...` |
| Ladder step 0.4 | 13.1 s, **86 lines** | 10.1 s, 39 lines, `אוו` |
| Ladder step 0.5 | **18.1 s**, **59 lines** | 13.7 s, 10 lines, `וא beyond` |
| No context carry-over | 12.4 s, 103 lines | 15.9 s, 20 lines |

Every change that makes the home video faster either makes the song slower or loses lyrics:

- **No fallback** is 4x faster on the video, but swaps an honest nothing for hallucinated
  repetition. On the song whisper stops rejecting bad windows, so it runs 36% slower and writes 55
  more lines of junk.
- **A 0.4 step** costs the song 17 lines of real lyrics. **A 0.5 step** costs it 44, which is 43%
  of the song.
- **No context carry-over** changes nothing measurable.

**Rejected, all of them.** Whisper's decoder settings stay exactly as shipped.

### Threads

| Threads | Song |
|---|---|
| 6 | 12.6 s |
| 12 (shipped) | 12.4 s |
| 24 | 13.6 s |

Within run-to-run noise (the same setting measured 12.4 s and 14.1 s in two runs). **No change.**

### Transcribing several files at once

`--whisperprobe <file> <seconds> sweep par` runs 2, 3, 4 and 6 copies of the same audio together:

| At once | Song throughput | Home video throughput |
|---|---|---|
| 2 | 1.03x | 0.94x |
| 3 | 0.86x | 1.57x |
| 4 | 0.91x | 1.95x |
| 6 | 0.47x | 1.96x |

**One song already fills the GPU.** Running more at once only helps on the home video, where the
time goes into the CPU-side retry loop. It is not worth rebuilding the indexer around, because
section 5 removes most of that same cost for far less.

### Not tried: a quantised model

A q5_0 or q8_0 large-v3-turbo would probably be faster on a decoder limited by memory transfer.
Unlike the vector models, compression does not break any rule here (a transcript is text, and e5
embeds it the same way whichever model wrote it). But it is a trade in how accurate the words are,
for Hebrew and lyrics above all, so it is a choice for the owner, not a quiet optimisation.
Left as is.

## 5. The one safe speed-up: don't transcribe a video nobody speaks in

Whisper.net 1.9.1 ships a separate voice detector: Silero VAD, an 865 KB ggml model run through
`WhisperVadFactory`. On these files it takes 0.2 to 1.3 s. The idea was to ask it first and skip
whisper when it hears nobody.

### It cannot be used on music

Run over 25 songs picked at random from `D:\Personal\Mp3`:

- VOICE: 16
- **SILENT: 9**, including Ace of Base "All That She Wants", Beyonce "Single Ladies", Boney M
  "Painter Man", and Hebrew songs.

Silero is trained on speech and does not reliably detect a sung vocal over a full mix. Using it to
skip audio files would have silently dropped the lyrics of about a third of the music collection.
**It was nearly shipped that way.** What caught it was running the check over a real sample of
songs and reading the results.

### It works on home video

The same detector over 11 home videos from the queue and 12 from `משפחה`: it is generous, detecting
as little as 0.3 s of talking in a 43 s clip. About a third came back silent (clips of 1.4 to
126 s).

The 200-file run over `D:\תמונות\גיבוי תמונות\משפחה`, which is 188 photos and 12 videos:

| | Before | After |
|---|---|---|
| The 4 videos with no voice | 44.7 s | **8.5 s** |
| The 8 videos with a voice | transcribed | transcribed, unchanged |
| 188 photos | 40.9 s | 42.9 s |

That folder also showed where video time goes. **12 videos took 254 of the 296 s, 86% of the time
for 6% of the files.**

### How it was built

- **On a video's sound track only, never on an audio file.** `Indexer.Speech` takes a `gate`
  argument: true from `Video()`, false from `Audio()`.
- **It decides yes or no for the whole file; it never cuts the audio into pieces.** If any voice is
  found anywhere, whisper transcribes the whole sound track exactly as before, so no existing
  transcript can change. Giving whisper only the detected spans would be faster, and would cut off
  quiet vocals.
- **If it fails, the file is transcribed anyway.** If the model is missing, will not load or
  throws, `Media.HasVoice` answers yes.
- **What it costs:** a music video short enough for the three-minute rule may lose its transcript.

## 6. The e5 step in speech was batched one at a time

`Indexer.Speech` embedded each 20-second window of a transcript with its own `EncodePassage` call,
a batch of one. `Document()` batches 16, and `EncodePassages` says in its own comment that batching
is "several times faster on the GPU". Speech now batches the same way: windows are cut first, then
embedded 16 at a time. An hour of speech is about 180 windows. This change is small next to
whisper, and costs nothing.

## 7. What actually decides how long the backlog takes

In order of size:

1. **The Indexing power setting.** At 75% the indexer rests a third as long as it works after
   every file, so 33% of the wall clock is deliberate. At 100% the backlog estimate falls from
   about 59 hours to about 45. This is the largest single factor, and it is a setting in the
   search widget's card, not code.
2. **Whisper on dense audio** is at the limit of this model on this card. Nothing that keeps the
   lyrics identical makes it faster.
3. **Home videos with no voice** no longer run whisper (section 5).
4. **Fullscreen apps** stop the indexer completely (`QuietState`), by design.

## 8. What changed in 2.34.0

**Fixed**
- `--searchindex` transcribed on the integrated GPU, about 15x slower than the real indexer.
  `VulkanAdapter.ReExecWithDiscrete` is now shared with `--whisperprobe`.

**Changed**
- Before transcribing a video's sound track, the indexer asks Silero VAD (`silero-vad-v5.1.2.bin`,
  885,098 bytes, downloaded with the other models) whether anyone speaks in it, and skips whisper
  when nobody does. Audio files always go to whisper.
- Speech transcripts are embedded 16 windows at a time instead of one.

**Added**
- `--whisperprobe <file> <seconds> sweep`: the decoder settings side by side, each with its time,
  line count and first line. Add `par` to also time 2 to 6 transcriptions running at once.
- `--whisperprobe <file> <seconds> vad`: the voice detector's verdict on one file, in under a
  second, so a sample of a library can be checked before trusting the detector on it.
- Log: `no voice in <file> - not transcribed` (once), a voiceless count in the `indexer idle` line,
  and warnings when the detector fails to load or run.

## 9. How to check this again

```
# where a file's time goes, and whether any whisper setting helps it
Prism.exe --whisperprobe "<file>" 400 sweep

# the voice detector's verdict, fast enough to run over a folder
Prism.exe --whisperprobe "<file>" 180 vad

# a folder through the real indexer path, into a scratch index
set PRISM_SEARCH_DIR=%TEMP%\scratch-index
Prism.exe --searchindex "<folder>"
```

**Before changing the detector's threshold or model, run `vad` over at least 25 songs from the
library.** A false "silent" on a song does not show up anywhere else.
