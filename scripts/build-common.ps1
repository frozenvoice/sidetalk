# Shared current-source checks. This file does not inspect or replace a deployment.
function Start-SidetalkStage {
    param([string]$Name, [int]$ExitCode)
    $script:sidetalkStage = $Name
    $script:sidetalkExitCode = $ExitCode
    $script:sidetalkStageTimer = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "[$Name] Starting"
}

function Complete-SidetalkStage {
    Write-Host ("[$script:sidetalkStage] Passed ({0:F2}s)" -f $script:sidetalkStageTimer.Elapsed.TotalSeconds)
}

function Invoke-SidetalkDotnet {
    param([string[]]$Arguments)
    # Check native exit codes ourselves, also when the caller enabled this preference.
    $PSNativeCommandUseErrorActionPreference = $false
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) exited with code $LASTEXITCODE. See the compiler/test diagnostics above. If a build output is locked, release that specific output before retrying; no process was stopped."
    }
}

function Invoke-SidetalkVerification {
    # Shared by verify.ps1 and build.ps1: always build app/tests/probe. Do not make
    # synthetic UI or installer generation/installation implicit for code changes.
    param([switch]$UiChecks)
    Start-SidetalkStage 'verify/app' 10
    Invoke-SidetalkDotnet @('build', 'src/Sidetalk.App/Sidetalk.App.csproj', '-c', 'Release', '--nologo')
    Complete-SidetalkStage
    Start-SidetalkStage 'verify/safety-tests' 11
    Invoke-SidetalkDotnet @('run', '--project', 'tests/Sidetalk.Tests/Sidetalk.Tests.csproj', '-c', 'Release')
    Complete-SidetalkStage
    Start-SidetalkStage 'verify/isolation-probe' 12
    Invoke-SidetalkDotnet @('build', 'src/Sidetalk.IsolationProbe/Sidetalk.IsolationProbe.csproj', '-c', 'Release', '--nologo')
    Complete-SidetalkStage
    if ($UiChecks) {
        Start-SidetalkStage 'verify/ui-fixtures' 13
        $UiOutputPath = Join-Path ([IO.Path]::GetTempPath()) ('sidetalk-ui-checks-' + [Guid]::NewGuid().ToString('N'))
        Invoke-SidetalkDotnet @('run', '--project', 'tests/Sidetalk.UiChecks/Sidetalk.UiChecks.csproj', '-c', 'Release', '--', $UiOutputPath)
        Write-Host "Synthetic UI fixtures: $UiOutputPath"
        Complete-SidetalkStage
    }
}

function Write-SidetalkFailure {
    param([System.Management.Automation.ErrorRecord]$Failure)
    $code = $Failure.Exception.Data['SidetalkExitCode']
    if ($null -eq $code) { $code = $script:sidetalkExitCode }
    if ($null -eq $code) { $code = 1 }
    [Console]::Error.WriteLine("[$script:sidetalkStage] Failed (exit $code): $($Failure.Exception.Message)")
    return [int]$code
}
