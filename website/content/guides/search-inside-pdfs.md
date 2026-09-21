# Search inside PDFs and documents on Windows

To search the words inside your PDFs and documents on Windows without installing a filter for each
file type, turn on content reading in Findra. It reads the files itself, on your own machine, and
from then on a search for a phrase finds the documents that contain it, whatever they are called.

```
findra --content on
```

The same switch is under Content in Findra's settings, beside a Start now button.

## What it reads

- PDF
- Word (.docx), Excel (.xlsx) and PowerPoint (.pptx)
- EPUB
- Plain text, Markdown and CSV
- HTML
- The text inside pictures, such as a screenshot or a photographed receipt

The older binary formats - .doc, .xls and .ppt - and RTF and the OpenDocument formats are found by
name but not read inside yet. Code, JSON and logs are deliberately left to name search, because a
search over the inside of every package file on a disk helps nobody.

## What it costs

Nothing to download. Searching the words in a document uses a full-text index on your own disk and
no model at all.

What it costs is time, once. The first pass walks every drive and can run for hours on a large
disk, which is why it never starts on its own. It reads only while Findra is open, it shows its
progress under the card, and it steps aside while a game is fullscreen or another program is
working the graphics card. After the first pass, a new or changed file is read as soon as it is
saved.

## Searching by meaning, not only by the words

An optional 1.04 GB download teaches Findra what a passage means as well as which words it uses, so
a search for "notice period" can find the paragraph that says "either party may end this agreement
with 30 days' warning". It runs on your own processor or graphics card:

```
findra --models install meaning
```

## Folders it leaves alone

Reading skips the folders on the exclusion list, which starts with build and package folders such
as `node_modules`, `.git`, `bin`, `obj`, `packages` and `site-packages`. The list is in Settings,
where you can read it and change it. Nothing else is skipped.

## When a document is not found

Ask Findra about that one file. It says why in plain words - excluded, still queued, failed to
read, read and edited since, or a kind of file it does not read inside - and changes nothing:

```
findra --searchindex "why:C:\Users\you\Documents\contract.pdf"
```

## Nothing leaves your machine

The words Findra reads stay in an index on your own disk. Nothing is uploaded to be read or
searched, there is no account and there is no telemetry. See the
[privacy policy](https://findra-search.netlify.app/privacy/) for the one request Findra makes on
its own.

## Try it

Findra is free and open source, for Windows 10 and 11:

```
winget install blakazulu.Findra
```

The installer is also on the releases page, https://github.com/blakazulu/findra/releases/latest,
for x64 and arm64.
