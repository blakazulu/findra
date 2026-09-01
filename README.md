# Findra

Desktop search for Windows that finds files by name in milliseconds, and by what is
*inside* them - words in documents, what a photo shows, what was said in a recording.

A capsule sits on your desktop. Click it, or press the hotkey, and it unfolds into results.

## Install

    winget install blakazulu.Findra

Or build it:

    git clone https://github.com/blakazulu/findra
    cd findra
    dotnet publish src/Findra -c Release -r win-x64 --self-contained

## What it costs

Names and full-text search inside documents are free and need no download.
Searching photos, speech and document *meaning* uses local models - up to 2.9 GB,
chosen a capability at a time on first run, and never downloaded without asking.

Nothing leaves your machine. No account, no cloud, no telemetry.

## Licence

Apache-2.0. Free to use, clone and modify - see NOTICE for the attribution you must keep.
