#Requires -Version 7
<#
.SYNOPSIS
    Every part of the end-to-end run sheet a script can answer, in one pass-or-fail table.

.DESCRIPTION
    `docs/e2e-run-sheet.md` is the session a person sits down and works through. Most of it needs
    a screen, an elevated prompt, a real disk or a public tag. This is the rest: the parts a
    machine can check on its own, so the human only does what genuinely needs eyes.

    It READS. It never uninstalls, never purges, never registers or unregisters a scheduled task,
    never kills a process it did not start, and never deletes anything under %LOCALAPPDATA%\Findra.
    Every mode it runs is one that reports rather than one that changes something - including
    `--uninstall --dry-run`, which prints the whole plan and touches nothing. If a check needs
    elevation it says so and skips.

    Three outcomes, not two, and the difference matters more than it looks. `ok` and `FAIL` mean
    what they say. `not yet` means the machine has not reached the state the check describes -
    there is no scheduled task on a machine that has never completed first run, and saying that is
    a failure would make the script useless on exactly the machine the run sheet starts from. Only
    FAIL is counted, and only FAIL sets the exit code.

.EXAMPLE
    pwsh -File build/Check-E2E.ps1 -Exe publish/win-x64/findra.exe
    pwsh -File build/Check-E2E.ps1 -Exe "C:\Program Files\Findra\findra.exe"
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Exe)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe)) { throw "e2e: no executable at '$Exe'" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$ExeDir = Split-Path -Parent $Exe

$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Pending = 0
$script:Passed = 0

function Say { param([string] $Text = '') [Console]::Out.WriteLine($Text) }

function Section { param([string] $Title) Say; Say "  $Title" }

function Row {
    param(
        [Parameter(Mandatory)] [ValidateSet('ok', 'FAIL', 'not yet')] [string] $State,
        [Parameter(Mandatory)] [string] $What,
        [string] $Detail = ''
    )
    switch ($State) {
        'ok'      { $script:Passed++ }
        'not yet' { $script:Pending++ }
        'FAIL'    { $script:Failures.Add($What) }
    }
    Say ("    {0,-7} {1}" -f $State, $What)
    if ($Detail) { foreach ($line in ($Detail -split "`n")) { Say "            $line" } }
}

# ---------------------------------------------------------------------------------------------
# The two measurements the product itself makes, reproduced here so the comparison is like for
# like. Sizes.Human is whole mebibytes below a gibibyte and two decimals above it; a check that
# rounded differently would report a disagreement that is only its own arithmetic.
# ---------------------------------------------------------------------------------------------

$Invariant = [cultureinfo]::InvariantCulture

function Human {
    param([long] $Bytes)
    $mb = 1024L * 1024L
    $gb = $mb * 1024L
    if ($Bytes -lt $gb) {
        return ([math]::Round($Bytes / [double]$mb, 0, [MidpointRounding]::AwayFromZero)).ToString('0', $Invariant) + ' MB'
    }
    return ([math]::Round($Bytes / [double]$gb, 2, [MidpointRounding]::AwayFromZero)).ToString('0.##', $Invariant) + ' GB'
}

function FolderBytes {
    param([string] $Dir)
    if (-not (Test-Path -LiteralPath $Dir)) { return [long]0 }
    $total = [long]0
    foreach ($f in Get-ChildItem -LiteralPath $Dir -Recurse -File -Force -ErrorAction SilentlyContinue) {
        $total += $f.Length
    }
    return $total
}

# ---------------------------------------------------------------------------------------------
# Running a mode. The exit code is the contract: a mistyped mode owes 1, and every reporting mode
# owes 0, because a script somewhere checks it.
# ---------------------------------------------------------------------------------------------

function Run {
    param([string[]] $Arguments)
    $text = & $Exe @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ Text = $text; Code = $LASTEXITCODE }
}

function Mode {
    param(
        [string] $What,
        [int[]] $Allowed,
        [string[]] $Arguments,
        [string] $MustSay = ''
    )
    $r = Run $Arguments
    if ($Allowed -notcontains $r.Code) {
        Row FAIL $What "exited $($r.Code), expected one of $($Allowed -join ', ')"
        return $r
    }
    if ($MustSay -and ($r.Text -notmatch [regex]::Escape($MustSay))) {
        Row FAIL $What "did not mention '$MustSay'"
        return $r
    }
    Row ok $What "exit $($r.Code)"
    return $r
}

function IsElevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

Say "findra end-to-end check"
Say "  exe      : $Exe"
Say "  elevated : $(if (IsElevated) { 'yes' } else { 'no - checks needing it are skipped, not elevated' })"

# =============================================================================================
Section 'the binary'
# =============================================================================================

# Subsystem 2 is IMAGE_SUBSYSTEM_WINDOWS_GUI and 3 is _WINDOWS_CUI. A console-subsystem binary is
# given a console window on every launch that has no terminal, and Findra has five of those. The
# project file's OutputType is asserted by the test suite; nothing else reads it back out of the
# built PE, which is the only place a build that ignored the project file would show.
function PeSubsystem {
    param([string] $Path)
    $fs = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Position = 0x3C
        $peAt = $br.ReadInt32()
        $fs.Position = $peAt
        if ($br.ReadUInt32() -ne 0x00004550) { return -1 }
        # PE signature (4) + COFF header (20) + 68 into the optional header, which is where
        # Subsystem sits in both PE32 and PE32+.
        $fs.Position = $peAt + 4 + 20 + 68
        return [int]$br.ReadUInt16()
    } finally { $fs.Dispose() }
}

$subsystem = PeSubsystem $Exe
if ($subsystem -eq 2) {
    Row ok 'the PE subsystem is 2 (windows), not 3 (console)'
} elseif ($subsystem -eq 3) {
    Row FAIL 'the PE subsystem is 2 (windows), not 3 (console)' `
        "it is 3. OutputType has gone back to Exe, and every launch with no terminal opens a`nconsole window: the installer's run step, the Start-menu shortcut, an Explorer`ndouble-click, the autostart entry, and the elevated logon task, which adds a second."
} else {
    Row FAIL 'the PE subsystem is 2 (windows), not 3 (console)' "read $subsystem, which is neither"
}

foreach ($name in 'LICENSE.txt', 'NOTICE.txt', 'OFL-Quicksand.txt') {
    if (Test-Path -LiteralPath (Join-Path $ExeDir $name)) {
        Row ok "$name sits beside findra.exe"
    } else {
        Row FAIL "$name sits beside findra.exe" `
            "the installer's [Files] entry copies the publish folder and nothing else, so a build`nthat stopped copying this installs without it and nothing warns. Apache-2.0 4(d) for`nLICENSE and NOTICE, OFL condition 2 for the font."
    }
}

$installedBy = Join-Path $ExeDir 'installed-by.txt'
if (Test-Path -LiteralPath $installedBy) {
    $source = (Get-Content -LiteralPath $installedBy -Raw).Trim()
    if ($source -in 'installer', 'winget') {
        Row ok "installed-by.txt records how this copy arrived" "it says '$source'"
    } else {
        Row FAIL "installed-by.txt records how this copy arrived" "it says '$source', which is not one of installer, winget"
    }
} else {
    Row 'not yet' 'installed-by.txt records how this copy arrived' `
        'absent, which is correct for a build made with dotnet publish. The installer writes it at ssPostInstall.'
}

# =============================================================================================
Section 'the install folder'
# =============================================================================================

$standardDir = Join-Path $env:ProgramFiles 'Findra'
if (Test-Path -LiteralPath $standardDir) {
    Row ok 'Findra is installed under Program Files' $standardDir
    $installedExe = Join-Path $standardDir 'findra.exe'
    if (Test-Path -LiteralPath $installedExe) {
        Row ok 'findra.exe is in the install folder'
    } else {
        Row FAIL 'findra.exe is in the install folder' "nothing at $installedExe"
    }
    # A version in the path points the scheduled task, which stores an absolute path, at a
    # directory that stops existing at the next upgrade.
    if ($standardDir -match '\d+\.\d+') {
        Row FAIL 'no version number in the install directory' $standardDir
    } else {
        Row ok 'no version number in the install directory'
    }
} else {
    Row 'not yet' 'Findra is installed under Program Files' `
        "nothing at $standardDir. Run sheet phase 1 is where that changes."
}

# Windows 11's Settings > Apps prefers QuietUninstallString over UninstallString, and Inno Setup
# registers one on its own. While it was there, removing Findra from Settings started the
# uninstaller with /SILENT: UninstallSilent() was true, InitializeUninstall returned before it had
# built anything, and the checkbox that asks about the models, the index and the settings was never
# shown, so all of it was kept. The installer deletes the value after installing, and an installed
# machine is the only place that can be seen. Found by DisplayName, so this holds no second copy of
# the AppId the installer declares.
$askedFirst = 'the uninstaller can still ask before it keeps or deletes your data'
$entry = Get-ChildItem -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.DisplayName -eq 'Findra' } |
    Select-Object -First 1
if (-not $entry) {
    Row 'not yet' $askedFirst `
        'no Findra entry under HKLM ... CurrentVersion\Uninstall. Only the installer writes one, so a source build has none.'
} elseif ($entry.PSObject.Properties.Name -contains 'QuietUninstallString') {
    Row FAIL $askedFirst `
        "QuietUninstallString is still registered:`n$($entry.QuietUninstallString)`nWindows Settings prefers it and removes Findra without asking about your data."
} else {
    Row ok $askedFirst 'no QuietUninstallString, so Settings runs the uninstaller that asks'
}

# =============================================================================================
Section 'the modes, and the exit code each one owes'
# =============================================================================================

$version = Mode '--version'               @(0) @('--version') 'log:'
$null    = Mode '--searchtest'            @(0) @('--searchtest')
$index   = Mode '--searchindex'           @(0) @('--searchindex')
$models  = Mode '--searchmodels'          @(0) @('--searchmodels')
$null    = Mode '--models'                @(0) @('--models')
$content = Mode '--content'               @(0) @('--content')
# 1 is correct with no elevated helper, 2 is a helper that answered with no rows. What is being
# checked is that it reached the pipe and said which way it went - a probe that crashed on the
# way would exit 134 or 255.
$probe   = Mode '--searchprobe'           @(0, 1, 2) @('--searchprobe', 'sunset') 'pipe'
$dry     = Mode '--uninstall --dry-run'   @(0) @('--uninstall', '--dry-run') 'scheduled task'
$null    = Mode 'a mistyped mode exits 1' @(1) @('--searchprob')

if ($version.Text -match 'findra\s+(\d+\.\d+\.\d+)') {
    $reported = $Matches[1]
    $props = Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props'
    if (Test-Path -LiteralPath $props) {
        $declared = if ((Get-Content -LiteralPath $props -Raw) -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '' }
        if ($declared -eq $reported) {
            Row ok 'the binary reports the version in Directory.Build.props' "$reported"
        } else {
            Row FAIL 'the binary reports the version in Directory.Build.props' `
                "the binary says '$reported', the props file says '$declared'. This can mean the build`nis stale rather than that the version leaked into a csproj - rebuild before believing it."
        }
    }
} else {
    Row FAIL 'the binary reports the version in Directory.Build.props' '--version printed no parsable version'
}

# =============================================================================================
Section 'the scheduled task'
# =============================================================================================

$taskName = 'Findra names helper'
$taskXml = (& schtasks /query /tn $taskName /xml ONE 2>&1 | Out-String) -replace "`0", ''
$taskCode = $LASTEXITCODE
$taskRegistered = ($taskCode -eq 0)

if ($taskXml -match 'Access is denied') {
    Row 'not yet' 'the scheduled task is registered' `
        'schtasks refused the query without elevation. Re-run this script from an elevated terminal; it will not elevate itself.'
} elseif (-not $taskRegistered) {
    Row 'not yet' 'the scheduled task is registered' `
        "schtasks found no task named '$taskName'. Correct on a machine that has not completed first`nrun. Do NOT create it by hand: run sheet step 2.2 exists because a build once asked the`nscheduler to start a task nothing had ever created, and starting the helper by hand hid it."
} else {
    Row ok 'the scheduled task is registered'

    if ($taskXml -match '<RunLevel>\s*HighestAvailable\s*</RunLevel>') {
        Row ok 'the task runs at HighestAvailable'
    } else {
        Row FAIL 'the task runs at HighestAvailable' `
            'without it the helper cannot open the volume handle, and name search is dead on every launch.'
    }

    if ($taskXml -match '<Arguments>\s*--names\s*</Arguments>') {
        Row ok 'the task starts findra.exe --names'
    } else {
        Row FAIL 'the task starts findra.exe --names' 'the Arguments element does not say --names'
    }

    if ($taskXml -match '<Command>\s*"?([^<"]+)"?\s*</Command>') {
        $taskExe = $Matches[1].Trim()
        if (Test-Path -LiteralPath $taskExe) {
            Row ok 'the task points at a findra.exe that exists' $taskExe
        } else {
            Row FAIL 'the task points at a findra.exe that exists' `
                "it points at $taskExe, which is not there. An elevated logon task aimed at a deleted`nbinary is what the specification calls a defect rather than an inconvenience - it is`nwhat a versioned install directory, or an uninstall that skipped the task, leaves behind."
        }
        if ((Test-Path -LiteralPath $standardDir) -and ($taskExe -notlike "$standardDir*")) {
            Row 'not yet' 'the task points into the install folder' `
                "it points at $taskExe, outside $standardDir. Expected while a source build registered`nit; a failure once the installed copy is the one in use."
        }
    } else {
        Row FAIL 'the task points at a findra.exe that exists' 'no Command element could be read out of the task XML'
    }
}

# The probe reports the task state through a different call than schtasks does. Two answers that
# disagree mean one of them is reading something else.
if ($probe.Text -match 'helper task registered\s*:\s*(\S+)') {
    $probeSays = $Matches[1].ToUpperInvariant()
    $expected = if ($taskRegistered) { 'YES' } else { 'NO' }
    if ($probeSays -eq $expected) {
        Row ok '--searchprobe agrees with schtasks about the task' "both say $expected"
    } elseif ($probeSays -eq 'UNKNOWN') {
        Row 'not yet' '--searchprobe agrees with schtasks about the task' `
            'the probe could not establish it. Three-valued on purpose: a locked-down machine must not look identical to a fresh one.'
    } else {
        Row FAIL '--searchprobe agrees with schtasks about the task' `
            "the probe says $probeSays and schtasks says $expected"
    }
} else {
    Row FAIL '--searchprobe agrees with schtasks about the task' 'the probe printed no task line at all'
}

# =============================================================================================
Section 'the autostart entry'
# =============================================================================================

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$autoValue = $null
try { $autoValue = (Get-ItemProperty -LiteralPath $runKey -Name 'Findra' -ErrorAction Stop).Findra } catch { }

if ($null -eq $autoValue) {
    Row 'not yet' 'the start-at-sign-in entry is present' `
        "no Findra value under HKCU\...\Run. Correct until somebody ticks it in Settings; Findra writes`nit from its own session and the installer deliberately does not, because an elevated`ninstaller's HKCU is whoever answered the prompt."
} else {
    Row ok 'the start-at-sign-in entry is present' $autoValue
    if ($autoValue.StartsWith('"') -and $autoValue.TrimEnd().EndsWith('"')) {
        Row ok 'the autostart command is quoted'
    } else {
        Row FAIL 'the autostart command is quoted' `
            'an unquoted path with a space in it makes Windows run the first word and pass the rest as arguments, at every sign-in, with no error anywhere.'
    }
    $autoExe = $autoValue.Trim().Trim('"')
    if (Test-Path -LiteralPath $autoExe) {
        Row ok 'the autostart entry points at a findra.exe that exists'
    } else {
        Row FAIL 'the autostart entry points at a findra.exe that exists' "it points at $autoExe"
    }
}

# =============================================================================================
Section 'the models on disk, against what --searchmodels reports'
# =============================================================================================

$modelsDir = Join-Path $env:LOCALAPPDATA 'Findra\models'
$reportedFiles = @{}
foreach ($line in ($models.Text -split "`r?`n")) {
    if ($line -match '^\s+(ok|--|\?\?)\s+(\S+\.(?:onnx|spm|bin))\s') {
        $reportedFiles[$Matches[2]] = $Matches[1]
    }
}

if ($reportedFiles.Count -eq 0) {
    Row FAIL '--searchmodels lists the declared model files' 'no file rows could be read out of its output'
} else {
    Row ok '--searchmodels lists the declared model files' "$($reportedFiles.Count) row(s)"

    $wrongSize = @($reportedFiles.GetEnumerator() | Where-Object { $_.Value -eq '??' })
    if ($wrongSize.Count -gt 0) {
        Row FAIL 'no model file is on disk at the wrong size' `
            "$(($wrongSize | ForEach-Object { $_.Key }) -join ', ')`na present file at the wrong size is a truncated download reported as installed, after which`nevery capability needing it fails quietly. ModelDownloader's floor exists for this."
    } else {
        Row ok 'no model file is on disk at the wrong size'
    }

    $disagreed = @()
    foreach ($entry in $reportedFiles.GetEnumerator()) {
        $onDisk = Test-Path -LiteralPath (Join-Path $modelsDir $entry.Key)
        $claims = ($entry.Value -ne '--')
        if ($onDisk -ne $claims) { $disagreed += "$($entry.Key): report says $(if ($claims) {'present'} else {'absent'}), disk says $(if ($onDisk) {'present'} else {'absent'})" }
    }
    if ($disagreed.Count -eq 0) {
        Row ok 'every file --searchmodels claims is on disk is on disk' $modelsDir
    } else {
        Row FAIL 'every file --searchmodels claims is on disk is on disk' ($disagreed -join "`n")
    }

    if (Test-Path -LiteralPath $modelsDir) {
        $stray = @(Get-ChildItem -LiteralPath $modelsDir -File -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.onnx', '.spm', '.bin' } |
            Where-Object { -not $reportedFiles.ContainsKey($_.Name) })
        if ($stray.Count -eq 0) {
            Row ok 'nothing in the models folder is unaccounted for'
        } else {
            Row FAIL 'nothing in the models folder is unaccounted for' `
                "$(($stray | ForEach-Object { $_.Name }) -join ', ')`na file the report does not know about is either a leftover partial or a model that was`nadded to the folder and not to the table."
        }
    }
}

if ($models.Text -match '(?m)^\s+(?:DirectML|CPU)\s*:\s*chosen') {
    Row ok '--searchmodels names the ONNX execution provider it chose'
} else {
    Row FAIL '--searchmodels names the ONNX execution provider it chose' `
        '"it is slow on my laptop" is unanswerable; "DirectML failed to initialise, fell back to CPU" is a bug report.'
}

if ($models.Text -match 'whisper execution provider') {
    if ($models.Text -match 'no model is on disk to open') {
        Row 'not yet' '--searchmodels names the Whisper execution provider' `
            'no Whisper model on disk, so nothing was tried. Run sheet step 2.5 is where this must start naming Vulkan or, with a reason, CPU.'
    } else {
        Row ok '--searchmodels names the Whisper execution provider'
    }
} else {
    Row FAIL '--searchmodels names the Whisper execution provider' 'the section is missing entirely'
}

# =============================================================================================
Section 'the measured sizes in --uninstall --dry-run, against the folders'
# =============================================================================================

$folders = @{
    models   = $modelsDir
    index    = Join-Path $env:LOCALAPPDATA 'Findra\index'
    logs     = Join-Path $env:LOCALAPPDATA 'Findra\logs'
    settings = Join-Path $env:APPDATA 'Findra'
}

$seen = 0
foreach ($line in ($dry.Text -split "`r?`n")) {
    if ($line -match '^\s+(models|index|logs|settings)\s+([\d.]+ (?:MB|GB))\s+(\S.*)$') {
        $label = $Matches[1]
        $said = $Matches[2]
        $path = $Matches[3].Trim()
        $seen++

        if ($path -ne $folders[$label]) {
            Row FAIL "the $label path in the report is the one in the specification's table" `
                "the report says $path`nthe table says $($folders[$label])"
            continue
        }

        $real = Human (FolderBytes $path)
        if ($real -eq $said) {
            Row ok "the $label size in the report matches the folder" "$said"
        } else {
            Row FAIL "the $label size in the report matches the folder" `
                "the report says $said, the folder measures $real.`nEverything in the product that quotes a size to somebody about to delete something comes`nthrough this one measurement, including the uninstall prompt's total."
        }
    }
}

if ($seen -ne 4) {
    Row FAIL 'the report measures all four folders' "it named $seen of models, index, logs, settings"
} else {
    Row ok 'the report measures all four folders'
}

if ($dry.Text -match 'scheduled task') {
    Row ok 'the plan says it removes the scheduled task' `
        'always removed, purge or not. Leaving it behind orphans an elevated logon task pointing at a deleted binary.'
} else {
    Row FAIL 'the plan says it removes the scheduled task' 'the dry run does not mention it at all'
}

# =============================================================================================
Section 'reading inside files'
# =============================================================================================

# Catalogue item 27. --content reads config.json; --searchindex reads the index's own
# index:paused row. On a machine where the interface has never run, the row was never written and
# the two describe different things while both behave exactly as specified.
$indexSaysOff = ($index.Text -match 'inside files is off')
$contentSaysOff = ($content.Text -match '(?m)^\s+inside files\s*:\s*off')

if ($indexSaysOff -eq $contentSaysOff) {
    Row ok '--content and --searchindex agree about whether reading inside files is on' `
        "both say $(if ($indexSaysOff) { 'off' } else { 'on' })"
} else {
    Row 'not yet' '--content and --searchindex agree about whether reading inside files is on' `
        "--searchindex says $(if ($indexSaysOff) { 'off' } else { 'on' }) and --content does not.`nExpected until the interface has run on this machine and written the index:paused row.`nAfter run sheet step 4.1 this is a failure: it would mean --searchindex is describing an`nindex nobody has told about a setting that changed."
}

if ($index.Text -match '(?m)^\s+transcribe:\s*(.+)$') {
    Row ok 'the transcription limit is reported' $Matches[1].Trim()
} else {
    Row FAIL 'the transcription limit is reported' '--searchindex printed no transcribe line'
}

# =============================================================================================
Say ''
Say ("  {0} ok, {1} not yet, {2} failed" -f $script:Passed, $script:Pending, $script:Failures.Count)

if ($script:Failures.Count -gt 0) {
    Say ''
    [Console]::Error.WriteLine('e2e: these checks disagreed -')
    foreach ($f in $script:Failures) { [Console]::Error.WriteLine("  $f") }
    exit 1
}

Say ''
Say '  Nothing here failed. Everything a script can answer is answered; the rest of'
Say '  docs/e2e-run-sheet.md needs a screen, an elevated prompt, a real disk or a tag.'
exit 0
