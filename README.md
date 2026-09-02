# Findra

Desktop search for Windows. A capsule sits on your desktop; click it, or press a global
hotkey, and it unfolds into a results card.

**Findra is being built in the open and is not ready to install yet.** This page describes
what runs today, and nothing else. When the product is finished this file gets its real
front page: screenshots produced by `findra --searchshot`, and every number produced by
`findra --searchbench` on a named machine. Until then it promises nothing it cannot show.

## What works today

- **Search by name, across NTFS volumes.** An elevated helper reads the Master File Table
  and the change journal and holds the name index in RAM. The interface runs unelevated and
  asks over a local pipe.
- **The card and the capsule**, drawn directly with Skia in six palettes, three dark and
  three light, following the Windows light/dark setting or pinned to either.
- **A global hotkey** with a fallback chain, because the first combination is taken on some
  machines. Findra tells you which one it registered.
- **A tray icon**, a settings file, and a version check.

Not built yet: searching *inside* files. Words in documents, what a photo shows, and what
was said in a recording are the next plans.

## Build it

Requires the .NET 10 SDK.

    git clone https://github.com/blakazulu/findra
    cd findra
    dotnet build
    dotnet test

Run the interface with `dotnet run --project src/Findra`. Name search needs the helper,
which needs administrator rights exactly once, to open the volume:

    dotnet run --project src/Findra -- --names

## Check it yourself

Findra is built to be verified without a screen. These four run today:

    findra --version                     which build this is, and where its logs are
    findra --searchtest                  engine self-check
    findra --searchprobe sunset          the whole query path, end to end
    findra --searchshot out.png results  render a surface to a PNG, no screen required

Three more arrive with the work they report on, and exit with an error until then:
`--searchindex` for what is indexed and what is queued, `--searchmodels` for which models
are present and which accelerator they chose, and `--searchbench` for measured numbers.

## What leaves your machine

Your files, their names, their contents and your searches never leave your machine. There
is no account, no cloud and no telemetry.

There is exactly one outbound request, and it is written down rather than buried: an
anonymous HTTPS GET to the GitHub releases API, at most once every 24 hours, in the
background, to learn whether a newer version exists. It sends no query parameters, no
machine or install identifier, and nothing about your files or searches. It never blocks
anything, and a failure is a line in the log rather than a dialog. It can be switched off,
and off means the request is not made. Findra never installs anything by itself.

Full detail, including what the index contains and what the logs record, is in
[PRIVACY.md](PRIVACY.md).

## Licence

Apache-2.0. Free to use, clone and modify. See `NOTICE` for the attribution you must keep.
