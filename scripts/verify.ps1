param([switch]$UiChecks)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-common.ps1')
$timer = [Diagnostics.Stopwatch]::StartNew()
Push-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
try {
    Invoke-SidetalkVerification -UiChecks:$UiChecks
    Write-Host ("Verification complete ({0:F2}s). No deployment was published or replaced." -f $timer.Elapsed.TotalSeconds)
} catch {
    exit (Write-SidetalkFailure $_)
} finally {
    Pop-Location
}
