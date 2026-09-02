#Requires -Version 7
<#
.SYNOPSIS
    Decides whether a tag may be released, and prints the notes it would be released with.

.DESCRIPTION
    Three rules, in the order a failure costs the most:

      1. The tag is v<major>.<minor>.<patch> with no suffix. Findra's own update check parses
         versions with System.Version, which cannot order a pre-release against a release, and
         GitHub's releases/latest endpoint skips pre-releases entirely - so a pre-release tag
         produces a build no installed copy will ever hear about.

      2. The tag matches Directory.Build.props. Findra compares its own version against the
         newest release tag; a release whose binary reports a different number tells every user
         they are current for ever, which the specification calls worse than no check at all.

      3. CHANGELOG.md has a non-empty section for that version. The release notes ARE that
         section - GitHub's generated notes are deliberately not used, because the changelog is
         also where Findra sends anyone who built from source.

    On success the notes go to stdout and nothing else does, so the workflow can redirect them
    straight into a file.

.EXAMPLE
    pwsh -File build/Check-Release.ps1 -Tag v1.2.0 > release-notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail {
    param([int] $Code, [string] $Message)
    [Console]::Error.WriteLine("release: $Message")
    exit $Code
}

if ($Tag -match '^v\d+\.\d+\.\d+-') {
    Fail 3 ("'$Tag' is a pre-release tag. Findra parses versions with System.Version, which " +
            "cannot order a pre-release against a release, and GitHub's releases/latest skips " +
            "pre-releases - so this build would be invisible to every installed copy. Tag a " +
            "plain version, or teach UpdateCheck.ParseVersion about pre-releases first.")
}

if ($Tag -notmatch '^v\d+\.\d+\.\d+$') {
    Fail 2 "'$Tag' is not of the form v<major>.<minor>.<patch>."
}

$version = $Tag.Substring(1)

$propsPath = Join-Path $Root 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath)) { Fail 4 "no Directory.Build.props under '$Root'." }

$node = ([xml](Get-Content -LiteralPath $propsPath -Raw)).SelectSingleNode('//Version')
if ($null -eq $node) { Fail 4 "Directory.Build.props declares no <Version>." }
$declared = $node.InnerText.Trim()

if ($declared -ne $version) {
    Fail 4 ("the tag says $version and Directory.Build.props says $declared. Findra compares its " +
            "own version against the newest release tag, so a release whose binary reports a " +
            "different number tells every installed copy it is up to date for ever.")
}

$changelogPath = Join-Path $Root 'CHANGELOG.md'
if (-not (Test-Path -LiteralPath $changelogPath)) { Fail 5 "no CHANGELOG.md under '$Root'." }

$lines = @(Get-Content -LiteralPath $changelogPath)
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match ('^##\s+\[' + [regex]::Escape($version) + '\]')) { $start = $i + 1; break }
}

if ($start -lt 0) {
    Fail 5 ("CHANGELOG.md has no '## [$version]' section. The release notes are that section and " +
            "nothing else, so there is nothing to release this tag with. CHANGELOG.md is updated " +
            "on every commit for exactly this reason.")
}

$body = [System.Collections.Generic.List[string]]::new()
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') { break }
    # Reference definitions are Markdown plumbing, not notes, and the oldest section runs to the
    # end of the file where they all live.
    if ($lines[$i] -match '^\[[^\]]+\]:\s') { continue }
    $body.Add($lines[$i])
}

$notes = ($body -join [Environment]::NewLine).Trim()
if ([string]::IsNullOrWhiteSpace($notes)) {
    Fail 5 "CHANGELOG.md's '## [$version]' section is empty. An empty section is not release notes."
}

Write-Output $notes
exit 0
