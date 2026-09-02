#Requires -Version 7
<#
.SYNOPSIS
    Self-contained publish for one runtime identifier.

.DESCRIPTION
    The RID lives HERE and nowhere else. No project file in the tree carries a
    <RuntimeIdentifier> or a <RuntimeIdentifiers> - ProjectFileTests asserts that - so
    win-arm64 stays reachable and nothing about the source assumes x64 (spec 6).

    Self-contained is not optional (spec 2): a stranger installing from winget must never meet an
    "install .NET first" prompt.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')] [string] $Rid = 'win-x64',
    [string] $Configuration = 'Release',
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$out = Join-Path $Root "publish/$Rid"
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }

dotnet publish (Join-Path $Root 'src/Findra/Findra.csproj') `
    --configuration $Configuration `
    --runtime $Rid `
    --self-contained true `
    --output $out
if ($LASTEXITCODE -ne 0) { throw "publish failed for $Rid" }

# The models are downloaded on first run into %LOCALAPPDATA%\Findra\models and must never be in
# the publish folder (spec 2) - an upgrade wipes this directory.
$strays = Get-ChildItem -LiteralPath $out -Recurse -Include '*.onnx', '*.spm', 'whisper-*.bin'
if ($strays) { throw "model files in the publish folder: $($strays.Name -join ', ')" }

Write-Output $out
