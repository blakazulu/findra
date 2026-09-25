# Findra - Fast, private desktop search for Windows

Desktop search for Windows. Filenames come back from an index held in RAM in 0.60 to 3.78 ms median across five measured
queries, measured on one named desktop. It also finds files by what is written inside them, by what a photo
shows and by what was said out loud, each an optional download. Free, open source, and nothing
leaves your machine.

- Home: https://findra-search.netlify.app/
- Source: https://github.com/blakazulu/findra
- Licence: Apache-2.0
- Platform: Windows 10 and 11, x64 and arm64
- Version: 0.4.2

## We fixed Windows Search. You're welcome.

Findra holds the file table in RAM and answers before your finger comes off Enter. Type one word
and it gives you the photo that looks like it, the document that says it and the recording where
somebody said it - and not one of them has it in the filename.

## The numbers

**Under 4 ms.** Type part of a filename and the matches are back: 0.60 to 3.78 ms median round trip
for every query measured, across 1,780,595 names, from the moment the query leaves the window to the
moment the results arrive.

- 5.4 s from cold to ready, for 1,780,595 names read off the disk when the helper starts.
- 200.3 MB for the name index of those 1,780,595 names.
- 0 bytes sent anywhere about your files.

Machine: AMD Ryzen 9 9900X3D, 47.1 GB RAM, NVMe SSD, Findra 0.3.1, n=50 per query. These numbers
are from one machine, and it has an NVIDIA card. AMD and Intel graphics have not been tested on
real hardware, and neither has an arm64 machine. Every table is on
https://findra-search.netlify.app/numbers/.

## Install

`winget install blakazulu.Findra` is the whole install. The installer on the releases page,
https://github.com/blakazulu/findra/releases/latest, carries one for x64 and one for arm64 and has
each new release first; the Windows Package Manager catalogue can take a few days to pick one up.
Uninstalling and building from source are on https://findra-search.netlify.app/install/.

## More

- [What it finds](https://findra-search.netlify.app/features/): names, words inside documents,
  photos by what they show, and speech.
- [Why Findra](https://findra-search.netlify.app/why/): how it differs from the search built into
  Windows.
- [The numbers](https://findra-search.netlify.app/numbers/): every measured table, and the machine.
- [Questions](https://findra-search.netlify.app/faq/): speed, privacy, install and hardware.
- [Install](https://findra-search.netlify.app/install/): winget, the installer, uninstalling, and
  building from source.

## Guides

- [Windows Search is not finding my files](https://findra-search.netlify.app/windows-search-not-finding-files/):
  why a file goes missing, and what finds it.
- [Search inside PDFs and documents](https://findra-search.netlify.app/search-inside-pdfs/): the
  words inside your files, with nothing else to install.
- [Find photos by describing them](https://findra-search.netlify.app/find-photos-by-description/):
  type what is in the picture, not what the file is called.
- [Search recordings by what was said](https://findra-search.netlify.app/search-recordings-by-speech/):
  find the recording by the sentence you remember hearing.
