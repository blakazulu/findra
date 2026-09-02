#Requires -Version 7
<#
.SYNOPSIS
    Runs every headless mode and checks the exit code each one owes.

.DESCRIPTION
    Spec 9: the diagnostic modes are how Findra is verified without a screen. This is what keeps
    them working, on a machine with no index, no models, no helper and no desktop - which is
    exactly the machine a stranger first runs them on.

    The interesting case is --searchprobe. With no elevated helper it CANNOT succeed, and it
    exits 1 by design. So the assertion is not "it works" - it is that it still reaches the pipe
    and says so. A probe that crashed on the way would exit 134 or 255, and this is what notices.

    Every mode Program.Main advertises is here except four, and WorkflowTests asserts that:
    --names wants an elevated volume handle, --index wants a parent process to be a child of,
    and --uninstall and --stop want a machine somebody is willing to lose. --uninstall appears
    below in its dry-run form only, which prints the same report and touches nothing.
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Exe)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe)) { throw "diagnostics: no executable at '$Exe'" }

$failed = 0
$shot = Join-Path ([System.IO.Path]::GetTempPath()) 'findra-ci-shot.png'
$bench = Join-Path ([System.IO.Path]::GetTempPath()) 'findra-ci-bench.md'

function Check {
    param([string] $What, [int[]] $Allowed, [string[]] $Arguments, [string] $MustSay = '')

    $output = & $Exe @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    if ($Allowed -notcontains $code) {
        [Console]::Error.WriteLine("diagnostics: $What exited $code, expected one of $($Allowed -join ', ')")
        [Console]::Error.WriteLine($output)
        $script:failed++
        return
    }
    if ($MustSay -and ($output -notmatch [regex]::Escape($MustSay))) {
        [Console]::Error.WriteLine("diagnostics: $What did not mention '$MustSay'")
        [Console]::Error.WriteLine($output)
        $script:failed++
        return
    }
    Write-Output "  ok  $What (exit $code)"
}

Check '--version'      @(0) @('--version') 'log:'
Check '--searchtest'   @(0) @('--searchtest')
Check '--searchindex'  @(0) @('--searchindex')
Check '--searchmodels' @(0) @('--searchmodels')
# The two settings commands, in the form that reports rather than the form that changes anything.
# `--models list` and `--content status` are what they do with no verb, and neither writes.
Check '--models'       @(0) @('--models')
Check '--content'      @(0) @('--content')
# One shot per surface family, so a painter that throws on a runner is caught here as well as in
# the test suite. The suite renders all of them; this proves the shipped binary can.
Check '--searchshot results'  @(0) @('--searchshot', $shot, 'results')
Check '--searchshot settings' @(0) @('--searchshot', $shot, 'settingscontent')
Check '--searchshot firstrun' @(0) @('--searchshot', $shot, 'firstrun')
# A small corpus: CI is not where a throughput number is produced, and nothing quotes this run.
Check '--searchbench'  @(0) @('--searchbench', $bench, '200')
# 1 is correct here: no elevated helper on a runner, and 2 is a helper that answered with no
# rows. What is being checked is that it got as far as the pipe and said which way it went.
Check '--searchprobe'  @(0, 1, 2) @('--searchprobe', 'sunset') 'pipe'
# Prints what it WOULD remove and touches nothing.
Check '--uninstall --dry-run' @(0) @('--uninstall', '--dry-run') 'scheduled task'
# A mistyped mode must not look like a success.
Check 'an unknown mode' @(1) @('--searchprob')

Remove-Item -LiteralPath $shot, $bench -ErrorAction SilentlyContinue

if ($failed -gt 0) { [Console]::Error.WriteLine("diagnostics: $failed check(s) failed"); exit 1 }
Write-Output 'diagnostics: all modes answered'
exit 0
