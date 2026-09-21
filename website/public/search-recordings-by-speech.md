# Search recordings by what was said in them

To find a voice memo, a meeting recording or a video by something somebody said in it, install
Findra's speech capability. It transcribes your audio and video on your own machine, offline, and
searches each transcript exactly the way it searches a document - so typing a phrase you remember
hearing finds the recording, and the result shows the line and where in the recording it was said.

```
findra --models install speech
findra --content on
```

The same choice is on Findra's first screen and under Content in its settings.

## What it costs

A 547 MB speech model, plus the 1.04 GB document models if you do not have them already, because a
transcript is searched the way a document is. Everything runs on your own processor or graphics
card: no recording is uploaded, there is no account and there is no cloud transcription service.

Transcribing takes real time, and it happens only while Findra is open. With a graphics card it
goes faster; without one it runs on the processor, which is slower but supported.

## How long a recording is worth transcribing

One number decides it, for sound files and videos alike: five minutes by default. A recording
longer than the limit is passed over and says so, and raising the limit later goes back for exactly
the recordings it passed over and nothing else. The choices are Off, 5 min, 30 min, 2 hr and No
limit, in Settings or from a terminal:

```
findra --content limit 30
```

## What it listens to

- Sound files: MP3, M4A, AAC, WAV, FLAC, OGG, Opus, WMA and AIFF
- The sound track of videos: MP4, MOV, MKV, AVI, WebM, M4V, WMV, MTS and M2TS

## Hebrew

Findra can take a second pass for Hebrew. The general model listens first and decides the
language, and only the recordings it calls Hebrew are transcribed again with a model trained for
Hebrew. It is a 1.51 GB download and needs the speech capability:

```
findra --models install hebrew
```

## When a recording is not found

Ask Findra about that one file. It says why in plain words - longer than the limit, still queued,
failed, or read - and changes nothing:

```
findra --searchindex "why:C:\Users\you\Music\Voice 014.m4a"
```

## Try it

Findra is free and open source, for Windows 10 and 11:

```
winget install blakazulu.Findra
```

The installer is also on the releases page, https://github.com/blakazulu/findra/releases/latest,
for x64 and arm64.
