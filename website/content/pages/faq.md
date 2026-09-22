# Questions people ask before they install it

## What is Findra?

Findra is a free, open-source desktop search program for Windows 10 and 11. A capsule sits on the
desktop and a global hotkey opens it from anywhere. It finds files by name from an index held in
RAM, and, if you turn it on, by the words inside documents, by what a photograph shows, and by
what was said in a recording.

Findra is an independent application written by one person. It is not made by, affiliated with or
endorsed by Microsoft, and it is not a version, component, fork or replacement of the Windows
Search service that ships inside Windows.

## Does Findra search inside files, or only filenames?

Both, but not at the same time. Filenames are searchable the second Findra starts, because a name
index costs seconds to build. Reading inside files is off until you turn it on, because it walks
every drive and can run for hours. `findra --content on` starts it and `findra --content off`
stops it without discarding anything already read.

## How fast is Findra?

0.60 to 3.78 ms median round trip for a filename across five measured queries - 0.60 ms for
"config" and 3.78 ms for "sunset" - including the hop between processes; the worst single sample
across the five measured name queries was 5.36 ms. 1,780,595 names were enumerated in 5.4 seconds
when the name helper started, and the index holding them takes 200.3 MB of RAM. Measured with
`findra --searchbench` on an AMD Ryzen 9 9900X3D, 47.1 GB of RAM, an NVMe SSD, Windows 11 Pro
10.0.26200.9445. Yours will differ.

## Does anything leave my machine?

One request, and it is not about you. Once every 24 hours on startup, Findra makes an anonymous
HTTPS GET to GitHub - the releases API, or the winget catalogue's listing for a winget install -
to ask whether a newer version exists: no query parameters, no
machine identifier, no install identifier, nothing about your files or your searches. It can be
switched off, and off means the request is not made. Model downloads happen only when you choose a
capability and ask for it.

## How do I install Findra?

`winget install blakazulu.Findra`, which picks the x64 or arm64 build for your machine. The same
installer is on the GitHub releases page, https://github.com/blakazulu/findra/releases/latest, and
that page carries each new release first: the Windows Package Manager catalogue can take a few days
to pick one up. Building from source needs the .NET 10 SDK and nothing else. Neither the installer
nor the executables are signed yet, so Windows warns about an unknown publisher until that changes.

## Can I find a photo by describing what is in it?

Yes, if you install the picture capability, which is a 629 MB download of model files that run on
your own machine. Findra compares what you typed against what the model saw in each image, so a
photograph of a receipt on a shop counter turns up for "paper receipt" with none of those words
anywhere in its filename. No image is ever uploaded to be understood.

## Can I search recordings by what was said in them?

Yes, if you install the speech capability. Findra transcribes audio and video on your own machine
and searches the transcript exactly as it searches a document, which is why speech brings the
document models with it: 547 MB on top of them. One number, in minutes, sets how long a recording
is worth transcribing - five by default - and raising it later goes back for exactly the files it
passed over.

## Does Findra need a graphics card?

No. Findra tries DirectML for the picture and meaning models and Vulkan for speech, and falls back
to the processor when neither answers. The processor is a supported configuration rather than a
failure state: only the first pass through your files is slower. When another program is working the graphics card hard, or something is running fullscreen, Findra stops reading, lets go of the models it had loaded, and carries on once the card has been free for a minute. Findra's published measurements come from one machine, and it has an NVIDIA card; AMD and Intel graphics have not been tested on real hardware, and
neither has an arm64 machine.

## Does Findra need administrator rights?

Once, for one call. A helper process opens the NTFS volume to read the file table, which is the
only thing in Findra that needs them. Everything else runs at normal integrity, including the part
that opens and decodes your files, so a malformed document never meets an elevated process.
Uninstalling always removes that elevated logon task.

## How much disk space does Findra need?

Nothing extra, if you only want filenames. The capabilities that use a model are separate downloads
you can decline: 629 MB for photos and video, 1.04 GB for meaning in documents, 547 MB for speech
on top of the document models, and 1.51 GB for the Hebrew second pass. Taking every one of them is
3.7 GB. A missing model is a normal state, not an error - that capability is skipped and nothing
else changes.

## What leaves your machine

Your files, their names, their contents and your searches never leave your computer. There is no
account, no cloud service, no analytics, no crash reporting and no telemetry.

The one exception is an anonymous HTTPS GET to GitHub - the releases API, or the winget
catalogue's listing for a winget install - at most once every 24
hours, on startup, in the background, to learn whether a newer version exists. No query
parameters, no machine identifier, no install identifier. It is disclosed on the first-run screen,
it can be switched off, and off means the request is not made.

Findra never installs an update by itself.

Full text: https://findra-search.netlify.app/privacy/
