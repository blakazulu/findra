# Find photos on your PC by describing them

To find a photo on a Windows PC by describing what is in it - "dog on a beach", "whiteboard with a
diagram", "receipt on a shop counter" - install Findra's photos capability. A picture model runs on
your own machine, looks at every photo once, and from then on the words you type are matched
against what each picture shows rather than what the file is called.

```
findra --models install photos
findra --content on
```

The same choice is on Findra's first screen and under Content in its settings.

## What it costs

A 629 MB download of model files, once. They are stored on your own disk and nothing is uploaded:
no photo leaves your machine to be understood, there is no account and there is no cloud service.

Looking at every photo takes time the first time through, and it reads only while Findra is open.
With a graphics card it goes faster, and without one it runs on the processor, which is a
supported configuration rather than a failure: only that first pass is slower.

## What it looks at

- Photos: JPEG, PNG, HEIC and HEIF, WebP, AVIF, GIF, BMP, TIFF, and the raw files most cameras
  write (.cr2, .cr3, .nef, .arw, .dng, .orf, .rw2 and .raf)
- Videos: frames taken through the film, so a clip can be found by a scene in the middle of it
- The words in a picture as well, such as a sign, a screenshot or a receipt, which are read without
  any download

A video Windows cannot decode because a codec is missing is still found by name, and Findra's
settings say which codec it needs. Findra installs nothing itself.

## Narrowing it down

The query grammar works alongside a description. `type:photo` keeps only pictures,
`in:D:\Photos` keeps only one folder and `modified:2025` keeps only one year:

```
dog on a beach type:photo modified:2025
```

## When a photo is not found

Ask Findra about that one file. It says why in plain words and changes nothing:

```
findra --searchindex "why:D:\Photos\IMG_4471.HEIC" "q:sunset over water"
```

With a `q:` beside it, Findra also scores that photo against the description and says whether it
cleared the bar a picture has to clear to be shown.

## Try it

Findra is free and open source, for Windows 10 and 11:

```
winget install blakazulu.Findra
```

The installer is also on the releases page, https://github.com/blakazulu/findra/releases/latest,
for x64 and arm64.
