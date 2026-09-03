# Findra - Windows Search. But it works.

Desktop search for Windows that answers in under a millisecond, straight from RAM. It finds files
by name, by what is written inside them, by what a photo shows and by what was said out loud.
Free, open source, and nothing leaves your machine.

- Home: https://findra-search.netlify.app/
- Source: https://github.com/blakazulu/findra
- Licence: Apache-2.0
- Platform: Windows 10 and 11, x64 and arm64
- Version: 0.1.0 (the first release is not tagged yet)

## What it finds

Names are searchable the second Findra starts, because a name index costs seconds to build.
Everything else is opt-in, because looking inside files walks every drive and can run for hours.

| Capability | Needs | Download |
|---|---|---|
| Names | nothing | 0 |
| Words in documents | nothing, just FTS5 | 0 |
| Photos and video | SigLIP-2 vision, text and spm | 629 MB |
| Meaning in documents | e5-base and e5-spm | 270 MB |
| Speech | Whisper turbo, plus the e5 pair | 547 MB |
| Hebrew speech | whisper-ivrit, requires speech | 1549 MB |

Every capability is independently installable and degrades silently when its model is absent. A
missing model is a normal state, not an error state.

Reading inside files is off until you turn it on, models or no models.

## The numbers

Measured with `findra --searchbench` and pasted without editing. A number without its machine is
marketing rather than measurement, so the machine is named.

| Name query | Round trip p50 | Round trip p95 | Index scan p50 | Worst | Hits |
|---|---|---|---|---|---|
| config | 0.50 ms | 0.54 ms | 0.06 ms | 0.56 ms | 50 |
| report | 0.54 ms | 0.61 ms | 0.16 ms | 1.31 ms | 50 |
| readme | 1.00 ms | 1.06 ms | 0.51 ms | 1.33 ms | 50 |
| invoice | 2.53 ms | 5.04 ms | 2.13 ms | 5.42 ms | 45 |
| sunset | 3.99 ms | 4.43 ms | 3.55 ms | 5.20 ms | 35 |

- 2.8 s from sign-in to ready, for 1,573,675 names enumerated off the volume.
- 73.1 MB resident for that whole name index.
- 0 bytes sent anywhere about your files.

Machine: AMD Ryzen 9 9900X3D, 47.1 GB RAM, NVMe SSD, Windows 11 Pro 10.0.26200.9168, ONNX via
DirectML, Findra 0.1.0, n=50 per query. Yours will differ.

Findra tries DirectML for the vision and meaning models and Vulkan for speech, and falls back to
the processor when neither answers. CPU is a supported configuration rather than a failure state:
only the first pass through your files is slower. `findra --searchmodels` prints which provider it
chose and every one it turned down, with reasons.

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

Building from source is the route that works today. It needs the .NET 10 SDK and nothing else.

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

When the first release is tagged, the whole install becomes `winget install blakazulu.Findra`.
Neither the installer nor the executables are signed yet, so Windows will warn about an unknown
publisher until that changes.

Uninstalling always removes the elevated logon task. It keeps your models, your index and your
settings unless you say otherwise, and tells you the measured size it would free first.
`findra --uninstall --dry-run` prints the whole plan and changes nothing.
