#Requires -Version 7
<#
.SYNOPSIS
    Redraws every screenshot the README shows, and copies each one the website also shows.

.DESCRIPTION
    Spec 9a: every picture on either page is a real --searchshot render with the command that
    produced it printed underneath, and both pages promise that running the command gets you the
    image. Two directories hold those renders - docs/shots for the README and
    website/public/shots for the site - and they have to be two, because Netlify publishes
    website/public exactly as it sits and nothing under it can reach up into docs/.

    Nothing kept them together and they drifted: the site served an adv, a firstrun and a
    settingscontent from an older build while printing the command for the current ones, and its
    Settings picture was missing two controls the product had gained. ShotTests fails if that
    ever happens again; this is how you make it stop failing, in one command rather than twelve.

    The list of images is READ OUT OF THE README rather than kept here. A third copy of it is a
    third thing to forget, which is the shape of the bug this script exists to prevent.

.PARAMETER Exe
    A built findra.exe. publish/win-x64/findra.exe, or the one in src/Findra/bin.

.EXAMPLE
    pwsh -File build/Make-Shots.ps1 -Exe publish/win-x64/findra.exe
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Exe)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe)) { throw "shots: no executable at '$Exe'" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path

$root = Split-Path -Parent $PSScriptRoot
$readme = Get-Content -Raw -LiteralPath (Join-Path $root 'README.md')

# The line printed under each image. Anything without the docs/shots/ prefix is one of the
# README's generic examples ("--searchshot out.png results Mond") and is not a picture it stores.
$calls = [regex]::Matches($readme,
    '--searchshot\s+docs/shots/(?<file>[A-Za-z0-9._-]+\.png)\s+(?<state>[a-z]+)(?:\s+(?<palette>[A-Za-z]+))?')

if ($calls.Count -eq 0) { throw 'shots: the README prints no --searchshot commands to run' }

$siteDir = Join-Path $root 'website/public/shots'
$drawn = 0
$copied = 0

foreach ($call in $calls) {
    $file = $call.Groups['file'].Value
    $out = Join-Path $root "docs/shots/$file"

    # Not $args: that name is an automatic variable and assigning to it here would be a quiet
    # fight with the shell rather than an error.
    $argv = @('--searchshot', $out, $call.Groups['state'].Value)
    if ($call.Groups['palette'].Success) { $argv += $call.Groups['palette'].Value }

    # Piped, and that is load-bearing. findra.exe is a windows-subsystem binary and PowerShell
    # does not wait for one it invokes directly - it would return with the file half written, or
    # not written at all, and the copy below would take whatever was there before. Consuming the
    # output stream to its end is what makes the shell wait. Check-Diagnostics.ps1 runs every
    # mode the same way and for the same reason.
    $output = & $Exe @argv 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "shots: $Exe $($argv -join ' ') exited $LASTEXITCODE`n$output"
    }
    $drawn++

    # Only where the site already carries that picture. WHICH shots the site shows is the site's
    # business and is decided in its markup; that its copy is the same render is not.
    $twin = Join-Path $siteDir $file
    if (Test-Path -LiteralPath $twin) {
        Copy-Item -LiteralPath $out -Destination $twin -Force
        $copied++
        "  $file  ->  docs/shots and website/public/shots"
    }
    else {
        "  $file  ->  docs/shots"
    }
}

""
"redrew $drawn image(s); $copied of them are also on the website"
