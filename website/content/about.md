# About

Findra is a desktop search widget for Windows. A capsule sits on the desktop, a global hotkey
brings it up from anywhere, and it unfolds into a card of results. It finds files by name, by
what is written inside them, by what a photograph shows and by what somebody said out loud in a
recording.

It was written because Windows Search rebuilds its index at three in the morning, spends six
minutes at a hundred percent disk activity, and still cannot find a file saved that morning.

## What it is built out of

.NET 10, Avalonia and SkiaSharp for a card that paints every pixel of itself, and SQLite for the
parts that have to survive a restart. Quicksand is embedded in the application under the SIL Open
Font License, one weight, and the same file sets the type on this page.

There are three processes, and the split is the whole architecture rather than an implementation
detail:

- An elevated helper that opens the NTFS volume, holds the name index in RAM and does nothing
  else. Exactly one call in Findra needs administrator rights, and this is the process that makes
  it.
- The interface, at normal integrity, which owns grammar, ranking, content search, settings, the
  card, the tray icon and the hotkey.
- A content indexer, which is a child of the interface. Because it is a child, indexing stops
  when the application quits, by construction rather than by lifetime code.

The decoders that read arbitrary files found on a disk - PDF, ONNX, Whisper, image codecs - run
in the indexer at normal integrity and never in the elevated helper. A malformed file is the most
likely thing on a computer to be exploitable, and it does not get to be exploitable with
administrator rights.

## Who made it

Liraz Amir, who publishes as blakazulu: https://github.com/blakazulu

There is no company behind Findra, no funding, no team and no roadmap document. The whole thing is
Apache-2.0 with a NOTICE file, which was chosen over MIT specifically because NOTICE is the
mechanism that carries attribution forward to anybody who forks it, ships it somewhere else, or
takes it apart and never speaks to me again.

## Where it is

Version 0.1.0, released on 4 September 2026. The install that works today is the installer on the
[releases page](https://github.com/blakazulu/findra/releases/latest), which carries one for x64 and one
for arm64. `winget install blakazulu.Findra` becomes the whole install once the submitted manifest
clears moderation in the Microsoft catalogue; it is awaiting a moderator and does not resolve yet.
Building from source still works and needs the .NET 10 SDK and nothing else.
Neither the installer nor the executables are signed, and nothing in the product, the repository
or on this site claims otherwise.

## What it will not do

Findra does not have an account, a cloud service, analytics, crash reporting or telemetry. It
makes one request on its own, once a day, to ask whether a newer version exists, and that request
is described in full on the privacy page. It never installs an update by itself.
