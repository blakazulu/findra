# Windows Search is not finding my files

When Windows Search misses a file you know is there, it is nearly always one of four things: its
index has not reached the file yet, the file is in a folder it does not index, you remember what
the file says rather than what it is called, or the question went to the web instead of your disk.
Each one has a fix inside Windows, and each one is a place where Findra takes a different route.

## The index has not got to it yet

Windows Search answers from an index it builds in the background. A file copied in a minute ago,
or a drive the indexer has only started walking, is not in that index yet, so a search for it
comes back empty even though the file is right there.

In Windows 11, Settings > Privacy & security > Searching Windows shows how far indexing has got.
If the count is not moving or the results look stale, Advanced indexing options > Advanced >
Rebuild starts it again from nothing, which can take hours on a large disk.

Findra does not wait for an index to catch up. It reads the NTFS file table when you sign in and
keeps it current from the change journal, so a file's name is searchable the moment the file
exists, and there is no index to rebuild.

## The folder is not one it indexes

In its Classic mode, Windows Search indexes your libraries and your desktop, and a file anywhere
else is found slowly or not at all. Settings > Privacy & security > Searching Windows > Find my
files offers Enhanced, which indexes the whole PC, and Customize search locations adds a single
folder.

Findra has no list of places to include. Every name on every NTFS volume is searchable from the
start. When you ask it to read inside files it reads every drive except the folders on its
exclusion list, which starts with build and package folders such as `node_modules`, `.git`, `bin`
and `obj`, and which you can read and change in Settings.

## You remember what it says, not what it is called

Windows reads inside a file only when its location is indexed with contents and a filter for that
file type is installed. A PDF, a scanned receipt or a voice memo can easily fall outside all of
that.

Findra reads PDF, Word, Excel, PowerPoint, EPUB, plain text, Markdown, CSV and HTML files itself,
and the text inside pictures, with nothing extra to install. Reading inside files is off until you
turn it on:

```
findra --content on
```

Findra can also find a photo by what it shows and a recording by what was said in it, each an
optional download that runs on your own machine. See
[finding photos by description](https://findra-search.netlify.app/find-photos-by-description/)
and [searching recordings by what was said](https://findra-search.netlify.app/search-recordings-by-speech/).

## The question went to the web

The search box on the taskbar mixes web results in with your files, and on most editions of
Windows 11 turning that off takes a policy or a registry change rather than a switch in Settings.
A search for your own tax return can end with a suggestion to look for it online.

Findra never sends a query anywhere. There are no web results, no account and no telemetry. The
one request it makes on its own is an anonymous check for a newer version, at most once a day,
and it can be switched off.

## When Findra does not find it either

Ask Findra about the one file. This prints the reason in plain words - not on the disk, not a kind
of file Findra reads inside, excluded, still queued, failed, or read and edited since - and changes
nothing:

```
findra --searchindex "why:C:\Users\you\Documents\report.pdf"
```

## Try it

Findra is free and open source, for Windows 10 and 11:

```
winget install blakazulu.Findra
```

The installer is also on the releases page, https://github.com/blakazulu/findra/releases/latest,
for x64 and arm64.
