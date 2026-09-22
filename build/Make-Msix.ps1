#Requires -Version 7
<#
.SYNOPSIS
    Pack one architecture of Findra as an MSIX for the Microsoft Store, or bundle the two.

.DESCRIPTION
    The Store path exists beside the installer path and shares its rules. Publish.ps1 does the
    publish - self-contained, RID on the command line, no model files - and this script does
    only what MSIX adds: a layout with AppxManifest.xml at its root, and makeappx.

    Three values in packaging/store/Package.appxmanifest exist nowhere until a Partner Center
    account reserves the app name. Packing with the placeholders still in it produces a package
    whose identity matches nothing Microsoft will accept, so the placeholders are REFUSED here,
    the way the winget manifest's zero hashes are refused by its workflow.

    Signing is deliberately absent. Store submissions are re-signed by Microsoft after
    certification, so an unsigned package is the correct artefact - and the same package is
    useless for sideloading, which is fine: the installer on the releases page is the
    sideloading story.

    -Bundle takes the two per-architecture packages and makes the single .msixbundle Partner
    Center prefers. It is a separate invocation because the two packs are separate invocations.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')] [string] $Rid = 'win-x64',
    [switch] $Bundle,
    [string] $Configuration = 'Release',
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The one version file, with the Store's mandatory fourth part. Appx/MSIX versions are four
# integers; Directory.Build.props carries three because System.Version and the tag both do.
$props = Get-Content (Join-Path $Root 'Directory.Build.props') -Raw
$m = [regex]::Match($props, '<Version>(\d+\.\d+\.\d+)</Version>')
if (-not $m.Success) { throw 'Directory.Build.props declares no <Version>' }
$packageVersion = "$($m.Groups[1].Value).0"

# makeappx ships with the Windows SDK that the windows-latest image carries. A FLOOR, not a
# pin - the same lesson the Inno Setup step learned: pick the newest kit present rather than
# name one the image may move past. The braced spelling is required for the variable Windows
# writes with brackets in its name (WorkflowTests reads for exactly this).
function Find-MakeAppx {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $kits)) { return $null }
    $found = Get-ChildItem -LiteralPath $kits -Directory |
        Where-Object { $_.Name -match '^10\.' } |
        Sort-Object { [version] $_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
    return $found
}

$makeappx = Find-MakeAppx
if (-not $makeappx) { throw 'no makeappx.exe under the Windows Kits directory on this machine' }
Write-Output "using $makeappx"

if ($Bundle) {
    $msixDir = Join-Path $Root 'store/msix'
    $packages = Get-ChildItem -LiteralPath $msixDir -Filter '*.msix' -ErrorAction SilentlyContinue
    if ($packages.Count -lt 2) {
        throw "expected two .msix packages in store/msix to bundle, found $($packages.Count) - run the pack step for both RIDs first"
    }
    $bundleDir = Join-Path $Root 'store/bundle'
    if (Test-Path -LiteralPath $bundleDir) { Remove-Item -LiteralPath $bundleDir -Recurse -Force }
    New-Item -ItemType Directory -Path $bundleDir | Out-Null
    $bundle = Join-Path $bundleDir "findra_$packageVersion.msixbundle"
    & $makeappx bundle /d $msixDir /p $bundle
    if ($LASTEXITCODE -ne 0) { throw "makeappx bundle exited with $LASTEXITCODE" }
    Write-Output $bundle
    return
}

$archOf = @{ 'win-x64' = 'x64'; 'win-arm64' = 'arm64' }
$arch = $archOf[$Rid]

$manifestTemplate = Join-Path $Root 'packaging/store/Package.appxmanifest'
$manifest = Get-Content $manifestTemplate -Raw

# The placeholders only Partner Center can fill. Any one of them surviving means the identity
# is invented, and an invented identity uploads to nothing - fail before the publish, not after.
foreach ($placeholder in '__PACKAGE_IDENTITY_NAME__', '__PACKAGE_PUBLISHER__', '__PUBLISHER_DISPLAY_NAME__') {
    if ($manifest.Contains($placeholder)) {
        throw "packaging/store/Package.appxmanifest still carries $placeholder - the real values come from Partner Center (Product management > App identity) once the app name is reserved; see docs/store.md"
    }
}

# Version and architecture are this script's to fill, and the committed file says so.
$manifest = $manifest -replace '(?<=<Identity[^>]*Version=")[^"]+', $packageVersion
$manifest = $manifest -replace '(?<=ProcessorArchitecture=")[^"]+', $arch

# The publish is Publish.ps1's job and nobody else's: self-contained, RID on the command line,
# model files refused. The layout is that folder plus the manifest and the tiles.
& pwsh -File (Join-Path $PSScriptRoot 'Publish.ps1') -Rid $Rid -Configuration $Configuration | Out-Null
if ($LASTEXITCODE -ne 0) { throw "publish failed for $Rid" }

$layout = Join-Path $Root "store/layout/$Rid"
if (Test-Path -LiteralPath $layout) { Remove-Item -LiteralPath $layout -Recurse -Force }
New-Item -ItemType Directory -Path $layout | Out-Null
Copy-Item (Join-Path $Root "publish/$Rid/*") $layout -Recurse
Copy-Item (Join-Path $Root 'packaging/store/Assets') (Join-Path $layout 'Assets') -Recurse
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding($true)))

$outDir = Join-Path $Root 'store/msix'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$package = Join-Path $outDir "findra_${packageVersion}_${arch}.msix"
& $makeappx pack /d $layout /p $package
if ($LASTEXITCODE -ne 0) { throw "makeappx pack exited with $LASTEXITCODE" }
Write-Output $package
