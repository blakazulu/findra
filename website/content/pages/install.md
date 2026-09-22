# Get Findra

`winget install blakazulu.Findra` is the whole install. The installer on the releases page,
https://github.com/blakazulu/findra/releases/latest, carries one for x64 and one for arm64 and has
each new release first; the Windows Package Manager catalogue can take a few days to pick one up.
Every release note is that version's section of the changelog,
https://findra-search.netlify.app/changelog/.

Neither the installer nor the executables are signed yet, so Windows will warn about an unknown
publisher until that changes. The code signing policy,
https://findra-search.netlify.app/code-signing/, says who writes Findra, who approves a release and
who signs it.

## How do I uninstall Findra?

Uninstalling always removes the elevated logon task. It keeps your models, your index and your
settings unless you say otherwise, and tells you the measured size it would free first.
`findra --uninstall --dry-run` prints the whole plan and changes nothing.

## Build it from source

Building from source needs the .NET 10 SDK and nothing else.

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
