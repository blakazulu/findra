# Findra - Windows Search. But it works.

Desktop search for Windows. Filenames come back from an index held in RAM in 0.33 to 2.05 ms median across five measured
queries, measured on one named desktop. It also finds files by what is written inside them, by what a photo
shows and by what was said out loud, each an optional download. Free, open source, and nothing
leaves your machine.

- Home: https://findra-search.netlify.app/
- Source: https://github.com/blakazulu/findra
- Licence: Apache-2.0
- Platform: Windows 10 and 11, x64 and arm64
- Version: 0.1.0

## What it finds

Names are searchable the second Findra starts, because a name index costs seconds to build.
Everything else is opt-in, because looking inside files walks every drive and can run for hours.

| Capability | Needs | Download |
|---|---|---|
| Names | nothing | 0 |
| Words in documents | nothing, just FTS5 | 0 |
| Photos and video | SigLIP-2 vision, text and spm | 629 MB |
| Meaning in documents | e5-base and e5-spm | 1.04 GB |
| Speech | Whisper turbo, plus the e5 pair | 547 MB |
| Hebrew speech | whisper-ivrit, requires speech | 1.51 GB |

Every capability is independently installable and degrades silently when its model is absent. A
missing model is a normal state, not an error state.

Taking every capability is 3.7 GB of model files. The rows do not sum to what a mixed selection
costs, because a transcript is searched exactly as a document is, so speech brings the document
models with it and you are never charged twice for the same file.

Reading inside files is off until you turn it on, models or no models.

## The numbers

Measured with `findra --searchbench` and pasted without editing. A number without its machine is
marketing rather than measurement, so the machine is named.

| Name query | Round trip p50 | Round trip p95 | Index scan p50 | Worst | Hits |
|---|---|---|---|---|---|
| config | 0.33 ms | 0.37 ms | 0.03 ms | 0.44 ms | 50 |
| report | 0.41 ms | 0.49 ms | 0.08 ms | 0.59 ms | 50 |
| readme | 0.52 ms | 0.67 ms | 0.21 ms | 0.93 ms | 50 |
| invoice | 1.72 ms | 2.16 ms | 1.31 ms | 2.37 ms | 46 |
| sunset | 2.05 ms | 2.42 ms | 1.70 ms | 2.48 ms | 35 |

- 2.6 s from sign-in to ready, for 1,580,825 names enumerated off the volume.
- 73.7 MB resident for that whole name index.
- 0 bytes sent anywhere about your files.

Machine: AMD Ryzen 9 9900X3D, 47.1 GB RAM, NVMe SSD, Windows 11 Pro 10.0.26200.9168, ONNX via
DirectML, Findra 0.1.0, n=50 per query. Yours will differ.

**These numbers are from one machine, and it has an NVIDIA card. AMD and Intel graphics have not
been tested on real hardware, and neither has an arm64 machine.** The paths Findra uses are
vendor-neutral by design so that they should work there; that is a decision rather than a
measurement, and it is said here as one.

Findra tries DirectML for the vision and meaning models and Vulkan for speech, and falls back to
the processor when neither answers. CPU is a supported configuration rather than a failure state:
only the first pass through your files is slower. `findra --searchmodels` prints which provider it
chose and every one it turned down, with reasons.

## How is Findra different from the search Windows already has?

Four differences, and every one of them is something you can check on your own machine rather
than a claim about somebody else's product.

**The names live in RAM, not in a database.** Findra reads the NTFS file table at sign-in and
holds every name in memory: 1,580,825 names in 2.6 seconds, 73.7 MB resident, and 0.33 ms median to answer the fastest of five measured queries.
There is no overnight pass and no index to rebuild, so a file saved a minute ago is findable now.

**Reading inside a PDF needs nothing installed.** Findra decodes documents itself, in a process of
its own that never runs elevated. There is no filter to install, no file type to register and no
index to rebuild afterwards. Turning a capability on later goes back for exactly the files that
capability covers.

**Filename-only search cannot find what you remember.** A file-table index is fast because names
are all it holds. Findra holds the table too, and then, only if you ask, the words in a document,
what a photograph shows, and what was said in a recording - each one a download you can decline.

**Nothing is sent anywhere to make it work.** The models run on your own processor or graphics
card. A photograph is never uploaded to be understood, a query is never sent anywhere to be
answered, and no web results are mixed in among your own files.

## Questions people ask before they install it

### What is Findra?

Findra is a free, open-source desktop search program for Windows 10 and 11. A capsule sits on the
desktop and a global hotkey opens it from anywhere. It finds files by name from an index held in
RAM, and, if you turn it on, by the words inside documents, by what a photograph shows, and by
what was said in a recording.

Findra is an independent application written by one person. It is not made by, affiliated with or
endorsed by Microsoft, and it is not a version, component, fork or replacement of the Windows
Search service that ships inside Windows.

### Does Findra search inside files, or only filenames?

Both, but not at the same time. Filenames are searchable the second Findra starts, because a name
index costs seconds to build. Reading inside files is off until you turn it on, because it walks
every drive and can run for hours. `findra --content on` starts it and `findra --content off`
stops it without discarding anything already read.

### How fast is Findra?

0.33 to 2.05 ms median round trip for a filename across five measured queries - 0.33 ms for "config" and 2.05 ms for "sunset" - including the hop between processes; the worst single sample across the five measured name queries was 2.48 ms. 1,580,825 names were enumerated in 2.6 seconds at sign-in and held in
73.7 MB of RAM. Measured with `findra --searchbench` on an AMD Ryzen 9 9900X3D, 47.1 GB of RAM, an
NVMe SSD, Windows 11 Pro 10.0.26200.9168. Yours will differ.

### Does anything leave my machine?

One request, and it is not about you. Once every 24 hours on startup, Findra makes an anonymous
HTTPS GET to the GitHub releases API to ask whether a newer version exists: no query parameters, no
machine identifier, no install identifier, nothing about your files or your searches. It can be
switched off, and off means the request is not made. Model downloads happen only when you choose a
capability and ask for it.

### How do I install Findra?

Today, the installer on the GitHub releases page, https://github.com/blakazulu/findra/releases/latest, which
carries one for x64 and one for arm64.
`winget install blakazulu.Findra` becomes the whole install once the submitted manifest clears
moderation in the Microsoft catalogue; it does not resolve yet. Building from source needs the
.NET 10 SDK and nothing else. Neither the installer nor the executables are
signed yet, so Windows warns about an unknown publisher until that changes.

### Can I find a photo by describing what is in it?

Yes, if you install the picture capability, which is a 629 MB download of model files that run on
your own machine. Findra compares what you typed against what the model saw in each image, so a
photograph of a receipt on a shop counter turns up for "paper receipt" with none of those words
anywhere in its filename. No image is ever uploaded to be understood.

### Can I search recordings by what was said in them?

Yes, if you install the speech capability. Findra transcribes audio and video on your own machine
and searches the transcript exactly as it searches a document, which is why speech brings the
document models with it: 547 MB on top of them. One number, in minutes, sets how long a recording
is worth transcribing - five by default - and raising it later goes back for exactly the files it
passed over.

### Does Findra need a graphics card?

No. Findra tries DirectML for the picture and meaning models and Vulkan for speech, and falls back
to the processor when neither answers. The processor is a supported configuration rather than a
failure state: only the first pass through your files is slower. Findra's published measurements come from one machine, and it has an NVIDIA card; AMD and Intel graphics have not been tested on real hardware, and
neither has an arm64 machine.

### Does Findra need administrator rights?

Once, for one call. A helper process opens the NTFS volume to read the file table, which is the
only thing in Findra that needs them. Everything else runs at normal integrity, including the part
that opens and decodes your files, so a malformed document never meets an elevated process.
Uninstalling always removes that elevated logon task.

### How much disk space does Findra need?

Nothing extra, if you only want filenames. The capabilities that use a model are separate downloads
you can decline: 629 MB for photos and video, 1.04 GB for meaning in documents, 547 MB for speech
on top of the document models, and 1.51 GB for the Hebrew second pass. Taking every one of them is
3.7 GB. A missing model is a normal state, not an error - that capability is skipped and nothing
else changes.

## Privacy

Your files, their names, their contents and your searches never leave your computer. There is no
account, no cloud service, no analytics, no crash reporting and no telemetry.

The one exception is an anonymous HTTPS GET to the GitHub releases API, at most once every 24
hours, on startup, in the background, to learn whether a newer version exists. No query
parameters, no machine identifier, no install identifier. It is disclosed on the first-run screen,
it can be switched off, and off means the request is not made.

Findra never installs an update by itself.

Full text: https://findra-search.netlify.app/privacy/

## Install

The install that works today is the installer on the releases page,
https://github.com/blakazulu/findra/releases/latest, which carries one for x64 and one for arm64.
`winget install blakazulu.Findra` becomes the whole install once the submitted manifest clears
moderation in the Microsoft catalogue; it is awaiting a moderator and does not resolve yet.
Building from source works too and needs the .NET 10 SDK and nothing else.

```
git clone https://github.com/blakazulu/findra
cd findra
dotnet build
dotnet publish src/Findra -c Release --self-contained
```

Name search needs the elevated helper, which asks for administrator rights exactly once, to open
the volume:

```
dotnet run --project src/Findra -- --names
```

Neither the installer nor the executables are signed yet, so Windows will warn about an unknown
publisher until that changes.

Uninstalling always removes the elevated logon task. It keeps your models, your index and your
settings unless you say otherwise, and tells you the measured size it would free first.
`findra --uninstall --dry-run` prints the whole plan and changes nothing.
